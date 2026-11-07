using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimaCaptioner.Core;

/// <summary>
/// Target base model architecture family that determines token budget, quality prefixes,
/// tag section ordering, artist prefix conventions (<c>@artist</c>), and OCR text quoting rules.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TargetModelFamily
{
    /// <summary>
    /// CircleStone Labs Anima (Qwen3 0.6B encoder + LLM adapter, 512 token hard limit, single-line hybrid/tags).
    /// </summary>
    Anima = 0,

    /// <summary>
    /// OpenMOSS MOSS-Image 5B DiT (Qwen3.5-9B encoder, 4096 token budget, strict OCR text preservation, watermark exclusion).
    /// </summary>
    MossImage = 1,

    /// <summary>
    /// Standard Danbooru/Booru models (Illustrious, NoobAI, Pony, SDXL).
    /// </summary>
    BooruStandard = 2,

    /// <summary>
    /// Pure natural language DiT models (Flux, SD3.5, Qwen-Image).
    /// </summary>
    NaturalLanguageDiT = 3
}

/// <summary>
/// LoRA training objective governing variable disentanglement (what features must be stripped/absorbed
/// into the LoRA weights vs. what features must be explicitly captioned to prevent concept bleeding).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoraTrainingIntent
{
    /// <summary>
    /// General dataset or base model captioning: describe all visible traits faithfully.
    /// </summary>
    General = 0,

    /// <summary>
    /// Character LoRA: lock character identity &amp; core physical traits into the trigger word;
    /// strip core hair/eye/facial traits while exhaustively captioning clothing, pose, background, and art style.
    /// </summary>
    CharacterLora = 1,

    /// <summary>
    /// Style / Artist LoRA: lock brushstroke, line weight, coloring, and rendering aesthetic into the @style trigger;
    /// strip style/medium tags while exhaustively captioning subject identity, hair/eyes, outfit, background, and visible text.
    /// </summary>
    StyleLora = 2,

    /// <summary>
    /// Outfit / Concept LoRA: lock the target garment or concept into the trigger word;
    /// strip sub-garment component tags while thoroughly captioning the wearer's appearance, pose, and environment.
    /// </summary>
    ConceptLora = 3
}

/// <summary>
/// Dynamic, JSON-serializable training &amp; captioning preset combining base model rules,
/// LoRA training intent, trigger word placement, tag stripping rules, and LLM prompt instructions.
/// </summary>
public sealed class CaptionPreset
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "anima-character-lora";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "Anima · 角色 LoRA (Character LoRA)";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("model_family")]
    public TargetModelFamily ModelFamily { get; set; } = TargetModelFamily.Anima;

    [JsonPropertyName("training_intent")]
    public LoraTrainingIntent TrainingIntent { get; set; } = LoraTrainingIntent.CharacterLora;

    [JsonPropertyName("caption_mode")]
    public CaptionMode CaptionMode { get; set; } = CaptionMode.Hybrid;

    [JsonPropertyName("hybrid_profile")]
    public HybridProfile HybridProfile { get; set; } = HybridProfile.AnimaStandard;

    /// <summary>
    /// Trigger word(s) to inject (e.g., <c>hatsune miku</c> for Character LoRA or <c>@my_style</c> for Style LoRA).
    /// </summary>
    [JsonPropertyName("trigger_word")]
    public string TriggerWord { get; set; } = string.Empty;

    /// <summary>
    /// Whether the trigger word should be pinned at the very front of the caption (index 0, e.g. MossAD style trigger)
    /// or placed in its canonical section slot (after quality/meta/count tags, e.g. Anima character trigger).
    /// </summary>
    [JsonPropertyName("pin_trigger_at_front")]
    public bool PinTriggerAtFront { get; set; } = false;

    /// <summary>
    /// Automatically prepend <c>@</c> to artist/style triggers (used by both Anima and MOSS-Image for artist/style tokens).
    /// </summary>
    [JsonPropertyName("prefix_style_trigger_with_at")]
    public bool PrefixStyleTriggerWithAt { get; set; } = true;

    /// <summary>
    /// Default quality/safety prefix tags automatically ensured at the start of the tag block if non-empty.
    /// </summary>
    [JsonPropertyName("quality_prefix_tags")]
    public List<string> QualityPrefixTags { get; set; } = new();

    /// <summary>
    /// Maximum token budget for the target model's text encoder (512 for Anima Qwen3-0.6B; 4096 for MOSS-Image Qwen3.5-9B).
    /// </summary>
    [JsonPropertyName("max_token_budget")]
    public int MaxTokenBudget { get; set; } = 512;

    /// <summary>
    /// Forces the final serialized <c>.txt</c> caption onto a single line without <c>\n</c> breaks
    /// (mandatory for Kohya <c>read_caption</c> compatibility in Anima training).
    /// </summary>
    [JsonPropertyName("force_single_line_output")]
    public bool ForceSingleLineOutput { get; set; } = true;

    /// <summary>
    /// Tag section keys to completely strip during post-processing (e.g., <c>["artist"]</c> in Style LoRA so only the style trigger remains).
    /// </summary>
    [JsonPropertyName("strip_tag_sections")]
    public List<string> StripTagSections { get; set; } = new();

    /// <summary>
    /// Specific tags or wildcard patterns to strip during post-processing.
    /// <list type="bullet">
    ///   <item><description>In <b>Character LoRA</b>: put the character's core invariant traits here (e.g. <c>blonde hair, twintails, blue eyes, ahoge</c>) so they are absorbed into the character trigger.</description></item>
    ///   <item><description>In <b>Style LoRA</b>: put medium/rendering tags here (e.g. <c>anime coloring, cel shading, thick outlines, watercolor</c>) so they are absorbed into the style trigger.</description></item>
    /// </list>
    /// </summary>
    [JsonPropertyName("strip_tags")]
    public List<string> StripTags { get; set; } = new();

    /// <summary>
    /// Whether to automatically strip standard hair color, hair length, and eye color tags when <see cref="TrainingIntent"/> is <see cref="LoraTrainingIntent.CharacterLora"/>.
    /// </summary>
    [JsonPropertyName("auto_strip_core_hair_eye_in_character_lora")]
    public bool AutoStripCoreHairEyeInCharacterLora { get; set; } = true;

    /// <summary>
    /// Whether to preserve exact visible text inside double quotes <c>"..."</c> without translating (critical for MOSS-Image).
    /// </summary>
    [JsonPropertyName("preserve_exact_visible_text_in_quotes")]
    public bool PreserveExactVisibleTextInQuotes { get; set; } = false;

    /// <summary>
    /// Whether to strictly forbid describing watermarks/signatures in captions or tags (critical for MOSS-Image &amp; Style LoRA).
    /// </summary>
    [JsonPropertyName("exclude_watermarks_and_signatures")]
    public bool ExcludeWatermarksAndSignatures { get; set; } = true;

    /// <summary>
    /// Custom system prompt rules injected into both <see cref="TagAgent"/> and <see cref="FastBatchCaptioner"/>.
    /// Supports placeholders: <c>{{trigger_word}}</c>, <c>{{model_family}}</c>, <c>{{training_intent}}</c>, <c>{{strip_tags}}</c>.
    /// </summary>
    [JsonPropertyName("system_prompt_template")]
    public string SystemPromptTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Explicit checklist of visual dimensions that the AI MUST describe in detail to decouple them from the LoRA weights.
    /// </summary>
    [JsonPropertyName("must_describe_dimensions")]
    public List<string> MustDescribeDimensions { get; set; } = new();

    /// <summary>
    /// Explicit list of visual dimensions that the AI MUST OMIT from both tags and prose so the LoRA learns them.
    /// </summary>
    [JsonPropertyName("must_omit_dimensions")]
    public List<string> MustOmitDimensions { get; set; } = new();
}

