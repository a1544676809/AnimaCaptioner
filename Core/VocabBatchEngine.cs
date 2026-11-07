using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AnimaCaptioner.Core;

/// <summary>
/// High-efficiency batch vocabulary resolution and text scanning engine ported and enhanced
/// from <c>AnimaAIProxy</c> (<c>vocab_search</c>, <c>danbooru_resolve_tags</c>, <c>danbooru_scan_texts</c>).
/// Eliminates multi-turn single-tag lookup latency during AI image captioning.
/// </summary>
public static partial class VocabBatchEngine
{
    public sealed record ResolvedTagItem(
        string Input,
        bool Matched,
        string CanonicalTag,
        string Cn,
        string Category,
        int PostCount,
        bool InAnima,
        bool Deprecated,
        IReadOnlyList<string> Suggestions);

    public sealed record ResolveBatchResult(
        IReadOnlyList<ResolvedTagItem> Items,
        IReadOnlyList<string> CanonicalKnownTags,
        IReadOnlyList<string> UnresolvedInputs)
    {
        public string ToJsonSummary()
        {
            var root = new JsonObject
            {
                ["resolved_count"] = CanonicalKnownTags.Count,
                ["unresolved_count"] = UnresolvedInputs.Count,
                ["known_tags"] = new JsonArray(CanonicalKnownTags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
                ["unresolved"] = new JsonArray(UnresolvedInputs.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray())
            };

            var details = new JsonArray();
            foreach (var item in Items)
            {
                var obj = new JsonObject
                {
                    ["input"] = item.Input,
                    ["matched"] = item.Matched,
                    ["canonical"] = item.CanonicalTag,
                    ["cn"] = item.Cn,
                    ["category"] = item.Category,
                    ["posts"] = item.PostCount,
                    ["in_anima"] = item.InAnima
                };
                if (!item.Matched && item.Suggestions.Count > 0)
                {
                    obj["suggestions"] = new JsonArray(
                        item.Suggestions.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
                }
                details.Add(obj);
            }

            root["items"] = details;
            return root.ToJsonString();
        }
    }

    public sealed record ScannedTagHit(
        string Tag,
        string Cn,
        string Category,
        int PostCount,
        bool InAnima,
        string MatchedPhrase);

    public sealed record ScanTextResult(
        IReadOnlyList<ScannedTagHit> MatchedTags,
        int CandidateCount)
    {
        public string ToJsonSummary()
        {
            var root = new JsonObject
            {
                ["matched_count"] = MatchedTags.Count,
                ["candidate_phrases_checked"] = CandidateCount,
                ["tags"] = new JsonArray(MatchedTags.Select(h => (JsonNode)new JsonObject
                {
                    ["tag"] = h.Tag,
                    ["cn"] = h.Cn,
                    ["category"] = h.Category,
                    ["posts"] = h.PostCount,
                    ["in_anima"] = h.InAnima,
                    ["phrase"] = h.MatchedPhrase
                }).ToArray())
            };
            return root.ToJsonString();
        }
    }

    // Common synonym/alias normalization dictionary for high-frequency vision LLM outputs
    private static readonly Dictionary<string, string> BuiltInAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1 girl"] = "1girl",
        ["single girl"] = "1girl",
        ["one girl"] = "1girl",
        ["1 boy"] = "1boy",
        ["single boy"] = "1boy",
        ["one boy"] = "1boy",
        ["2 girls"] = "2girls",
        ["two girls"] = "2girls",
        ["looking at camera"] = "looking at viewer",
        ["staring at viewer"] = "looking at viewer",
        ["eye contact"] = "looking at viewer",
        ["anime style"] = "anime coloring",
        ["blonde"] = "blonde hair",
        ["blond hair"] = "blonde hair",
        ["silver-haired"] = "silver hair",
        ["white-haired"] = "white hair",
        ["black-haired"] = "black hair",
        ["blue-eyed"] = "blue eyes",
        ["red-eyed"] = "red eyes",
        ["green-eyed"] = "green eyes",
        ["golden eyes"] = "yellow eyes",
        ["schoolgirl uniform"] = "school uniform",
        ["sailor uniform"] = "serafuku",
        ["japanese school uniform"] = "serafuku",
        ["thighhighs"] = "thighhighs",
        ["thigh highs"] = "thighhighs",
        ["thigh-high socks"] = "thighhighs",
        ["knee high socks"] = "kneehighs",
        ["cat ears"] = "cat ears",
        ["nekomimi"] = "cat ears",
        ["fox ears"] = "fox ears",
        ["bunny ears"] = "rabbit ears",
        ["upper body shot"] = "upper body",
        ["cowboy_shot"] = "cowboy shot",
        ["medium shot"] = "upper body",
        ["close up"] = "close-up",
        ["closeup"] = "close-up",
        ["portrait shot"] = "portrait",
        ["depth of field"] = "depth of field",
        ["bokeh"] = "depth of field",
        ["blurred background"] = "blurry background"
    };

    /// <summary>
    /// Resolves a batch of candidate tag strings (English tags, aliases, or Chinese keywords)
    /// against <see cref="VocabDb"/> in a single pass.
    /// </summary>
    public static ResolveBatchResult ResolveBatch(
        VocabDb? vocab,
        IEnumerable<string> rawTags,
        int maxSuggestionsPerMiss = 3)
    {
        var items = new List<ResolvedTagItem>();
        var known = new List<string>();
        var knownSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();

        foreach (var raw in rawTags)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var cleaned = NormalizeCandidate(raw);
            if (cleaned.Length == 0)
                continue;

            if (vocab is null)
            {
                items.Add(new ResolvedTagItem(raw, false, cleaned, string.Empty, "general", 0, false, false, Array.Empty<string>()));
                unresolved.Add(raw);
                continue;
            }

            // 1. Direct exact lookup
            var exact = vocab.FindExact(cleaned);

            // 2. Underscore <-> Space canonicalization
            if (exact is null && cleaned.Contains('_'))
                exact = vocab.FindExact(cleaned.Replace('_', ' '));
            if (exact is null && cleaned.Contains(' '))
                exact = vocab.FindExact(cleaned.Replace(' ', '_'));

            // 3. Built-in alias table
            if (exact is null && BuiltInAliases.TryGetValue(cleaned, out var mappedAlias))
                exact = vocab.FindExact(mappedAlias);

            if (exact is not null && !exact.Deprecated)
            {
                items.Add(new ResolvedTagItem(
                    Input: raw,
                    Matched: true,
                    CanonicalTag: exact.Tag,
                    Cn: exact.Cn,
                    Category: exact.Category,
                    PostCount: exact.PostCount,
                    InAnima: exact.InAnima,
                    Deprecated: exact.Deprecated,
                    Suggestions: Array.Empty<string>()));

                if (knownSeen.Add(exact.Tag))
                    known.Add(exact.Tag);
                continue;
            }

            // 4. Fallback search for Chinese gloss match or close prefix/fuzzy match
            var searchHits = TagSearch.Query(vocab, cleaned, limit: Math.Max(8, maxSuggestionsPerMiss * 2));
            var exactCnMatch = searchHits.FirstOrDefault(h =>
                !h.Deprecated &&
                (string.Equals(h.Cn, cleaned, StringComparison.OrdinalIgnoreCase) ||
                 h.Cn.Split(new[] { '/', '、', ',', '，', ';' }, StringSplitOptions.RemoveEmptyEntries)
                     .Any(part => string.Equals(part.Trim(), cleaned, StringComparison.OrdinalIgnoreCase))));

            if (exactCnMatch is not null)
            {
                items.Add(new ResolvedTagItem(
                    Input: raw,
                    Matched: true,
                    CanonicalTag: exactCnMatch.Tag,
                    Cn: exactCnMatch.Cn,
                    Category: exactCnMatch.Category,
                    PostCount: exactCnMatch.PostCount,
                    InAnima: exactCnMatch.InAnima,
                    Deprecated: exactCnMatch.Deprecated,
                    Suggestions: Array.Empty<string>()));

                if (knownSeen.Add(exactCnMatch.Tag))
                    known.Add(exactCnMatch.Tag);
                continue;
            }

            var suggestions = searchHits
                .Where(h => !h.Deprecated)
                .Take(maxSuggestionsPerMiss)
                .Select(h => string.IsNullOrEmpty(h.Cn) ? h.Tag : $"{h.Tag} ({h.Cn})")
                .ToList();

            items.Add(new ResolvedTagItem(
                Input: raw,
                Matched: false,
                CanonicalTag: string.Empty,
                Cn: string.Empty,
                Category: string.Empty,
                PostCount: 0,
                InAnima: false,
                Deprecated: exact?.Deprecated ?? false,
                Suggestions: suggestions));

            unresolved.Add(raw);
        }

        return new ResolveBatchResult(items, known, unresolved);
    }

