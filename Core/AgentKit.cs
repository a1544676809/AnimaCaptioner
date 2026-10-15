using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimaCaptioner.Core;

/// <summary>下发给服务端的一次工具声明。</summary>
public sealed record AgentTool(string Name, string Description, JsonObject Parameters);

/// <summary>
/// agent 视角下的一张图。刻意只带它做判断需要的字段——把整个数据集对象交出去
/// 既浪费上下文，也会让模型开始"引用"它不该碰的内部状态。
/// </summary>
/// <param name="Name">文件名（显示名，不带扩展名）。也是切图的键。</param>
/// <param name="Index">在列表里的位置（从 0 起）。</param>
/// <param name="Labeled">是否已经有 caption。</param>
/// <param name="TagCount">已有标签数。</param>
/// <param name="IsCurrent">是不是当前正在编辑的那张。</param>
/// <param name="Tags">
/// 这张图**当前已有哪些标签**（按原顺序，字面量）。只有"当前那张"会被填上，
/// 其余的留 null——把整个数据集的标签都列出来既浪费上下文，也不是模型要的信息。
///
/// 这一项是补上的：原先只有 <see cref="TagCount"/>，于是模型**知道有 16 个标签、
/// 却不知道是哪 16 个**。用户说"把标签覆盖成这三条"时它连该删哪些都说不出来，
/// 只能一边往 add_tags 里塞、一边在 note 里写"已替换"——实测就是这么发生的
/// （见 tests/OverwriteProbe.cs）。
/// </param>
public sealed record AgentImage(string Name, int Index, bool Labeled, int TagCount,
                                bool IsCurrent = false,
                                IReadOnlyList<string>? Tags = null);

/// <summary>对话历史里的一轮。带图的那一轮由调用方用 <see cref="LlmClient.UserMessage"/> 组装。</summary>
public sealed record AgentTurn(bool FromUser, string Text);

/// <summary>
/// 模型提出的改动。**它是提案，不是改动**——真正写入由用户在界面上点一下才发生。
///
/// <see cref="Known"/> / <see cref="Unknown"/> 是**代码**算出来的，不是模型说的：
/// 判据是词库认不认得（<see cref="TagSections.IsKnownTag"/>），和中间那个标签列表
/// 用的是同一条规则。词库不是全集（实测 679 个真实标签里 74 个查不到），所以
/// Unknown **不丢弃**、只在界面上标出来——静默改掉用户的写法等于偷改训练集
/// （见 <see cref="TagEdit"/> 的类注释），这一条比"看起来干净"重要得多。
///
/// <param name="RemoveTags">
/// 要从提示词里**删掉**的标签（<see cref="Remove"/>，永不为 null）。
///
/// 这个通道是补上的，来历值得记：原先只有 <c>add_tags</c>，于是用户说"把标签覆盖成
/// 这三条"时，模型**根本没有手段表达"删掉那些"**——它只能往里加，然后如实与否全看
/// 它自己的措辞。实测（tests/OverwriteProbe.cs，本地模型）：它发出
/// <c>{"add_tags":["solo","smile","upper body"]}</c>，note 写"将标签替换为用户指定的
/// 三个标签"，而落地的结果是 16 个现有标签一个没少、三个新标签里三个都已在列表里，
/// **列表看起来完全没变**，用户看到的却是"它说覆盖了"。
///
/// 用"删哪些 + 加哪些"而不是"直接给出最终列表"（<c>set_tags</c>），是因为后者一旦模型
/// 少写一个标签就会**静默清掉**一条真实标注；而逐条列出的删除在提案卡片上看得见、
/// 可以核对，漏了也只是"没删干净"，不会毁掉东西。这个取舍与"Unknown 不丢弃"同源。
/// </param>
/// <param name="SaveToDisk">
/// 模型是否要求把结果**写进磁盘**（它调了 save_caption）。
///
/// 这个标记与"谁在什么时候写"是分开的，因为**写盘必须发生在提案落进编辑器之后**：
/// 模型在循环里调 save_caption 那一刻，编辑器里还是旧内容（提案要等循环返回、
/// 由界面应用，见 MainWindow 的 ApplyAgentProposal）。所以工具只表达**意图**，
/// 真正的写在应用之后由界面执行——否则存下去的是上一次的内容，
/// 而它看起来"保存成功"了。
///
/// 实测踩到过：完全权限下让助手"改完保存"，模型没能把两件事串起来
/// （propose 一调循环就结束，save 排不到），文件一个字都没变。
/// </param>
public sealed record AgentProposal(
    IReadOnlyList<string> AddTags,
    IReadOnlyList<string> Known,
    IReadOnlyList<string> Unknown,
    string Prose,
    bool ReplaceProse,
    string Note,
    IReadOnlyList<string>? RemoveTags = null,
    bool SaveToDisk = false)
{
    public bool HasTags => AddTags.Count > 0;
    public bool HasProse => Prose.Length > 0;

    /// <summary>要移除的标签。**永不为 null**——调用方不必到处判空。</summary>
    public IReadOnlyList<string> Remove => RemoveTags ?? Array.Empty<string>();

    public bool HasRemovals => Remove.Count > 0;

    public bool IsEmpty => !HasTags && !HasProse && !HasRemovals;
}

/// <summary>
/// AI 助手的**纯逻辑**部分：工具声明、系统提示、把模型的 <c>propose</c> 参数
/// 解析成一条可核对的提案。全是不碰网络、不碰界面的函数，所以 net10 回归
/// （tests/Agent.cs）能直接对着真实报文形状断言。
/// </summary>
public static class AgentKit
{
    public const string SearchToolName = "search_tags";
    public const string ProposeToolName = "propose";
    public const string WebSearchToolName = "web_search";
    public const string CurrentImageToolName = "current_image";
    public const string SelectImageToolName = "select_image";
    public const string ImageInfoToolName = "image_info";
    public const string ViewRegionToolName = "view_region";
    public const string SaveToolName = "save_caption";
    public const string LabelImagesToolName = "label_images";
    public const string ListTagsToolName = "list_tags";
    public const string SetTagSectionToolName = "set_tag_section";
    public const string SortTagsToolName = "sort_tags";
    public const string AddTagsToImagesToolName = "add_tags_to_images";

    /// <summary>九个大类的 `key(中文标题)` 清单，塞进工具描述里让模型知道有哪些可选值。</summary>
    public static string SectionCatalog() =>
        string.Join(", ", TagSections.All.Select(s => $"{s.Key}({s.Title})"));

