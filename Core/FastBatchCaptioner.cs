using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace AnimaCaptioner.Core;

/// <summary>
/// Two-Stage Fast Batch Captioning Pipeline for high-throughput dataset labeling.
/// <para>
/// Instead of running 4-8 multi-turn LLM tool loops per image during batch processing,
/// Stage 1 prompts the vision model for a single structured JSON observation according to
/// the target <see cref="CaptionMode"/>, and Stage 2 runs local <see cref="VocabBatchEngine"/>
/// resolution, prose scanning, and canonical Anima section sorting in <c>&lt; 2ms</c>.
/// </para>
/// </summary>
public static class FastBatchCaptioner
{
    public sealed record FastCaptionDraft(
        IReadOnlyList<string> CandidateTags,
        string Prose,
        string ParseError);

    public sealed record FastCaptionResult(
        CaptionMode Mode,
        IReadOnlyList<string> FinalTags,
        IReadOnlyList<string> UnresolvedTags,
        string FinalProse,
        string FormattedCaptionText,
        bool NeedsAgentFallback,
        string DiagnosticNote);

    /// <summary>
    /// Builds the single-shot vision system prompt for Stage 1 of the fast batch pipeline.
    /// </summary>
    public static string BuildSingleShotSystemPrompt(
        CaptionMode mode,
        HybridProfile profile = HybridProfile.AnimaStandard,
        string extraUserInstruction = "")
    {
        var basePrompt = mode switch
        {
            CaptionMode.TagsOnly => """
                You are an expert anime/illustration dataset tagger operating in TAGS_ONLY mode.
                Inspect the image carefully and output ONLY a valid JSON object with this exact shape:
                {
                  "tags": ["1girl", "solo", "long hair", "blue eyes", ...]
                }
                Rules:
                - Include subject count, character name (if recognizable), copyright/series, hair color & style, eye color, expression, clothing & accessories, pose, camera framing, background, and lighting.
                - Use standard Danbooru tag phrasing (spaces instead of underscores, lowercase).
                - Do NOT include a "prose" field or any natural language sentences.
                """,

            CaptionMode.ProseOnly => """
                You are an expert visual dataset captioner operating in PROSE_ONLY mode.
                Inspect the image carefully and output ONLY a valid JSON object with this exact shape:
                {
                  "prose": "An anime girl with long silver hair and crimson eyes stands on a rain-slicked balcony..."
                }
                Rules:
                - Write 2 to 5 objective, descriptive English sentences covering subject appearance, clothing details, posture and hand actions, spatial layout, background setting, and lighting atmosphere.
                - Do NOT output comma-separated Booru tag lists and do NOT include a "tags" array.
                """,

            _ => $$"""
                You are an expert Anima dataset captioner operating in HYBRID (Tags + Natural Language Prose) mode (Profile: {{profile}}).
                Inspect the image carefully and output ONLY a valid JSON object with this exact shape:
                {
                  "tags": ["1girl", "solo", "long hair", ...],
                  "prose": "She stands near a sunlit classroom window, gently holding a closed sketchbook against her chest..."
                }
                Rules:
                - "tags": Standard Danbooru tags for character count, character/series identity, hair/eyes, core outfit pieces, and shot framing.
                - "prose": Natural language sentences describing nuanced poses, interactions, spatial composition, lighting, and atmosphere.
                """
        };

        if (!string.IsNullOrWhiteSpace(extraUserInstruction))
            return basePrompt + "\n\nAdditional User Constraint:\n" + extraUserInstruction.Trim();

        return basePrompt;
    }

    /// <summary>
    /// Parses the raw LLM single-shot response (supporting JSON fences or plain text fallback)
    /// into a <see cref="FastCaptionDraft"/>.
    /// </summary>
    public static FastCaptionDraft ParseModelResponse(string? modelOutput, CaptionMode mode)
    {
        if (string.IsNullOrWhiteSpace(modelOutput))
            return new FastCaptionDraft(Array.Empty<string>(), string.Empty, "Empty model response.");

        var cleaned = ExtractJsonBlock(modelOutput.Trim());
        if (cleaned.StartsWith('{') && cleaned.EndsWith('}'))
        {
            try
            {
                if (JsonNode.Parse(cleaned) is JsonObject obj)
                {
                    var tags = new List<string>();
                    if (obj["tags"] is JsonArray arr)
                    {
                        foreach (var item in arr)
                        {
                            var t = item?.GetValue<string>()?.Trim();
                            if (!string.IsNullOrEmpty(t))
                                tags.Add(t);
                        }
                    }

                    string prose = obj["prose"]?.GetValue<string>()?.Trim() ?? string.Empty;
                    return new FastCaptionDraft(tags, prose, string.Empty);
                }
            }
            catch (Exception ex)
            {
                // Fall through to heuristic parsing via MultiModePromptText
                var fallback = MultiModePromptText.Parse(modelOutput, vocab: null, CaptionFormatOptions.ForMode(mode));
                return new FastCaptionDraft(fallback.Tags, fallback.ProseTail, $"JSON parse warning (used text fallback): {ex.Message}");
            }
        }

        var parts = MultiModePromptText.Parse(modelOutput, vocab: null, CaptionFormatOptions.ForMode(mode));
        return new FastCaptionDraft(parts.Tags, parts.ProseTail, string.Empty);
    }