    /// <summary>
    /// Scans a free-form natural language description or draft caption using n-gram sliding windows
    /// (1 to 4 words) and clause tokens to extract canonical Danbooru/Anima vocabulary tags automatically.
    /// </summary>
    public static ScanTextResult ScanText(
        VocabDb? vocab,
        string? text,
        int maxResults = 40)
    {
        if (vocab is null || string.IsNullOrWhiteSpace(text))
            return new ScanTextResult(Array.Empty<ScannedTagHit>(), 0);

        var matched = new List<ScannedTagHit>();
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int candidatesChecked = 0;

        // 1. First check comma/semicolon-separated clauses directly
        var clauses = text.Split(new[] { ',', '，', ';', '；', '.', '。', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawClause in clauses)
        {
            var phrase = NormalizeCandidate(rawClause);
            if (phrase.Length is < 2 or > 48)
                continue;

            candidatesChecked++;
            TryMatchPhrase(vocab, phrase, rawClause.Trim(), seenTags, matched);
            if (matched.Count >= maxResults)
                return new ScanTextResult(matched, candidatesChecked);
        }

        // 2. Sliding n-gram window (4-gram down to 1-gram) over English words
        var wordMatches = WordTokenRegex().Matches(text);
        var words = wordMatches.Select(m => m.Value.ToLowerInvariant()).ToList();

        // Common English stop words to skip for 1-gram lookups
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "of", "in", "on", "at", "to", "for", "with", "by",
            "from", "up", "about", "into", "over", "after", "is", "are", "was", "were", "be",
            "been", "being", "have", "has", "had", "do", "does", "did", "but", "if", "then",
            "else", "when", "where", "why", "how", "all", "any", "both", "each", "few", "more",
            "most", "other", "some", "such", "no", "nor", "not", "only", "own", "same", "so",
            "than", "too", "very", "can", "will", "just", "her", "his", "their", "its", "she",
            "he", "they", "it", "this", "that", "these", "those", "wearing", "holding", "standing",
            "sitting", "looking", "featured", "shown", "image", "picture", "scene", "view"
        };

        for (int n = 4; n >= 1; n--)
        {
            for (int i = 0; i <= words.Count - n; i++)
            {
                if (n == 1 && (words[i].Length < 3 || stopWords.Contains(words[i])))
                    continue;

                var phrase = string.Join(" ", words.Skip(i).Take(n));
                candidatesChecked++;
                TryMatchPhrase(vocab, phrase, phrase, seenTags, matched);

                if (matched.Count >= maxResults)
                    return new ScanTextResult(matched, candidatesChecked);
            }
        }

        // Order matched tags by post count descending so primary tags surface first
        var ordered = matched
            .OrderByDescending(m => m.InAnima)
            .ThenByDescending(m => m.PostCount)
            .Take(maxResults)
            .ToList();

        return new ScanTextResult(ordered, candidatesChecked);
    }