    /// <summary>
    /// 设置页「工具」那一页的清单：每个工具一行。
    /// </summary>
    /// <param name="Name">工具名（存进 <see cref="AppSettings.DisabledTools"/> 的就是它）。</param>
    /// <param name="Title">给人看的中文名。</param>
    /// <param name="Detail">它是干什么的、什么时候会被调用。</param>
    /// <param name="Requires">
    /// 前提条件的人话说明；空串 = 没有前提。**前提没满足时这个工具本来就不会下发**
    /// （不发一个注定失败的工具），设置页要把这件事写出来，否则用户会以为勾了就生效。
    /// </param>
    /// <param name="Required">
    /// true = 关掉它助手基本就没用了（`propose` 是唯一能改动提示词的途径）。
    /// 仍然允许关（用户要"自由选择"），但设置页会给出警告。
    /// </param>
    public sealed record AgentToolInfo(string Name, string Title, string Detail,
                                       string Requires, bool Required = false);

    /// <summary>
    /// 全部工具。顺序即设置页里的显示顺序（按用途分组）。
    /// **这份清单是设置页与过滤逻辑的唯一来源**：漏写一个工具，它就会永远无法关闭
    /// ——而那种缺失在界面上看不出来（列表里就是少一行）。
    /// </summary>
    public static readonly AgentToolInfo[] ToolCatalog =
    {
        new(ProposeToolName, "提出改动（propose）",
            "助手唯一的改动途径：把要加的标签、要删的标签、英文散文交出来。" +
            "关掉它，助手就只能回答问题、不能改提示词。", "", Required: true),
        new(SearchToolName, "查词库（search_tags）",
            "在本地标签词库里检索，确认标签名拼写正确、拿到中文释义。", ""),
        new(WebSearchToolName, "联网搜索（web_search）",
            "遇到不认识的标签时上网查一下。", "需要先在「联网搜索」页启用搜索后端"),
        new(CurrentImageToolName, "查当前图片（current_image）",
            "读当前这张图的文件名、已有标注、以及数据集里还有哪些图。", "需要打开一个数据集（标注模式）"),
        new(ImageInfoToolName, "查图片信息（image_info）",
            "读分辨率、宽高比等，用来判断构图。", "需要打开一个数据集（标注模式）"),
        new(SelectImageToolName, "切图（select_image）",
            "切到数据集里的另一张图。", "需要打开一个数据集（标注模式）"),
        new(ViewRegionToolName, "放大细节（view_region）",
            "把图裁一块、放大后再看，用来确认小配饰之类的细节。", "需要这次对话带了图"),
        new(ListTagsToolName, "读标签清单（list_tags）",
            "按顺序列出提示词里的标签，带每个的大类、词库认不认得、类型是不是手改的。",
            "需要开启「完全权限」"),
        new(SetTagSectionToolName, "定标签类型（set_tag_section）",
            "给标签指定大类。**词库里没有的自定义标签只能靠它定类型**。",
            "需要开启「完全权限」"),
        new(SortTagsToolName, "规整顺序（sort_tags）",
            "按官方大类顺序重排标签（段内顺序不动，散文尾巴不碰）。", "需要开启「完全权限」"),
        new(LabelImagesToolName, "批量打标（label_images）",
            "一次给整个数据集打标，无人值守跑完。", "需要打开一个数据集（标注模式）"),
        new(AddTagsToImagesToolName, "批量补标签（add_tags_to_images）",
            "给多张图统一补几个标签：只加缺的那些，已有的不重复、英文描述不动。" +
            "不读图也不叫模型，所以是瞬间完成的纯文本操作。",
            "需要打开一个数据集（标注模式）"),
        new(SaveToolName, "写盘（save_caption）",
            "把改动写进 .txt 文件。", "需要开启「完全权限」"),
    };

    /// <summary>这个工具被用户在设置里关掉了吗。</summary>
    public static bool IsDisabled(AppSettings cfg, string name) =>
        cfg.DisabledTools.Any(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>按用户的开关过滤一遍（前提条件由调用方的 flag 负责）。</summary>
    private static List<AgentTool> ApplyDisabled(AppSettings? cfg, IEnumerable<AgentTool> tools) =>
        cfg is null ? tools.ToList() : tools.Where(t => !IsDisabled(cfg, t.Name)).ToList();

    /// <summary>
    /// 设置页里的默认值：`propose` 与查词库不给关（关掉助手就没用了），
    /// 但**仍然允许**——用户要的是"自由选择"，我们只把后果说清楚。
    /// </summary>
    public static bool IsRequired(string name) =>
        ToolCatalog.FirstOrDefault(t => t.Name == name)?.Required == true;

    /// <summary>
    /// 写：**给多张图统一补几个标签**（不读图、不叫模型，纯文本操作）。
    ///
    /// 补的是助手自己指出过的缺口："`label_images` 只能选未标注或全部重做，
    /// 没法只往里塞几个标签；而逐张修改一次只能改当前这一张"——于是"给所有缺
    /// `you chan` 的图补上"原本只能一张一张来。
    /// </summary>
    public static AgentTool AddTagsToImagesTool => new(
        AddTagsToImagesToolName,
        "Add a few tags to MANY images at once, keeping each caption's existing tags and its " +
        "English description untouched. Each image only receives the tags it is missing, so this " +
        "is the right tool for \"make sure every image has the trigger word\" / " +
        "\"给所有缺 X 的图补上\" / \"把 newest, safe 加到所有图里\". " +
        "This does NOT look at the images and does NOT call a model: it is a text edit, so it is " +
        "instant and costs nothing. Use it instead of " + LabelImagesToolName + " whenever the " +
        "user wants to ADD tags rather than re-caption; " + LabelImagesToolName + " re-reads the " +
        "images and OVERWRITES the captions, which is not what \"补上这几个标签\" means. " +
        "Only images that already have a caption are touched unless include_unlabeled is true.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["tags"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "The exact tag strings to add, e.g. [\"you chan\", " +
                                      "\"newest\", \"safe\"].",
                },
                ["include_unlabeled"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Also touch images that have no caption yet (default " +
                                      "false). Those would end up with only these few tags and " +
                                      "no description, so leave it off unless the user asks.",
                },
                ["limit"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "Maximum number of images (0 or omitted = all that need it).",
                },
            },
            ["required"] = new JsonArray { "tags" },
        });

    /// <summary>`add_tags_to_images` 解析出来的请求。</summary>
    public sealed record AddTagsRequest(IReadOnlyList<string> Tags, bool IncludeUnlabeled,
                                        int Limit, string Problem)
    {
        public bool Ok => Problem.Length == 0 && Tags.Count > 0;
    }

