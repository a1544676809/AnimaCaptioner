using System.Text;
using System.Text.Json.Nodes;

namespace AnimaCaptioner.Core;

/// <summary>
/// AI 助手的执行体：把「当前提示词 + 可选的一张图 + 用户的一句话」变成一条可核对的
/// 提案（加哪些标签、散文怎么写）。工具循环的形状沿用
/// <see cref="TagSearchAgent"/>，但目的不同：
///
/// - TagSearchAgent 回答的是"有没有这个标签"，产出是一段给人读的话；
/// - 这里要**改东西**，所以产出必须结构化，而且**必须能核对**——
///   标签名先过词库（Known/Unknown），散文原样给出，等用户点"应用"。
///
/// 两条刻意的设计：
///
/// 1. **写入不在这个类里。** 它只产出提案。把提案落到文档上要用户点一下，
///    这样一次应用就是一个撤销点（见 MainWindow 的 ApplyAgentProposal），
///    而"模型直接改训练集"这件事永远不会在没人看着的时候发生。
/// 2. **网络与词库都从 <see cref="IHost"/> 注入。** 循环里的控制流（何时停、
///    轮数上限、未知工具名怎么办）才是这个类真正的逻辑，而它不该需要一台
///    服务器才能被测——tests/Agent.cs 用一个假 host 直接驱动它。
/// </summary>
public sealed class TagAgent
{
    /// <summary>循环需要外部提供的东西。真实实现见仓库根的 AgentHost.cs。</summary>
    public interface IHost
    {
        /// <summary>本次是否下发了联网搜索工具。</summary>
        bool CanWebSearch { get; }

        /// <summary>标签名在词库里是否存在（真实路径是 TagSections.IsKnownTag）。</summary>
        bool IsKnown(string tag);

        /// <summary>
        /// 查词库。返回值是回灌给模型的工具结果文本（<see cref="TagSearch.ToToolContent"/>），
        /// 同时把命中交出来做汇总——界面要显示"这次一共查到过哪些标签"。
        /// </summary>
        string Search(string query, out List<TagSearch.Hit> hits, out bool splitMatch);

        /// <summary>
        /// 每一轮的 max_tokens 从哪里来。**由 host 在每轮现取**，不在这里写死。
        ///
        /// 原先这里是个常量 2400，而它实际是"答案 400 + 思考余量"的合成值——
        /// 复杂打标（带图 + 十几轮查库 + 提案）会把它吃光，症状是"模型不听话"或
        /// "没提出提案"，而用户调大设置里的 max_tokens 也没用（那条路读不到它）。
        ///
        /// 现在统一走 host：真实实现从 <see cref="AppSettings.AgentMaxTokens"/> 取，
        /// **0 = 自动**（按服务端声明的能力解析，见 <see cref="LlmClient.ResolveBudget"/>）。
        /// </summary>
        int MaxTokensPerTurn { get; }

        /// <summary>
        /// 能不能用 `save_caption` 工具要求写盘。
        ///
        /// 与「完全权限」同一个开关：那个开关的语义就是"让模型自己动手，不用你点
        /// 确认"，而"改完还要手动 Ctrl+S"会让"完全权限"名不副实——实测就发生过
        /// 用户以为自动写入即落盘、而盘上文件没变的事（见 docs/12-ai-assistant.md）。
        /// 关着的时候不下发这个工具（它必然失败），符合"不发注定失败的工具"那条。
        ///
        /// 注意**只表达意图**：真正写盘由界面在提案落进编辑器之后做
        /// （<see cref="AgentProposal.SaveToDisk"/>）。
        /// </summary>
        bool CanSave { get; }

        /// <summary>
        /// 能不能用 `label_images` 批量给数据集打标。
        ///
        /// 只有标注模式、有数据集、**且当前不在批量循环里**时为真。最后一条防的是
        /// 自我递归：批量循环内部对每张图各跑一轮助手，那一轮里若再有批量工具，
        /// 模型完全可能再发起一次批量——而它会一直套下去。
        /// </summary>
        bool CanBatchLabel { get; }

        /// <summary>
        /// 跑一次批量打标。<paramref name="arguments"/> 是工具的原始参数。
        ///
        /// 返回：进度文字（界面显示用）、一段给模型看的结果说明、以及一句"用户有没有
        /// 中途叫停"。**实现必须自己把整批跑完**（界面只负责显示进度），
        /// 因为模型在等这一次工具调用的结果。
        /// </summary>
        Task<(string Message, bool Cancelled)> LabelImagesAsync(string arguments,
                                                                 CancellationToken ct);

        /// <summary>
        /// 把进度贴到界面上（"第 3/20 张，写入 2"）。批量要跑几分钟，
        /// 没有这个用户只会看到界面卡住。可以为 null（探针里不需要）。
        /// </summary>
        void ReportBatchProgress(string text);

        /// <summary>
        /// 能不能读标签清单、改标签类型、规整顺序（`list_tags` / `set_tag_section` /
        /// `sort_tags`）。与「完全权限」同一个开关。
        /// </summary>
        bool CanEditTags { get; }

        /// <summary>
        /// 当前提示词的标签清单（带每一个的大类、词库认不认得、类型是不是手改的）。
        /// 只读。
        /// </summary>
        string DescribeTags();

        /// <summary>
        /// 给标签指定大类（<paramref name="section"/> 为 null = 清除手改）。
        /// 返回：改动条数 + 一句给模型看的结果说明。
        /// </summary>
        (int Changed, string Message) SetTagSections(IReadOnlyList<string> tags, string? section);

        /// <summary>
        /// 规整大类顺序（段内顺序不动）。返回：是否真的变了 + 一句说明。
        /// </summary>
        (bool Changed, string Message) SortTags();

