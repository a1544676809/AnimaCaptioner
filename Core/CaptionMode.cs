using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AnimaCaptioner.Core;

/// <summary>
/// Defines the structural paradigm for reading, editing, and generating image captions.
/// </summary>
public enum CaptionMode
{
    /// <summary>
    /// Pure Booru/Danbooru tag list mode (e.g., Illustrious, NoobAI, Pony, SDXL, Kohya).
    /// All comma-delimited tokens are preserved as tags; no natural language prose tail is split out.
    /// </summary>
    TagsOnly = 0,

    /// <summary>
    /// Pure natural language prose mode (e.g., Flux, SD3.5, Qwen-Image).
    /// The entire text is preserved as continuous natural language prose; commas do not split tags.
    /// </summary>
    ProseOnly = 1,

    /// <summary>
    /// Hybrid structured tags + natural language tail mode (Anima standard / Hunyuan).
    /// Structured tags are ordered by canonical Anima sections, followed by a natural language prose tail.
    /// </summary>
    Hybrid = 2
}

/// <summary>
/// Controls fine-grained division between tags and prose when operating in <see cref="CaptionMode.Hybrid"/>.
/// </summary>
public enum HybridProfile
{
    /// <summary>
    /// Standard Anima dataset convention: Quality/Meta/Safety/Character/Series/Artist + core General tags,
    /// followed by natural language prose describing interactions, lighting, and scene composition.
    /// </summary>
    AnimaStandard = 0,

    /// <summary>
    /// Compact identity/meta tags (Quality, Rating, Character, Series, Artist) + rich, detailed natural language prose.
    /// </summary>
    CompactTagsRichProse = 1,

    /// <summary>
    /// Exhaustive Danbooru tags + supplementary natural language summary sentence.
    /// </summary>
    FullTagsWithSummary = 2
}

/// <summary>
/// Configuration options governing how captions are parsed, formatted, validated, and serialized.
/// </summary>
public sealed record CaptionFormatOptions(
    CaptionMode Mode = CaptionMode.Hybrid,
    HybridProfile HybridProfile = HybridProfile.AnimaStandard,
    string TagSeparator = ", ",
    string HybridDelimiter = ". ",
    bool EnforceAnimaSectionOrder = true,
    bool PinTriggerWordsFirst = false,
    int PinnedTriggerCount = 1,
    bool EscapeParentheses = false)
{
    public static readonly CaptionFormatOptions DefaultHybrid = new(CaptionMode.Hybrid);
    public static readonly CaptionFormatOptions DefaultTagsOnly = new(CaptionMode.TagsOnly);
    public static readonly CaptionFormatOptions DefaultProseOnly = new(CaptionMode.ProseOnly, EnforceAnimaSectionOrder: false);

    public static CaptionFormatOptions ForMode(CaptionMode mode) => mode switch
    {
        CaptionMode.TagsOnly => DefaultTagsOnly,
        CaptionMode.ProseOnly => DefaultProseOnly,
        _ => DefaultHybrid
    };

    public static bool TryParseMode(string? value, out CaptionMode mode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            mode = CaptionMode.Hybrid;
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "tags":
            case "tag":
            case "tagsonly":
            case "tags_only":
            case "booru":
            case "danbooru":
                mode = CaptionMode.TagsOnly;
                return true;
            case "prose":
            case "proseonly":
            case "prose_only":
            case "natural":
            case "natural_language":
            case "nl":
                mode = CaptionMode.ProseOnly;
                return true;
            case "hybrid":
            case "anima":
            case "mixed":
            case "tags_and_prose":
            case "both":
                mode = CaptionMode.Hybrid;
                return true;
            default:
                mode = CaptionMode.Hybrid;
                return false;
        }
    }

    public static string ToKey(CaptionMode mode) => mode switch
    {
        CaptionMode.TagsOnly => "tags_only",
        CaptionMode.ProseOnly => "prose_only",
        _ => "hybrid"
    };

    public static string ToDisplayName(CaptionMode mode, bool chinese = true) => mode switch
    {
        CaptionMode.TagsOnly => chinese ? "纯标签 (Tags Only)" : "Tags Only",
        CaptionMode.ProseOnly => chinese ? "纯自然语言 (Prose Only)" : "Prose Only",
        _ => chinese ? "标签 + 自然语言混合 (Anima Hybrid)" : "Hybrid (Tags + Prose)"
    };
}

