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
        Task<LlmClient.ToolTurn> TurnAsync(string systemPrompt, IReadOnlyList<AgentTool> tools,
                                           List<JsonObject> messages, int maxTokens,
                                           CancellationToken ct);

        /// <summary>联网搜索。未启用时返回一句说明即可，不抛异常。</summary>
        Task<string> SearchWebAsync(string query, CancellationToken ct);
    }

    /// <summary>
    /// <paramref name="Proposal"/> 为 null 表示这一轮只是回答（用户在问问题，不是要改）。
    /// </summary>
    public sealed record Outcome(bool Ok, string Reply, AgentProposal? Proposal,
                                 List<TagSearch.Hit> Found, List<string> Log,
                                 int Rounds, string Error);

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
                                        LlmClient.ChatImage? image, CancellationToken ct = default)
    {
        var found = new List<TagSearch.Hit>();
        var log = new List<string>();

        var hasImage = image is not null && history.Any(t => t.FromUser);
        var system = AgentKit.SystemPrompt(hasImage, host.CanWebSearch);
        var tools = AgentKit.Tools(host.CanWebSearch);

        var messages = new List<JsonObject>(history.Count);
        for (var i = 0; i < history.Count; i++)
        {
            var turn = history[i];
            var isLast = i == history.Count - 1;
            messages.Add(turn.FromUser
                ? LlmClient.UserMessage(turn.Text, isLast ? image : null)
                : new JsonObject { ["role"] = "assistant", ["content"] = turn.Text });
        }

        if (messages.Count == 0)
            return new Outcome(false, "", null, found, log, 0, "没有可发送的内容");

        var rounds = 0;
        while (rounds < MaxRounds)
        {
            rounds++;

            var turn = await host.TurnAsync(system, tools, messages, Budget, ct).ConfigureAwait(false);
            if (!turn.Ok)
                return new Outcome(false, "", null, found, log, rounds, turn.Error);

            // 每轮记一行：这条路径一次要发好几次请求，出问题时（模型不调工具、
            // 预算被思考吃光、图片没被接受）只有这行能看出是哪一轮。
            Log.Write($"agent: round={rounds} calls={turn.Calls.Count} " +
                      $"content={turn.Content.Length} reasoning={turn.Reasoning.Length} " +
                      $"image={(image is not null ? image.Bytes.Length + "B" : "none")}");

            if (turn.Calls.Count == 0)
            {
                var answer = (turn.Content ?? "").Trim();
                return answer.Length > 0
                    ? new Outcome(true, answer, null, TagSearch.RankForUser(found), log, rounds, "")
                    : new Outcome(false, "", null, found, log, rounds,
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
                    return new Outcome(true, reply, proposal, TagSearch.RankForUser(found),
                                       log, rounds, "");
                }

                if (string.Equals(call.Name, AgentKit.SearchToolName, StringComparison.Ordinal))
                {
                    var query = DescribeQuery(call.Arguments);
                    if (query.Length == 0)
                    {
                        messages.Add(LlmClient.ToolMessage(call.Id,
                            "The query was empty. Call search_tags again with one or two words."));
                        log.Add("查（空查询）→ 已要求重试");
                        continue;
                    }

                    var res = host.Search(query, out var hits, out var split);
                    found.AddRange(hits);
                    log.Add(hits.Count > 0 ? $"查「{query}」→ {hits.Count} 条" : $"查「{query}」→ 无结果");
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
                    messages.Add(LlmClient.ToolMessage(call.Id, text));
                    continue;
                }

                // 未知工具名：如实回一句，别让它空转。模型下一轮通常会改用正确的工具。
                log.Add($"未知工具 {call.Name}");
                messages.Add(LlmClient.ToolMessage(call.Id,
                    $"Unknown tool \"{call.Name}\". Available tools: " +
                    string.Join(", ", tools.Select(t => t.Name)) + "."));
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
        var final = await host.TurnAsync(system, Array.Empty<AgentTool>(), messages, Budget, ct)
                              .ConfigureAwait(false);
        if (!final.Ok)
            return new Outcome(false, "", null, found, log, rounds, final.Error);

        var text2 = (final.Content ?? "").Trim();
        return text2.Length > 0
            ? new Outcome(true, text2, null, TagSearch.RankForUser(found), log, rounds, "")
            : new Outcome(false, "", null, found, log, rounds,
                           "工具循环超出轮数上限，且模型没有给出最终回答");
    }

    /// <summary>
    /// 取工具调用里的 <c>query</c>。取不到时返回空串——注意**不能**退回用户的整句话：
    /// 实测把一整句中文当查询发出去，一次命中都没有（见 TagSearchAgent 的系统提示第 3 条）。
    /// </summary>
    private static string DescribeQuery(string arguments) =>
        LlmClient.QueryFromArguments(arguments).Trim();
}