        /// <summary>
        /// 当前配置。**只为了让"设置里关掉的工具"能被过滤掉**：前提条件（有没有数据集、
        /// 有没有完全权限）走上面那些 flag，用户的开关走这一份。
        /// 可以是 null（探针/测试里不关心设置）——那时不过滤。
        /// </summary>
        AppSettings? Settings { get; }

        /// <summary>
        /// 给多张图统一补几个标签（**纯文本操作**：不读图、不叫模型）。
        /// 返回：真的改了几张 + 一句给模型看的结果说明。
        /// </summary>
        (int Changed, string Message) AddTagsToImages(IReadOnlyList<string> tags,
                                                     bool includeUnlabeled, int limit);

        /// <summary>
        /// 带工具的一轮对话。工具列表为空 = 这一轮没有工具可调。
        /// </summary>
        /// <param name="onProgress">
        /// 给了就要求实现走**流式**，把生成中的（已累积思考, 已累积正文）回调出来，
        /// 界面据此实时显示思考链。为 null 时行为同以前（非流式）。
        /// </param>
        Task<LlmClient.ToolTurn> TurnAsync(string systemPrompt, IReadOnlyList<AgentTool> tools,
                                           List<JsonObject> messages, int maxTokens,
                                           CancellationToken ct,
                                           Action<string, string>? onProgress = null);

        /// <summary>联网搜索。未启用时返回一句说明即可，不抛异常。</summary>
        Task<string> SearchWebAsync(string query, CancellationToken ct);

        /// <summary>
        /// 数据集里的图片（按列表顺序）。**写标签模式返回空**——那里没有图片，
        /// 相应的两个工具也就不会下发。
        /// </summary>
        IReadOnlyList<AgentImage> Images { get; }

        /// <summary>
        /// 切到指定名字的图片。返回是否成功，以及一句给模型看的结果说明。
        ///
        /// **这是本循环里唯一的写操作**（其余一切只产出提案）。它的副作用是
        /// 编辑器会先静默保存当前 caption——那本来就是换图的固有行为，
        /// 用户手动翻图也一样。所以工具说明里要求模型只在用户明确要求时调用。
        ///
        /// 返回的第三项（新那张图的字节）**是必需的，不是顺手给的**：图片只挂在
        /// 最后一条用户消息上，所以切图之后模型看到的还是旧那张——**切了也看不见**。
        /// 实测里模型因此干脆不切（切了没意义），用户看到的就是"让它逐张检查，
        /// 它只检查了当前这一张"。所以切成功的图要当成一张新图塞回对话
        /// （与 view_region 同一条路）。
        ///
        /// **为什么是异步的**：它要读盘 + 解码新那张图，而这两件事原先在 UI 线程上
        /// **同步**等（<c>GetAwaiter().GetResult()</c>）。那个 Task 需要回到 UI 线程
        /// 才能完成——自己等自己，整个界面就此卡死（托管栈实测：UI 线程停在
        /// <c>TaskAwaiter.GetResult()</c> 里，进程 CPU 归零，窗口彻底不动）。
        /// 所以读图必须 await 到后台去，UI 线程只做"校验 + 换文档"这一小段。
        /// </summary>
        Task<(bool Ok, string Message, LlmClient.ChatImage? Image)> SelectImageAsync(
            string name, CancellationToken ct);

        /// <summary>
        /// 这里能不能切图。**批量内层那一轮必须是 false。**
        ///
        /// 外层批量循环已经替它选好了这一张，模型若再切走，`propose` 落到的就是
        /// 另一张图——而外层记账还以为在改这一张。实测过这条：内层那一轮的系统提示
        /// 里没有"别自己走"那句（那一段只在批量工具下发时才拼），而 `select_image`
        /// 却照旧在工具表里，两个条件凑在一起就是一个洞。
        /// </summary>
        bool CanSwitchImages { get; }

        /// <summary>
        /// 能不能**逐张走**：模型自己用 `select_image` 一张一张看、每张 `propose` 一次，
        /// 而 <see cref="ApplyProposalNow"/> 当场把那张写进盘。
        ///
        /// 真实实现要求「完全权限」开着（逐张走的每一步都是写盘，而"不许它自己动手"
        /// 的语义下不该有这条路）。没接上实现时恒为 false——那时 `propose` 仍旧
        /// 结束整轮，模型一次只能改当前这一张。
        /// </summary>
        bool CanApplyNow { get; }

        /// <summary>
        /// 逐张走时把提案**立即**应用到当前那张图并落盘，返回一句给模型看的回执。
        ///
        /// 与"提案结束整轮、由界面在最后应用"那条路的区别就在**时机**：这里必须现在
        /// 就落到盘上，否则模型切到下一张时，这一张的改动会被下一次应用覆盖掉
        /// （编辑器只有一份文档）。只在 <see cref="CanApplyNow"/> 为真时被调用。
        /// </summary>
        (string Message, bool Ok) ApplyProposalNow(AgentProposal proposal);

        /// <summary>
        /// 当前这张图的**文件事实**（尺寸、EXIF 方向、格式、大小、被缩到多小），
        /// 说成一段给模型看的文字。**纯读、纯文本**，没有任何成本，
        /// 实现见 MainWindow 的 AgentImageInfoAsync。
        ///
        /// 是异步的：读文件与解码走 WinRT，别在 UI 线程上同步等它。
        /// </summary>
        Task<string> ImageInfo();