/// <summary>
/// Manages the 4 core built-in presets (Anima × Character/Style, MOSS-Image × Character/Style),
/// user custom JSON preset files in <c>%LOCALAPPDATA%\AnimaCaptioner\presets\</c>,
/// dynamic prompt rendering, and deterministic post-processing filters.
/// </summary>
public static class CaptionPresetEngine
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Standard hair/eye color & basic hair length tags that belong to a character's core identity
    private static readonly HashSet<string> StandardCharacterIdentityTraits = new(StringComparer.OrdinalIgnoreCase)
    {
        "blonde hair", "blue hair", "black hair", "brown hair", "silver hair", "white hair",
        "pink hair", "red hair", "purple hair", "green hair", "orange hair", "grey hair",
        "multicolored hair", "two-tone hair", "streaked hair", "gradient hair",
        "short hair", "medium hair", "long hair", "very long hair", "absurdly long hair",
        "twintails", "low twintails", "short twintails", "ponytail", "high ponytail",
        "side ponytail", "braid", "single braid", "twin braids", "french braid", "hair bun",
        "double bun", "bob cut", "hime cut", "drill hair", "ahoge", "blunt bangs",
        "swept bangs", "parted bangs", "crossed bangs", "hair between eyes", "sidelocks",
        "hair intakes", "floating hair",
        "blue eyes", "red eyes", "brown eyes", "green eyes", "purple eyes", "yellow eyes",
        "pink eyes", "aqua eyes", "orange eyes", "grey eyes", "black eyes", "heterochromia",
        "slit pupils", "symbol-shaped pupils", "heart-shaped pupils", "pointy ears",
        "animal ears", "cat ears", "fox ears", "rabbit ears", "dog ears", "wolf ears",
        "horns", "halo", "wings"
    };

    // Standard artistic style, medium, rendering, and watermark tags that must be stripped in Style LoRA training
    private static readonly HashSet<string> StandardStyleAndMediumTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "anime coloring", "cel shading", "flat color", " painterly", "watercolor (medium)",
        "oil painting (medium)", "acrylic paint (medium)", "colored pencil (medium)",
        "graphite (medium)", "ink (medium)", "marker (medium)", "pastel (medium)",
        "digital media (artwork)", "traditional media", "sketch", "lineart", "monochrome",
        "greyscale", "thick outlines", "thin outlines", "no lineart", "lineless",
        "soft shading", "hard shading", "hatching", "crosshatching", "stippling",
        "halftone", "chromatic aberration", "film grain", "lens flare", "bloom",
        "pastel colors", "vibrant colors", "muted colors", "high contrast", "limited palette",
        "retro artstyle", "1990s (style)", "1980s (style)", "2000s (style)", "official art",
        "game cg", "anime screencap", "watermark", "signature", "artist name", "dated",
        "web address", "twitter username", "patreon username", "pixiv id"
    };

    /// <summary>按 id 找一个预设（内置 + 用户目录）。找不到返回 null。</summary>
    public static CaptionPreset? GetPresetById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var p in LoadAllPresets())
            if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }
    public static string UserPresetsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimaCaptioner", "presets");

    /// <summary>
    /// Returns the 4 foundational built-in presets grounded in official Anima and MOSS-Image training specifications,
    /// plus 2 general baseline presets.
    /// </summary>
    public static IReadOnlyList<CaptionPreset> GetBuiltInPresets() => new[]
    {
        CreateAnimaCharacterLoraPreset(),
        CreateAnimaStyleLoraPreset(),
        CreateMossCharacterLoraPreset(),
        CreateMossStyleLoraPreset(),
        CreateAnimaGeneralPreset(),
        CreateMossGeneralPreset()
    };

    /// <summary>
    /// Preset 1: Anima · 角色 LoRA (Character LoRA)
    /// Grounded in CircleStone Labs Anima Model Card &amp; community Kohya/diffusion-pipe Anima recipes:
    /// - 512 token cap (Qwen3 0.6B encoder), single-line output (`\n` forbidden in Kohya).
    /// - No score tags (`score_9` etc. are dead weight in Anima).
    /// - Character trigger placed after quality/meta/count tags (no `@` prefix).
    /// - Core hair/eye/facial identity traits stripped so they bind to the character trigger;
    ///   outfit, pose, camera framing, background, and lighting described thoroughly in tags + prose tail.
    /// </summary>
    public static CaptionPreset CreateAnimaCharacterLoraPreset() => new()
    {
        Id = "anima-character-lora",
        Name = "Anima · 角色 LoRA (Character LoRA)",
        Description = "针对 Anima (Qwen3-0.6B, 512 Token 单行) 训练角色 LoRA：将角色触发词置于人数/质量词之后（不加 @），自动剥离固定发色/发型/瞳色等固有外貌标签使其焊入角色权重，详尽标注服装单品、姿态动作、镜头与背景以实现自由换装。",
        ModelFamily = TargetModelFamily.Anima,
        TrainingIntent = LoraTrainingIntent.CharacterLora,
        CaptionMode = CaptionMode.Hybrid,
        HybridProfile = HybridProfile.AnimaStandard,
        TriggerWord = "",
        PinTriggerAtFront = false,
        PrefixStyleTriggerWithAt = false,
        QualityPrefixTags = new List<string> { "masterpiece", "best quality", "safe" },
        MaxTokenBudget = 512,
        ForceSingleLineOutput = true,
        AutoStripCoreHairEyeInCharacterLora = true,
        PreserveExactVisibleTextInQuotes = true,
        ExcludeWatermarksAndSignatures = true,
        StripTagSections = new List<string>(),
        StripTags = new List<string> { "watermark", "signature", "artist name" },
        MustOmitDimensions = new List<string>
        {
            "Character's permanent hair color, base hairstyle (e.g. twintails, ahoge, bangs), and eye color (bound to trigger word)",
            "Permanent physical markings, animal ears, horns, or halo belonging to the character's base design",
            "Watermarks, artist signatures, or border artifacts"
        },
        MustDescribeDimensions = new List<string>
        {
            "Every piece of clothing, footwear, and temporary accessories (even if wearing the default canonical outfit, describe it so the LoRA allows outfit swapping)",
            "Body posture, hand gestures, head tilt, and gaze direction (e.g. looking at viewer)",
            "Facial expression (smile, blush, open mouth, serious, etc.)",
            "Camera framing and angle (upper body, cowboy shot, full body, from above/below)",
            "Background environment, props, time of day, and lighting direction"
        },
        SystemPromptTemplate = """
            You are captioning a training image for an **Anima Character LoRA** (Qwen3-0.6B encoder, max 512 tokens, strict single-line output).
            - **Trigger Word**: `{{trigger_word}}` (placed in the Character section, NEVER prefixed with `@`).
            - **Variable Disentanglement Rule (CRITICAL for Character LoRA)**:
              1. **DO NOT** output tags or prose describing the character's permanent physical identity traits (hair color, base hairstyle, bangs, eye color, animal ears/horns). Those traits MUST remain uncaptioned so they are absorbed into the character trigger word.
              2. **DO** exhaustively describe all variable elements: every garment and accessory worn in this specific image (even default outfits), exact pose and hand actions, facial expression, camera shot/angle, background objects, and lighting atmosphere.
              3. Format as Anima Hybrid on a SINGLE LINE: canonical Danbooru tags first (excluding character hair/eye traits), followed by `. ` and a concise 1-3 sentence natural language scene description referring to the subject simply as "she"/"he"/"the character" without restating hair or eye color.
            """
    };

    /// <summary>
    /// Preset 2: Anima · 画风 LoRA (Style / Artist LoRA)
    /// Grounded in Anima style training conventions:
    /// - Uses `@style_name` artist prefix convention.
    /// - Strips all medium/rendering/shading/brushstroke tags and prohibits purple prose about art style.
    /// - Exhaustively captions character identity, hair/eye colors, clothing, pose, and background to prevent concept bleed.
    /// </summary>
    public static CaptionPreset CreateAnimaStyleLoraPreset() => new()
    {
        Id = "anima-style-lora",
        Name = "Anima · 画风 LoRA (Style / Artist LoRA)",
        Description = "针对 Anima 训练画风/画师 LoRA：自动使用 @画师名 触发词格式，严禁在标签或自然语言中描述线条、笔触、上色技法与光影风格（使其完全沉入 LoRA 权重），同时极度详尽地标注人物身份、发色瞳色、服装与背景以防止画风污染构图或脸型。",
        ModelFamily = TargetModelFamily.Anima,
        TrainingIntent = LoraTrainingIntent.StyleLora,
        CaptionMode = CaptionMode.Hybrid,
        HybridProfile = HybridProfile.AnimaStandard,
        TriggerWord = "@my_style",
        PinTriggerAtFront = false,
        PrefixStyleTriggerWithAt = true,
        QualityPrefixTags = new List<string> { "masterpiece", "best quality", "safe" },
        MaxTokenBudget = 512,
        ForceSingleLineOutput = true,
        AutoStripCoreHairEyeInCharacterLora = false,
        PreserveExactVisibleTextInQuotes = true,
        ExcludeWatermarksAndSignatures = true,
        StripTagSections = new List<string> { "artist" },
        StripTags = new List<string>(StandardStyleAndMediumTags),
        MustOmitDimensions = new List<string>
        {
            "Artistic style, rendering technique, line weight, shading style (cel shading, painterly, watercolor, thick outlines, anime coloring)",
            "Color palette mood adjectives or aesthetic judgements (vibrant, pastel, retro, masterpiece aesthetic)",
            "Other artist names, studio names, watermarks, or signatures"
        },
        MustDescribeDimensions = new List<string>
        {
            "Subject count, character name, and copyright/series if recognizable",
            "Complete physical appearance: exact hair color, hairstyle, bangs, eye color, skin tone, body proportions, and facial expression",
            "Complete outfit breakdown: every garment, color, pattern, and held prop",
            "Exact pose, camera angle, framing, and all background objects/architecture"
        },
        SystemPromptTemplate = """
            You are captioning a training image for an **Anima Style / Artist LoRA** (Qwen3-0.6B encoder, max 512 tokens, strict single-line output).
            - **Style Trigger Word**: `{{trigger_word}}` (automatically formatted with Anima's `@` artist prefix).
            - **Variable Disentanglement Rule (CRITICAL for Style LoRA)**:
              1. **NEVER** describe the art style, drawing medium, line weight, brushwork, shading technique (e.g. do NOT write "anime style", "cel shading", "soft painterly brushwork", "thick outlines", "pastel palette"), or other artist names. The LoRA must learn the entire visual aesthetic under `{{trigger_word}}`.
              2. **DO** exhaustively describe WHAT is depicted: character identity/series, exact hair color & style, eye color, facial expression, every clothing item and color, physical pose, camera framing, and every background element.
              3. Keep natural language prose strictly factual and content-focused (no purple prose or aesthetic commentary), formatted on a SINGLE LINE after the tag block.
            """
    };

    /// <summary>
    /// Preset 3: MOSS-Image · 角色 LoRA (MOSS-Image Character LoRA)
    /// Grounded in OpenMOSS MOSS-Image-Base User Guide &amp; Qwen3.5-9B text encoder capabilities:
    /// - Recommended tag order: Quality -> Score -> Meta -> Year -> Character -> Series -> Artist -> General.
    /// - Up to 4,096 token budget via Qwen3.5-9B encoder; excels at rich, structured spatial natural language + tags.
    /// - Preserves any visible Chinese/English text verbatim inside double quotes `""` without translating.
    /// - Excludes watermarks completely (watermarks stick heavily in MOSS-Image fine-tuning).
    /// </summary>
    public static CaptionPreset CreateMossCharacterLoraPreset() => new()
    {
        Id = "moss-character-lora",
        Name = "MOSS-Image · 角色 LoRA (Character LoRA)",
        Description = "针对 OpenMOSS MOSS-Image (5B DiT + Qwen3.5-9B 4K 编码器) 训练角色 LoRA：遵循官方 Quality->Score->Meta->Year->Character->Series->@Artist->General 顺序，剥离角色固有发色/发型/瞳色，充分发挥 Qwen3.5-9B 长文本优势详述服饰材质、空间方位、光影与画面内双语文字（原样保留在双引号内），严格剔除水印描述。",
        ModelFamily = TargetModelFamily.MossImage,
        TrainingIntent = LoraTrainingIntent.CharacterLora,
        CaptionMode = CaptionMode.Hybrid,
        HybridProfile = HybridProfile.CompactTagsRichProse,
        TriggerWord = "",
        PinTriggerAtFront = false,
        PrefixStyleTriggerWithAt = true,
        QualityPrefixTags = new List<string> { "best quality", "highres" },
        MaxTokenBudget = 4096,
        ForceSingleLineOutput = false,
        AutoStripCoreHairEyeInCharacterLora = true,
        PreserveExactVisibleTextInQuotes = true,
        ExcludeWatermarksAndSignatures = true,
        StripTagSections = new List<string>(),
        StripTags = new List<string> { "watermark", "signature", "jpeg artifacts", "username" },
        MustOmitDimensions = new List<string>
        {
            "Character's fixed hair color, base hairstyle, and eye color (bound to the character trigger word)",
            "Any watermarks, artist signatures, or platform logos (never mention them in tags or prose)"
        },
        MustDescribeDimensions = new List<string>
        {
            "Detailed clothing layers, fabric textures, colors, and accessories worn in this specific image",
            "Spatial composition: foreground, midground, background, camera perspective, and subject placement",
            "Nuanced posture, hand/finger actions, and facial expression",
            "Lighting direction, shadow quality, bokeh/depth of field, and artistic medium/style (so style is not baked into the character LoRA)",
            "All non-watermark visible Chinese/English text in the image verbatim inside double quotes \"...\" along with its position and typography"
        },
        SystemPromptTemplate = """
            You are captioning a training image for a **MOSS-Image (5B DiT + Qwen3.5-9B encoder) Character LoRA**.
            - **Trigger Word**: `{{trigger_word}}` (follows MOSS-Image official tag order: `Quality -> Score -> Meta -> Year -> Character -> Series -> @Artist -> General`).
            - **MOSS-Image Character LoRA Rules**:
              1. **Identity Lock**: Do NOT describe the character's permanent hair color, base hairstyle, or eye color in either tags or prose. Refer to the character by `{{trigger_word}}` or pronouns so those core traits bind tightly to the LoRA trigger.
              2. **Rich Spatial & Outfit Prose**: Leverage Qwen3.5-9B's long-context comprehension to write a thorough, structured natural language description covering attire (fabrics, colors, layering), pose and hand interactions, spatial layout (foreground/midground/background), lighting/depth of field, and rendering style.
              3. **Exact Visible Text Preservation**: If the image contains non-watermark text (signs, speech bubbles, clothing prints, UI, titles in Chinese, English, or Japanese), transcribe the exact characters verbatim inside double quotes `"..."` without translating or romanizing, and note its position.
              4. **Zero Watermark Mention**: Never describe or tag watermarks or signatures; MOSS-Image bakes watermark tokens into weights if mentioned.
            """
    };

    /// <summary>
    /// Preset 4: MOSS-Image · 画风 LoRA (MOSS-Image Style / Artist LoRA)
    /// Grounded in OpenMOSS User Guide &amp; MossAD 57-Artist Finetune findings:
    /// - Artist/style trigger uses `@artist_name` and is pinned at the front (or Artist slot) per MossAD attribution system.
    /// - Strips all style/medium/brushwork descriptors so MOSS-Image's DiT learns the pure visual style within ~4.5k-7k views.
    /// - Exhaustively describes character appearance, objects, spatial layout, and exact visible text; completely excludes watermarks.
    /// </summary>
    public static CaptionPreset CreateMossStyleLoraPreset() => new()
    {
        Id = "moss-style-lora",
        Name = "MOSS-Image · 画风 LoRA (Style / Artist LoRA)",
        Description = "针对 OpenMOSS MOSS-Image 训练画风/画师 LoRA（融合官方指南与 MossAD 训练实践）：自动使用 @style 触发词并支持前置锚定，彻底剥离笔触/上色/媒介/色调等画风描述词，全量详写人物五官发色、服饰、空间构图与双语文字，严防画风与特定人物或水印耦合。",
        ModelFamily = TargetModelFamily.MossImage,
        TrainingIntent = LoraTrainingIntent.StyleLora,
        CaptionMode = CaptionMode.Hybrid,
        HybridProfile = HybridProfile.AnimaStandard,
        TriggerWord = "@my_moss_style",
        PinTriggerAtFront = true,
        PrefixStyleTriggerWithAt = true,
        QualityPrefixTags = new List<string> { "best quality", "highres" },
        MaxTokenBudget = 4096,
        ForceSingleLineOutput = false,
        AutoStripCoreHairEyeInCharacterLora = false,
        PreserveExactVisibleTextInQuotes = true,
        ExcludeWatermarksAndSignatures = true,
        StripTagSections = new List<string> { "artist" },
        StripTags = new List<string>(StandardStyleAndMediumTags),
        MustOmitDimensions = new List<string>
        {
            "All art style, medium, brushwork, line weight, shading, and rendering descriptors (e.g. cel shading, watercolor, anime style, thick outlines, painterly)",
            "Color grading and aesthetic style adjectives (绑定至 @style 触发词)",
            "All watermarks, artist signatures, logos, and border margins"
        },
        MustDescribeDimensions = new List<string>
        {
            "Every subject's full physical appearance: hair color, hairstyle, bangs, eye color, expression, age/gender tags, and body framing",
            "Every garment, accessory, prop, and material worn or held",
            "Complete spatial composition: foreground, midground, background architecture/nature, and camera angle",
            "Physical lighting source (e.g. sunlight through window, neon streetlamp) without describing the artistic rendering technique",
            "All non-watermark visible text verbatim inside double quotes \"...\""
        },
        SystemPromptTemplate = """
            You are captioning a training image for a **MOSS-Image (5B DiT + Qwen3.5-9B encoder) Style / Artist LoRA**.
            - **Style Trigger Word**: `{{trigger_word}}` (uses MOSS-Image's `@` artist prefix convention and stays pinned as the style anchor).
            - **MOSS-Image Style LoRA Rules**:
              1. **Strict Style Stripping**: Do NOT mention art style, medium, linework, shading method, brushwork, or aesthetic genre in either tags or natural language prose. All stylistic qualities must be absorbed into `{{trigger_word}}`.
              2. **Exhaustive Content Disentanglement**: Describe every concrete content element in high detail—character identity/series, hair color & style, eye color, facial expression, full outfit breakdown, physical pose, spatial layout (foreground/midground/background), and physical light sources.
              3. **Exact Visible Text**: Preserve any non-watermark visible text (Chinese/English/Japanese) verbatim inside double quotes `"..."` without translating.
              4. **Watermark Immunity**: Never mention watermarks, signatures, or borders anywhere in the output.
            """
    };

    public static CaptionPreset CreateAnimaGeneralPreset() => new()
    {
        Id = "anima-general-hybrid",
        Name = "Anima · 通用混合标注 (General Hybrid)",
        Description = "Anima 官方标准混合标注：遵循 Quality/Meta/Safety -> Character -> Series -> @Artist -> General 标签排序 + 单行自然语言尾段。",
        ModelFamily = TargetModelFamily.Anima,
        TrainingIntent = LoraTrainingIntent.General,
        CaptionMode = CaptionMode.Hybrid,
        HybridProfile = HybridProfile.AnimaStandard,
        QualityPrefixTags = new List<string> { "masterpiece", "best quality", "safe" },
        MaxTokenBudget = 512,
        ForceSingleLineOutput = true,
        AutoStripCoreHairEyeInCharacterLora = false,
        PreserveExactVisibleTextInQuotes = true,
        ExcludeWatermarksAndSignatures = true
    };

    public static CaptionPreset CreateMossGeneralPreset() => new()
    {
        Id = "moss-general-hybrid",
        Name = "MOSS-Image · 通用混合/长描述 (General Recaptioner)",
        Description = "MOSS-Image 官方标准混合标注：支持 4K Token 预算、@artist 前缀、双语文字原样双引号转录与水印过滤。",
        ModelFamily = TargetModelFamily.MossImage,
        TrainingIntent = LoraTrainingIntent.General,
        CaptionMode = CaptionMode.Hybrid,
        HybridProfile = HybridProfile.CompactTagsRichProse,
        QualityPrefixTags = new List<string> { "best quality", "highres" },
        MaxTokenBudget = 4096,
        ForceSingleLineOutput = false,
        AutoStripCoreHairEyeInCharacterLora = false,
        PreserveExactVisibleTextInQuotes = true,
        ExcludeWatermarksAndSignatures = true
    };

    /// <summary>
    /// Loads all presets (built-in presets + any custom <c>*.json</c> files in <see cref="UserPresetsDirectory"/>).
    /// Also writes the 4 core presets as editable JSON files on first run so users can inspect or tweak them.
    /// </summary>
    public static List<CaptionPreset> LoadAllPresets(string? customDirectory = null)
    {
        string dir = string.IsNullOrWhiteSpace(customDirectory) ? UserPresetsDirectory : customDirectory;
        var map = new Dictionary<string, CaptionPreset>(StringComparer.OrdinalIgnoreCase);

        foreach (var builtin in GetBuiltInPresets())
            map[builtin.Id] = builtin;

        try
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                foreach (var builtin in GetBuiltInPresets())
                    SavePresetToFile(builtin, Path.Combine(dir, $"{builtin.Id}.json"));
            }
            else
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
                {
                    var loaded = LoadPresetFromFile(file);
                    if (loaded is not null && !string.IsNullOrWhiteSpace(loaded.Id))
                        map[loaded.Id] = loaded;
                }
            }
        }
        catch
        {
            // Ignore IO errors in restricted sandboxes; built-in presets remain available in memory.
        }

        return map.Values.ToList();
    }

    public static CaptionPreset? LoadPresetFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        return JsonSerializer.Deserialize<CaptionPreset>(json, JsonOpts);
    }

    public static CaptionPreset? LoadPresetFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            return null;
        string json = File.ReadAllText(filePath, Encoding.UTF8);
        return LoadPresetFromJson(json);
    }

    public static string ToJson(CaptionPreset preset) =>
        JsonSerializer.Serialize(preset, JsonOpts);

    public static void SavePresetToFile(CaptionPreset preset, string filePath)
    {
        var parent = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        string json = ToJson(preset);
        File.WriteAllText(filePath, json, Encoding.UTF8);
    }

    /// <summary>
    /// Renders the complete System Prompt section for a <see cref="CaptionPreset"/>,
    /// combining model architecture constraints, LoRA disentanglement rules, trigger word instructions,
    /// and mode formatting rules.
    /// </summary>
    public static string RenderSystemPrompt(CaptionPreset preset)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BuildModeSystemPromptLocal(preset.CaptionMode, preset.HybridProfile));
        sb.AppendLine();
        sb.AppendLine($"## Active Dynamic Training Preset: {preset.Name} (`{preset.Id}`)");
        sb.AppendLine($"- **Target Base Model Family**: `{preset.ModelFamily}` (Max Token Budget: `{preset.MaxTokenBudget}` tokens)");
        sb.AppendLine($"- **LoRA Training Objective**: `{preset.TrainingIntent}`");

        string formattedTrigger = FormatTriggerWord(preset);
        if (!string.IsNullOrWhiteSpace(formattedTrigger))
        {
            sb.AppendLine($"- **Configured Trigger Word**: `{formattedTrigger}` (will be automatically injected and positioned during post-processing)");
        }

        if (!string.IsNullOrWhiteSpace(preset.SystemPromptTemplate))
        {
            string renderedTemplate = preset.SystemPromptTemplate
                .Replace("{{trigger_word}}", string.IsNullOrWhiteSpace(formattedTrigger) ? "(auto/none)" : formattedTrigger)
                .Replace("{{model_family}}", preset.ModelFamily.ToString())
                .Replace("{{training_intent}}", preset.TrainingIntent.ToString())
                .Replace("{{strip_tags}}", string.Join(", ", preset.StripTags.Take(20)));
            sb.AppendLine();
            sb.AppendLine(renderedTemplate.Trim());
        }

        if (preset.MustOmitDimensions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Features You MUST OMIT (Bound to LoRA Weights / Forbidden):");
            foreach (var omit in preset.MustOmitDimensions)
                sb.AppendLine($"- DO NOT mention: {omit}");
        }

        if (preset.MustDescribeDimensions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Features You MUST DESCRIBE Thoroughly (Decoupled from LoRA):");
            foreach (var desc in preset.MustDescribeDimensions)
                sb.AppendLine($"- Describe in detail: {desc}");
        }

        if (preset.PreserveExactVisibleTextInQuotes)
        {
            sb.AppendLine("- **OCR & Visible Text Rule**: Preserve all non-watermark visible text (Chinese, English, Japanese, numbers) verbatim inside double quotes `\"...\"`. Never translate or romanize visible text inside the image.");
        }

        if (preset.ExcludeWatermarksAndSignatures)
        {
            sb.AppendLine("- **Watermark Rule**: Completely ignore watermarks, artist signatures, usernames, and borders. Never include `watermark` or `signature` in tags or prose.");
        }

        if (preset.ForceSingleLineOutput)
        {
            sb.AppendLine("- **Single-Line Constraint**: Never output newline breaks (`\\n`) inside `prose`; keep the entire prose tail as a single continuous paragraph so Kohya `read_caption` does not truncate lines.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Applies the preset's deterministic post-processing rules to a list of tags and prose:
    /// <list type="number">
    ///   <item><description>Strips blacklisted tags, character core identity traits (in Character LoRA), or style/medium tags (in Style LoRA).</description></item>
    ///   <item><description>Formats artist tags with <c>@</c> prefix when required by Anima/MOSS-Image rules.</description></item>
    ///   <item><description>Injects quality prefix tags and the configured trigger word (either pinned at index 0 or in its canonical section slot).</description></item>
    ///   <item><description>Enforces single-line prose normalization and token budget checks.</description></item>
    /// </list>
    /// </summary>
    public static (List<string> FinalTags, string FinalProse, List<string> StrippedTags) ApplyPresetFilter(
        CaptionPreset preset,
        IReadOnlyList<string>? rawTags,
        string? rawProse,
        VocabDb? vocab = null,
        AppSettings? settings = null)
    {
        var inputTags = rawTags ?? Array.Empty<string>();
        var stripped = new List<string>();

        if (preset.CaptionMode == CaptionMode.ProseOnly)
        {
            string proseOnly = NormalizeProseForPreset(preset, rawProse ?? string.Empty);
            string trigger = FormatTriggerWord(preset);
            if (!string.IsNullOrWhiteSpace(trigger) &&
                !proseOnly.Contains(trigger, StringComparison.OrdinalIgnoreCase))
            {
                proseOnly = string.IsNullOrWhiteSpace(proseOnly)
                    ? trigger
                    : $"{trigger}, {proseOnly}";
            }
            return (new List<string>(), proseOnly, stripped);
        }

        var explicitStripSet = new HashSet<string>(
            preset.StripTags.Select(t => t.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var stripSectionSet = new HashSet<string>(
            preset.StripTagSections.Select(s => s.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        var filtered = new List<string>(inputTags.Count + 4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. If PinTriggerAtFront is true, insert trigger word at index 0 first
        string formattedTrigger = FormatTriggerWord(preset);
        if (preset.PinTriggerAtFront && !string.IsNullOrWhiteSpace(formattedTrigger))
        {
            if (seen.Add(formattedTrigger))
                filtered.Add(formattedTrigger);
        }

        // 2. Insert configured quality prefix tags if missing
        foreach (var qTag in preset.QualityPrefixTags)
        {
            var cleanQ = qTag.Trim();
            if (cleanQ.Length > 0 && seen.Add(cleanQ))
                filtered.Add(cleanQ);
        }

        // 3. Filter and normalize candidate tags
        foreach (var raw in inputTags)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var tag = raw.Trim().Trim(',');
            if (tag.Length == 0)
                continue;

            // Never strip the configured trigger word itself
            if (!string.IsNullOrWhiteSpace(formattedTrigger) &&
                (string.Equals(tag, formattedTrigger, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tag, preset.TriggerWord.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                if (seen.Add(formattedTrigger))
                    filtered.Add(formattedTrigger);
                continue;
            }

            // Strip Anima dead-weight score tags if model family is Anima
            if (preset.ModelFamily == TargetModelFamily.Anima &&
                tag.StartsWith("score_", StringComparison.OrdinalIgnoreCase))
            {
                stripped.Add(tag);
                continue;
            }

            // Check explicit strip_tags blacklist
            if (explicitStripSet.Contains(tag))
            {
                stripped.Add(tag);
                continue;
            }

            // Check Character LoRA core hair/eye auto-stripping
            if (preset.TrainingIntent == LoraTrainingIntent.CharacterLora &&
                preset.AutoStripCoreHairEyeInCharacterLora &&
                StandardCharacterIdentityTraits.Contains(tag))
            {
                stripped.Add(tag);
                continue;
            }

            // Check Style LoRA medium/rendering auto-stripping
            if (preset.TrainingIntent == LoraTrainingIntent.StyleLora &&
                StandardStyleAndMediumTags.Contains(tag))
            {
                stripped.Add(tag);
                continue;
            }

            // Check section-based stripping (e.g. strip 'artist' section in Style LoRA)
            var sectionKey = TagSections.Classify(tag, vocab);
            if (stripSectionSet.Count > 0 && !string.IsNullOrEmpty(sectionKey) &&
                stripSectionSet.Contains(sectionKey))
            {
                stripped.Add(tag);
                continue;
            }

            // Format artist tags with '@' prefix for MOSS-Image / Anima when enabled
            if (preset.PrefixStyleTriggerWithAt &&
                string.Equals(sectionKey, "artist", StringComparison.OrdinalIgnoreCase) &&
                !tag.StartsWith('@'))
            {
                tag = "@" + tag;
            }

            if (seen.Add(tag))
                filtered.Add(tag);
        }

        // 4. Ensure trigger word is present even if not pinned at index 0
        if (!preset.PinTriggerAtFront && !string.IsNullOrWhiteSpace(formattedTrigger) && seen.Add(formattedTrigger))
        {
            filtered.Add(formattedTrigger);
        }

        // 5. Order tags according to canonical section order while respecting pinned front trigger
        var formatOpts = ToFormatOptions(preset);
        var ordered = OrderTagsForPreset(filtered, preset, vocab, settings, formatOpts);

        // 6. Format prose according to mode and single-line constraint
        string finalProse = preset.CaptionMode == CaptionMode.TagsOnly
            ? string.Empty
            : NormalizeProseForPreset(preset, rawProse ?? string.Empty);

        return (ordered, finalProse, stripped);
    }

    /// <summary>
    /// Applies preset filtering directamente to an <see cref="AgentProposal"/> before displaying or saving it.
    /// </summary>
    public static AgentProposal ApplyPresetToProposal(
        CaptionPreset preset,
        AgentProposal proposal,
        VocabDb? vocab = null,
        AppSettings? settings = null)
    {
        var modeEnforced = AgentKit.EnforceProposalMode(proposal, preset.CaptionMode).Proposal;
        var (finalTags, finalProse, stripped) = ApplyPresetFilter(
            preset,
            modeEnforced.AddTags,
            modeEnforced.Prose,
            vocab,
            settings);

        string note = modeEnforced.Note ?? string.Empty;
        if (stripped.Count > 0)
        {
            string strippedSummary = $"[Preset '{preset.Id}' auto-stripped {stripped.Count} tag(s): {string.Join(", ", stripped.Take(8))}]";
            note = string.IsNullOrWhiteSpace(note) ? strippedSummary : $"{note} {strippedSummary}";
        }

        return new AgentProposal(
            AddTags: finalTags,
            Known: modeEnforced.Known.Where(k => finalTags.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList(),
            Unknown: modeEnforced.Unknown.Where(u => finalTags.Contains(u, StringComparer.OrdinalIgnoreCase)).ToList(),
            Prose: finalProse,
            ReplaceProse: modeEnforced.ReplaceProse,
            Note: note,
            RemoveTags: modeEnforced.RemoveTags,
            SaveToDisk: modeEnforced.SaveToDisk);
    }

    public static CaptionFormatOptions ToFormatOptions(CaptionPreset preset) => new(
        Mode: preset.CaptionMode,
        HybridProfile: preset.HybridProfile,
        TagSeparator: ", ",
        HybridDelimiter: ". ",
        EnforceAnimaSectionOrder: true,
        PinTriggerWordsFirst: preset.PinTriggerAtFront && !string.IsNullOrWhiteSpace(preset.TriggerWord),
        PinnedTriggerCount: preset.PinTriggerAtFront && !string.IsNullOrWhiteSpace(preset.TriggerWord) ? 1 : 0,
        EscapeParentheses: false);

    public static string FormatTriggerWord(CaptionPreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.TriggerWord))
            return string.Empty;

        string raw = preset.TriggerWord.Trim();
        if (preset.TrainingIntent == LoraTrainingIntent.StyleLora &&
            preset.PrefixStyleTriggerWithAt &&
            !raw.StartsWith('@'))
        {
            return "@" + raw;
        }

        if (preset.TrainingIntent == LoraTrainingIntent.CharacterLora &&
            !preset.PrefixStyleTriggerWithAt &&
            raw.StartsWith('@'))
        {
            return raw.TrimStart('@');
        }

        return raw;
    }

    private static List<string> OrderTagsForPreset(
        List<string> tags,
        CaptionPreset preset,
        VocabDb? vocab,
        AppSettings? settings,
        CaptionFormatOptions formatOpts)
    {
        if (tags.Count <= 1)
            return tags;

        string trigger = FormatTriggerWord(preset);
        int pinCount = preset.PinTriggerAtFront && !string.IsNullOrWhiteSpace(trigger) &&
                       string.Equals(tags[0], trigger, StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;

        var pinned = tags.Take(pinCount).ToList();
        var rest = tags.Skip(pinCount).ToList();

        var scored = new List<(string Tag, int Bucket, int OriginalIdx)>(rest.Count);
        for (int i = 0; i < rest.Count; i++)
        {
            string t = rest[i];
            int bucket = GetPresetTagBucket(t, preset, vocab, settings);
            scored.Add((t, bucket, i));
        }

        var orderedRest = scored
            .OrderBy(x => x.Bucket)
            .ThenBy(x => x.OriginalIdx)
            .Select(x => x.Tag)
            .ToList();

        var finalList = new List<string>(tags.Count);
        finalList.AddRange(pinned);
        finalList.AddRange(orderedRest);
        return finalList;
    }

    private static int GetPresetTagBucket(
        string tag,
        CaptionPreset preset,
        VocabDb? vocab,
        AppSettings? settings)
    {
        string lower = tag.ToLowerInvariant();
        string trigger = FormatTriggerWord(preset);

        // 0: Quality / Score / Meta / Safety
        if (preset.QualityPrefixTags.Contains(tag, StringComparer.OrdinalIgnoreCase) ||
            lower is "masterpiece" or "best quality" or "highres" or "absurdres" or "safe" or "general" or "sensitive" or "nsfw" or "explicit" ||
            lower.StartsWith("score_", StringComparison.Ordinal))
        {
            return 0;
        }

        // In Anima Character LoRA, character count (1girl/1boy/solo) and character trigger sit right after quality/meta
        if (lower is "1girl" or "1boy" or "2girls" or "2boys" or "multiple girls" or "multiple boys" or "solo")
            return 1;

        if (!string.IsNullOrWhiteSpace(trigger) && string.Equals(tag, trigger, StringComparison.OrdinalIgnoreCase))
        {
            return preset.TrainingIntent == LoraTrainingIntent.StyleLora ? 4 : 2;
        }

        if (tag.StartsWith('@'))
            return 4;

        var sec = TagSections.Classify(tag, vocab);
        return sec.ToLowerInvariant() switch
        {
            "quality" or "meta" or "period" or "safety" or "count" => 0,
            "character" => 2,
            "series" => 3,
            "artist" => 4,
            _ => 5
        };
    }
    /// <summary>简明的输出格式说明（替代升级包的 ModeCaptioningKit.BuildModeSystemPrompt）。</summary>
    private static string BuildModeSystemPromptLocal(CaptionMode mode, HybridProfile profile) => mode switch
    {
        CaptionMode.TagsOnly =>
            "## Output format: TAGS ONLY\nEmit ONLY comma-separated Danbooru tags. There is no natural-language part.",
        CaptionMode.ProseOnly =>
            "## Output format: PROSE ONLY\nEmit ONLY a natural-language description. Do NOT emit comma-separated tags.",
        _ => profile == HybridProfile.CompactTagsRichProse
            ? "## Output format: HYBRID (compact tags + rich prose)\nKeep tags to identity / core appearance; put the detail into the prose paragraph."
            : "## Output format: HYBRID (Anima standard)\nStructured tags first, then ONE natural-language paragraph."
    };

    private static string NormalizeProseForPreset(CaptionPreset preset, string prose)
    {
        if (string.IsNullOrWhiteSpace(prose))
            return string.Empty;

        if (!preset.ForceSingleLineOutput)
            return prose.Trim();

        var lines = prose.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", lines.Select(l => l.Trim()).Where(l => l.Length > 0));
    }
}
