using System.Globalization;

namespace AnimaCaptioner.Core;

/// <summary>
/// 一个大类的定义。
/// </summary>
public sealed class TagSectionInfo
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string DefaultHex { get; init; }
    public required int Index { get; init; }
}

/// <summary>
/// Anima 的标签大类。官方模型卡（circlestone-labs/Anima，Prompting → Tag order）:
///
///   [quality/meta/year/safety tags] [1girl/1boy/1other etc] [character]
///   [series] [artist] [general tags]
///
///   "Within each tag section, the tags can be in arbitrary order."
///
/// 所以这里只固定**大类之间**的顺序；大类内部的顺序完全保留用户/翻译器给的
/// 相对次序（用稳定排序，绝不按引用数之类的启发式重排）。见 <see cref="Canonicalize"/>。
///
/// 这个文件刻意不引用任何 WinUI 类型，以便对拍/回归用的 net8 控制台程序
/// 能直接编译同一份源码（见 tools\corecheck\Sections.cs）。
/// </summary>
public static class TagSections
{
    // ---- 大类定义（顺序即官方顺序，不要随意调换）----

    public const string Meta = "meta";
    public const string Count = "count";
    public const string Character = "character";
    public const string Series = "series";
    public const string Artist = "artist";
    public const string General = "general";

    /// <summary>
    /// 不是大类，而是「自然语言」这一档的配色键。
    ///
    /// Anima 的提示词允许标签和英文散文混排（官方："You can mix tags and natural
    /// language in arbitrary order."），散文不参与标签排序，但要能一眼和标签区分开。
    /// 它没有自己的"段"（不占官方大类顺序里的位置），所以只在配色表里单列一项。
    /// </summary>
    public const string Prose = "prose";

    public static readonly TagSectionInfo[] All =
    {
        new() { Key = Meta,      Index = 0, Title = "质量 / 元信息 / 安全", DefaultHex = "#E8A33D" },
        new() { Key = Count,     Index = 1, Title = "人物数量",             DefaultHex = "#2FA39B" },
        new() { Key = Character, Index = 2, Title = "角色",                 DefaultHex = "#9B6BD6" },
        new() { Key = Series,    Index = 3, Title = "作品 / 系列",           DefaultHex = "#4A90D9" },
        new() { Key = Artist,    Index = 4, Title = "画师",                 DefaultHex = "#E0607E" },
        new() { Key = General,   Index = 5, Title = "一般标签",             DefaultHex = "#7A8B99" },
    };

    /// <summary>设置页里的全部配色项：六个大类 + 自然语言。</summary>
    public static readonly TagSectionInfo[] Colorable =
        All.Append(new TagSectionInfo
        {
            Key = Prose,
            Index = All.Length,
            Title = "自然语言（散文）",
            DefaultHex = "#8FA876",
        }).ToArray();

    /// <summary>
    /// 按 key 取大类定义。只认六个真大类——<see cref="Prose"/> 不是大类
    /// （它不占官方顺序里的位置），传进来会落到 general，因为调用方拿它做的是
    /// "这段文字属于哪个大类"的归类，而散文的答案就是"没有大类"。
    /// 配色要取散文色请用 <see cref="ColorOf"/>。
    /// </summary>
    public static TagSectionInfo Get(string key) =>
        All.FirstOrDefault(s => s.Key == key) ?? All[^1];

    /// <summary>配色用：六个大类之外还认自然语言。</summary>
    public static TagSectionInfo ColorOf(string key) =>
        Colorable.FirstOrDefault(s => s.Key == key) ?? All[^1];

    public static int IndexOf(string key) => Get(key).Index;

    /// <summary>
    /// 自然语言那一段在列表里排最后。取一个大过所有大类的值，让它自成一"段"，
    /// 免得混进 general：混进去的话它会排在 general 的行里，还可能抢到
    /// general 的段头条带，标题写成「一般标签」——那正好是我们不想暗示的事。
    /// </summary>
    public const int ProseIndex = 99;