        /// <summary>
        /// 把一块区域放大。返回是否成功、一句给模型看的结果说明，以及**要插进对话的图**
        /// （工具结果只能是文本，图只能靠额外的一条 user 消息送出去，见 RunAsync 里那段）。
        ///
        /// 裁剪必须在 UI 侧：Core 不引用任何图像库。未启用带图时返回失败即可，
        /// 不抛异常。
        /// </summary>
        Task<(bool Ok, string Message, IReadOnlyList<LlmClient.ChatImage> Images)>
            ViewRegion(string arguments);
    }

    /// <summary>
    /// 一次工具调用的完整记录：**调了什么、参数是什么、返回了什么**。
    ///
    /// 与 <see cref="Outcome.Log"/> 的区别：那个是给人读的一句话摘要（"查「x」→ 3 条"），
    /// 这个是给界面做"可展开的调用详情"用的，所以要原文——用户要看的正是
    /// "它到底拿什么去查的、服务端到底回了什么"。
    /// </summary>
    public sealed record ToolCallLog(string Name, string Arguments, string Result);

    /// <summary>
    /// <paramref name="Proposal"/> 为 null 表示这一轮只是回答（用户在问问题，不是要改）。
    ///
    /// <paramref name="Reasoning"/> 是这一轮**所有请求**的思考内容拼接（工具循环会发
    /// 好几次请求，每次都可能有思考）。它只拿来给用户看，**绝不参与任何判定**——
    /// 判定一律看 <paramref name="Reply"/>／<paramref name="Proposal"/>。
    /// 翻译那条路已经明确不许把思考当译文（见 TagTranslator），这里是同一个道理。
    /// </summary>
    public sealed record Outcome(bool Ok, string Reply, AgentProposal? Proposal,
                                 List<TagSearch.Hit> Found, List<string> Log,
                                 int Rounds, string Error, string Reasoning = "",
                                 IReadOnlyList<ToolCallLog>? ToolCalls = null);

    /// <summary>
    /// 工具循环轮数上限。**一轮 = 一次请求**，一轮里模型可以同时发多个工具调用。
    ///
    /// 原来是 4，实测不够：一问"查 A 和 B 两个标签"，模型会一轮里发好几个
    /// search_tags，几轮下来就用光了，然后被迫走收尾那条路——用户看到的是
    /// "由于工具调用已用尽，无法再调用 propose"。改成 8。
    ///
    /// 8 轮不是拍脑袋：每轮十几秒，最坏情况约两分钟；而收尾那一轮现在**仍然允许
    /// propose**（见下面 digest 那段），所以就算真用光了也不会白跑——这比单纯
    /// 加轮数更管用，加轮数只是降低触发概率。
    /// </summary>
    private const int DefaultMaxRounds = 32;

    /// <summary>
    /// 一次对话里最多插入多少张放大图。**这是给上下文兜底的上限**，不是省事。
    ///
    /// 每张 1024 长边约 850 token，而且**一旦插进 messages 就会留在后面每一轮请求里**
    /// （同一个 RunAsync 内不会摘掉），所以模型要是每轮都裁一次，token 是叠加的。
    /// 3×3 一次用掉 9 张，再给 3 张的余量（"再裁一处"这种正常需求刚好够）。
    /// 用光了就如实告诉它用完，让它拿现有的图作答——比让它静默裁不出东西强。
    /// </summary>
    private const int MaxRegionImages = 12;

    /// <summary>
    /// 逐张走时一次对话最多处理几张图。**这是防跑飞的上限**，不是省事。
    ///
    /// 逐张走是"模型自己决定走到哪一张"，没有这个上限时它可能一直在数据集里转
    /// （尤其在小数据集上：改完一圈又回头"再检查一遍"）。20 张覆盖了"先过一遍新收的图"
    /// 这个常见用法，而每张都要读图 + 好几次请求，20 张已经是十几分钟的量级——
    /// 到顶就让它收尾，用户想继续可以在下一条消息里说。
    /// </summary>
    public const int MaxWalkImages = 20;

    /// <summary>逐张走到上限时回给模型的话（让它收尾而不是硬停，测试里也按它断言）。</summary>
    public const string WalkLimitNotice =
        "You have reached the limit of images for one pass. Stop switching images now " +
        "and give the user a short summary in Simplified Chinese of what you changed.";