    /// <summary>解析 `add_tags_to_images`。</summary>
    public static AddTagsRequest ParseAddTagsRequest(string arguments)
    {
        var tags = new List<string>();
        var includeUnlabeled = false;
        var limit = 0;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new AddTagsRequest(tags, false, 0, "The arguments were not a JSON object.");

            var root = doc.RootElement;
            if (root.TryGetProperty("tags", out var arr))
            {
                if (arr.ValueKind == JsonValueKind.Array)
                    foreach (var e in arr.EnumerateArray())
                    {
                        var t = (e.ValueKind == JsonValueKind.String ? e.GetString() : null)?.Trim() ?? "";
                        if (t.Length > 0) tags.Add(t);
                    }
                else if (arr.ValueKind == JsonValueKind.String)
                {
                    // 写成 "a, b, c" 或单个标签也认（模型常这么写）
                    foreach (var piece in (arr.GetString() ?? "")
                             .Split(new[] { ',', '，', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var t = piece.Trim();
                        if (t.Length > 0) tags.Add(t);
                    }
                }
            }

            includeUnlabeled = root.TryGetProperty("include_unlabeled", out var iu) &&
                               iu.ValueKind == JsonValueKind.True;
            limit = (int)Num(root, "limit");
        }
        catch (JsonException)
        {
            return new AddTagsRequest(tags, false, 0, "The arguments were not valid JSON.");
        }

        if (tags.Count == 0)
            return new AddTagsRequest(tags, false, 0, "No tags were given.");

        // 去掉重复（大小写不敏感），保留第一次出现的顺序
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        tags = tags.Where(seen.Add).ToList();

        return new AddTagsRequest(tags, includeUnlabeled, Math.Max(0, limit), "");
    }
    ///
    /// 与 `current_image` 的分工：那个回答"这是哪张图、数据集里还有什么"，
    /// 这个回答"现在这行提示词由哪些标签组成、各自属于哪一类"。
    /// 后者是模型要动顺序或改类型时的前提——不知道类型就没法判断该排到哪。
    /// </summary>
    /// <summary>
    /// 读：当前提示词的标签清单，**带每一个的大类**。
    ///
    /// 与 `current_image` 的分工：那个回答"这是哪张图、数据集里还有什么"，
    /// 这个回答"现在这行提示词由哪些标签组成、各自属于哪一类"。
    /// </summary>
    public static AgentTool ListTagsTool => new(
        ListTagsToolName,
        "List the tags of the caption currently open in the editor, in order, each with the " +
        "section (category) it belongs to, whether the vocabulary knows it, and whether its " +
        "section was set by hand. Use this before reordering or re-typing tags. " +
        "Sections are: " + SectionCatalog() + ".",
        new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(),
                         ["required"] = new JsonArray() });

