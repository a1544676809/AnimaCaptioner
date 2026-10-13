namespace AnimaCaptioner.Core;

/// <summary>
/// 词库的上游素材清单。
///
/// 词库不是从某一个仓库"下载"来的——Anima 官方**根本没有发布标签表**，模型卡只规定
/// 规则（下划线换成空格、画师加 <c>@</c>、<c>score_*</c> 例外），不给名单。所以词库是
/// 下面这些来源合并出来的，每个来源的许可还不一样。
///
/// 这个清单同时被三处使用：设置页的构建页（告诉用户缺哪个文件）、构建器（决定读哪些
/// 文件）、README（交代出处与许可）。写在一处是为了防止三处各说一套。
/// </summary>
public static class VocabSources
{
    /// <summary>一个上游素材文件。</summary>
    /// <param name="FileName">放进素材目录时用的文件名（构建器按名字查找，不能改）。</param>
    /// <param name="What">它提供什么。</param>
    /// <param name="Upstream">上游仓库 / 接口。</param>
    /// <param name="Url">下载地址。</param>
    /// <param name="License">许可。</param>
    /// <param name="Paginated">
    /// 该地址是**分页**的 JSON 数组接口，必须翻页抓完。
    ///
    /// 这一项不能省：Danbooru 的 <c>tag_aliases.json</c> 有 4 万多条，单次请求最多给
    /// 1000 条。只抓第一页不会报任何错，只会让改名表少掉 97%——表现为"旧写法查不到新标签"，
    /// 属于静默降级，比直接失败难查得多。
    /// </param>
    public sealed record File(
        string FileName, string What, string Upstream, string Url, string License,
        bool Paginated = false);

    /// <summary>
    /// ffdkj 的全量中英对照表。词库的**主体**（33.1 万条），也是唯一逐日更新的来源。
    /// </summary>
    public const string BulkDb = "tag.sqlite";

    /// <summary>Anima 自己的标签索引。判定"这个词是不是合法标签"的权威依据。</summary>
    public const string AnimaIndexCsv = "anima-1.0.csv";

    /// <summary>Danbooru 活跃改名表（旧写法 → 现用标签）。</summary>
    public const string AliasesJson = "db_aliases_active.json";

    /// <summary>Danbooru 废弃标签表。</summary>
    public const string DeprecatedJson = "db_tags_deprecated.json";

    /// <summary>画师下架清单。</summary>
    public const string BannedCsv = "banned_tags.csv";

    /// <summary>社区精选包（Qiongyi44）里的文件名，去掉 .json 就是 pack id。</summary>
    public static readonly string[] PackFiles =
    {
        "anima.json", "base.json", "characters.json",
        "clothing.json", "poses.json", "camera.json"
    };

    private const string Ffdkj =
        "https://raw.githubusercontent.com/ffdkj/ffdkj-Danbooru_Tag-Chinese-English-Translation-Table/main/";

    private const string Qiongyi =
        "https://raw.githubusercontent.com/Qiongyi44/ComfyUI-Bilingual-Prompt-Inspector/main/data/packs/";

    private const string GoodAnima =
        "https://raw.githubusercontent.com/ShiroEirin/comfyui-good-anima/main/danbooru-tags/";

    /// <summary>
    /// 全部素材。顺序即"该先下哪个"——大文件在前，缺了它整个词库没有意义。
    /// </summary>
    public static readonly File[] All =
    {
        new(BulkDb,
            "Danbooru 全量标签中英对照表（33.1 万条，词库主体）",
            "ffdkj/ffdkj-Danbooru_Tag-Chinese-English-Translation-Table",
            Ffdkj + BulkDb,
            "MIT"),

        new(AnimaIndexCsv,
            "Anima 官方标签索引（10.8 万条；Anima 不用 Danbooru 的标签集，这是判定的权威）",
            "ShiroEirin/comfyui-good-anima",
            GoodAnima + AnimaIndexCsv,
            "GPL-3.0"),

        new(BannedCsv,
            "画师下架清单（只做提示，不删除标签）",
            "ShiroEirin/comfyui-good-anima",
            GoodAnima + BannedCsv,
            "GPL-3.0"),

        new(AliasesJson,
            "Danbooru 活跃改名表（旧写法仍能查到新标签；分页抓完约 4.1 万条）",
            "Danbooru 公开 API",
            "https://danbooru.donmai.us/tag_aliases.json?search[status]=active&limit=1000&page={0}",
            "事实数据",
            Paginated: true),

        new(DeprecatedJson,
            "Danbooru 已废弃标签表（分页抓完约 4 千条）",
            "Danbooru 公开 API",
            "https://danbooru.donmai.us/tags.json?search[is_deprecated]=true&limit=1000&page={0}",
            "事实数据",
            Paginated: true),
    };

    /// <summary>社区精选包的清单项（六个包，许可统一 MIT）。</summary>
    public static IEnumerable<File> PackSourceFiles() =>
        PackFiles.Select(f => new File(
            f,
            "社区精选词条（带中文分类，优先级高于全量表的翻译）",
            "Qiongyi44/ComfyUI-Bilingual-Prompt-Inspector",
            Qiongyi + f,
            "MIT"));

    /// <summary>构建时必须存在的文件；缺一个就没法构建。</summary>
    public static IEnumerable<string> RequiredFiles() =>
        new[] { BulkDb, AnimaIndexCsv }.Concat(PackFiles);

    /// <summary>缺失时只记一条警告、继续构建的文件。</summary>
    public static IEnumerable<string> OptionalFiles() =>
        new[] { AliasesJson, DeprecatedJson, BannedCsv };

    /// <summary>画师下架清单与 Anima 索引来自同一个 GPL-3.0 仓库——再分发时要注意。</summary>
    public const string CopyleftNote =
        "「Anima 官方标签索引」与「画师下架清单」来自 GPL-3.0 仓库 ShiroEirin/comfyui-good-anima。" +
        "本软件本身也是 GPL-3.0，所以兼容；但若单独再分发构建出来的词库，请一并遵守其条款。";
}
