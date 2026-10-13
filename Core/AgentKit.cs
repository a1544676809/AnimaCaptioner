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
public sealed record AgentImage(string Name, int Index, bool Labeled, int TagCount,
                                bool IsCurrent = false);

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
/// </summary>
public sealed record AgentProposal(
    IReadOnlyList<string> AddTags,
    IReadOnlyList<string> Known,
    IReadOnlyList<string> Unknown,
    string Prose,
    bool ReplaceProse,
    string Note)
{
    public bool HasTags => AddTags.Count > 0;
    public bool HasProse => Prose.Length > 0;
    public bool IsEmpty => !HasTags && !HasProse;
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
    /// <paramref name="withImages"/> 为假时（写标签模式，没有数据集）不下发那两个
    /// 图片工具：让模型调一个必然失败的工具是白烧一轮预算。
    /// </summary>
    public static List<AgentTool> Tools(bool webSearch, bool withImages = false)
    {
        var tools = new List<AgentTool>();
        if (withImages) tools.Add(CurrentImageTool);
        tools.Add(SearchTool);
        if (webSearch) tools.Add(WebSearchTool);
        if (withImages) tools.Add(SelectImageTool);
        tools.Add(ProposeTool);
        return tools;
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
    public static string SystemPrompt(bool hasImage, bool canWebSearch)
    {
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
            "sentences; the user reads the proposal, not the reply.\n");

        if (!canWebSearch)
            sb.Append("- The web search tool is not available. Do not look for one.\n");

        return sb.ToString();
    }

    /// <summary>
    /// 把 <c>propose</c> 的 arguments 解析成提案。<paramref name="isKnown"/> 由调用方
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
        var prose = "";
        var replace = false;
        var note = "";

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            var root = doc.RootElement;

            if (root.TryGetProperty("add_tags", out var at))
                CollectTags(at, tags);

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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<string>(tags.Count);
        foreach (var t in tags)
            if (seen.Add(t)) unique.Add(t);

        var known = new List<string>();
        var unknown = new List<string>();
        foreach (var t in unique)
        {
            if (isKnown is not null && isKnown(t)) known.Add(t);
            else unknown.Add(t);
        }

        return new AgentProposal(unique, known, unknown, prose, replace, note);
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