    /// <summary>
    /// 写：给标签指定大类。**这是"词库里没有的自定义标签"唯一的定类型途径**——
    /// 词库查不到时程序只能兜底成 general，模型再聪明也改不了，除非有这个工具。
    /// </summary>
    public static AgentTool SetTagSectionTool => new(
        SetTagSectionToolName,
        "Set which section (category) one or more tags belong to. Use this for tags the " +
        "vocabulary does not know (custom trigger words, made-up character names) — those " +
        "default to 'general' and there is no other way to type them. " +
        "Sections: " + SectionCatalog() + ". Pass section=\"auto\" to clear a hand-set " +
        "section and go back to the automatic guess. " +
        "This is a persistent preference (it is remembered across sessions), not a change to " +
        "the caption text, but it DOES reorder the caption so the tag lands in its section.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["tags"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "The exact tag strings, as they appear in the caption.",
                },
                ["section"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "A section key or its Chinese title (e.g. \"character\" " +
                                      "or \"角色\"), or \"auto\" to clear a hand-set section.",
                },
            },
            ["required"] = new JsonArray { "tags", "section" },
        });

    /// <summary>
    /// 写：规整大类顺序（把标签按官方大类顺序重排）。段内顺序不动。
    /// </summary>
    public static AgentTool SortTagsTool => new(
        SortTagsToolName,
        "Reorder the caption's tags into the official section order " +
        "(quality → meta → year → safety → count → character → series → artist → general), " +
        "keeping the order inside each section unchanged. The natural-language tail is not " +
        "touched. Use this after adding tags, or when the user asks to tidy/sort the tags.",
        new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(),
                         ["required"] = new JsonArray() });

    /// <summary>`set_tag_section` 解析出来的请求。</summary>
    /// <param name="Section">要设的段名（已归一成 key）；null = 清除手改。</param>
    public sealed record SectionRequest(IReadOnlyList<string> Tags, string? Section, string Problem)
    {
        public bool Ok => Problem.Length == 0 && Tags.Count > 0;
    }

    /// <summary>
    /// 把模型给的段名归一成大类的 key。接受三种写法：key（`character`）、
    /// 中文标题（`角色`）、以及"清除"的几种说法（`auto`/`none`/空）。
    ///
    /// 认不出时返回 <c>""</c>（空串）——与 null（= 明确要求清除）区分开，
    /// 这样调用方能如实告诉模型"这个名字我不认识"而不是悄悄当成清除。
    /// </summary>
    public static string? NormalizeSection(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return null;
        if (s.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
            s == "跟随词库" || s == "自动") return null;

        foreach (var sec in TagSections.All)
        {
            if (s.Equals(sec.Key, StringComparison.OrdinalIgnoreCase)) return sec.Key;
            if (s.Equals(sec.Title, StringComparison.OrdinalIgnoreCase)) return sec.Key;
        }
        return "";
    }

    /// <summary>
    /// 解析 `set_tag_section`。**段名认不出时如实报错**，不猜成 general
    /// ——猜错会把一个错误的大类**持久化**下来，而用户下次开程序才发现。
    /// </summary>
    public static SectionRequest ParseSectionRequest(string arguments)
    {
        var tags = new List<string>();
        string? section = null;
        var rawSection = "";

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new SectionRequest(tags, null, "The arguments were not a JSON object.");

            var root = doc.RootElement;
            if (root.TryGetProperty("tags", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                {
                    var t = (e.ValueKind == JsonValueKind.String ? e.GetString() : null)?.Trim() ?? "";
                    if (t.Length > 0) tags.Add(t);
                }
            else if (root.TryGetProperty("tags", out var one) && one.ValueKind == JsonValueKind.String)
            {
                // 单个标签写成字符串也认（模型常这么写）
                var t = (one.GetString() ?? "").Trim();
                if (t.Length > 0) tags.Add(t);
            }

            rawSection = Str(root, "section");
        }
        catch (JsonException)
        {
            return new SectionRequest(tags, null, "The arguments were not valid JSON.");
        }

        if (tags.Count == 0)
            return new SectionRequest(tags, null, "No tags were given.");

        section = NormalizeSection(rawSection);
        if (section is { Length: 0 })
            return new SectionRequest(tags, null,
                $"Unknown section \"{rawSection}\". Valid sections: {SectionCatalog()}, " +
                "or \"auto\" to clear.");

        return new SectionRequest(tags, section, "");
    }

    /// <summary>
    /// 批量给数据集里的图打标。**用户用一句话下令，这个循环由模型发起的工具调用驱动**
    /// ——不是让用户去菜单里点。
    ///
    /// 它是个"长工具"：一次调用可能要跑几分钟、处理几十张图，期间界面显示进度。
    /// 实现上它会**为每张图各跑一轮完整的助手循环**（同样的提示词、工具、预算），
    /// 所以批量结果与手动一张张打是同一种东西。
    ///
    /// 只在标注模式、有数据集、且当前不在批量循环里时下发——否则就是必然失败的工具，
    /// 或者自我递归（内层循环不该再有批量能力）。
    /// </summary>
    public static AgentTool LabelImagesTool => new(
        LabelImagesToolName,
        "Label images of the dataset in bulk, unattended: each selected image is read, tagged " +
        "and written to its .txt, one after another. Use this when the user asks you to label " +
        "the whole dataset or the remaining unlabeled images (\"把剩下的都标了\" / \"批量打标\" / " +
        "\"auto-caption everything\"). Do NOT call it to label a single image — for that, look at " +
        "the image and use " + ProposeToolName + " as usual.\n" +
        "scope=\"unlabeled\" (default) only touches images that have no caption yet; " +
        "scope=\"all\" REDOES every image, overwriting existing captions (irreversible — only " +
        "use it when the user explicitly asks to redo everything).\n" +
        "limit is how many images to process at most (0 = no limit). It takes minutes when the " +
        "dataset is large; the user can stop it.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["scope"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray { "unlabeled", "all" },
                    ["description"] = "\"unlabeled\" (default) skips images that already have a " +
                                      "caption; \"all\" redoes every image and overwrites.",
                },
                ["limit"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "Maximum number of images to process (0 or omitted = all).",
                },
            },
            ["required"] = new JsonArray(),
        });

    /// <summary>`label_images` 解析出来的范围。<see cref="Problem"/> 为空 = 可以用。</summary>
    public sealed record LabelRequest(string Scope, int Limit, string Problem)
    {
        public bool Ok => Problem.Length == 0;

        /// <summary>是否重做全部（会覆盖已有标注）。</summary>
        public bool RedoAll => Scope == "all";
    }

    /// <summary>
    /// 解析 `label_images` 的参数。宽容两处模型的手抖：
    /// 不写 scope（当 unlabeled——**保守的一侧**，绝不默认覆盖用户的标注）、
    /// 认不出的 scope 值（同样退回 unlabeled，而不是猜成 all）。
    /// </summary>
    public static LabelRequest ParseLabelRequest(string arguments)
    {
        var scope = "";
        var limit = 0;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new LabelRequest("unlabeled", 0, "The arguments were not a JSON object.");

            var root = doc.RootElement;
            scope = Str(root, "scope").Trim().ToLowerInvariant();
            limit = (int)Num(root, "limit");
        }
        catch (JsonException)
        {
            return new LabelRequest("unlabeled", 0, "The arguments were not valid JSON.");
        }

        if (scope.Length == 0) scope = "unlabeled";
        if (scope is not ("unlabeled" or "all"))
            scope = "unlabeled";   // 认不出就选保守那一侧，不猜成覆盖

        return new LabelRequest(scope, Math.Max(0, limit), "");
    }

    /// <summary>
    /// 把改动**写进磁盘**。
    ///
    /// 只在开了「完全权限」时下发。那个开关的语义是"让模型自己动手，不用你点确认"，
    /// 而在此之前"改完还得用户手动 Ctrl+S"——名不副实，且实测造成过误解：
    /// 用户以为自动写入即落盘，去看文件发现没变，于是"它说覆盖了其实没覆盖"。
    ///
    /// 说明里强调两件事：
    /// 1. **它落盘的是编辑器里当前那段内容**（包含你之前用 propose 写进去的），
    ///    不是"另存一份"；
    /// 2. 没把握就不要存——训练集里的 .txt 被写错是**不可逆**的（撤销只在内存里，
    ///    不会把文件回滚）。
    /// </summary>
    public static AgentTool SaveTool => new(
        SaveToolName,
        "Save the current image's caption to disk (the .txt next to the image). Until this " +
        "is called, everything you proposed lives only in the editor and the user has to press " +
        "Ctrl+S themselves. Call it once after your changes are complete, when the user asked " +
        "you to do the whole job (or agreed that you may write). Do NOT call it when you are " +
        "only proposing something for the user to review — and if you are unsure whether the " +
        "prompt is right, do not save: overwriting a training caption is not reversible from " +
        "here (Ctrl+Z only undoes the editor in memory, not the file). No arguments.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        });

    /// <summary>
    /// 问"这张图本身是什么"。**纯文本、没有 token 成本**，所以尽可以调。
    ///
    /// 它存在的理由只有一个：模型看不见原始分辨率。整图是缩到长边 1024 发过去的
    /// （实测 4000×6500 → 630×1024，细节全糊），而它**无从判断**"这个细节看不清
    /// 是因为我没放大、还是原图就这么多"。这句话告诉它，它才知道该不该调 view_region，
    /// 也才不会一遍遍重复裁同一块。
    /// </summary>
    public static AgentTool ImageInfoTool => new(
        ImageInfoToolName,
        "Get the facts about the image the user is editing right now: file name, format, file " +
        "size, the original pixel size (with the EXIF rotation applied) and how far the copy you " +
        "were given was scaled down. Call this when you are unsure whether a small detail is " +
        "worth zooming into. No arguments.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        });

    /// <summary>
    /// 把图的一块放大给模型看。
    ///
    /// **坐标一律归一化（0–1）**：模型看不到原始分辨率，让它填像素等于让它猜；
    /// 归一化之后它自己推得出 `x=0.4, w=0.2` 这种值。
    ///
    /// 结果不是文本而是**紧随其后的一条带图 user 消息**（工具结果只能是文本，
    /// 见 <see cref="TagAgent"/> 里 view_region 那段）。工具结果本身只回一句确认。
    /// 块数上限见 <see cref="ImageRegion.MaxTiles"/>，说明里写明了"块越多越贵"。
    /// </summary>
    public static AgentTool ViewRegionTool => new(
        ViewRegionToolName,
        "Zoom into the current image: the part you ask for comes back at much higher " +
        "resolution, as a new user message right after this tool result. Use it BEFORE " +
        "describing any fine detail (small text, patterns, fingers, eyes, jewellery, lace) " +
        "instead of guessing or apologising that you cannot see it.\n" +
        "mode=\"crop\": give x, y, w, h — all normalized to 0-1 of the displayed image. " +
        "x,y is the top-left corner of the region, w,h are its WIDTH and HEIGHT (not the " +
        "right/bottom edge).\n" +
        "mode=\"tiles\": give cols, rows to split the whole image into a grid, in case you " +
        "must examine all of it part by part.\n" +
        "Every image you ask for costs tokens on its own, so ask for ONE crop when one crop " +
        "is enough. With mode=\"tiles\", cols * rows must not exceed " + ImageRegion.MaxTiles +
        ". Do not ask for the same region twice.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["mode"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray { "crop", "tiles" },
                    ["description"] = "\"crop\" for one region (x, y, w, h) or \"tiles\" for a " +
                                      "grid over the whole image (cols, rows).",
                },
                ["x"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "crop only: left edge, 0-1 (0 = left edge of the image).",
                },
                ["y"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "crop only: top edge, 0-1 (0 = top of the image).",
                },
                ["w"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "crop only: width of the region, 0-1.",
                },
                ["h"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "crop only: height of the region, 0-1.",
                },
                ["cols"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "tiles only: number of columns (>= 1).",
                },
                ["rows"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "tiles only: number of rows (>= 1).",
                },
            },
            ["required"] = new JsonArray { "mode" },
        });

    /// <summary>
    /// 问"我现在在编辑哪张图"。
    ///
    /// **做成工具而不是塞进系统提示**：塞进去的话它每一轮都得重新发一遍，
    /// 而且换图之后就过时了——历史里的"当前图"两轮前就不再是当前图，
    /// 模型却会拿它当事实。按需查永远是最新的。
    /// </summary>
    public static AgentTool CurrentImageTool => new(
        CurrentImageToolName,
        "Find out which image of the dataset the user is editing right now, and how far " +
        "along it is (already captioned or not, how many tags). Call this first whenever " +
        "you are about to say something about 'this image' — do not assume it is the same " +
        "picture as in earlier turns.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        });

    /// <summary>
    /// 切到另一张图。
    ///
    /// **它会落盘一次**：编辑器换文档时一律先静默保存当前 caption（用户手动翻图也是这样，
    /// 见 MainWindow 里换图那几处的 <c>SaveCurrent(showDialog: false)</c>）。
    /// 所以工具说明里明确写了"只在用户要求时用"，避免模型拿它当"随便看看"。
    /// </summary>
    public static AgentTool SelectImageTool => new(
        SelectImageToolName,
        "Switch the editor to another image of the dataset. Give the file name as shown by " +
        "current_image (with or without the extension). Use this ONLY when the user asks to " +
        "move to another image — for example 'go to the next one' or 'open xxx'. Switching " +
        "saves the current image's caption to disk first, so do not call it just to look around.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "File name of the target image, as listed by current_image.",
                }
            },
            ["required"] = new JsonArray { "name" },
        });

    /// <summary>查词库。与 <see cref="TagSearchAgent"/> 共用同一段说明，避免两处说法不一致。</summary>
    public static AgentTool SearchTool => new(
        SearchToolName, TagSearchAgent.ToolDescription, new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "One or two words, one concept. May be English or Chinese.",
                }
            },
            ["required"] = new JsonArray { "query" },
        });

    /// <summary>
    /// 联网查一个词的意思。只在设置里开了联网搜索时下发——没配后端时下发它，
    /// 模型会调一个必然失败的工具，白烧一轮预算。
    /// </summary>
    public static AgentTool WebSearchTool => new(
        WebSearchToolName,
        "Search the web for what an image tag means. Use it only for character names, " +
        "artist names, memes, or unusual garment and pose terms you do not know. Do not " +
        "use it for tags you already know, and never use it to look for tag names.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject { ["type"] = "string", ["description"] = "The search query." }
            },
            ["required"] = new JsonArray { "query" },
        });

    /// <summary>
    /// 收尾工具。**提案走工具调用而不是解析模型的话**——小模型写的 JSON 或
    /// 列表格式极不稳定，而 tool_call 的 arguments 是服务端保证的合法 JSON。
    /// 上一轮"问模型找标签"那条路也是这么做的（可点的标签一律取自工具返回），
    /// 代价是模型没法直接把标签写进回答里，收益是错的东西进不来。
    /// </summary>
    public static AgentTool ProposeTool => new(
        ProposeToolName,
        "Propose the changes to the user's prompt. Call this EXACTLY ONCE, at the very " +
        "end, after you have finished searching. Do not describe the changes in your reply " +
        "text instead of calling this. Nothing is written to the user's prompt until they " +
        "click apply, so propose freely — but propose only what the input actually shows " +
        "or says.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["add_tags"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "Tag names to ADD to the prompt, copied exactly from " +
                                      "search_tags results. Omit tags already present in the " +
                                      "prompt. Empty array if nothing should be added.",
                },
                ["remove_tags"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = "Tag names to REMOVE from the prompt, copied exactly from " +
                                      "the list current_image gives you (the tags it already " +
                                      "has). Use this when the user asks to replace, clear or " +
                                      "rework the tags instead of adding to them: pass the ones " +
                                      "that must go here and the ones that must stay or be " +
                                      "added in add_tags. Without this the prompt can only " +
                                      "grow, so never say you replaced or removed something " +
                                      "unless you listed it here. Empty array if nothing " +
                                      "should be removed.",
                },
                ["prose"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "One English paragraph for anything that is not a tag: " +
                                      "concepts with no matching tag, and relations between " +
                                      "things. Empty string if the prose should stay as it is.",
                },
                ["replace_prose"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "true to overwrite the existing prose paragraph, false " +
                                      "to append this one after it.",
                },
                ["note"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "One short Simplified Chinese sentence saying what you " +
                                      "proposed and why.",
                },
            },
            ["required"] = new JsonArray(),
        });

    /// <summary>
    /// 本次可用工具集。顺序固定，便于测试与日志比对。
    ///
    /// <paramref name="withImages"/> 为假时（写标签模式，没有数据集）不下发那三个
    /// 依赖数据集的图片工具：让模型调一个必然失败的工具是白烧一轮预算。
    ///
    /// <paramref name="canViewRegion"/> 是**能不能真的把图放大**，与有没有数据集是
    /// 两件事：放大后的图是靠"往对话里插一条图文消息"送出去的，而这次没带图
    /// （用户关掉了「带当前图片」）时那条路走不通。这时既不能下发 view_region
    /// （它必然失败），也不该让它以为能看——那等于绕过用户明确关掉的那个开关。
    ///
    /// <paramref name="canSave"/> 对应「完全权限」。关着时**不下发** save_caption：
    /// 那个开关的意义就是"不许它自己动手"，下发一个必然被拒的工具只是白烧一轮。
    /// </summary>
    public static List<AgentTool> Tools(bool webSearch, bool withImages = false,
                                        bool canViewRegion = false, bool canSave = false,
                                        bool canBatchLabel = false, bool canEditTags = false,
                                        AppSettings? cfg = null)
    {
        var tools = new List<AgentTool>();
        if (withImages)
        {
            tools.Add(CurrentImageTool);
            tools.Add(ImageInfoTool);
        }
        tools.Add(SearchTool);
        if (webSearch) tools.Add(WebSearchTool);
        if (withImages) tools.Add(SelectImageTool);
        if (canViewRegion) tools.Add(ViewRegionTool);

        if (canEditTags)
        {
            tools.Add(ListTagsTool);
            tools.Add(SetTagSectionTool);
            tools.Add(SortTagsTool);
        }

        // 批量放在 save 之前：它们是"一次做完一大批"，语义上更接近主动作。
        // 两个批量工具同一个前提（有数据集）：都是"用户明确要求的整集操作"，
        // 所以都不额外要求完全权限。
        if (canBatchLabel)
        {
            tools.Add(AddTagsToImagesTool);
            tools.Add(LabelImagesTool);
        }
        if (canSave) tools.Add(SaveTool);
        tools.Add(ProposeTool);

        // 最后再按用户在设置里的开关过一遍：**前提**（上面那些 flag）与**意愿**
        // （这里）是两件事，两者都满足才真的下发。
        return ApplyDisabled(cfg, tools);
    }

    /// <summary>
    /// 系统提示。几条承重规则都有来历：
    ///
    /// 1. **标签名只能来自 search_tags。** Danbooru 标签是精确字符串，
    ///    `blush` 与 `blushing` 是两个不同的东西（后者根本不存在）。这条沿用
    ///    <see cref="TagSearchAgent"/> 的结论。
    /// 2. **没有对应标签的概念写进散文，而不是编一个标签。** 这是"找不到对应
    ///    概念就用自然语言表达"那条需求；但**判据不能是"词库认不认得"**——
    ///    画师触发词 `@syrstyle`、`2 fingers`、`bangs` 词库都不认，却必须是标签
    ///    （画师词不带 `@` 官方说效果非常弱，见 docs/09）。所以这里按**形状**分：
    ///    单词/短语 → 标签；成句、有空间或归属关系 → 散文。
    /// 3. **`@` 开头原样留在标签里**：它已经被转义/权重那套逻辑保护，但模型很爱
    ///    把它"顺成一句话"。单独写一条。
    /// 4. **不许编。** 带图时只写看得见的；用户没说的不许补。散文那条路已经因为
    ///    "至少两句"吃过一次训练集污染（见 LlmClient.ProseSystemPrompt 的长注释），
    ///    所以这里同样把长度交给输入决定。
    /// </summary>
    public static string SystemPrompt(bool hasImage, bool canWebSearch, bool canViewRegion = false,
                                      bool canSave = false, bool canBatchLabel = false,
                                      bool canEditTags = false, AppSettings? cfg = null)
    {
        // 用户在设置里关掉的工具**不能出现在提示词里**：提示词说了而工具表里没有，
        // 模型会去调一个不存在的工具（然后拿到一条错误，白花一轮）。
        bool On(string name) => cfg is null || !IsDisabled(cfg, name);
        canWebSearch &= On(WebSearchToolName);
        canViewRegion &= On(ViewRegionToolName);
        canSave &= On(SaveToolName);
        canBatchLabel &= On(LabelImagesToolName);
        canEditTags &= On(ListTagsToolName) || On(SetTagSectionToolName) || On(SortTagsToolName);

        var sb = new System.Text.StringBuilder();
        sb.Append(
            "You are the prompt assistant inside AnimaCaptioner, a tool a Chinese-speaking " +
            "artist uses to caption training images and to write prompts for the Anima " +
            "image model.\n" +
            "A finished prompt is comma-separated English Danbooru tags, optionally " +
            "followed by one paragraph of English natural language.\n" +
            "Rules:\n" +
            "- Call search_tags for EVERY tag name before you use it. Tag names are exact " +
            "strings; a name you write from memory is probably not a real tag.\n" +
            "- Search with SHORT queries of one or two words, one concept at a time. Never " +
            "pass a whole sentence: split \"white lace dress\" into \"white dress\" and " +
            "\"lace\" and search both.\n" +
            "- If a concept has no matching tag, DO NOT invent a tag for it. Say it in the " +
            "prose paragraph in plain English instead. Relations between things (\"the cat " +
            "sits on her shoulder\"), actions in a sentence, and made-up names belong in " +
            "the prose.\n" +
            "- A single word or a short noun phrase stays a tag even when the database has " +
            "no such entry: unknown-but-tag-shaped wording (\"@syrstyle\", \"2 fingers\") " +
            "must stay in add_tags. Never rewrite those into the prose and never drop " +
            "them.\n" +
            "- Anything starting with @ is an artist trigger word. Keep it in add_tags " +
            "exactly as written.\n" +
            "- Tags are English only. The prose is English only. Your reply text is " +
            "Simplified Chinese.\n" +
            "- Do not add quality, score or safety tags (masterpiece, best quality, " +
            "score_7, safe) unless the user explicitly asks for them; the app manages " +
            "those separately.\n" +
            "- Match the length of what the user wrote. Do not pad short input out to look " +
            "fuller.\n");

        sb.Append(hasImage
            ? "- An image from the user's dataset is attached to the first message. Read it " +
              "and base the tags and prose on what you ACTUALLY see in it: subject count, " +
              "hair, eyes, expression, clothing parts, pose, framing, background, lighting. " +
              "If you cannot see the image, say so plainly instead of guessing.\n"
            : "- No image is attached in this conversation. Work only from what the user " +
              "writes; never describe a picture you have not seen.\n");

        sb.Append(
            "- NEVER invent detail. No invented colours, materials, lengths, textures, " +
            "positions, or praise such as \"adding a soft and dynamic element\". Five " +
            "accurate tags are worth more than twenty guesses.\n" +
            "- At the very end call " + ProposeToolName + " exactly once with the tags to " +
            "add and the prose paragraph. Keep your reply text to one or two short Chinese " +
            "sentences; the user reads the proposal, not the reply.\n" +
            "- The prompt can only be changed through " + ProposeToolName + ": " +
            "`add_tags` adds, `remove_tags` deletes. When the user asks to REPLACE, clear or " +
            "rework the tags (\"覆盖成\", \"换成\", \"只留\", \"原来那些不要了\"), you MUST list " +
            "the tags that have to go in `remove_tags` — otherwise they stay and the prompt " +
            "only grows. Call " + CurrentImageToolName + " first to see exactly which tags it " +
            "has. **Never say you replaced or removed something you did not put in " +
            "remove_tags**: if you only added, say you added.\n");

        // Anima 的提示词结构。**模型不可能自己知道**（这是这个模型的约定，不是通用
        // 常识），而不知道就会犯两种错：把标签按字母序或热度排，以及把画师名写成
        // 普通标签。两者都不会报错，只是出图质量变差——正是最该提前讲清的那类事。
        sb.Append(
            "- Anima reads the prompt in a FIXED order, and the app files new tags into it:\n" +
            "    quality / year / safety -> subject count (1girl, solo, 2girls) -> character " +
            "-> series -> artist -> general (appearance, clothing, expression, pose, " +
            "background, lighting).\n" +
            "  If you propose tags, keep them in that order. Do NOT sort alphabetically or " +
            "by popularity.\n" +
            "- An artist tag MUST start with '@' (e.g. '@dairi'). Never write an artist " +
            "name as a plain tag. 'score_' tags keep their underscore (score_7) -- that is " +
            "the only family where the underscore stays; everything else uses spaces, never " +
            "underscores (write 'long hair', not 'long_hair').\n" +
            "- A prompt is a comma-separated tag list, optionally followed by ONE " +
            "natural-language paragraph. Never mix prose into the tag list, and never write " +
            "a sentence as a tag.\n");

        if (!canWebSearch)
            sb.Append("- The web search tool is not available. Do not look for one.\n");

        // 放大那条路。三条都必须写，缺一条都会出现"它明明能看却不敢用/乱用"：
        //  1. 它**看不见**整图的细节（整图缩到了长边 1024），所以细处不许猜；
        //  2. 放大后的图是**另一条 user 消息**送来的——不说清，它会当成"用户又说话了"，
        //     甚至去回答那条消息；
        //  3. 每张图都单独算 token，所以别一口气要一堆。
        if (canViewRegion)
            sb.Append(
                "- Zooming: the copy of the image you were given is scaled down to 1024 px on " +
                "its long edge, so fine detail in it is genuinely lost. Before you describe " +
                "anything small (text, patterns, fingers, eyes, jewellery, fabric texture) call " +
                ViewRegionToolName + " and look at that part enlarged — never guess a detail and " +
                "never apologise for not being able to see it.\n" +
                "- The enlarged piece arrives as an extra user message, right after the tool " +
                "result. That is the app showing you what you asked for, NOT the user saying " +
                "something new: do not answer it as a message, just look at it.\n" +
                "- Every image you ask for costs tokens of its own. Prefer one crop over a grid, " +
                "and never ask for the same region twice.\n");

        // 标签清单 / 类型 / 排序。仅在允许改标签时下发。
        if (canEditTags)
        {
            var bits = new List<string>();
            if (On(ListTagsToolName))
                bits.Add("`" + ListTagsToolName + "` lists the caption's tags with the section " +
                         "each one is in");
            if (On(SetTagSectionToolName))
                bits.Add("`" + SetTagSectionToolName + "` sets which section a tag belongs to — " +
                         "use it for tags the vocabulary does not know (custom trigger words, " +
                         "made-up names), which default to \"general\" and cannot be typed any " +
                         "other way");
            if (On(SortTagsToolName))
                bits.Add("`" + SortTagsToolName + "` tidies the order (quality → meta → year → " +
                         "safety → count → character → series → artist → general; order inside " +
                         "a section is preserved)");

            sb.Append("- Reading and re-typing tags: ").Append(string.Join(". ", bits))
              .Append(".\n")
              .Append("  Sections: ").Append(SectionCatalog()).Append(".\n")
              .Append("  Setting a section is a persistent preference, so only do it when you " +
                      "are sure: read the tags first, and never guess a section for a tag you " +
                      "have not seen. The user can undo it, but they should not have to.\n");
        }

        if (canSave)
            sb.Append(
                "- Saving: you have \"full access\", so changes are written into the editor " +
                "automatically and `" + SaveToolName + "` writes them to the .txt file on disk. " +
                "Nothing is on disk until you call it. Call it once at the end when the user " +
                "asked you to do the whole job; do not call it when you are only proposing " +
                "something for review. Overwriting a training caption is NOT reversible from " +
                "here, so never save a prompt you are not confident about.\n");
        else
            sb.Append(
                "- You may NOT write files: everything you propose stays in the editor until the " +
                "user applies and saves it themselves. Never say you saved, wrote or changed a " +
                "file.\n");

        // 批量。仅在标注模式、有数据集、且不在批量循环里时才有这些工具。
        if (canBatchLabel)
            sb.Append(
                "- Bulk work on the dataset. Two different jobs — pick the right one:\n" +
                "  · `" + AddTagsToImagesToolName + "` ADDS a few tags to many images, keeping " +
                "every caption's existing tags and its description. It does not read the images " +
                "and does not call a model, so it is instant and free. Use this for \"every image " +
                "should have the trigger word\", \"给所有缺 X 的图补上\", \"把 newest, safe 加到所有图里\".\n" +
                "  · `" + LabelImagesToolName + "` RE-CAPTIONS images from scratch: each image is " +
                "read and its .txt is OVERWRITTEN. Use it only when the user wants captions " +
                "written, not when they want tags added — overwriting a caption they polished is " +
                "not what \"补上这几个标签\" means. Its scope defaults to \"unlabeled\" (only " +
                "images with no caption yet); use \"all\" ONLY when the user explicitly asks to " +
                "redo every image.\n" +
                "  Both can take a while for a large dataset; say so before you start.\n");

        return sb.ToString();
    }

    /// <summary>
    /// 把一条提案合并进一份标签列表。**纯逻辑**，所以"覆盖"这条语义能被直接断言，
    /// 不必开起界面来试（tests/Agent.cs 第 29 节）。
    ///
    /// 顺序是**先删后加**：模型偶尔会把同一个标签同时写进两边（实测），
    /// 而 <see cref="ParseProposal"/> 已经把它判给"加"，这里再保一道——
    /// 于是无论调用方按什么顺序理解，结果都一样。
    ///
    /// 大小写按 OrdinalIgnoreCase 比：`1Girl` 与 `1girl` 是同一个标签。
    /// 返回的是新列表，不改入参。
    /// </summary>
    public static (List<string> Tags, int Added, int Removed) MergeProposal(
        IReadOnlyList<string> current, AgentProposal p)
    {
        var tags = new List<string>(current);

        var removed = 0;
        foreach (var tag in p.Remove)
        {
            var hit = tags.FindIndex(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
            if (hit < 0) continue;
            tags.RemoveAt(hit);
            removed++;
        }

        var added = 0;
        foreach (var tag in p.AddTags)
        {
            if (tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))) continue;
            tags.Add(tag);
            added++;
        }

        return (tags, added, removed);
    }

    /// <summary>
    /// <see cref="RegionRequest"/> 之外的另一处参数解析：把 <c>propose</c> 的
    /// arguments 解析成提案。<paramref name="isKnown"/> 由调用方
    /// 注入（真实路径是 <see cref="TagSections.IsKnownTag"/> 配当前词库），
    /// 这样回归测试不需要词库也能验 Known/Unknown 的分流。
    ///
    /// 对模型的手抖一律宽容：<c>add_tags</c> 收数组也收"a, b, c"这样的单个字符串，
    /// 每个元素再按逗号拆一次（实测小模型很爱把多个标签塞进一个字符串），
    /// 反引号和项目符号会被剥掉。取不到 <c>prose</c> 就当没有散文。
    /// </summary>
    public static AgentProposal ParseProposal(string arguments, Func<string, bool>? isKnown)
    {
        var tags = new List<string>();
        var removes = new List<string>();
        var prose = "";
        var replace = false;
        var note = "";

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            var root = doc.RootElement;

            if (root.TryGetProperty("add_tags", out var at))
                CollectTags(at, tags);

            if (root.TryGetProperty("remove_tags", out var rt))
                CollectTags(rt, removes);

            if (root.TryGetProperty("prose", out var p) && p.ValueKind == JsonValueKind.String)
                prose = (p.GetString() ?? "").Trim();

            if (root.TryGetProperty("replace_prose", out var rp) &&
                rp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                replace = rp.GetBoolean();

            if (root.TryGetProperty("note", out var nt) && nt.ValueKind == JsonValueKind.String)
                note = (nt.GetString() ?? "").Trim();
        }
        catch (JsonException)
        {
            // 参数不是合法 JSON：当作空提案，由调用方把这条如实报出来。
            // 绝不能退回"解析模型的话"——那正是提案走工具调用要避免的东西。
        }

        // 去重按 OrdinalIgnoreCase，与列表里的"已在列表里了"判据一致
        var unique = Dedupe(tags);

        // **同一个标签既说要删又说要加**是模型的手抖（实测常见的自我矛盾）。
        // 判给"加"：删了再加等于没删，而用户多半是想让它留下，且加法本来就是
        // 原有行为——保守的那一侧留在这里，免得一次手抖把标签删掉。
        var removeUnique = Dedupe(removes)
            .Where(t => !unique.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var known = new List<string>();
        var unknown = new List<string>();
        foreach (var t in unique)
        {
            if (isKnown is not null && isKnown(t)) known.Add(t);
            else unknown.Add(t);
        }

        return new AgentProposal(unique, known, unknown, prose, replace, note, removeUnique);
    }

    /// <summary>忽略大小写去重，同时保留第一次出现的写法与顺序。</summary>
    private static List<string> Dedupe(IReadOnlyList<string> src)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<string>(src.Count);
        foreach (var t in src)
            if (seen.Add(t)) unique.Add(t);
        return unique;
    }

    /// <summary>
    /// `view_region` 的一次请求，已解析并校验过。<see cref="Problem"/> 为空 = 可以用。
    ///
    /// 特意做成"解析 + 校验"一个函数：模型的手抖（少给字段、给 0、给 4×4）与
    /// 真正的裁剪是两件事，前者该在这里被拦下并回一句话，后者在 UI 侧碰像素。
    /// </summary>
    public sealed record RegionRequest(string Mode, double X, double Y, double W, double H,
                                       int Cols, int Rows, string Problem)
    {
        public bool Ok => Problem.Length == 0;

        /// <summary>这次会产出几张图——用于给上下文设上限，见 TagAgent.MaxRegionImages。</summary>
        public int ImageCount => !Ok ? 0 : Mode == "tiles" ? Cols * Rows : 1;
    }

    /// <summary>
    /// 解析 `view_region` 的参数。
    ///
    /// 容错两处，都是实测里小模型真会犯的：
    /// 1. **不写 mode**：靠给了哪一组字段推断（有 cols/rows 就是 tiles，有 x/y/w/h 就是 crop）。
    ///    直接报"缺少 mode"会让它白烧一轮。
    /// 2. **w/h 给了负数或 0**：0 无法裁剪，如实要求重试；负数是"方向反了"的意思，
    ///    由 <see cref="ImageRegion.Crop"/> 交换两端处理，这里放行。
    /// </summary>
    public static RegionRequest ParseRegion(string arguments)
    {
        var mode = "";
        double x = 0, y = 0, w = 0, h = 0;
        int cols = 0, rows = 0;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Bad("The arguments were not a JSON object.");

            var root = doc.RootElement;
            mode = Str(root, "mode").Trim().ToLowerInvariant();
            x = Num(root, "x");
            y = Num(root, "y");
            w = Num(root, "w");
            h = Num(root, "h");
            cols = (int)Num(root, "cols");
            rows = (int)Num(root, "rows");
        }
        catch (JsonException)
        {
            return Bad("The arguments were not valid JSON.");
        }

        if (mode.Length == 0)
            mode = cols > 0 || rows > 0 ? "tiles" : "crop";

        if (mode == "crop")
        {
            if (w <= 0 || h <= 0)
                return new RegionRequest(mode, x, y, w, h, cols, rows,
                    $"x, y, w, h were x={x:0.###} y={y:0.###} w={w:0.###} h={h:0.###}, but w and h " +
                    "must both be greater than 0 — they are the size of the region, 0-1.");
            return new RegionRequest(mode, x, y, w, h, cols, rows, "");
        }

        if (mode == "tiles")
        {
            var problem = ImageRegion.TileProblem(cols, rows);
            return new RegionRequest(mode, x, y, w, h, cols, rows, problem);
        }

        return Bad($"mode was \"{mode}\", but it must be \"crop\" or \"tiles\".");
    }

    /// <summary>这次调用会要几张图（供 TagAgent 判断预算）。参数读不懂时返回 0。</summary>
    public static int PlannedRegionImages(string arguments) => ParseRegion(arguments).ImageCount;

    private static RegionRequest Bad(string why) => new("", 0, 0, 0, 0, 0, 0, why);

    private static string Str(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    /// <summary>取一个数字字段。数字、能解析成数字的字符串都收——小模型两种都写过。</summary>
    private static double Num(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        return 0;
    }

    private static void CollectTags(JsonElement el, List<string> sink)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) AddTagText(item.GetString(), sink);
                break;

            case JsonValueKind.String:
                AddTagText(el.GetString(), sink);
                break;
        }
    }

    /// <summary>
    /// 清洗一个标签字符串：剥掉反引号/项目符号/句末标点，再按逗号拆开。
    ///
    /// 要**反复**剥（最多几轮）而不是剥一次：实测模型会写成
    /// "- \`1girl\`" 或 "* solo*"，而 Trim 遇到不认识的字符就停——
    /// 只剥一次的话，先剥掉连字符后被空格挡住，前导反引号会留下来，
    /// 于是 `- \`1girl\`` 变成 "\`1girl" 这种既不是标签也不是名字的东西。
    /// </summary>
    private static void AddTagText(string? raw, List<string> sink)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        foreach (var piece in raw.Split(new[] { ',', '，', '\n', '\r' },
                                        StringSplitOptions.RemoveEmptyEntries))
        {
            var t = piece.Trim();
            for (var i = 0; i < 4; i++)
            {
                var before = t;
                t = t.Trim().Trim(TrimChars).Trim();
                if (t == before) break;
            }
            if (t.Length > 0) sink.Add(t);
        }
    }

    private static readonly char[] TrimChars =
        { '`', '"', '\'', '-', '*', '·', '。', '.', '、', ':', ';' };
}
