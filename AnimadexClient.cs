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
/// AnimaDex 在线角色库 REST 客户端：本地角色索引未命中时的在线扩充检索。
/// 返回角色 trigger/特征 tags/热度（基于 Danbooru 标签聚合）。
/// 上游：https://github.com/2786886095/animadex-mcp-server（MIT），数据来自 animadex.net。
/// 注意：搜索接口只接受英文/罗马字；中文需插件侧先转成英文名（见 ComfyuiService 的映射）。
/// 与 DanbooruSearchClient 解耦；失败一律 soft-fail，不阻塞生图。
/// </summary>
static class AnimadexClient
{
    public const string DefaultBaseUrl = "https://animadex.net";

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);

    const int CacheTtlMinutes = 10;
    const int CacheMaxEntries = 24;
    const int MaxResults = 12;
    const int MaxResponseBytes = 4 * 1024 * 1024;

    sealed record CacheEntry(AnimadexResult Result, DateTime ExpiresAt);

    public sealed class AnimadexResult
    {
        public List<AnimadexCharacterMatch> Matches { get; } = new();

        /// <summary>可读错误（非空时表示本次查询失败）</summary>
        public string? Error { get; set; }

        /// <summary>true=服务不可用/超时/网络异常（soft-fail，不当作「未找到」）</summary>
        public bool Unavailable { get; set; }

        /// <summary>查询时给定了作品名（workEn）且至少一条结果命中该作品</summary>
        public bool WorkMatched { get; set; }
    }

    public sealed class AnimadexCharacterMatch
    {
        public string Slug { get; set; } = "";
        public string Name { get; set; } = "";
        public string CopyrightName { get; set; } = "";
        public string Trigger { get; set; } = "";
        public List<string> Tags { get; set; } = new();
        public long Count { get; set; }
    }

    public static async Task<AnimadexResult> SearchCharactersAsync(
        string query, string? workEn, string baseUrl, int timeoutSeconds)
    {
        query = (query ?? "").Trim();
        var result = new AnimadexResult();
        if (query.Length == 0)
        {
            result.Error = "角色名为空";
            return result;
        }

        var cacheKey = $"ch|{Norm(query)}|{Norm(workEn ?? "")}|{NormHost(baseUrl)}";
        if (TryGetCache(cacheKey, out var cached))
            return cached.Result;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 60)));
            var url = Combine(baseUrl, "/api/characters/search")
                + "?q=" + Uri.EscapeDataString(query) + "&page=1&sort=count";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            var body = await ReadBodyLimitedAsync(resp, cts.Token);

            if ((int)resp.StatusCode >= 500)
            {
                result.Unavailable = true;
                result.Error = $"HTTP {(int)resp.StatusCode}: {Trim(body, 120)}";
                return result;
            }
            if (string.IsNullOrWhiteSpace(body))
            {
                result.Unavailable = true;
                result.Error = "空响应";
                return result;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(body);
            }
            catch
            {
                result.Unavailable = true;
                result.Error = "响应非 JSON";
                return result;
            }

            if (node is not JsonObject obj)
            {
                result.Unavailable = true;
                result.Error = "响应格式异常";
                return result;
            }

            var workN = Norm(workEn ?? "");
            if (obj["results"] is JsonArray results)
            {
                foreach (var item in results.OfType<JsonObject>())
                {
                    var match = new AnimadexCharacterMatch
                    {
                        Slug = item["slug"]?.GetValue<string>() ?? "",
                        Name = item["name"]?.GetValue<string>() ?? "",
                        CopyrightName = item["copyright_name"]?.GetValue<string>()
                            ?? item["copyright"]?.GetValue<string>() ?? "",
                        Trigger = item["trigger"]?.GetValue<string>() ?? "",
                        Count = TryGetLong(item["count"]),
                    };
                    if (item["tags"] is JsonArray tagArr)
                    {
                        foreach (var t in tagArr)
                        {
                            var s = t?.GetValue<string>() ?? "";
                            if (s.Length > 0)
                                match.Tags.Add(s);
                        }
                    }
                    result.Matches.Add(match);
                }

                // workEn 命中优先（忽略大小写/下划线/空格）；未命中时保留热度排序由调用方决定
                if (workN.Length > 0 && result.Matches.Count > 0)
                {
                    var hits = result.Matches
                        .Where(m => Norm(m.Name).Contains(workN)
                                    || Norm(m.CopyrightName).Contains(workN)
                                    || Norm(m.Trigger).Contains(workN))
                        .ToList();
                    result.WorkMatched = hits.Count > 0;
                    if (result.WorkMatched && result.Matches.Count > 1)
                    {
                        var rest = result.Matches.Where(m => !hits.Contains(m)).ToList();
                        result.Matches.Clear();
                        result.Matches.AddRange(hits.Concat(rest));
                    }
                }

                if (result.Matches.Count > MaxResults)
                    result.Matches.RemoveRange(MaxResults, result.Matches.Count - MaxResults);
            }
            else if (obj["detail"] != null)
            {
                result.Error = Trim(obj["detail"]!.ToString(), 200);
            }

            PutCache(cacheKey, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Unavailable = true;
            result.Error = "查询超时";
            return result;
        }
        catch (Exception ex)
        {
            result.Unavailable = true;
            result.Error = ex.Message;
            return result;
        }
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 60)));
            var url = Combine(baseUrl, "/api/characters/search") + "?q=hatsune%20miku&page=1&sort=count";
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token);
            var body = await ReadBodyLimitedAsync(resp, cts.Token);
            sw.Stop();
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}: {Trim(body, 120)}", sw.ElapsedMilliseconds);
            var ok = body.Contains("\"results\"", StringComparison.Ordinal);
            return (ok, ok ? "ok" : "响应异常", sw.ElapsedMilliseconds);
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

    // ---------- helpers ----------

    static long TryGetLong(JsonNode? node)
    {
        if (node == null) return 0;
        try
        {
            return node.GetValue<long>();
        }
        catch
        {
            return long.TryParse(node.ToString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
        }
    }

    static async Task<string> ReadBodyLimitedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[Math.Min(MaxResponseBytes + 1, 128 * 1024)];
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

    static bool TryGetCache(string key, out CacheEntry entry)
    {
        entry = null!;
        if (!Cache.TryGetValue(key, out var e)) return false;
        if (DateTime.UtcNow > e.ExpiresAt)
        {
            Cache.TryRemove(key, out _);
            return false;
        }
        entry = e;
        return true;
    }

    static void PutCache(string key, AnimadexResult result)
    {
        if (Cache.Count >= CacheMaxEntries)
        {
            foreach (var old in Cache.OrderBy(kv => kv.Value.ExpiresAt).Take(8).Select(kv => kv.Key).ToList())
                Cache.TryRemove(old, out _);
        }
        Cache[key] = new CacheEntry(result, DateTime.UtcNow.AddMinutes(CacheTtlMinutes));
    }

    static string Norm(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch))
                sb.Append(ch);
        return sb.ToString();
    }

    static string NormalizeBase(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        return url.Trim().TrimEnd('/');
    }

    static string Combine(string baseUrl, string path)
        => NormalizeBase(baseUrl) + (path.StartsWith('/') ? path : "/" + path);

    static string ShortHost(string baseUrl)
    {
        try
        {
            return new Uri(NormalizeBase(baseUrl)).Host;
        }
        catch
        {
            return Trim(baseUrl, 40);
        }
    }

    static string NormHost(string? url) => ShortHost(NormalizeBase(url ?? ""));

    static string Trim(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
