using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Alife.Plugin.Comfyui;

/// <summary>
/// DanbooruSearch 在线镜像的角色检索客户端（国内可直连，约 0.3~1.5s，自带中文角色名与 wiki）。
///
/// 为什么优先用它：
/// - Danbooru 官方 API（danbooru.donmai.us）在国内经常连不上/20s 超时；
/// - 该镜像（用户已用于在线标签检索）返回的 /api/search 结果直接带 cn_name（中文名/别名/作品）
///   与 count（图片数），/api/related 能给出角色高频通用标签；
/// - 支持中文查询，不必先转英文。
///
/// 端点（POST JSON，与 DanbooruSearchClient 同一服务）：
/// - /api/search  {query, top_k, limit, show_nsfw} → results[]（category=Character/Copyright/General）
/// - /api/related {tags[], limit, show_nsfw} → results[]（category 多为 General，即特征标签）
/// </summary>
static class MirrorCharacterClient
{
    public const string DefaultBaseUrl = "https://sakizuki-danboorusearchonline.ms.show";

    const string UserAgent = "Alife-Comfyui-Plugin/1.0 (character lookup; local desktop app)";
    const int CacheTtlMinutes = 10;
    const int CacheMaxEntries = 32;
    const int MaxCandidates = 10;
    const int MaxTagsPerSection = 18;
    const double MinScore = 0.655;

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);

    sealed record CacheEntry(MirrorResult Result, DateTime ExpiresAt);

    public sealed class MirrorResult
    {
        public List<MirrorCharacterMatch> Matches { get; } = new();
        public string? Error { get; set; }
        public bool Unavailable { get; set; }
        public bool WorkMatched { get; set; }
    }

    public sealed class MirrorCharacterMatch
    {
        public string Tag { get; set; } = "";
        /// <summary>中文名（镜像 cn_name 第一段）；没有中文时为英文可读名</summary>
        public string Name { get; set; } = "";
        public string EnglishName { get; set; } = "";
        /// <summary>作品：优先中文（wiki「来自《…》」），否则英文限定符</summary>
        public string Copyright { get; set; } = "";
        public string EnglishCopyright { get; set; } = "";
        public string Trigger { get; set; } = "";
        public string Appearance { get; set; } = "";
        public string Outfit { get; set; } = "";
        public long Count { get; set; }
        public double Score { get; set; }
    }

    public static async Task<(bool Ok, string Message, long ElapsedMs)> HealthAsync(
        string baseUrl, int timeoutSeconds = 15)
    {
        baseUrl = NormalizeBase(baseUrl);
        if (baseUrl.Length == 0)
            return (false, "URL 为空", 0);

        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 3, 60)));
            var (status, body) = await PostAsync(baseUrl, "/api/search",
                new JsonObject { ["query"] = "hatsune miku", ["top_k"] = 5, ["limit"] = 5, ["show_nsfw"] = false },
                cts.Token);
            sw.Stop();
            if (status != 200)
                return (false, $"HTTP {status}: {Trim(body, 160)}", sw.ElapsedMilliseconds);

            var obj = JsonNode.Parse(body) as JsonObject;
            var first = (obj?["results"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
            var tag = first?["tag"]?.GetValue<string>() ?? "";
            return (tag.Length > 0, tag.Length > 0 ? $"ok，示例角色：{tag}" : "响应格式异常", sw.ElapsedMilliseconds);
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

    public static async Task<MirrorResult> SearchCharactersAsync(
        string query, string? work, string baseUrl, int timeoutSeconds, int minPosts)
    {
        query = (query ?? "").Trim();
        var result = new MirrorResult();
        if (query.Length == 0)
        {
            result.Error = "角色名为空";
            return result;
        }

        baseUrl = NormalizeBase(baseUrl);
        if (baseUrl.Length == 0)
            baseUrl = DefaultBaseUrl;

        minPosts = Math.Clamp(minPosts, 0, 1_000_000);
        var cacheKey = $"mirror|{Norm(query)}|{Norm(work ?? "")}|{NormHost(baseUrl)}|{minPosts}";
        if (TryGetCache(cacheKey, out var cached))
            return cached.Result;

        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 5, 60));
        try
        {
            // 带作品名时把作品拼进查询词：镜像的语义检索对「asuna」只给 SAO 版，
            // 拼成「asuna blue archive」才会把 BA 版排上来（后面仍做作品校验）。
            var searchQuery = query;
            if (!string.IsNullOrWhiteSpace(work)
                && !query.Contains(work.Trim(), StringComparison.OrdinalIgnoreCase))
                searchQuery = query + " " + work.Trim();

            var searchPayload = new JsonObject
            {
                ["query"] = searchQuery,
                ["top_k"] = 24,
                ["limit"] = 24,
                ["show_nsfw"] = false
            };
            var searchObj = await PostJsonAsync(baseUrl, "/api/search", searchPayload, deadline);
            if (searchObj == null)
            {
                result.Unavailable = true;
                result.Error = "查询失败或超时";
                return result;
            }

            var candidates = new List<MirrorCharacterMatch>();
            foreach (var row in (searchObj["results"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                if (!string.Equals(row["category"]?.GetValue<string>(), "Character", StringComparison.OrdinalIgnoreCase))
                    continue;
                var tag = row["tag"]?.GetValue<string>() ?? "";
                if (tag.Length == 0)
                    continue;
                var count = ReadLong(row["count"]);
                var score = ReadDouble(row["final_score"]);
                var nameMatched = NameMatches(tag, query);
                if (count < minPosts || (score < MinScore && !nameMatched))
                    continue;

                var enWork = EnglishWorkOf(row, tag);
                var cnWork = ExtractCnWork(row["wiki"]?.GetValue<string>());
                candidates.Add(new MirrorCharacterMatch
                {
                    Tag = tag,
                    Count = count,
                    Score = score,
                    EnglishName = BooruCharacterClient.PrettyNameOf(tag),
                    Name = CnNameOf(row["cn_name"]?.GetValue<string>()) is { Length: > 0 } cn
                        ? cn
                        : BooruCharacterClient.PrettyNameOf(tag),
                    EnglishCopyright = enWork,
                    Copyright = cnWork.Length > 0 ? cnWork : enWork
                });

                if (candidates.Count >= MaxCandidates)
                    break;
            }

            var workQuery = Norm(work ?? "");
            if (workQuery.Length > 0)
            {
                var filtered = candidates.Where(c => WorkMatches(c, workQuery)).ToList();
                if (filtered.Count > 0)
                {
                    candidates = filtered;
                    result.WorkMatched = true;
                }
            }

            if (candidates.Count == 0)
            {
                SetCache(cacheKey, result);
                return result;
            }

            // 同档热度优先：简体查询时常出现「正体 0.88 / 同人变体 0.90」，
            // 直接取最高分会选到变体（如 sakayori_iroha_(ray)），而正体通常热度高一个量级。
            candidates.Sort((a, b) =>
                Math.Abs(a.Score - b.Score) > 0.06
                    ? b.Score.CompareTo(a.Score)
                    : b.Count.CompareTo(a.Count));

            // 只对最佳候选拉特征标签（镜像的 related 一次 0.5s 左右）
            var best = candidates[0];
            var traits = await FetchTraitsAsync(best.Tag, baseUrl, deadline);

            foreach (var match in candidates)
            {
                match.Trigger = match.EnglishCopyright.Length > 0
                    ? $"{match.EnglishName}, {match.EnglishCopyright}"
                    : match.EnglishName;
                if (match.Tag == best.Tag)
                {
                    match.Appearance = traits.Appearance;
                    match.Outfit = traits.Outfit;
                }
                result.Matches.Add(match);
            }

            SetCache(cacheKey, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Unavailable = true;
            result.Error = "超时";
            return result;
        }
        catch (Exception ex)
        {
            result.Unavailable = true;
            result.Error = ex.Message;
            return result;
        }
    }

    static async Task<(string Appearance, string Outfit)> FetchTraitsAsync(
        string tag, string baseUrl, DateTime deadline)
    {
        var payload = new JsonObject
        {
            ["tags"] = new JsonArray { tag },
            ["limit"] = 80,
            ["show_nsfw"] = false
        };
        var obj = await PostJsonAsync(baseUrl, "/api/related", payload, deadline);
        if (obj == null)
            return ("", "");

        var appearance = new List<string>();
        var outfit = new List<string>();
        var hairColors = new List<string>();
        var eyeColors = new List<string>();

        foreach (var row in (obj["results"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            if (!string.Equals(row["category"]?.GetValue<string>(), "General", StringComparison.OrdinalIgnoreCase))
                continue;
            var raw = row["tag"]?.GetValue<string>() ?? "";
            if (raw.Length == 0)
                continue;

            var display = raw.Replace('_', ' ');
            var kind = BooruCharacterClient.ClassifyTraitName(raw);
            if (kind == 1 && appearance.Count < MaxTagsPerSection)
            {
                if (BooruCharacterClient.IsColorConflictWith(display, hairColors, "hair")
                    || BooruCharacterClient.IsColorConflictWith(display, eyeColors, "eyes"))
                    continue;
                if (!appearance.Contains(display))
                    appearance.Add(display);
            }
            else if (kind == 2 && outfit.Count < MaxTagsPerSection)
            {
                if (!outfit.Contains(display))
                    outfit.Add(display);
            }
        }

        return (string.Join(", ", appearance), string.Join(", ", outfit));
    }

    /// <summary>镜像 cn_name 第一段即中文名，如「薇薇安,Zenless Zone Zero,嘲鸫」→ 薇薇安</summary>
    static string CnNameOf(string? cnName)
    {
        var first = (cnName ?? "").Split(',')[0].Trim();
        if (first.Length == 0)
            return "";
        foreach (var ch in first)
            if (ch >= 0x4E00 && ch <= 0x9FFF)
                return first;
        return "";
    }

    /// <summary>wiki「来自《绝区零》的角色」→ 绝区零</summary>
    static string ExtractCnWork(string? wiki)
    {
        if (string.IsNullOrEmpty(wiki))
            return "";
        var m = Regex.Match(wiki, "来自《([^》]+)》");
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    /// <summary>标签名与查询词有包含关系时视为名字命中（短名/低分角色豁免分数阈值）。</summary>
    static bool NameMatches(string tag, string query)
    {
        var t = Norm(tag);
        var q = Norm(query);
        if (t.Length == 0 || q.Length < 3)
            return false;
        return t.Contains(q, StringComparison.Ordinal) || q.Contains(t, StringComparison.Ordinal);
    }

    /// <summary>英文作品名：优先标签限定符；其次 cn_name 第二段（纯 ASCII 时）。</summary>
    static string EnglishWorkOf(JsonObject row, string tag)
    {
        var fromTag = BooruCharacterClient.WorkOf(tag);
        if (fromTag.Length > 0)
            return fromTag.Replace('_', ' ');

        var parts = (row["cn_name"]?.GetValue<string>() ?? "").Split(',');
        if (parts.Length >= 2)
        {
            var candidate = parts[1].Trim();
            if (candidate.Length > 0 && candidate.All(ch => ch < 128))
                return candidate;
        }
        return "";
    }

    static bool WorkMatches(MirrorCharacterMatch match, string normalizedWork)
    {
        foreach (var text in new[] { match.Copyright, match.EnglishCopyright, match.Name, match.EnglishName })
        {
            var norm = Norm(text);
            if (norm.Length == 0)
                continue;
            if (norm == normalizedWork || norm.Contains(normalizedWork) || normalizedWork.Contains(norm))
                return true;
        }
        return false;
    }

    // ---------- HTTP ----------

    static async Task<(int Status, string Body)> PostAsync(
        string baseUrl, string path, JsonObject payload, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + path);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        return ((int)resp.StatusCode, await ReadBodyLimitedAsync(resp, ct));
    }

    static async Task<JsonObject?> PostJsonAsync(
        string baseUrl, string path, JsonObject payload, DateTime deadline)
    {
        var remain = deadline - DateTime.UtcNow;
        if (remain < TimeSpan.FromSeconds(2))
            return null;

        using var cts = new CancellationTokenSource(remain);
        var (status, body) = await PostAsync(baseUrl, path, payload, cts.Token);
        if (status != 200 || string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    // ---------- 工具 ----------

    static bool TryGetCache(string key, out CacheEntry entry)
    {
        if (Cache.TryGetValue(key, out entry!) && entry.ExpiresAt > DateTime.UtcNow)
            return true;
        Cache.TryRemove(key, out _);
        entry = null!;
        return false;
    }

    static void SetCache(string key, MirrorResult result)
    {
        if (Cache.Count >= CacheMaxEntries)
        {
            var oldest = Cache.OrderBy(kv => kv.Value.ExpiresAt).FirstOrDefault();
            if (oldest.Key != null)
                Cache.TryRemove(oldest.Key, out _);
        }
        Cache[key] = new CacheEntry(result, DateTime.UtcNow.AddMinutes(CacheTtlMinutes));
    }

    static async Task<string> ReadBodyLimitedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > 4 * 1024 * 1024)
                break;
            ms.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static string NormalizeBase(string baseUrl)
        => (baseUrl ?? "").Trim().TrimEnd('/');

    static string NormHost(string baseUrl)
    {
        try
        {
            return new Uri(NormalizeBase(baseUrl)).Host.ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    static string Norm(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
        return sb.ToString();
    }

    static long ReadLong(JsonNode? node)
    {
        if (node == null)
            return 0;
        try
        {
            return node.GetValue<long>();
        }
        catch
        {
            return 0;
        }
    }

    static double ReadDouble(JsonNode? node)
    {
        if (node == null)
            return 0;
        try
        {
            return node.GetValue<double>();
        }
        catch
        {
            return 0;
        }
    }

    static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
