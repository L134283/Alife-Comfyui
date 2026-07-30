using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Alife.Plugin.Comfyui;

/// <summary>
/// DanbooruSearchOnline REST 客户端：多源故障转移、总超时预算、短时缓存、熔断。
/// 与 ComfyUI 的 HttpClient 解耦，避免 10 分钟超时拖死对话。
/// 上游：https://github.com/SuzumiyaAkizuki/DanbooruSearchOnline （MIT）
/// </summary>
static class DanbooruSearchClient
{
    public const string DefaultPrimaryUrl = "https://sakizuki-danboorusearchonline.ms.show";
    public const string DefaultFallbackUrl = "https://sakizuki-danboorusearch.hf.space";

    static readonly HttpClient Http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan // 由每次 CTS 控制
    };

    static readonly SemaphoreSlim Gate = new(2, 2);
    static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);
    static readonly object FailLock = new();
    static int _consecutiveFailures;
    static DateTime _circuitOpenUntil = DateTime.MinValue;
    static string? _stickyHost;
    static DateTime _stickyUntil = DateTime.MinValue;

    const int CacheTtlMinutes = 10;
    const int CacheMaxEntries = 48;
    const int CircuitFailThreshold = 3;
    const int CircuitOpenMinutes = 3;
    const int StickyMinutes = 10;
    const int MaxResponseBytes = 2 * 1024 * 1024;

    record CacheEntry(string Text, DateTime ExpiresAt);

    public sealed class SearchModeParams
    {
        public int TopK { get; init; } = 5;
        public int Limit { get; init; } = 50;
        public bool UseSegmentation { get; init; } = true;
    }

    public static SearchModeParams ResolveMode(string? mode)
    {
        return (mode ?? "full_scene").Trim().ToLowerInvariant() switch
        {
            "concept_explore" => new SearchModeParams { TopK = 40, Limit = 60, UseSegmentation = true },
            "subject_describe" => new SearchModeParams { TopK = 20, Limit = 20, UseSegmentation = false },
            "precise_lookup" => new SearchModeParams { TopK = 20, Limit = 10, UseSegmentation = false },
            _ => new SearchModeParams { TopK = 5, Limit = 50, UseSegmentation = true } // full_scene
        };
    }

    public static async Task<(bool Ok, string Message, long ElapsedMs)> HealthAsync(
        string baseUrl, int timeoutSeconds = 15)
    {
        baseUrl = NormalizeBase(baseUrl);
        if (string.IsNullOrEmpty(baseUrl))
            return (false, "URL 为空", 0);

        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 3, 60)));
            using var req = new HttpRequestMessage(HttpMethod.Get, Combine(baseUrl, "/api/health"));
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            var body = await ReadBodyLimitedAsync(resp, cts.Token);
            sw.Stop();
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}: {Trim(body, 200)}", sw.ElapsedMilliseconds);

            try
            {
                var node = JsonNode.Parse(body) as JsonObject;
                var status = node?["status"]?.GetValue<string>() ?? "";
                var loaded = node?["loaded"]?.GetValue<bool>() ?? false;
                var ok = string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) && loaded;
                return (ok,
                    ok ? $"ok, loaded=true" : $"status={status}, loaded={loaded}",
                    sw.ElapsedMilliseconds);
            }
            catch
            {
                return (false, "响应非 JSON: " + Trim(body, 120), sw.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return (false, "超时", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public static async Task<string> SearchAsync(
        string query,
        string? mode,
        bool showNsfw,
        string primaryUrl,
        string? fallbackUrl,
        int totalTimeoutSeconds)
    {
        query = (query ?? "").Trim();
        if (string.IsNullOrEmpty(query))
            return Unavailable("query 为空", "请补全画面描述后重试，或直接用英文写 prompt");

        if (query.Length > 500)
            query = query[..500];

        var mp = ResolveMode(mode);
        var cacheKey = $"search|{Norm(query)}|{mode}|{showNsfw}|{NormHost(primaryUrl)}|{NormHost(fallbackUrl)}";
        if (TryGetCache(cacheKey, out var cached))
            return cached;

        if (IsCircuitOpen(out var remain))
            return Unavailable($"熔断中（约 {remain}s）", "请直接用英文 Danbooru 风格写 prompt 并 generateimage，勿反复重试检索");

        var hosts = BuildHostList(primaryUrl, fallbackUrl);
        if (hosts.Count == 0)
            return Unavailable("未配置检索地址", "请在插件 UI 填写主源 URL，或关闭在线检索");

        var totalBudget = TimeSpan.FromSeconds(Math.Clamp(totalTimeoutSeconds, 10, 120));
        var deadline = DateTime.UtcNow + totalBudget;
        var errors = new List<string>();

        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(2)))
            return Unavailable("检索繁忙", "请稍后重试，或直接自写英文 prompt 生图");

        try
        {
            foreach (var host in hosts)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining < TimeSpan.FromSeconds(2))
                {
                    errors.Add("总超时预算耗尽");
                    break;
                }

                // 多 host 平分剩余时间，至少 5s
                var perHost = TimeSpan.FromMilliseconds(
                    Math.Max(5000, remaining.TotalMilliseconds / Math.Max(1, hosts.Count - hosts.IndexOf(host))));
                if (perHost > remaining) perHost = remaining;

                try
                {
                    var payload = new JsonObject
                    {
                        ["query"] = query,
                        ["top_k"] = mp.TopK,
                        ["limit"] = mp.Limit,
                        ["show_nsfw"] = showNsfw,
                        ["use_segmentation"] = mp.UseSegmentation
                    };

                    var json = await PostJsonAsync(host, "/api/search", payload, perHost);
                    if (json == null)
                    {
                        errors.Add($"{ShortHost(host)}: 空响应");
                        continue;
                    }

                    if (json["detail"] != null && json["tags_all"] == null && json["results"] == null)
                    {
                        errors.Add($"{ShortHost(host)}: {json["detail"]}");
                        continue;
                    }

                    var text = FormatSearchResult(json, showNsfw, mode ?? "full_scene", host);
                    RememberSuccess(host);
                    PutCache(cacheKey, text);
                    return text;
                }
                catch (Exception ex)
                {
                    errors.Add($"{ShortHost(host)}: {ex.Message}");
                }
            }

            RememberFailure();
            var hint = errors.Any(e => e.Contains("超时", StringComparison.Ordinal) || e.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                ? "可能冷启或网络慢：可浏览器打开 Space 唤醒，或改用备份/自建源"
                : "请检查网络、代理或自建服务";
            return Unavailable(string.Join(" | ", errors.Take(3)),
                $"直接按当前提示词模式写英文并 generateimage，勿声称已检索到。({hint})");
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<string> RelatedAsync(
        string tagsCsv,
        bool showNsfw,
        string primaryUrl,
        string? fallbackUrl,
        int totalTimeoutSeconds,
        int limit = 30)
    {
        var tags = ParseTagList(tagsCsv);
        if (tags.Count == 0)
            return Unavailable("tags 为空", "请传入逗号分隔的英文 Danbooru tag");

        var cacheKey = $"related|{string.Join(",", tags)}|{showNsfw}|{NormHost(primaryUrl)}|{NormHost(fallbackUrl)}";
        if (TryGetCache(cacheKey, out var cached))
            return cached;

        if (IsCircuitOpen(out var remain))
            return Unavailable($"熔断中（约 {remain}s）", "请直接组合已有 tag 生图，勿反复重试");

        var hosts = BuildHostList(primaryUrl, fallbackUrl);
        if (hosts.Count == 0)
            return Unavailable("未配置检索地址", "请配置主源或关闭在线检索");

        var totalBudget = TimeSpan.FromSeconds(Math.Clamp(totalTimeoutSeconds, 10, 120));
        var deadline = DateTime.UtcNow + totalBudget;
        var errors = new List<string>();

        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(2)))
            return Unavailable("检索繁忙", "请稍后或自写 tag");

        try
        {
            foreach (var host in hosts)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining < TimeSpan.FromSeconds(2)) break;

                var perHost = TimeSpan.FromMilliseconds(
                    Math.Max(5000, remaining.TotalMilliseconds / Math.Max(1, hosts.Count - hosts.IndexOf(host))));
                if (perHost > remaining) perHost = remaining;

                try
                {
                    var arr = new JsonArray();
                    foreach (var t in tags) arr.Add(t);
                    var payload = new JsonObject
                    {
                        ["tags"] = arr,
                        ["limit"] = Math.Clamp(limit, 1, 80),
                        ["show_nsfw"] = showNsfw
                    };

                    var json = await PostJsonAsync(host, "/api/related", payload, perHost);
                    if (json == null)
                    {
                        errors.Add($"{ShortHost(host)}: 空响应");
                        continue;
                    }

                    if (json["error"] != null)
                    {
                        errors.Add($"{ShortHost(host)}: {json["error"]}");
                        continue;
                    }

                    var text = FormatRelatedResult(json, host);
                    RememberSuccess(host);
                    PutCache(cacheKey, text);
                    return text;
                }
                catch (Exception ex)
                {
                    errors.Add($"{ShortHost(host)}: {ex.Message}");
                }
            }

            RememberFailure();
            return Unavailable(string.Join(" | ", errors.Take(3)),
                "请用已有英文 tag 直接 generateimage，勿反复重试");
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<string> ArtistsAsync(
        string tagsCsv,
        bool showNsfw,
        string primaryUrl,
        string? fallbackUrl,
        int totalTimeoutSeconds,
        int limit = 5)
    {
        var tags = ParseTagList(tagsCsv);
        if (tags.Count == 0)
            return Unavailable("tags 为空", "请传入已确定的英文 tag 再查画师");

        var cacheKey = $"artists|{string.Join(",", tags)}|{showNsfw}|{NormHost(primaryUrl)}|{NormHost(fallbackUrl)}";
        if (TryGetCache(cacheKey, out var cached))
            return cached;

        if (IsCircuitOpen(out var remain))
            return Unavailable($"熔断中（约 {remain}s）", "请跳过画师推荐，直接生图");

        var hosts = BuildHostList(primaryUrl, fallbackUrl);
        if (hosts.Count == 0)
            return Unavailable("未配置检索地址", "请配置主源或关闭画师推荐");

        var totalBudget = TimeSpan.FromSeconds(Math.Clamp(totalTimeoutSeconds, 10, 120));
        var deadline = DateTime.UtcNow + totalBudget;
        var errors = new List<string>();

        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(2)))
            return Unavailable("检索繁忙", "请跳过画师或稍后");

        try
        {
            foreach (var host in hosts)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining < TimeSpan.FromSeconds(2)) break;

                var perHost = TimeSpan.FromMilliseconds(
                    Math.Max(5000, remaining.TotalMilliseconds / Math.Max(1, hosts.Count - hosts.IndexOf(host))));
                if (perHost > remaining) perHost = remaining;

                try
                {
                    var arr = new JsonArray();
                    foreach (var t in tags) arr.Add(t);
                    var payload = new JsonObject
                    {
                        ["tags"] = arr,
                        ["limit"] = Math.Clamp(limit, 1, 30),
                        ["show_nsfw"] = showNsfw
                    };

                    var json = await PostJsonAsync(host, "/api/artists", payload, perHost);
                    if (json == null)
                    {
                        errors.Add($"{ShortHost(host)}: 空响应");
                        continue;
                    }

                    if (json["error"] != null)
                    {
                        errors.Add($"{ShortHost(host)}: {json["error"]}");
                        continue;
                    }

                    var text = FormatArtistsResult(json, host);
                    RememberSuccess(host);
                    PutCache(cacheKey, text);
                    return text;
                }
                catch (Exception ex)
                {
                    errors.Add($"{ShortHost(host)}: {ex.Message}");
                }
            }

            RememberFailure();
            return Unavailable(string.Join(" | ", errors.Take(3)),
                "跳过画师，直接按当前 prompt 生图");
        }
        finally
        {
            Gate.Release();
        }
    }

    // ---------- formatting ----------

    static string FormatSearchResult(JsonObject json, bool showNsfw, string mode, string host)
    {
        var tagsAll = json["tags_all"]?.GetValue<string>() ?? "";
        var tagsSfw = json["tags_sfw"]?.GetValue<string>() ?? tagsAll;
        var promptTags = showNsfw ? tagsAll : tagsSfw;
        if (string.IsNullOrWhiteSpace(promptTags))
            promptTags = tagsAll;

        // 控制长度：约 25 个 tag
        promptTags = LimitTagString(promptTags, 25);

        var sb = new StringBuilder();
        sb.AppendLine("status: ok");
        sb.Append("source: ").AppendLine(ShortHost(host));
        sb.Append("mode: ").AppendLine(mode);
        sb.Append("prompt_tags: ").AppendLine(promptTags);

        if (json["keywords"] is JsonArray kws && kws.Count > 0)
        {
            var kw = string.Join(", ", kws.Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0).Take(12));
            if (kw.Length > 0)
                sb.Append("keywords: ").AppendLine(kw);
        }

        if (json["results"] is JsonArray results)
        {
            sb.AppendLine("top_tags:");
            var n = 0;
            foreach (var item in results.OfType<JsonObject>())
            {
                if (n >= 20) break;
                var tag = item["tag"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(tag)) continue;
                if (!showNsfw)
                {
                    var nsfw = item["nsfw"]?.ToString() ?? "0";
                    if (nsfw != "0" && !string.Equals(nsfw, "false", StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                var cn = item["cn_name"]?.GetValue<string>() ?? "";
                var cat = item["category"]?.GetValue<string>() ?? "";
                var score = item["final_score"]?.ToString() ?? "";
                sb.Append("- ").Append(tag);
                if (!string.IsNullOrWhiteSpace(cn)) sb.Append(" (").Append(Trim(cn, 40)).Append(')');
                if (!string.IsNullOrWhiteSpace(cat)) sb.Append(" [").Append(cat).Append(']');
                if (!string.IsNullOrWhiteSpace(score)) sb.Append(" score=").Append(Trim(score, 8));
                sb.AppendLine();
                n++;
            }
        }

        sb.Append("usage: 仅使用 status: ok 的英文 tag；按当前提示词模式组织进 generateimage（natural 用短句消化语义，勿整段 tag 列表当 prompt；角色 trigger/appearance 不被覆盖）");
        return sb.ToString().TrimEnd();
    }

    static string FormatRelatedResult(JsonObject json, string host)
    {
        var sb = new StringBuilder();
        sb.AppendLine("status: ok");
        sb.Append("source: ").AppendLine(ShortHost(host));

        if (json["correction_note"] != null)
            sb.Append("note: ").AppendLine(json["correction_note"]!.ToString());

        var tags = new List<string>();
        if (json["results"] is JsonArray results)
        {
            sb.AppendLine("related_tags:");
            var n = 0;
            foreach (var item in results.OfType<JsonObject>())
            {
                if (n >= 25) break;
                var tag = item["tag"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(tag)) continue;
                tags.Add(tag);
                var cn = item["cn_name"]?.GetValue<string>() ?? "";
                var cat = item["category"]?.GetValue<string>() ?? "";
                sb.Append("- ").Append(tag);
                if (!string.IsNullOrWhiteSpace(cn)) sb.Append(" (").Append(Trim(cn, 40)).Append(')');
                if (!string.IsNullOrWhiteSpace(cat)) sb.Append(" [").Append(cat).Append(']');
                sb.AppendLine();
                n++;
            }
        }

        if (tags.Count > 0)
            sb.Append("prompt_tags: ").AppendLine(string.Join(", ", tags.Take(20)));

        sb.Append("usage: 从 related 中挑选搭配 tag 补进 prompt；去重，勿覆盖角色固定外貌");
        return sb.ToString().TrimEnd();
    }

    static string FormatArtistsResult(JsonObject json, string host)
    {
        var sb = new StringBuilder();
        sb.AppendLine("status: ok");
        sb.Append("source: ").AppendLine(ShortHost(host));

        if (json["correction_note"] != null)
            sb.Append("note: ").AppendLine(json["correction_note"]!.ToString());

        var names = new List<string>();
        if (json["results"] is JsonArray results)
        {
            sb.AppendLine("artists:");
            var n = 0;
            foreach (var item in results.OfType<JsonObject>())
            {
                if (n >= 5) break;
                var artist = item["artist"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(artist)) continue;
                names.Add(artist);
                var cooc = item["cooc_count"]?.ToString() ?? "";
                var posts = item["post_count"]?.ToString() ?? "";
                sb.Append("- ").Append(artist);
                if (!string.IsNullOrWhiteSpace(cooc)) sb.Append(" cooc=").Append(cooc);
                if (!string.IsNullOrWhiteSpace(posts)) sb.Append(" posts=").Append(posts);
                if (item["top_tags"] is JsonArray tops)
                {
                    var tt = string.Join(", ", tops.Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0).Take(6));
                    if (tt.Length > 0) sb.Append(" | top: ").Append(tt);
                }
                sb.AppendLine();
                n++;
            }
        }

        if (names.Count > 0)
            sb.Append("artist_names: ").AppendLine(string.Join(", ", names));

        sb.Append("usage: 用户明确要画风/画师时，可将画师名按工作流习惯写入 prompt（常见加 artist: 或 @）；未要求则不要强加");
        return sb.ToString().TrimEnd();
    }

    static string Unavailable(string reason, string action)
        => $"status: unavailable\nreason: {reason}\naction: {action}";

    // ---------- HTTP ----------

    static async Task<JsonObject?> PostJsonAsync(string baseUrl, string path, JsonObject payload, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var url = Combine(baseUrl, path);
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        var raw = payload.ToJsonString();
        req.Content = new StringContent(raw, Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        var body = await ReadBodyLimitedAsync(resp, cts.Token);

        if ((int)resp.StatusCode >= 500)
            throw new Exception($"HTTP {(int)resp.StatusCode}: {Trim(body, 160)}");

        if (string.IsNullOrWhiteSpace(body))
            return null;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch
        {
            throw new Exception("响应非 JSON: " + Trim(body, 120));
        }

        if (node is not JsonObject obj)
            throw new Exception("响应非对象");

        // 4xx 仍可能带 detail
        if (!resp.IsSuccessStatusCode && obj["results"] == null && obj["tags_all"] == null)
            throw new Exception($"HTTP {(int)resp.StatusCode}: {obj["detail"] ?? obj["error"] ?? Trim(body, 120)}");

        return obj;
    }

    static async Task<string> ReadBodyLimitedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[Math.Min(MaxResponseBytes + 1, 256 * 1024)];
        using var ms = new System.IO.MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxResponseBytes)
                throw new Exception("响应过大");
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ---------- hosts / cache / circuit ----------

    static List<string> BuildHostList(string primaryUrl, string? fallbackUrl)
    {
        var primary = NormalizeBase(primaryUrl);
        var fallback = NormalizeBase(fallbackUrl ?? "");
        var list = new List<string>();

        var customOnly = IsCustomPrimary(primary);
        void Add(string? h)
        {
            if (string.IsNullOrEmpty(h)) return;
            if (list.Any(x => string.Equals(x, h, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(h);
        }

        // sticky 优先（若仍在列表中）
        if (!string.IsNullOrEmpty(_stickyHost)
            && DateTime.UtcNow < _stickyUntil
            && (string.Equals(_stickyHost, primary, StringComparison.OrdinalIgnoreCase)
                || (!customOnly && string.Equals(_stickyHost, fallback, StringComparison.OrdinalIgnoreCase))))
        {
            Add(_stickyHost);
        }

        Add(primary);
        if (!customOnly)
            Add(fallback);

        return list;
    }

    static bool IsCustomPrimary(string primary)
    {
        if (string.IsNullOrEmpty(primary)) return false;
        var p = primary.TrimEnd('/').ToLowerInvariant();
        var d1 = DefaultPrimaryUrl.TrimEnd('/').ToLowerInvariant();
        var d2 = DefaultFallbackUrl.TrimEnd('/').ToLowerInvariant();
        return p != d1 && p != d2;
    }

    static void RememberSuccess(string host)
    {
        lock (FailLock)
        {
            _consecutiveFailures = 0;
            _circuitOpenUntil = DateTime.MinValue;
            _stickyHost = NormalizeBase(host);
            _stickyUntil = DateTime.UtcNow.AddMinutes(StickyMinutes);
        }
    }

    static void RememberFailure()
    {
        lock (FailLock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= CircuitFailThreshold)
            {
                _circuitOpenUntil = DateTime.UtcNow.AddMinutes(CircuitOpenMinutes);
                _consecutiveFailures = 0;
            }
        }
    }

    static bool IsCircuitOpen(out int remainSeconds)
    {
        lock (FailLock)
        {
            if (DateTime.UtcNow < _circuitOpenUntil)
            {
                remainSeconds = (int)Math.Ceiling((_circuitOpenUntil - DateTime.UtcNow).TotalSeconds);
                return true;
            }
            remainSeconds = 0;
            return false;
        }
    }

    static bool TryGetCache(string key, out string text)
    {
        text = "";
        if (!Cache.TryGetValue(key, out var entry)) return false;
        if (DateTime.UtcNow > entry.ExpiresAt)
        {
            Cache.TryRemove(key, out _);
            return false;
        }
        text = entry.Text;
        return true;
    }

    static void PutCache(string key, string text)
    {
        if (Cache.Count >= CacheMaxEntries)
        {
            foreach (var old in Cache.OrderBy(kv => kv.Value.ExpiresAt).Take(8).Select(kv => kv.Key).ToList())
                Cache.TryRemove(old, out _);
        }
        Cache[key] = new CacheEntry(text, DateTime.UtcNow.AddMinutes(CacheTtlMinutes));
    }

    static List<string> ParseTagList(string csv)
    {
        return (csv ?? "")
            .Split(new[] { ',', '，', ';', '；', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().Replace(' ', '_'))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }

    static string LimitTagString(string tags, int maxCount)
    {
        var parts = tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Take(maxCount);
        return string.Join(", ", parts);
    }

    static string NormalizeBase(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var u = url.Trim().TrimEnd('/');
        if (u.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            u = u[..^4].TrimEnd('/');
        if (u.EndsWith("/mcp/mcp", StringComparison.OrdinalIgnoreCase))
            u = u[..^"/mcp/mcp".Length].TrimEnd('/');
        if (u.EndsWith("/mcp", StringComparison.OrdinalIgnoreCase))
            u = u[..^4].TrimEnd('/');
        return u;
    }

    static string Combine(string baseUrl, string path)
    {
        baseUrl = NormalizeBase(baseUrl);
        if (!path.StartsWith('/')) path = "/" + path;
        return baseUrl + path;
    }

    static string ShortHost(string baseUrl)
    {
        try
        {
            var u = new Uri(NormalizeBase(baseUrl));
            return u.Host;
        }
        catch
        {
            return Trim(baseUrl, 40);
        }
    }

    static string NormHost(string? url) => ShortHost(NormalizeBase(url ?? ""));

    static string Norm(string s) => s.Trim().ToLowerInvariant();

    static string Trim(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