    /// <summary>
    /// 跑一轮。历史里最后一轮必须是本次的输入，图片只挂在那一轮上——
    /// 把图挂到每一轮会让后面每一次请求都重复付一次图片的 token。
    /// </summary>
    public async Task<Outcome> RunAsync(IHost host, IReadOnlyList<AgentTurn> history,
                                        LlmClient.ChatImage? image, CancellationToken ct = default,
                                        Action<string, string>? onProgress = null,
                                        Action<string, string, string?>? onToolCall = null,
                                        int maxRounds = DefaultMaxRounds)
    {
        var found = new List<TagSearch.Hit>();
        var log = new List<string>();

        var hasImage = image is not null && history.Any(t => t.FromUser);
        var dataset = host.Images;

        // 预算由 host 现取（0 = 自动，见 IHost.MaxTokensPerTurn）。**不在这里写死**：
        // 写死的 2400 正是"复杂打标永远不够用、而用户调设置也没用"的那个原因。
        var budget = host.MaxTokensPerTurn;

        // 能不能真的把图放大：放大后的图是靠"往对话里插一条图文 user 消息"送出去的，
        // 而这条这次对话里必须已经有图（否则等于绕过用户关掉的「带当前图片」开关）。
        var canViewRegion = hasImage;
        var canSave = host.CanSave;
        var canEditTags = host.CanEditTags;
        // 批量只在"有数据集、且当前不在批量循环里"时下发（防自我递归，见 CanBatchLabel）。
        var canBatchLabel = host.CanBatchLabel && dataset.Count > 0;

        // 切图工具的来源：有数据集 + 这里允许切（批量内层不允许，见 CanSwitchImages）
        // + 用户没在设置里把它关掉（Settings 为 null = 探针/测试，不做过滤）。
        var canSwitch = host.CanSwitchImages && dataset.Count > 0
                        && (host.Settings is null
                            || !AgentKit.IsDisabled(host.Settings, AgentKit.SelectImageToolName));

        // 逐张走：模型自己 select_image → propose → select_image，每张当场落盘。
        // **必须开「完全权限」**：这条路的每一步都是写盘，而那个开关的语义正是
        // "允许它自己动手，不用你点确认"。关着时 propose 仍旧结束整轮（一次改一张）。
        var canWalk = canSwitch && canSave && host.CanApplyNow;

        var system = AgentKit.SystemPrompt(hasImage, host.CanWebSearch, canViewRegion, canSave,
                                           canBatchLabel, canEditTags, host.Settings,
                                           canWalk: canWalk, canSwitch: canSwitch);
        var tools = AgentKit.Tools(host.CanWebSearch, withImages: dataset.Count > 0,
                                   canViewRegion: canViewRegion, canSave: canSave,
                                   canBatchLabel: canBatchLabel, canEditTags: canEditTags,
                                   cfg: host.Settings, canSwitchImages: canSwitch);

        var messages = new List<JsonObject>(history.Count);
        for (var i = 0; i < history.Count; i++)
        {
            var turn = history[i];
            var isLast = i == history.Count - 1;
            messages.Add(turn.FromUser
                ? LlmClient.UserMessage(turn.Text, isLast ? image : null)
                : new JsonObject { ["role"] = "assistant", ["content"] = turn.Text });
        }

        var rounds = 0;

        // 这一轮攒下的放大图，以及整次对话还剩多少张图的额度（见 MaxRegionImages）。
        var pendingImages = new List<LlmClient.ChatImage>();
        var imageBudget = MaxRegionImages;

        // 逐张走已经处理过几张（见 MaxWalkImages）。只在 canWalk 时有意义。
        var walked = 0;

        // 模型有没有要求把结果写进磁盘。**必须跨轮存活**：它可能在第 1 轮就说
        // "改完存一下"、第 4 轮才 propose，而"每轮重置"会把那个意图丢掉
        // （实测：完全权限下让它"改完保存"，文件一个字都没变）。
        // 只表达意图，真正的写盘在界面那边、等提案应用之后（见 AgentProposal.SaveToDisk）。
        var wantsSave = false;

        // 这三样必须在**第一个 Done 之前**声明：Done 是捕获它们的局部函数，
        // 声明晚于使用会报 CS0165（用了未赋值的局部变量）——编译器只看赋值顺序。
        // 工具循环会发好几次请求，每次的思考内容都攒起来给用户看。只用于显示。
        var thinking = new StringBuilder();
        // 每次工具调用的"调了什么 / 返回了什么"，界面拿它做可展开的详情。
        var toolCalls = new List<ToolCallLog>();

        if (messages.Count == 0)
            return Done(false, "", null, 0, "没有可发送的内容");

        void Collect(string r)
        {
            if (string.IsNullOrWhiteSpace(r)) return;
            if (thinking.Length > 0) thinking.Append("\n\n────────\n\n");
            thinking.Append(r.Trim());
        }

        // 所有返回都走这里：找到的标签要按用户序整理、思考与工具调用要一并带走。
        // 八处返回各写一遍的话，迟早有一处漏掉新加的东西——那处的表现是
        // "这一轮的思考/工具调用莫名不见了"，很难查。
        Outcome Done(bool ok, string reply, AgentProposal? proposal, int rn, string err) =>
            new(ok, reply, proposal, TagSearch.RankForUser(found), log, rn, err,
                thinking.ToString(), toolCalls);

        // 记一次工具调用，并**当场**告诉界面——界面据此把它插在对话流里它实际发生的
        // 位置上（用户要求：不许攒到最后补一段）。各调用点走同一个出口，
        // 免得将来加了工具却忘了通知界面。
        void Report(ToolCallLog c)
        {
            toolCalls.Add(c);
            try { onToolCall?.Invoke(c.Name, c.Arguments, c.Result); }
            catch (Exception ex) { Log.Write("tool call progress failed: " + ex.Message); }
        }

        // 发起时先通知一次（结果为 null）：查一个词可能要好几秒，先让用户看到
        // "正在查什么"比干等更诚实。界面会把块先放上去、拿到结果再填。
        void ReportStart(string name, string arguments)
        {
            try { onToolCall?.Invoke(name, arguments, null); }
            catch (Exception ex) { Log.Write("tool call start failed: " + ex.Message); }
        }

        while (rounds < Math.Max(1, maxRounds))
        {
            rounds++;

            var turn = await host.TurnAsync(system, tools, messages, budget, ct, onProgress)
                                  .ConfigureAwait(false);
            Collect(turn.Reasoning);
            if (!turn.Ok)
                return Done(false, "", null, rounds, turn.Error);

            // 每轮记一行：这条路径一次要发好几次请求，出问题时（模型不调工具、
            // 预算被思考吃光、图片没被接受）只有这行能看出是哪一轮。
            Log.Write($"agent: round={rounds} calls={turn.Calls.Count} " +
                      $"content={turn.Content.Length} reasoning={turn.Reasoning.Length} " +
                      $"image={(image is not null ? image.Bytes.Length + "B" : "none")}");

            if (turn.Calls.Count == 0)
            {
                var answer = (turn.Content ?? "").Trim();
                return answer.Length > 0
                    ? Done(true, answer, null, rounds, "")
                    : Done(false, "", null, rounds,
                                   "模型既没有回答问题，也没有提出改动");
            }

            // tool_call_id 必须原样回灌，否则小模型对不上号会反复调同一个工具
            messages.Add(LlmClient.AssistantMessage(turn.Content, turn.Calls));

            // 这一轮模型有没有要求写盘。**它在 propose 之前还是之后都可能出现**，
            // 所以在整轮扫描里记下来，而不是就地写盘——写盘必须等提案落进编辑器
            // （那一刻编辑器里还是旧内容，见 AgentProposal.SaveToDisk 的注释）。
            //
            // **wantsSave 声明在循环外**（见上面）：模型完全可能在第 1 轮就说
            // "改完存一下"、第 4 轮才 propose，而每轮重置会把那个意图丢掉
            // ——实测就这么错过一次。
            foreach (var call in turn.Calls)
            {
                if (string.Equals(call.Name, AgentKit.ProposeToolName, StringComparison.Ordinal))
                {
                    var proposal = AgentKit.ParseProposal(call.Arguments, host.IsKnown);
                    var reply = (turn.Content ?? "").Trim();
                    if (reply.Length == 0)
                        reply = proposal.Note.Length > 0
                            ? proposal.Note
                            : $"已提出 {proposal.AddTags.Count} 个标签" +
                              (proposal.HasRemovals ? $"，并删除 {proposal.Remove.Count} 个" : "") +
                              (proposal.HasProse ? "和一段散文" : "") + "。";

                    log.Add($"提案：{proposal.Known.Count} 个词库认得、" +
                            $"{proposal.Unknown.Count} 个词库查不到" +
                            (proposal.HasRemovals ? $"，删除 {proposal.Remove.Count} 个" : "") +
                            (proposal.HasProse ? "，另有一段散文" : ""));

                    // 提案到手就停：后面即使还有搜到一半的概念也不必再花请求。
                    Report(new ToolCallLog(call.Name, call.Arguments,
                        $"认得 {proposal.Known.Count} 个、查不到 {proposal.Unknown.Count} 个" +
                        (proposal.HasRemovals ? $"，删除 {proposal.Remove.Count} 个" : "") +
                        (proposal.HasProse ? "，含一段散文" : "")));

                    // **逐张走**：当场应用并落盘，然后继续这一轮——模型才能接着
                    // `select_image` 去下一张。必须现在落盘而不是攒着：编辑器只有一份
                    // 文档，下一张的提案会覆盖这一张的改动（见 ApplyProposalNow 的注释）。
                    if (canWalk && walked < MaxWalkImages)
                    {
                        var (msg, applied) = host.ApplyProposalNow(proposal);
                        walked++;
                        log.Add(applied
                            ? $"逐张应用（第 {walked} 张）"
                            : $"逐张应用失败（第 {walked} 张）");
                        Report(new ToolCallLog(call.Name, call.Arguments, msg));

                        var left = MaxWalkImages - walked;
                        messages.Add(LlmClient.ToolMessage(call.Id,
                            msg + $" That was image {walked} of this pass; {left} more are " +
                                  "allowed. If the user asked you to go through more images, " +
                            "call " + AgentKit.SelectImageToolName + " to open the next one, " +
                            "look at it, and " + AgentKit.ProposeToolName + " again. When you " +
                            "are done, reply with a short summary in Simplified Chinese."));
                        continue;
                    }

                    // 到上限了：如实说清，并把工具意思收回来（让它收尾而不是硬停）。
                    if (canWalk)
                    {
                        log.Add("逐张走：已到张数上限");
                        Report(new ToolCallLog(call.Name, call.Arguments, WalkLimitNotice));
                        messages.Add(LlmClient.ToolMessage(call.Id, WalkLimitNotice));
                        continue;
                    }

                    // 提案到手就停：后面即使还有搜到一半的概念也不必再花请求。
                    // 写盘意图挂在提案上带走，由界面在应用之后执行。本轮里 save_caption
                    // 可能出现在 propose **之前或之后**（模型两种顺序都写过），所以这里
                    // 先扫一遍整轮——直接 return 会把排在 propose 后面的那次 save 漏掉
                    // （实测：完全权限下让它"改完保存"，文件没变）。
                    var saveWanted = wantsSave || turn.Calls.Any(c =>
                        string.Equals(c.Name, AgentKit.SaveToolName, StringComparison.Ordinal));

                    return Done(true, reply, proposal with { SaveToDisk = saveWanted && canSave },
                                rounds, "");
                }

                if (string.Equals(call.Name, AgentKit.CurrentImageToolName, StringComparison.Ordinal))
                {
                    var text = DescribeCurrentImage(host);
                    log.Add("查当前图片");
                    Report(new ToolCallLog(call.Name, call.Arguments, text));
                    messages.Add(LlmClient.ToolMessage(call.Id, text));
                    continue;
                }

                if (string.Equals(call.Name, AgentKit.SelectImageToolName, StringComparison.Ordinal))
                {
                    var want = ArgString(call.Arguments, "name");
                    if (want.Length == 0)
                    {
                        const string retry =
                            "The name was empty. Call select_image again with a file name from current_image.";
                        messages.Add(LlmClient.ToolMessage(call.Id, retry));
                        log.Add("切图（空名字）→ 已要求重试");
                        Report(new ToolCallLog(call.Name, call.Arguments, retry));
                        continue;
                    }

                    var (ok, msg, switched) = await host.SelectImageAsync(want, ct)
                                                         .ConfigureAwait(false);
                    log.Add(ok ? $"切到「{want}」" : $"切图失败（{want}）");

                    // **把新图塞回对话**，否则模型切了也看不见（见 SelectImage 的注释）。
                    if (ok && switched is not null)
                    {
                        pendingImages.Add(switched);
                        msg += " The image you switched to is attached below — look at it " +
                               "before you judge or describe anything.";
                    }

                    Report(new ToolCallLog(call.Name, call.Arguments, msg));
                    messages.Add(LlmClient.ToolMessage(call.Id, msg));
                    continue;
                }

                if (string.Equals(call.Name, AgentKit.ImageInfoToolName, StringComparison.Ordinal))
                {
                    var text = await host.ImageInfo().ConfigureAwait(false);
                    log.Add("查图像信息");
                    Report(new ToolCallLog(call.Name, call.Arguments, text));
                    messages.Add(LlmClient.ToolMessage(call.Id, text));
                    continue;
                }

                if (string.Equals(call.Name, AgentKit.ViewRegionToolName, StringComparison.Ordinal))
                {
                    // 先按参数估一下这次会要几张图，超预算就**不要真的去裁**——
                    // 裁一次要解码 + 编码，白干一遍再告诉模型"不给"是纯浪费。
                    var want = AgentKit.PlannedRegionImages(call.Arguments);
                    if (want > imageBudget)
                    {
                        var full = $"You have already been given the maximum of {MaxRegionImages} " +
                                   "enlarged images for this turn, and this call would need " +
                                   $"{want} more. No more crops are available: answer now with " +
                                   "what you have already seen, and say plainly which detail you " +
                                   "still cannot make out.";
                        log.Add("放大（已超出图片预算）");
                        Report(new ToolCallLog(call.Name, call.Arguments, full));
                        messages.Add(LlmClient.ToolMessage(call.Id, full));
                        continue;
                    }

                    ReportStart(call.Name, call.Arguments);
                    var (ok, msg, imgs) = await host.ViewRegion(call.Arguments).ConfigureAwait(false);
                    log.Add(ok ? $"放大 → {imgs.Count} 张图" : "放大失败");
                    Report(new ToolCallLog(call.Name, call.Arguments, msg));
                    messages.Add(LlmClient.ToolMessage(call.Id, msg));

                    // 图不能挂在 tool 结果上（协议里工具结果只能是文本），只能攒起来，
                    // 等这一轮**所有** tool 结果都回完之后再插一条 user 消息——顺序不能乱，
                    // 插在中间会让严格的网关直接 400（tool 消息必须紧跟要它的 assistant）。
                    if (ok && imgs.Count > 0)
                    {
                        imageBudget -= imgs.Count;
                        pendingImages.AddRange(imgs);
                    }
                    continue;
                }

                // ---- 读标签清单 / 改类型 / 规整顺序 ----
                if (string.Equals(call.Name, AgentKit.ListTagsToolName, StringComparison.Ordinal))
                {
                    var text = canEditTags
                        ? host.DescribeTags()
                        : "Reading the caption's tags is not available in this conversation.";
                    log.Add("查标签清单");
                    Report(new ToolCallLog(call.Name, call.Arguments, text));
                    messages.Add(LlmClient.ToolMessage(call.Id, text));
                    continue;
                }

                if (string.Equals(call.Name, AgentKit.SetTagSectionToolName, StringComparison.Ordinal))
                {
                    string text;
                    if (!canEditTags)
                    {
                        text = "Changing tag sections is not allowed in this conversation: the " +
                               "user has not given you write access. Tell them they can set it " +
                               "themselves with 标签 → 标签类型.";
                        log.Add("改标签类型（未开完全权限）→ 已回绝");
                    }
                    else
                    {
                        var req = AgentKit.ParseSectionRequest(call.Arguments);
                        if (!req.Ok)
                        {
                            text = req.Problem.Length > 0 ? req.Problem : "No tags were given.";
                            log.Add("改标签类型 → 参数不合法");
                        }
                        else
                        {
                            var (changed, msg) = host.SetTagSections(req.Tags, req.Section);
                            text = msg;
                            log.Add($"改标签类型 {changed} 个 -> {(req.Section ?? "(跟随词库)")}");
                        }
                    }
                    Report(new ToolCallLog(call.Name, call.Arguments, text));
                    messages.Add(LlmClient.ToolMessage(call.Id, text));
                    continue;
                }

                if (string.Equals(call.Name, AgentKit.SortTagsToolName, StringComparison.Ordinal))
                {
                    string text;
                    if (!canEditTags)
                    {
                        text = "Reordering the caption is not allowed in this conversation: the " +
                               "user has not given you write access. Tell them they can use " +
                               "标签 → 规整大类顺序.";
                        log.Add("规整顺序（未开完全权限）→ 已回绝");
                    }
                    else
                    {
                        var (changed, msg) = host.SortTags();
                        text = msg;
                        log.Add(changed ? "规整顺序 → 已重排" : "规整顺序 → 本来就是有序的");
                    }
                    Report(new ToolCallLog(call.Name, call.Arguments, text));
                    messages.Add(LlmClient.ToolMessage(call.Id, text));
                    continue;
                }

                if (string.Equals(call.Name, AgentKit.AddTagsToImagesToolName, StringComparison.Ordinal))
                {
                    string text;
                    if (!canBatchLabel)
                    {
                        text = "Adding tags across the dataset is not available in this " +
                               "conversation (it needs a dataset open in label mode).";
                        log.Add("批量补标签（不可用）→ 已回绝");
                    }
                    else
                    {
                        var req = AgentKit.ParseAddTagsRequest(call.Arguments);
                        if (!req.Ok)
                        {
                            text = req.Problem.Length > 0 ? req.Problem : "No tags were given.";
                            log.Add("批量补标签 → 参数不合法");
                        }
                        else
                        {
                            // 同步、瞬时：不读图也不发请求，所以不需要"长工具"那套进度机制
                            var (changed, msg) = host.AddTagsToImages(req.Tags, req.IncludeUnlabeled,
                                                                      req.Limit);
                            text = msg;
                            log.Add($"批量补标签 {changed} 张 -> {string.Join(", ", req.Tags)}");
                        }
                    }
                    Report(new ToolCallLog(call.Name, call.Arguments, text));
                    messages.Add(LlmClient.ToolMessage(call.Id, text));
                    continue;
                }

                if (string.Equals(call.Name, AgentKit.LabelImagesToolName, StringComparison.Ordinal))
                {
                    if (!canBatchLabel)
                    {
                        const string denied =
                            "Bulk labelling is not available in this conversation (it needs a " +
                            "dataset open in label mode).";
                        log.Add("批量打标（不可用）→ 已回绝");
                        Report(new ToolCallLog(call.Name, call.Arguments, denied));
                        messages.Add(LlmClient.ToolMessage(call.Id, denied));
                        continue;
                    }

                    var req = AgentKit.ParseLabelRequest(call.Arguments);
                    // **进度先贴出来**：这一跑可能几分钟，界面不能只是转圈。
                    host.ReportBatchProgress(req.RedoAll
                        ? "批量打标：重做全部图片…"
                        : "批量打标：只处理还没有标注的图片…");

                    // 这一路是"长工具"：它会自己把整批跑完（每张图各跑一轮完整助手）。
                    // 期间界面靠 ReportBatchProgress 更新；模型在这里等结果。
                    var (text, cancelled) = await host.LabelImagesAsync(call.Arguments, ct)
                                                      .ConfigureAwait(false);
                    log.Add("批量打标 → " + text.Replace("\n", "；"));
                    Report(new ToolCallLog(call.Name, call.Arguments, text));
                    messages.Add(LlmClient.ToolMessage(call.Id, text));

                    // 用户叫停了：**这一轮就到这里**。继续问下去只会又发起一次批量，
                    // 而用户刚刚明确说不要了。
                    if (cancelled)
                        return Done(true,
                            "批量打标已按你的要求停下。已完成的那几张已经写进各自的 .txt。",
                            null, rounds, "");
                    continue;
                }

                if (string.Equals(call.Name, AgentKit.SaveToolName, StringComparison.Ordinal))
                {
                    // 不下发时模型不会调它；真调了（幻觉工具名）也如实回绝，
                    // 而不是"当作成功"——那正是"它说存了其实没存"的来源。
                    if (!canSave)
                    {
                        const string denied =
                            "Saving is not allowed in this conversation. The user has not given " +
                            "you write access — everything you propose stays in the editor until " +
                            "they apply and save it themselves.";
                        log.Add("保存（未开完全权限）→ 已回绝");
                        Report(new ToolCallLog(call.Name, call.Arguments, denied));
                        messages.Add(LlmClient.ToolMessage(call.Id, denied));
                        continue;
                    }

                    // **只记意图，不在这里写盘。** 此刻编辑器里还是旧内容（提案还没被
                    // 应用），现在写下去的是上一次的东西，而它会回报"保存成功"。
                    // 真正的写盘由界面在应用提案之后执行（见 AgentProposal.SaveToDisk）。
                    wantsSave = true;
                    const string queued =
                        "Understood — the caption will be written to disk as soon as your " +
                        "proposal is applied. Keep going: the tags and prose still have to be " +
                        "proposed with " + AgentKit.ProposeToolName + " (that is what actually " +
                        "changes the prompt).";
                    log.Add("保存 → 已排队（提案应用后落盘）");
                    Report(new ToolCallLog(call.Name, call.Arguments, queued));
                    messages.Add(LlmClient.ToolMessage(call.Id, queued));
                    continue;
                }

                if (string.Equals(call.Name, AgentKit.SearchToolName, StringComparison.Ordinal))
                {
                    var query = DescribeQuery(call.Arguments);
                    if (query.Length == 0)
                    {
                        const string retry =
                            "The query was empty. Call search_tags again with one or two words.";
                        messages.Add(LlmClient.ToolMessage(call.Id, retry));
                        log.Add("查（空查询）→ 已要求重试");
                        Report(new ToolCallLog(call.Name, call.Arguments, retry));
                        continue;
                    }

                    ReportStart(call.Name, call.Arguments);

                    var res = host.Search(query, out var hits, out var split);
                    found.AddRange(hits);
                    log.Add(hits.Count > 0 ? $"查「{query}」→ {hits.Count} 条" : $"查「{query}」→ 无结果");
                    Report(new ToolCallLog(call.Name, call.Arguments, res));
                    messages.Add(LlmClient.ToolMessage(call.Id, res));
                    continue;
                }

                if (string.Equals(call.Name, AgentKit.WebSearchToolName, StringComparison.Ordinal))
                {
                    var query = DescribeQuery(call.Arguments);
                    var text = query.Length == 0
                        ? "The query was empty."
                        : await host.SearchWebAsync(query, ct).ConfigureAwait(false);
                    log.Add($"联网查「{query}」");
                    Report(new ToolCallLog(call.Name, call.Arguments, text));
                    messages.Add(LlmClient.ToolMessage(call.Id, text));
                    continue;
                }

                // 未知工具名：如实回一句，别让它空转。模型下一轮通常会改用正确的工具。
                log.Add($"未知工具 {call.Name}");
                var unknown = $"Unknown tool \"{call.Name}\". Available tools: " +
                              string.Join(", ", tools.Select(t => t.Name)) + ".";
                Report(new ToolCallLog(call.Name, call.Arguments, unknown));
                messages.Add(LlmClient.ToolMessage(call.Id, unknown));
            }

            // 放大后的图在这里补上：**必须等这一轮所有工具结果都回完**（见上面 view_region
            // 那段）。文案刻意说清"这是你要的放大区域，不是用户新说的话"——
            // 不然模型会去"回答"这条消息，而不是看它。
            if (pendingImages.Count > 0)
            {
                var note = pendingImages.Count == 1
                    ? "The image you asked for is attached below (an enlarged region, or the " +
                      "image you just switched to). Look at it."
                    : $"The {pendingImages.Count} images you asked for are attached below " +
                      "(enlarged regions and/or the image you switched to), in the order you " +
                      "asked for them. Look at them.";
                messages.Add(LlmClient.UserMessage(note, pendingImages));
                Log.Write($"agent: region images attached this round = {pendingImages.Count} " +
                          $"(budget left {imageBudget})");
                pendingImages.Clear();
            }
        }

        // 轮数用完了：把已有检索结果整理成一段，**不带工具**再问一次。
        //
        // 不带工具是关键——上一轮它还会继续调工具，而这一路没有工具可调，
        // 所以它只能给答案（TagSearchAgent 里同一条做法，实测有效）。
        // 注意这里沿用**同一个 messages 列表**而不是另起一条纯文本请求：
        // 换个请求就等于把第一轮挂上去的那张图丢了，而"读图"正是这一路
        // 最容易把轮数用完的场景。
        var digest = new StringBuilder();
        digest.Append("You are out of SEARCHING. You may not search any more, and you may " +
                      "not name any tag that is not in the results below. Call " +
                      AgentKit.ProposeToolName + " now, exactly once, with the tags and " +
                      "prose you are confident about.\nSearch results so far:\n");
        foreach (var h in TagSearch.RankForUser(found))
            digest.Append("- tag=").Append(h.Tag).Append(" | cn=").Append(h.Cn).Append('\n');
        digest.Append("If the results are enough, propose them. If they are not enough, " +
                      "say so in one short Chinese sentence — but do not write the tag list " +
                      "out as text instead of proposing.");

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = digest.ToString() });

        // **收尾这一轮只给 propose，不给搜索。** 这不是小改动：原来一个工具都不给，
        // 于是模型只能把标签写成一段话，用户看到的是"由于工具调用已用尽，无法再调用
        // propose，请手动录入"——而提案恰恰是这一路唯一的产出。
        // 只留 propose 同时满足两件事：它没法继续搜（不然会一直搜下去），
        // 但**仍然能交出结构化提案**。
        var proposeOnly = tools.Where(t => t.Name == AgentKit.ProposeToolName).ToList();
        var final = await host.TurnAsync(system, proposeOnly, messages, budget, ct,
                                         onProgress)
                              .ConfigureAwait(false);
        Collect(final.Reasoning);
        if (!final.Ok)
            return Done(false, "", null, rounds, final.Error);

        var text2 = (final.Content ?? "").Trim();
        return text2.Length > 0
            ? Done(true, text2, null, rounds, "")
            : Done(false, "", null, rounds,
                           "工具循环超出轮数上限，且模型没有给出最终回答");
    }

    /// <summary>
    /// 「当前是哪张图」的工具结果。**每次都重新算**，所以永远是最新的——
    /// 这也是它必须做成工具、而不是写进系统提示的原因。
    /// </summary>
    private static string DescribeCurrentImage(IHost host)
    {
        var imgs = host.Images;
        var cur = imgs.FirstOrDefault(i => i.IsCurrent);
        if (cur is null)
            return "No image is currently selected (the editor is in prompt-writing mode, " +
                   "which has no dataset).";

        var sb = new StringBuilder();
        sb.Append("Current image: ").Append(cur.Name)
          .Append(" (").Append(cur.Index + 1).Append(" of ").Append(imgs.Count).Append("). ");
        sb.Append(cur.Labeled
            ? $"It already has a caption with {cur.TagCount} tag(s)."
            : "It has no caption yet.");

        // **把现有标签原样列出来。** 只报数量是不够的：用户说"覆盖成这三条"时，
        // 模型必须知道是哪几条才能把它们放进 remove_tags。不列的话它只知道
        // "有 16 个"，于是只能往 add_tags 里塞、然后在 note 里声称替换了——
        // 实测就是这么发生的（见 tests/OverwriteProbe.cs）。
        if (cur.Tags is { Count: > 0 })
            sb.Append("\nIts current tags, in order: ").Append(string.Join(", ", cur.Tags))
              .Append("\nTo REMOVE any of them, list them in remove_tags of the proposal.");

        // 顺带把邻居列出来，模型才可能"切到下一张"——否则它连名字都不知道。
        sb.Append("\nDataset:");
        foreach (var i in imgs)
            sb.Append("\n- ").Append(i.Name)
              .Append(i.IsCurrent ? " (current)" : "")
              .Append(i.Labeled ? $" [{i.TagCount} tags]" : " [uncaptioned]");
        return sb.ToString();
    }

    /// <summary>
    /// 从工具参数里取一个字符串字段。取不到返回空串——**不能退回用户的整句话**：
    /// 那是 <see cref="DescribeQuery"/> 里已经记过的教训（把一整句中文当查询发出去，
    /// 一次都命中不了）。
    /// </summary>
    private static string ArgString(string arguments, string key)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(arguments);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(key, out var v) &&
                   v.ValueKind == System.Text.Json.JsonValueKind.String
                ? (v.GetString() ?? "").Trim()
                : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// 取工具调用里的 <c>query</c>。取不到时返回空串——注意**不能**退回用户的整句话：
    /// 实测把一整句中文当查询发出去，一次命中都没有（见 TagSearchAgent 的系统提示第 3 条）。
    /// </summary>
    private static string DescribeQuery(string arguments) =>
        LlmClient.QueryFromArguments(arguments).Trim();
}