    /// <summary>
    /// Executes Stage 2 local post-processing: resolves candidate tags via <see cref="VocabBatchEngine"/>,
    /// scans prose for implicit tags when in <see cref="CaptionMode.TagsOnly"/> or <see cref="CaptionMode.Hybrid"/>,
    /// orders tags by Anima sections, and formats the final caption text.
    /// </summary>
    public static FastCaptionResult FinalizeDraft(
        FastCaptionDraft draft,
        CaptionFormatOptions options,
        VocabDb? vocab = null,
        AppSettings? settings = null,
        IReadOnlyList<string>? existingTagsToKeep = null)
    {
        var mode = options.Mode;
        var mergedSeedTags = new List<string>();
        if (existingTagsToKeep is not null)
            mergedSeedTags.AddRange(existingTagsToKeep);
        mergedSeedTags.AddRange(draft.CandidateTags);

        if (mode == CaptionMode.ProseOnly)
        {
            string finalProse = draft.Prose.Trim();
            if (finalProse.Length == 0 && draft.CandidateTags.Count > 0)
                finalProse = string.Join(", ", draft.CandidateTags);

            string formatted = MultiModePromptText.Rebuild(Array.Empty<string>(), finalProse, options);
            return new FastCaptionResult(
                Mode: CaptionMode.ProseOnly,
                FinalTags: Array.Empty<string>(),
                UnresolvedTags: Array.Empty<string>(),
                FinalProse: finalProse,
                FormattedCaptionText: formatted,
                NeedsAgentFallback: finalProse.Length < 12,
                DiagnosticNote: draft.ParseError);
        }

        // Resolve candidate tags in batch
        var resolved = VocabBatchEngine.ResolveBatch(vocab, mergedSeedTags);
        var finalTags = new List<string>(resolved.CanonicalKnownTags);
        var seen = new HashSet<string>(finalTags, StringComparer.OrdinalIgnoreCase);

        // If prose exists in Hybrid mode (or LLM accidentally returned prose in TagsOnly), scan prose for extra canonical tags
        if (!string.IsNullOrWhiteSpace(draft.Prose) && vocab is not null)
        {
            var scanned = VocabBatchEngine.ScanText(vocab, draft.Prose, maxResults: 25);
            foreach (var hit in scanned.MatchedTags)
            {
                if (seen.Add(hit.Tag))
                    finalTags.Add(hit.Tag);
            }
        }

        // If vocab is null (unit test or uninitialized DB), preserve cleaned candidate tags directly
        if (vocab is null)
        {
            foreach (var raw in mergedSeedTags)
            {
                var t = raw.Trim();
                if (t.Length > 0 && seen.Add(t))
                    finalTags.Add(t);
            }
        }

        var orderedTags = MultiModePromptText.OrderTags(finalTags, vocab, settings, options);
        string outputProse = mode == CaptionMode.TagsOnly ? string.Empty : draft.Prose.Trim();
        string finalCaption = MultiModePromptText.Rebuild(orderedTags, outputProse, options);

        bool needsFallback = orderedTags.Count == 0 ||
                             (mode == CaptionMode.Hybrid && outputProse.Length == 0);

        return new FastCaptionResult(
            Mode: mode,
            FinalTags: orderedTags,
            UnresolvedTags: resolved.UnresolvedInputs,
            FinalProse: outputProse,
            FormattedCaptionText: finalCaption,
            NeedsAgentFallback: needsFallback,
            DiagnosticNote: draft.ParseError);
    }

    private static string ExtractJsonBlock(string input)
    {
        if (input.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = input.IndexOf('\n');
            if (firstNewline > 0)
                input = input[(firstNewline + 1)..];
            int endFence = input.LastIndexOf("```", StringComparison.Ordinal);
            if (endFence >= 0)
                input = input[..endFence];
            return input.Trim();
        }

        int firstBrace = input.IndexOf('{');
        int lastBrace = input.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            return input[firstBrace..(lastBrace + 1)].Trim();

        return input;
    }
}
