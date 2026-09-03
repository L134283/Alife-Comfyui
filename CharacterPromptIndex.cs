using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alife.Plugin.Comfyui;

sealed class CharacterPromptIndex
{
    readonly List<IndexedEntry> _entries;

    CharacterPromptIndex(List<CharacterPromptEntry> entries)
    {
        _entries = entries.Select(entry => new IndexedEntry(entry)).ToList();
    }

    public int Count => _entries.Count;

    /// <summary>全部条目（仅用于启动时构建 AnimaDex 中→英查询映射，避免重复解析大 JSON）。</summary>
    public IReadOnlyList<CharacterPromptEntry> Entries => _entries.Select(entry => entry.Entry).ToArray();

    public static CharacterPromptIndex Load(string path)
    {
        using var stream = File.OpenRead(path);
        var catalog = JsonSerializer.Deserialize<CharacterPromptCatalog>(stream)
            ?? throw new InvalidDataException("角色提示词索引 JSON 为空");
        if (catalog.Items.Count == 0)
            throw new InvalidDataException("角色提示词索引中没有角色数据");
        return new CharacterPromptIndex(catalog.Items);
    }

    public IReadOnlyList<CharacterPromptMatch> Search(string name, string? work, int limit = 3)
    {
        var query = Normalize(name);
        if (string.IsNullOrEmpty(query))
            return Array.Empty<CharacterPromptMatch>();

        var workQuery = Normalize(work);
        var minimumScore = query.Length switch
        {
            <= 2 => 0.88,
            3 => 0.62,
            _ => 0.58,
        };

        var matches = new List<CharacterPromptMatch>();
        foreach (var indexed in _entries)
        {
            var canonicalScore = BestSimilarity(query, indexed.PrimaryNames);
            var aliasScore = BestSimilarity(query, indexed.Aliases) * 0.98;
            var nameScore = Math.Max(canonicalScore, aliasScore);
            if (indexed.IsQualifiedVariant && !indexed.FullNames.Contains(query))
                nameScore *= 0.95;
            if (nameScore < minimumScore)
                continue;

            var workScore = string.IsNullOrEmpty(workQuery)
                ? 0.0
                : BestSimilarity(workQuery, indexed.WorkNames);
            if (!string.IsNullOrEmpty(workQuery) && workScore < 0.35)
                continue;

            var score = string.IsNullOrEmpty(workQuery)
                ? nameScore + ImplicitWorkBoost(query, indexed.WorkNames)
                : nameScore * 0.78 + workScore * 0.22;

            matches.Add(new CharacterPromptMatch(
                indexed.Entry,
                Math.Min(score, 1.0),
                nameScore,
                workScore));
        }

        return matches
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Entry.Popularity)
            .ThenBy(match => match.Entry.EnglishName, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 5))
            .ToList();
    }

    static double ImplicitWorkBoost(string query, IReadOnlyList<string> workNames)
    {
        foreach (var workName in workNames)
        {
            if (workName.Length >= 2 && query.Contains(workName, StringComparison.Ordinal))
                return 0.06;
        }
        return 0.0;
    }

    static double BestSimilarity(string query, IReadOnlyList<string> candidates)
    {
        var best = 0.0;
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
                continue;
            if (query == candidate)
                return 1.0;

            double score;
            if (candidate.Contains(query, StringComparison.Ordinal))
            {
                var coverage = (double)query.Length / candidate.Length;
                score = 0.86 + 0.12 * coverage;
            }
            else if (candidate.Length >= 3
                && query.Contains(candidate, StringComparison.Ordinal)
                && (double)candidate.Length / query.Length >= 0.50)
            {
                var coverage = (double)candidate.Length / query.Length;
                score = 0.80 + 0.12 * coverage;
            }
            else
            {
                score = Math.Max(DiceSimilarity(query, candidate), EditSimilarity(query, candidate));
            }

            if (score > best)
                best = score;
        }
        return best;
    }

    static double DiceSimilarity(string left, string right)
    {
        if (left.Length < 2 || right.Length < 2)
            return 0.0;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < left.Length - 1; i++)
        {
            var gram = left.Substring(i, 2);
            counts[gram] = counts.TryGetValue(gram, out var count) ? count + 1 : 1;
        }

        var intersection = 0;
        for (var i = 0; i < right.Length - 1; i++)
        {
            var gram = right.Substring(i, 2);
            if (!counts.TryGetValue(gram, out var count) || count <= 0)
                continue;
            intersection++;
            counts[gram] = count - 1;
        }

        return 2.0 * intersection / (left.Length + right.Length - 2);
    }

    static double EditSimilarity(string left, string right)
    {
        var maxLength = Math.Max(left.Length, right.Length);
        if (maxLength == 0)
            return 1.0;
        return 1.0 - (double)DamerauLevenshtein(left, right) / maxLength;
    }

    static int DamerauLevenshtein(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        var beforePrevious = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
            previous[column] = column;

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);

                if (row > 1 && column > 1
                    && left[row - 1] == right[column - 2]
                    && left[row - 2] == right[column - 1])
                {
                    current[column] = Math.Min(current[column], beforePrevious[column - 2] + 1);
                }
            }

            (beforePrevious, previous, current) = (previous, current, beforePrevious);
        }

        return previous[right.Length];
    }

    static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(character);
        }
        return builder.ToString();
    }

    sealed class IndexedEntry
    {
        public IndexedEntry(CharacterPromptEntry entry)
        {
            Entry = entry;
            PrimaryNames = NormalizeAll(new[]
            {
                entry.ChineseName,
                WithoutQualifier(entry.ChineseName),
                entry.EnglishName,
                entry.EnglishName.Replace('_', ' '),
                WithoutQualifier(entry.EnglishName).Replace('_', ' '),
            });
            FullNames = NormalizeAll(new[]
            {
                entry.ChineseName,
                entry.EnglishName,
                entry.EnglishName.Replace('_', ' '),
            });
            IsQualifiedVariant = WithoutQualifier(entry.ChineseName) != entry.ChineseName
                || WithoutQualifier(entry.EnglishName) != entry.EnglishName;
            Aliases = NormalizeAll(entry.Aliases);
            WorkNames = NormalizeAll(new[]
            {
                entry.ChineseWork,
                entry.EnglishWork,
                entry.EnglishWork.Replace('_', ' '),
            }.Concat(entry.WorkAliases));
        }

        public CharacterPromptEntry Entry { get; }
        public List<string> PrimaryNames { get; }
        public List<string> FullNames { get; }
        public bool IsQualifiedVariant { get; }
        public List<string> Aliases { get; }
        public List<string> WorkNames { get; }

        static List<string> NormalizeAll(IEnumerable<string> values)
            => values.Select(Normalize)
                .Where(value => !string.IsNullOrEmpty(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();

        static string WithoutQualifier(string value)
        {
            var ascii = value.IndexOf('(');
            var fullWidth = value.IndexOf('（');
            var index = ascii < 0 ? fullWidth
                : fullWidth < 0 ? ascii
                : Math.Min(ascii, fullWidth);
            return index <= 0 ? value : value[..index].TrimEnd('_', ' ');
        }
    }
}

sealed class CharacterPromptCatalog
{
    [JsonPropertyName("items")]
    public List<CharacterPromptEntry> Items { get; set; } = new();
}

sealed class CharacterPromptEntry
{
    [JsonPropertyName("n")]
    public string ChineseName { get; set; } = "";

    [JsonPropertyName("e")]
    public string EnglishName { get; set; } = "";

    [JsonPropertyName("w")]
    public string ChineseWork { get; set; } = "";

    [JsonPropertyName("we")]
    public string EnglishWork { get; set; } = "";

    [JsonPropertyName("t")]
    public string Trigger { get; set; } = "";

    [JsonPropertyName("x")]
    public string Appearance { get; set; } = "";

    [JsonPropertyName("o")]
    public string Outfit { get; set; } = "";

    [JsonPropertyName("a")]
    public List<string> Aliases { get; set; } = new();

    [JsonPropertyName("wa")]
    public List<string> WorkAliases { get; set; } = new();

    [JsonPropertyName("r")]
    public int Popularity { get; set; }

    public string TriggerTags => JoinDistinctTags(new[] { Trigger });

    public string AppearanceTags => JoinDistinctTags(
        new[] { Appearance }, SplitTags(Trigger));

    public string DefaultOutfitTags => JoinDistinctTags(
        new[] { Outfit }, SplitTags(Trigger).Concat(SplitTags(Appearance)));

    public string PromptTags => JoinDistinctTags(new[] { Trigger, Appearance, Outfit });

    static string JoinDistinctTags(
        IEnumerable<string> sources,
        IEnumerable<string>? excluded = null)
    {
        var seen = new HashSet<string>(
            excluded ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var tag in sources.SelectMany(SplitTags))
        {
            if (seen.Add(tag))
                result.Add(tag);
        }
        return string.Join(", ", result);
    }

    static IEnumerable<string> SplitTags(string source)
        => (source ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => !string.IsNullOrWhiteSpace(tag));
}

sealed record CharacterPromptMatch(
    CharacterPromptEntry Entry,
    double Score,
    double NameScore,
    double WorkScore);