    /// <summary>
    /// Executes the <c>danbooru_resolve_tags</c> agent tool call from JSON arguments.
    /// </summary>
    public static string ExecuteVocabResolveTool(VocabDb? vocab, string jsonArguments)
    {
        try
        {
            var node = JsonNode.Parse(jsonArguments) as JsonObject;
            var tagsArray = node?["tags"] as JsonArray;
            if (tagsArray is null || tagsArray.Count == 0)
                return """{"error": "Missing or empty 'tags' array parameter."}""";

            var inputs = tagsArray
                .Select(t => t?.GetValue<string>() ?? string.Empty)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Take(60)
                .ToList();

            var result = ResolveBatch(vocab, inputs);
            return result.ToJsonSummary();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"danbooru_resolve_tags failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Executes the <c>danbooru_scan_texts</c> agent tool call from JSON arguments.
    /// </summary>
    public static string ExecuteVocabScanTool(VocabDb? vocab, string jsonArguments)
    {
        try
        {
            var node = JsonNode.Parse(jsonArguments) as JsonObject;
            var text = node?["text"]?.GetValue<string>() ?? string.Empty;
            int limit = node?["limit"]?.GetValue<int>() ?? 40;
            limit = Math.Clamp(limit, 1, 100);

            if (string.IsNullOrWhiteSpace(text))
                return """{"error": "Missing or empty 'text' parameter."}""";

            var result = ScanText(vocab, text, limit);
            return result.ToJsonSummary();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"danbooru_scan_texts failed: {ex.Message}" });
        }
    }

    private static void TryMatchPhrase(
        VocabDb vocab,
        string normalizedPhrase,
        string originalPhrase,
        HashSet<string> seenTags,
        List<ScannedTagHit> output)
    {
        var hit = vocab.FindExact(normalizedPhrase);
        if (hit is null && BuiltInAliases.TryGetValue(normalizedPhrase, out var aliasTarget))
            hit = vocab.FindExact(aliasTarget);

        if (hit is not null && !hit.Deprecated && seenTags.Add(hit.Tag))
        {
            output.Add(new ScannedTagHit(
                Tag: hit.Tag,
                Cn: hit.Cn,
                Category: hit.Category,
                PostCount: hit.PostCount,
                InAnima: hit.InAnima,
                MatchedPhrase: originalPhrase));
        }
    }

    private static string NormalizeCandidate(string raw)
    {
        var s = raw.Trim().Trim(',', '.', ';', ':', '"', '\'', '`');
        s = s.Replace(@"\(", "(").Replace(@"\)", ")");
        while (s.Contains("  ", StringComparison.Ordinal))
            s = s.Replace("  ", " ", StringComparison.Ordinal);
        return s.Trim();
    }

    [GeneratedRegex(@"[a-zA-Z0-9_\-\(\)]+")]
    private static partial Regex WordTokenRegex();
}