/// <summary>
/// Multi-mode parsing, serialization, and conversion engine that extends <see cref="PromptText"/>
/// without breaking existing <see cref="PromptText"/> callers.
/// </summary>
public static class MultiModePromptText
{
    /// <summary>
    /// Parses raw caption text according to the specified <see cref="CaptionFormatOptions"/>.
    /// </summary>
    public static PromptText.Parts Parse(
        string? text,
        VocabDb? vocab,
        CaptionFormatOptions? options = null,
        bool unescapeTags = false)
    {
        var opts = options ?? CaptionFormatOptions.DefaultHybrid;
        if (string.IsNullOrWhiteSpace(text))
            return new PromptText.Parts(new List<string>(), string.Empty);

        return opts.Mode switch
        {
            CaptionMode.TagsOnly => ParseTagsOnly(text, unescapeTags),
            CaptionMode.ProseOnly => ParseProseOnly(text),
            _ => ParseHybrid(text, vocab, opts, unescapeTags)
        };
    }

    /// <summary>
    /// Serializes structured tags and natural language prose into a single caption string
    /// strictly honoring the target <see cref="CaptionFormatOptions"/>.
    /// </summary>
    public static string Rebuild(
        IReadOnlyList<string>? tags,
        string? proseTail,
        CaptionFormatOptions? options = null)
    {
        var opts = options ?? CaptionFormatOptions.DefaultHybrid;
        var safeTags = tags ?? Array.Empty<string>();
        var safeProse = (proseTail ?? string.Empty).Trim();

        switch (opts.Mode)
        {
            case CaptionMode.TagsOnly:
            {
                var cleanedTags = CleanTagList(safeTags, opts.EscapeParentheses);
                return string.Join(opts.TagSeparator, cleanedTags);
            }

            case CaptionMode.ProseOnly:
            {
                if (safeProse.Length > 0)
                    return NormalizeProseWhitespace(safeProse);

                // Fallback: if transitioning from a tagged document with empty prose, join tags naturally.
                var cleanedTags = CleanTagList(safeTags, escapeParentheses: false);
                return cleanedTags.Count == 0
                    ? string.Empty
                    : string.Join(", ", cleanedTags);
            }

            case CaptionMode.Hybrid:
            default:
            {
                var cleanedTags = CleanTagList(safeTags, opts.EscapeParentheses);
                if (cleanedTags.Count == 0)
                    return NormalizeProseWhitespace(safeProse);
                if (safeProse.Length == 0)
                    return string.Join(opts.TagSeparator, cleanedTags);

                // Delegate to existing PromptText.Rebuild if standard settings are used, or format with custom delimiter
                if (opts.TagSeparator == ", " && opts.HybridDelimiter == ". ")
                    return PromptText.Rebuild(cleanedTags, safeProse, opts.TagSeparator);

                var tagBlock = string.Join(opts.TagSeparator, cleanedTags).TrimEnd('.', ',', ' ');
                var delimiter = string.IsNullOrEmpty(opts.HybridDelimiter) ? ". " : opts.HybridDelimiter;
                return tagBlock + delimiter + NormalizeProseWhitespace(safeProse);
            }
        }
    }

    /// <summary>
    /// Orders tags according to canonical Anima sections while optionally pinning trigger words at the start.
    /// </summary>
    public static List<string> OrderTags(
        IReadOnlyList<string> tags,
        VocabDb? vocab,
        AppSettings? settings,
        CaptionFormatOptions? options = null)
    {
        var opts = options ?? CaptionFormatOptions.DefaultHybrid;
        if (tags.Count <= 1 || !opts.EnforceAnimaSectionOrder)
            return new List<string>(tags);

        int pinCount = opts.PinTriggerWordsFirst ? Math.Clamp(opts.PinnedTriggerCount, 0, tags.Count) : 0;
        var pinned = tags.Take(pinCount).ToList();
        var rest = tags.Skip(pinCount).ToList();

        // **不依赖 TagItem**：TagItem 是 WinUI 类型（带 Brush/Visibility），而本文件
        // 要能编进纯 net10 的回归工程。分类用 TagSections.Classify，排序用
        // "大类序号 + 原位置"的稳定排序——与 TagSections.Canonicalize 同一个口径。
        var orderedRest = rest
            .Select((t, pos) => (Tag: t, Sec: TagSections.IndexOf(TagSections.Classify(t, vocab)), Pos: pos))
            .OrderBy(x => x.Sec).ThenBy(x => x.Pos)
            .Select(x => x.Tag)
            .ToList();

        var result = new List<string>(tags.Count);
        result.AddRange(pinned);
        result.AddRange(orderedRest);
        return result;
    }

