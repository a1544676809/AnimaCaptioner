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

        /// <summary>带工具的一轮对话。工具列表为空 = 这一轮没有工具可调。</summary>
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
        /// </summary>
        (bool Ok, string Message) SelectImage(string name);
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
    /// 工具循环轮数上限。**一轮 = 一次请求**，一轮里模型可以同时发多个工具调用，
    /// 所以 4 轮足够"先搜五六个概念、再提一次案"。再多等待时间就失控了
    /// （每次请求十几秒）。
    /// </summary>
    private const int MaxRounds = 4;

    /// <summary>
    /// 这一路的预算。带工具时思考链是开着的（见 LlmClient.ChatWithToolsAsync 的注释：
    /// 关掉思考模型根本不发 tool_calls），而读图之后思考还会更长，所以比
    /// TagSearchAgent 的 1600 再宽一些。
    /// </summary>
    private const int Budget = 2400;

    /// <summary>
    /// 跑一轮。历史里最后一轮必须是本次的输入，图片只挂在那一轮上——
    /// 把图挂到每一轮会让后面每一次请求都重复付一次图片的 token。
    /// </summary>
    public async Task<Outcome> RunAsync(IHost host, IReadOnlyList<AgentTurn> history,
                                        LlmClient.ChatImage? image, CancellationToken ct = default,
                                        Action<string, string>? onProgress = null,
                                        Action<string, string, string?>? onToolCall = null)
    {
        var found = new List<TagSearch.Hit>();
        var log = new List<string>();

        var hasImage = image is not null && history.Any(t => t.FromUser);
        var dataset = host.Images;
        var system = AgentKit.SystemPrompt(hasImage, host.CanWebSearch);
        var tools = AgentKit.Tools(host.CanWebSearch, withImages: dataset.Count > 0);

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

        while (rounds < MaxRounds)
        {
            rounds++;

            var turn = await host.TurnAsync(system, tools, messages, Budget, ct, onProgress)
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
                              (proposal.HasProse ? "和一段散文" : "") + "。";

                    log.Add($"提案：{proposal.Known.Count} 个词库认得、" +
                            $"{proposal.Unknown.Count} 个词库查不到" +
                            (proposal.HasProse ? "，另有一段散文" : ""));

                    // 提案到手就停：后面即使还有搜到一半的概念也不必再花请求。
                    Report(new ToolCallLog(call.Name, call.Arguments,
                        $"认得 {proposal.Known.Count} 个、查不到 {proposal.Unknown.Count} 个" +
                        (proposal.HasProse ? "，含一段散文" : "")));
                    return Done(true, reply, proposal, rounds, "");
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

                    var (ok, msg) = host.SelectImage(want);
                    log.Add(ok ? $"切到「{want}」" : $"切图失败（{want}）");
                    Report(new ToolCallLog(call.Name, call.Arguments, msg));
                    messages.Add(LlmClient.ToolMessage(call.Id, msg));
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
        }

        // 轮数用完了：把已有检索结果整理成一段，**不带工具**再问一次。
        //
        // 不带工具是关键——上一轮它还会继续调工具，而这一路没有工具可调，
        // 所以它只能给答案（TagSearchAgent 里同一条做法，实测有效）。
        // 注意这里沿用**同一个 messages 列表**而不是另起一条纯文本请求：
        // 换个请求就等于把第一轮挂上去的那张图丢了，而"读图"正是这一路
        // 最容易把轮数用完的场景。
        var digest = new StringBuilder();
        digest.Append("You are out of tool calls. Answer the user now, in Simplified " +
                      "Chinese, without tools.\nSearch results so far:\n");
        foreach (var h in TagSearch.RankForUser(found))
            digest.Append("- tag=").Append(h.Tag).Append(" | cn=").Append(h.Cn).Append('\n');
        digest.Append("If changes are needed, describe them in words; do not name any tag " +
                      "that is not in the results above.");

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = digest.ToString() });
        var final = await host.TurnAsync(system, Array.Empty<AgentTool>(), messages, Budget, ct,
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