    /// <summary>段头条带要显示的标题。散文不是大类，单独给名字。</summary>
    public static string TitleOf(int sectionIndex) =>
        sectionIndex >= ProseIndex ? ColorOf(Prose).Title : All[sectionIndex].Title;

    // ---- 归类 ----

    /// <summary>官方列出的 quality 标签（人类评分那一套）。</summary>
    private static readonly HashSet<string> QualityTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "masterpiece", "best quality", "good quality", "normal quality",
        "low quality", "worst quality", "very aesthetic",
    };

    /// <summary>官方列出的 meta 标签，以及负面提示里那几个。</summary>
    private static readonly HashSet<string> MetaTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "highres", "absurdres", "anime screenshot", "jpeg artifacts", "official art",
        "blurry", "chromatic aberration", "artist name",
    };

    /// <summary>官方列出的 time period 标签。</summary>
    private static readonly HashSet<string> PeriodTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "newest", "recent", "mid", "early", "old", "oldest",
    };

    /// <summary>官方列出的 safety 标签。questionable 是 Danbooru 的对应档，一并收进来。</summary>
    private static readonly HashSet<string> SafetyTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "safe", "sensitive", "questionable", "nsfw", "explicit",
    };

    /// <summary>
    /// 这个字符串是否是一个"能认出来的标签"。
    ///
    /// 用途是**把标签和英文散文区分开**：Anima 的提示词是两者混排
    /// （官方："You can mix tags and natural language in arbitrary order."），
    /// 而散文必须留白、不参与标签校验，也不能被染成 general 标签色。
    ///
    /// 判定顺序刻意是"先认控制词、再查词库"：官方逐条列出的控制词
    /// （masterpiece / score_7 / year 2025 / safe / 1girl / solo …）在
    /// Danbooru 上要么没有条目、要么归类与 Anima 的用法不一致。
    ///
    /// 实测（679 个真实标签 + 官方两段提示词）：含句末标点的标签 0 个、
    /// 首字母大写的标签 0 个，所以把"句子形状"的串判成散文不会误伤真标签；
    /// 而 74 个词库不认的标签（`2 fingers`、`bangs`）都是小写短语，
    /// 仍然会被当成标签保留在列表里（标成"不在任何索引中"），不会消失。
    /// </summary>
    public static bool IsKnownTag(string tag, VocabDb? vocab)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var t = tag.Trim();
        var low = t.ToLowerInvariant();

        if (QualityTags.Contains(low) || MetaTags.Contains(low) ||
            PeriodTags.Contains(low) || SafetyTags.Contains(low) ||
            CountTags.Contains(low) || IsScore(low) || IsYear(low))
            return true;

        // 画师标签必须带 @（官方规则），@ 开头即视为标签
        if (t.StartsWith('@')) return true;

        return vocab?.Status(t) is "anima" or "valid" or "retired";
    }

    /// <summary>官方第二节：1girl / 1boy / 1other 这类数量与人称标签。</summary>
    private static readonly HashSet<string> CountTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "1girl", "2girls", "3girls", "4girls", "5girls", "6+girls", "multiple girls",
        "1boy", "2boys", "3boys", "4boys", "5boys", "6+boys", "multiple boys",
        "1other", "2others", "multiple others",
        "solo", "solo focus", "male focus", "female focus", "other focus", "no humans",
    };

    /// <summary>词库里用的中文大类名，映射到官方大类。</summary>
    private static readonly Dictionary<string, string> CnCategoryMap = new(StringComparer.Ordinal)
    {
        ["质量"] = Meta,
        ["负面词"] = Meta,
        ["Anima控制词"] = Meta,
        ["画面风格"] = Meta,
        ["Anima风格"] = Meta,
        ["Anima构图"] = Meta,
        ["人物数量"] = Count,
    };

    private static bool IsScore(string low) =>
        low.Length == 7 && low.StartsWith("score_", StringComparison.Ordinal)
        && char.IsAsciiDigit(low[6]);

    private static bool IsYear(string low) =>
        low.StartsWith("year ", StringComparison.Ordinal)
        && int.TryParse(low.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var y)
        && y >= 1900 && y <= 2200;

    /// <summary>
    /// 把标签归到官方六个大类之一。
    ///
    /// 判断顺序有讲究：先认官方在模型卡里逐条列出的控制词。这些词在 Danbooru
    /// 上要么没有条目、要么归类与 Anima 的用法不一致（`sensitive` 在 Danbooru
    /// 是 general，但官方把它列在 safety 段），所以硬编码的清单优先。
    /// 之后才查词库，且 Anima 自带索引优先——它才是 Anima 认的标签集。
    /// </summary>
    public static string Classify(string tag, VocabDb? vocab)
    {
        if (string.IsNullOrWhiteSpace(tag)) return General;
        var t = tag.Trim();
        var low = t.ToLowerInvariant();

        if (QualityTags.Contains(low) || MetaTags.Contains(low) ||
            PeriodTags.Contains(low) || SafetyTags.Contains(low) ||
            IsScore(low) || IsYear(low))
            return Meta;

        if (CountTags.Contains(low)) return Count;

        // 画师标签必须带 @（官方：不加 @ 效果会非常弱），所以 @ 开头就是画师
        if (t.StartsWith('@')) return Artist;

        // 词库查不到时兜底到 general：这是最保守的选择，因为 general 段本来
        // 就是"其余全部"。实测训练集 679 个标签里有 71 个走到这里（都是
        // 颜色+名词的复合短语，本来也属于 general）。
        var cat = vocab?.CategoryOf(t) ?? "";
        return cat switch
        {
            "character" => Character,
            "copyright" => Series,
            "artist" => Artist,
            "meta" => Meta,
            "anima-cat6" => General,     // Anima 索引里自成一类的杂项，官方没有对应段
            "anima-cat-1" => General,
            _ => CnCategoryMap.TryGetValue(cat, out var m) ? m : General,
        };
    }

    /// <summary>
    /// 只按大类做**稳定**排序：大类之间按官方顺序，大类内部的相对次序原样保留。
    ///
    /// 这里刻意不按 post_count / prior 之类的启发式重排同大类内部——用户明确
    /// 要求大类内部可调，任何"顺手优化"都会把手工调好的顺序弄丢。而且稳定
    /// 排序意味着：对已经合规的 caption 调用它是恒等变换（实测训练集 53/53
    /// 都是恒等变换，这条被当成回归测试）。
    /// </summary>
    public static List<string> Canonicalize(IEnumerable<string> tags, VocabDb? vocab)
    {
        var keyed = new List<(string Tag, int Pos, int Sec)>();
        var i = 0;
        foreach (var raw in tags)
        {
            var t = (raw ?? "").Trim();
            if (t.Length == 0) continue;
            keyed.Add((t, i++, IndexOf(Classify(t, vocab))));
        }

        // 比较里带上原始位置，等于稳定排序，且不依赖 List.Sort 是否稳定
        keyed.Sort((a, b) =>
        {
            var c = a.Sec.CompareTo(b.Sec);
            return c != 0 ? c : a.Pos.CompareTo(b.Pos);
        });

        return keyed.Select(k => k.Tag).ToList();
    }

    // ---- 颜色（纯逻辑部分；画刷构造在 TagColors 里）----

    /// <summary>
    /// 把用户填的颜色串规范化成 "#RRGGBB"。接受带不带 #、6 位或 8 位（8 位丢弃
    /// alpha——大类颜色不需要透明度）。非法输入返回 null，由调用方回落默认色。
    /// </summary>
    public static string? NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6 && s.Length != 8) return null;
        foreach (var ch in s)
            if (!Uri.IsHexDigit(ch)) return null;
        // 8 位时后 6 位才是 RGB（前两位是 alpha）
        return "#" + s[^6..].ToUpperInvariant();
    }
}