    /// <summary>
    /// Converts an existing parsed caption into another <see cref="CaptionMode"/> using <see cref="VocabBatchEngine"/>
    /// when extracting tags from prose.
    /// </summary>
    public static PromptText.Parts ConvertMode(
        PromptText.Parts source,
        CaptionMode targetMode,
        VocabDb? vocab = null)
    {
        return targetMode switch
        {
            CaptionMode.TagsOnly => ConvertToTagsOnly(source, vocab),
            CaptionMode.ProseOnly => ConvertToProseOnly(source),
            _ => source
        };
    }

    private static PromptText.Parts ParseTagsOnly(string text, bool unescapeTags)
    {
        // In TagsOnly mode, even period-separated quality prefixes or multi-word custom trigger phrases
        // remain inside the tag collection.
        var normalized = text.Replace("\r\n", ",").Replace('\n', ',');
        var rawTokens = normalized.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
        var tags = new List<string>(rawTokens.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in rawTokens)
        {
            var token = raw.Trim().TrimEnd('.');
            if (token.Length == 0)
                continue;

            if (unescapeTags)
                token = token.Replace(@"\(", "(").Replace(@"\)", ")");

            if (seen.Add(token))
                tags.Add(token);
        }

        return new PromptText.Parts(tags, string.Empty);
    }

    private static PromptText.Parts ParseProseOnly(string text)
    {
        var cleaned = NormalizeProseWhitespace(text);
        return new PromptText.Parts(new List<string>(), cleaned);
    }

    private static PromptText.Parts ParseHybrid(
        string text,
        VocabDb? vocab,
        CaptionFormatOptions opts,
        bool unescapeTags)
    {
        // Check explicit paragraph break first if present
        int doubleNewline = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (doubleNewline < 0)
            doubleNewline = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);

        if (doubleNewline > 0)
        {
            var head = text[..doubleNewline].Trim();
            var tail = text[doubleNewline..].Trim();
            if (head.Length > 0 && tail.Length > 0 && !head.EndsWith('.'))
            {
                var headTags = ParseTagsOnly(head, unescapeTags).Tags;
                return new PromptText.Parts(headTags, NormalizeProseWhitespace(tail));
            }
        }

        // Delegate to AnimaCaptioner's battle-tested PromptText.Parse
        return PromptText.Parse(text, vocab, unescapeTags);
    }

    private static PromptText.Parts ConvertToTagsOnly(PromptText.Parts source, VocabDb? vocab)
    {
        var merged = new List<string>(source.Tags);
        var seen = new HashSet<string>(source.Tags, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(source.ProseTail) && vocab is not null)
        {
            var scanned = VocabBatchEngine.ScanText(vocab, source.ProseTail, maxResults: 50);
            foreach (var hit in scanned.MatchedTags)
            {
                if (seen.Add(hit.Tag))
                    merged.Add(hit.Tag);
            }
        }

        return new PromptText.Parts(merged, string.Empty);
    }

    private static PromptText.Parts ConvertToProseOnly(PromptText.Parts source)
    {
        if (!string.IsNullOrWhiteSpace(source.ProseTail) && source.Tags.Count == 0)
            return new PromptText.Parts(new List<string>(), NormalizeProseWhitespace(source.ProseTail));

        if (!string.IsNullOrWhiteSpace(source.ProseTail) && source.Tags.Count > 0)
        {
            var tagPrefix = string.Join(", ", source.Tags).TrimEnd('.');
            var combined = $"{tagPrefix}. {NormalizeProseWhitespace(source.ProseTail)}";
            return new PromptText.Parts(new List<string>(), combined);
        }

        var fallback = string.Join(", ", source.Tags);
        return new PromptText.Parts(new List<string>(), fallback);
    }

    private static List<string> CleanTagList(IReadOnlyList<string> tags, bool escapeParentheses)
    {
        var result = new List<string>(tags.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in tags)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var t = raw.Trim().Trim(',');
            if (t.Length == 0)
                continue;

            if (escapeParentheses)
            {
                t = t.Replace(@"\(", "(").Replace(@"\)", ")")
                     .Replace("(", @"\(").Replace(")", @"\)");
            }

            if (seen.Add(t))
                result.Add(t);
        }

        return result;
    }

    private static string NormalizeProseWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder(text.Length);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(trimmed);
        }

        return sb.ToString();
    }

    private static int SectionSortIndex(string? key) => (key ?? string.Empty).ToLowerInvariant() switch
    {
        "quality" or "meta" or "year" or "rating" or "safety" => 0,
        "character" => 1,
        "series" or "copyright" => 2,
        "artist" => 3,
        _ => 4
    };
}
