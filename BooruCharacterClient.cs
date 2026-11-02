using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Alife.Plugin.Comfyui;

/// <summary>
/// Danbooru 官方角色库 REST 客户端（AnimaDex 的替代/主源）。
///
/// 为什么用它：AnimaDex 在大陆部分网络无法直连；Danbooru 官方 API 数据最新（角色标签实时），
/// 且一次查询只需要 2 个请求（tags 搜名字 + related_tag 取特征）。
///
/// 端点：
/// - GET /tags.json?search[category]=4&amp;search[name_matches]=xxx*&amp;search[order]=count&amp;only=name,post_count
///   category=4 即角色（character）标签。
/// - GET /related_tag.json?query=&lt;角色 tag&gt;&amp;category=general&amp;limit=80 取该角色高频共现的通用标签。
///
/// 注意：Danbooru 对无 User-Agent 的请求直接 403，必须带 UA；匿名限速约 1~10 req/s，
/// 本类单次查询只打 2 个请求，并做 10 分钟缓存，正常使用不会触发限速。
/// 失败一律 soft-fail（Unavailable），由调用方回退本地提示。
/// </summary>
static class BooruCharacterClient
{
    public const string DefaultBaseUrl = "https://danbooru.donmai.us";

    // Danbooru 要求可识别的 UA；留一个说明用途的字符串即可
    const string UserAgent = "Alife-Comfyui-Plugin/1.0 (character lookup; local desktop app)";

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);

    const int CacheTtlMinutes = 10;
    const int CacheMaxEntries = 32;
    const int MaxResponseBytes = 4 * 1024 * 1024;
    const int MaxCandidates = 12;
    const int MaxTagsPerSection = 24;

    sealed record CacheEntry(BooruResult Result, DateTime ExpiresAt);

    public sealed class BooruResult
    {
        public List<BooruCharacterMatch> Matches { get; } = new();

        /// <summary>可读错误（非空表示本次查询失败）</summary>
        public string? Error { get; set; }

        /// <summary>true=服务不可用/超时/网络异常（soft-fail，不当作「未找到」）</summary>
        public bool Unavailable { get; set; }

        /// <summary>查询时给了作品名，且最佳结果的作品与之一致</summary>
        public bool WorkMatched { get; set; }
    }

    public sealed class BooruCharacterMatch
    {
        /// <summary>Danbooru 角色标签（英文，下划线形式），如 vivian_banshee</summary>
        public string Tag { get; set; } = "";

        /// <summary>可读角色名（去限定符、下划线转空格），如 Vivian Banshee</summary>
        public string Name { get; set; } = "";

        /// <summary>作品标签（英文），如 zenless_zone_zero；无法判断时为空</summary>
        public string Copyright { get; set; } = "";

        /// <summary>触发词：角色名 + 作品名（空格形式）</summary>
        public string Trigger { get; set; } = "";

        public string Appearance { get; set; } = "";
        public string Outfit { get; set; } = "";

        /// <summary>Danbooru 图片数（热度）</summary>
        public long Count { get; set; }
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
            var url = Combine(baseUrl,
                "/tags.json?search%5Bcategory%5D=4&search%5Border%5D=count&limit=1&only=name%2Cpost_count");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
            var body = await ReadBodyLimitedAsync(resp, cts.Token);
            sw.Stop();
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}: {Trim(body, 160)}", sw.ElapsedMilliseconds);

            try
            {
                var arr = JsonNode.Parse(body) as JsonArray;
                var name = arr?.FirstOrDefault()?["name"]?.GetValue<string>() ?? "";
                return (!string.IsNullOrEmpty(name),
                    string.IsNullOrEmpty(name) ? "响应格式异常" : $"ok，示例角色标签：{name}",
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

    public static async Task<BooruResult> SearchCharactersAsync(
        string query, string? workEn, string baseUrl, int timeoutSeconds, int minPosts)
    {
        query = (query ?? "").Trim();
        var result = new BooruResult();
        if (query.Length == 0)
        {
            result.Error = "角色名为空";
            return result;
        }
        if (!IsAscii(query))
        {
            result.Error = "Danbooru 只支持英文/罗马字角色名";
            return result;
        }

        minPosts = Math.Clamp(minPosts, 0, 1_000_000);
        var cacheKey = $"booru|{Norm(query)}|{Norm(workEn ?? "")}|{NormHost(baseUrl)}|{minPosts}";
        if (TryGetCache(cacheKey, out var cached))
            return cached.Result;

        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 60));
        var deadline = DateTime.UtcNow + timeout;

        try
        {
            // 1) 按名字模糊匹配角色标签，热度降序
            var escaped = Uri.EscapeDataString(query.Replace(' ', '_') + "*");
            var searchUrl = Combine(NormalizeBase(baseUrl),
                $"/tags.json?search%5Bcategory%5D=4&search%5Bname_matches%5D={escaped}" +
                "&search%5Border%5D=count&limit=" + MaxCandidates + "&only=name%2Cpost_count%2Cis_deprecated");
            var rows = await GetJsonArrayAsync(searchUrl, deadline);
            if (rows == null)
            {
                result.Unavailable = true;
                result.Error = "查询失败或超时";
                return result;
            }

            var candidates = new List<(string Tag, long Count)>();
            foreach (var row in rows.OfType<JsonObject>())
            {
                if (row["is_deprecated"]?.GetValue<bool>() == true)
                    continue;
                var tag = row["name"]?.GetValue<string>() ?? "";
                if (tag.Length == 0)
                    continue;
                var count = ReadLong(row["post_count"]);
                if (count <= 0 || count < minPosts)
                    continue;
                candidates.Add((tag, count));
            }

            var workQuery = Norm(workEn ?? "");
            if (workQuery.Length > 0)
            {
                var filtered = candidates
                    .Where(c => WorkMatches(c.Tag, workQuery))
                    .ToList();
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

            // 2) 只对最佳候选取特征标签（其余候选只给名字，避免请求过多）
            var best = candidates[0];
            var baseAddr = NormalizeBase(baseUrl);
            var (appearance, outfit) = await FetchTraitsAsync(best.Tag, baseAddr, deadline);

            // 无作品限定符时补一次 copyright：trigger 带作品名能显著减少画错作品
            var bestWork = WorkOf(best.Tag);
            if (bestWork.Length == 0)
                bestWork = await FetchCopyrightAsync(best.Tag, baseAddr, deadline);

            foreach (var item in candidates)
            {
                var work = item.Tag == best.Tag ? bestWork : WorkOf(item.Tag);
                var match = new BooruCharacterMatch
                {
                    Tag = item.Tag,
                    Name = PrettyNameOf(item.Tag),
                    Copyright = work,
                    Trigger = BuildTrigger(item.Tag, work),
                    Count = item.Count
                };
                if (item.Tag == best.Tag)
                {
                    match.Appearance = appearance;
                    match.Outfit = outfit;
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

    /// <summary>取角色高频共现的通用标签，并按「外貌 / 服装」粗略分类（过滤动作、构图、表情、NSFW 与剧情向标签）。</summary>
    static async Task<(string Appearance, string Outfit)> FetchTraitsAsync(
        string tag, string baseUrl, DateTime deadline)
    {
        if (baseUrl.Length == 0)
            baseUrl = DefaultBaseUrl;
        var url = Combine(baseUrl,
            "/related_tag.json?query=" + Uri.EscapeDataString(tag) + "&category=general&limit=80");
        var obj = await GetJsonObjectAsync(url, deadline);
        if (obj?["related_tags"] is not JsonArray related)
            return ("", "");

        var appearance = new List<string>();
        var outfit = new List<string>();
        var hairColors = new List<string>();
        var eyeColors = new List<string>();
        var gender = "";

        foreach (var item in related.OfType<JsonObject>())
        {
            var raw = item["tag"]?["name"]?.GetValue<string>() ?? "";
            if (raw.Length == 0)
                continue;
            if (raw is "1girl" or "1boy" or "female" or "male")
            {
                if (gender.Length == 0 && raw is "1girl" or "1boy")
                    gender = raw;
                continue;
            }

            var display = raw.Replace('_', ' ');
            var kind = ClassifyTraitName(raw);
            if (kind == 1 && appearance.Count < MaxTagsPerSection)
            {
                if (IsColorConflictWith(display, hairColors, "hair")
                    || IsColorConflictWith(display, eyeColors, "eyes"))
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

        var appearanceText = string.Join(", ", appearance);
        if (gender.Length > 0)
            appearanceText = appearanceText.Length > 0 ? gender + ", " + appearanceText : gender;
        return (appearanceText, string.Join(", ", outfit));
    }

    internal static int ClassifyTraitName(string tag)
    {
        if (NsfwTags.Contains(tag) || SceneTags.Contains(tag))
            return 0;

        if (AppearanceKeywords.Any(k => tag.Contains(k, StringComparison.Ordinal)))
            return 1;
        if (OutfitKeywords.Any(k => tag.Contains(k, StringComparison.Ordinal)))
            return 2;
        return 0;
    }

    static readonly string[] HairColorWords =
    {
        "black", "blonde", "blue", "brown", "green", "grey", "gray", "orange", "pink", "purple",
        "red", "silver", "white", "aqua", "magenta", "platinum", "lavender", "turquoise", "gold"
    };

    static readonly string[] MultiColorWords = { "multicolored", "two-tone", "gradient", "streaked", "colored" };

    /// <summary>同一角色只保留一个主发色/瞳色，避免 related_tag 里不同同人变体的颜色互相打架。</summary>
    internal static bool IsColorConflictWith(string display, List<string> seen, string kindWord)
    {
        var parts = display.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!parts.Contains(kindWord))
            return false;
        if (MultiColorWords.Any(m => display.Contains(m, StringComparison.Ordinal)))
            return false;

        var color = parts.FirstOrDefault(p => HairColorWords.Contains(p, StringComparer.Ordinal));
        if (color == null)
            return false;
        if (seen.Count > 0)
            return true;
        seen.Add(color);
        return false;
    }

    /// <summary>标签本身没有作品限定符时，从最新帖子补 copyright（如 vivian_banshee → zenless_zone_zero）。</summary>
    static async Task<string> FetchCopyrightAsync(string tag, string baseUrl, DateTime deadline)
    {
        if (baseUrl.Length == 0)
            baseUrl = DefaultBaseUrl;
        var url = Combine(baseUrl,
            "/posts.json?tags=" + Uri.EscapeDataString(tag) + "&limit=1&only=tag_string_copyright");
        var arr = await GetJsonArrayAsync(url, deadline);
        var row = arr?.OfType<JsonObject>().FirstOrDefault();
        var raw = row?["tag_string_copyright"]?.GetValue<string>() ?? "";
        if (raw.Length == 0)
            return "";
        var first = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim('_') ?? "";
        return first is "original" or "original_(series)" or "artist_request" ? "" : first;
    }

    static readonly HashSet<string> NsfwTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "nude", "naked", "nipples", "penis", "pussy", "sex", "cum", "breasts_out", "topless",
        "bottomless", "underwear", "panties", "bra", "ass", "spread_legs", "ahegao", "cum_in_pussy"
    };

    static readonly HashSet<string> SceneTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "solo", "1girl", "1boy", "2girls", "multiple_girls", "looking_at_viewer", "simple_background",
        "white_background", "black_background", "grey_background", "upper_body", "lower_body", "full_body",
        "cowboy_shot", "portrait", "from_side", "from_behind", "from_above", "from_below", "standing",
        "sitting", "lying", "walking", "running", "smile", "open_mouth", "closed_mouth", "blush",
        "official_art", "official_alternate_costume", "alternate_costume",
        "closed_eyes", "one_eye_closed", "half-closed_eyes", "open_eyes", "teeth", "grin",
        "smirk", "frown", "pout", "tongue", "wink", "expressionless", "angry", "sad", "happy",
        "surprised", "serious", "smug", "light_smile", "parted_lips"
    };

    static readonly string[] AppearanceKeywords =
    {
        "hair", "eyes", "eye_", "_eyes", "pupil", "eyelash", "eyebrow", "skin", "horn", "wing", "tail",
        "ear", "ears", "fang", "freckle", "mole", "tattoo", "scar", "halo", "ahoge", "braid", "twintail",
        "ponytail", "sidelock", "bangs", "abs", "muscular", "heterochromia", "beard", "mustache",
        "pointy", "sharp_teeth", "forehead", "makeup", "lipstick", "nail", "claw"
    };

    static readonly string[] OutfitKeywords =
    {
        "shirt", "skirt", "dress", "uniform", "jacket", "coat", "pants", "shorts", "sock", "thighhigh",
        "boot", "shoe", "sandal", "glove", "hat", "ribbon", "bow", "choker", "necklace", "earring",
        "armor", "swimsuit", "bikini", "lingerie", "pantyhose", "leotard", "apron", "vest", "sweater",
        "hoodie", "cape", "cloak", "veil", "crown", "tiara", "headband", "headwear", "eyewear",
        "glasses", "mask", "belt", "buckle", "pendant", "jewelry", "bracelet", "watch", "backpack",
        "bag", "scarf", "necktie", "kimono", "yukata", "hanfu", "maid", "suit", "sarong", "kimono",
        "footwear", "hairband", "hairpin", "hairclip", "hair_ornament", "garter", "collared", "collar",
        "sleeve", "strap", "zipper", "button", "pocket", "hood", "feather", "fur", "lace", "frill"
    };

    static string BuildTrigger(string tag, string work)
    {
        var name = PrettyNameOf(tag);
        return work.Length > 0 ? $"{name}, {work.Replace('_', ' ')}" : name;
    }

    /// <summary>取 Danbooru 标签里最后一个括号作为作品限定符：cyrene_(demiurge)_(honkai:_star_rail) → honkai:_star_rail</summary>
    internal static string WorkOf(string tag)
    {
        var end = tag.LastIndexOf(')');
        if (end != tag.Length - 1 || end <= 0)
            return "";
        var start = tag.LastIndexOf('(', end);
        if (start <= 0)
            return "";
        return tag.Substring(start + 1, end - start - 1).Trim();
    }

    internal static string PrettyNameOf(string tag)
    {
        var work = WorkOf(tag);
        var name = work.Length > 0 ? tag[..tag.LastIndexOf('(')].TrimEnd('_') : tag;
        return name.Replace('_', ' ');
    }

    static bool WorkMatches(string tag, string normalizedWork)
    {
        var work = Norm(WorkOf(tag));
        if (work.Length == 0)
            return false;
        return work == normalizedWork || work.Contains(normalizedWork) || normalizedWork.Contains(work);
    }

    static async Task<JsonArray?> GetJsonArrayAsync(string url, DateTime deadline)
    {
        var body = await GetStringAsync(url, deadline);
        if (body == null)
            return null;
        try
        {
            return JsonNode.Parse(body) as JsonArray;
        }
        catch
        {
            return null;
        }
    }

    static async Task<JsonObject?> GetJsonObjectAsync(string url, DateTime deadline)
    {
        var body = await GetStringAsync(url, deadline);
        if (body == null)
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

    static async Task<string?> GetStringAsync(string url, DateTime deadline)
    {
        var remain = deadline - DateTime.UtcNow;
        if (remain < TimeSpan.FromSeconds(2))
            return null;

        using var cts = new CancellationTokenSource(remain);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
            return null;
        return await ReadBodyLimitedAsync(resp, cts.Token);
    }

    static bool TryGetCache(string key, out CacheEntry entry)
    {
        if (Cache.TryGetValue(key, out entry!) && entry.ExpiresAt > DateTime.UtcNow)
            return true;
        Cache.TryRemove(key, out _);
        entry = null!;
        return false;
    }

    static void SetCache(string key, BooruResult result)
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
            if (ms.Length + read > MaxResponseBytes)
                break;
            ms.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static string NormalizeBase(string baseUrl)
        => (baseUrl ?? "").Trim().TrimEnd('/');

    static string Combine(string baseUrl, string path)
        => baseUrl.Length == 0 ? path : baseUrl + path;

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

    static bool IsAscii(string value)
    {
        foreach (var ch in value)
            if (ch >= 128)
                return false;
        return true;
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

    static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
