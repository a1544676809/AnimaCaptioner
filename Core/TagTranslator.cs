using System.Text.Json.Nodes;

namespace AnimaCaptioner.Core;

/// <summary>
/// 「模型翻译」的执行体：给一个词库查不到中文的标签，让模型给出中文释义，
/// 需要时让它先联网搜一下再回答。
///
/// 这件事的定位必须说清楚：**它产出的是推断，不是官方释义**。
/// 词库里的中文来自 Danbooru 对照表；这里的中文来自模型，可能错。
/// 所以结果一律进 <see cref="ModelGlossStore"/>，UI 上单独标记，
/// 绝不写回词库、绝不覆盖官方值。
///
/// 联网搜索在协议上走 function calling：模型自己决定要不要查、查什么，
/// 我们只负责执行并把结果原样回灌。**不能替它决定**——实测同一个标签，
/// 模型知道的时候直接答（5 token），不确定时才发起搜索（487 token）；
/// 一律强制先搜会把它已经会的也搜一遍，既慢又容易被无关结果带偏。
/// </summary>
public sealed class TagTranslator
{
    private readonly LlmClient _llm;
    private readonly SearchClient _search;

    public TagTranslator(LlmClient llm, SearchClient search)
    {
        _llm = llm;
        _search = search;
    }

    public sealed record Outcome(bool Ok, string Cn, bool Unknown, bool Searched,
                                 int Rounds, string Error, string Note);

    /// <summary>工具循环的轮数上限。超过就放弃，避免小模型陷入无限搜索。</summary>
    private const int MaxRounds = 3;

    /// <summary>
    /// 工具轮的预算。**0 = 自动**，与其它带工具的路一致（见 <see cref="LlmClient.ResolveBudget"/>）。
    ///
    /// 原来这里写死 1600，而那个数字其实是 `cfg.MaxTokens(默认 400) + 思考余量(1200)`
    /// 手工算出来的——用户一旦把翻译那档调大（比如 8192），**只有不走工具的翻译变了
    /// （9392），带工具的那一轮还停在 1600**。同一个功能两条路两个额度，而设置页上
    /// 看不出这回事。
    ///
    /// 现在：**纯翻译那条路**（<c>ChatAsync</c>）仍按 `cfg.MaxTokens + 思考余量`，
    /// 也就是"翻译维持 400"；**带工具的那一轮**走自动——它要"先思考、再发 tool_calls、
    /// 再写答案"，额度该由服务端声明的能力决定，而不是猜一个数。
    /// </summary>
    private const int ToolBudget = 0;

    /// <summary>
    /// 这一路的工具集。说明直接复用 <see cref="AgentKit.WebSearchTool"/>——
    /// 工具描述是承重的（"你已经知道的就别查"，实测过同一模型对 `cat smile`
    /// 直接答、对 `kimekomi` 才发起搜索，这个判断该由它自己做），所以两个入口
    /// 必须逐字一致，不能让它们各自漂移。
    /// </summary>
    private static readonly AgentTool[] Tools = { AgentKit.WebSearchTool };

    /// <summary>这一轮实际要发的 max_tokens。0 = 自动，解析不出来时按工具路兜底。</summary>
    private int Budget(AppSettings cfg) =>
        _llm.BudgetFor(cfg, ToolBudget, LlmClient.ToolAutoFallback);

    public async Task<Outcome> TranslateAsync(AppSettings cfg, string tag, CancellationToken ct = default)
    {
        var useSearch = cfg.SearchEnabled;
        var messages = new List<JsonObject>
        {
            new() { ["role"] = "user", ["content"] = $"Tag: {tag}" }
        };

        var rounds = 0;
        var searched = false;
        var notes = new List<string>();

        while (rounds < MaxRounds)
        {
            rounds++;

            string content;
            if (useSearch)
            {
                var turn = await _llm.ChatWithToolsAsync(
                    cfg, LlmClient.TagGlossSystemPrompt, messages, Tools, Budget(cfg), ct)
                    .ConfigureAwait(false);

                if (!turn.Ok)
                    return new Outcome(false, "", false, searched, rounds, turn.Error, "");

                // 模型要求搜索 -> 执行 -> 回灌 -> 再问一轮
                if (turn.Calls.Count > 0)
                {
                    messages.Add(LlmClient.AssistantMessage(turn.Content, turn.Calls));

                    foreach (var call in turn.Calls)
                    {
                        var q = LlmClient.QueryFromArguments(call.Arguments);
                        if (q.Length == 0) q = tag;   // 参数没解析出来就用标签本身

                        var res = await _search.SearchAsync(cfg, q, ct).ConfigureAwait(false);
                        searched = true;
                        notes.Add(res.Ok
                            ? $"搜「{q}」→ {res.Hits.Count} 条"
                            : $"搜「{q}」失败：{res.Error}");

                        // tool_call_id 必须原样回灌，否则小模型会对不上号而反复调用
                        messages.Add(LlmClient.ToolMessage(
                            call.Id, SearchClient.ToToolContent(res, q)));
                    }

                    if (rounds >= MaxRounds)
                    {
                        // 轮数用完仍在搜：用已有信息逼一个答案出来
                        messages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = "Based on the information above, answer with ONLY " +
                                          "the Chinese translation, or UNKNOWN. No explanation."
                        });
                        var final = await _llm.ChatWithToolsAsync(
                            cfg, LlmClient.TagGlossSystemPrompt, messages, Tools,
                            Budget(cfg), ct).ConfigureAwait(false);
                        content = final.Ok ? final.Content : "";
                        if (!final.Ok && final.Error.Length > 0) notes.Add(final.Error);
                    }
                    else
                    {
                        continue;
                    }
                }
                else
                {
                    content = turn.Content;
                    // 刻意不退回 turn.Reasoning：思考过程不是译文（可能被截断在
                    // 半个句子上，而且是模型的自言自语）。ChatWithToolsAsync
                    // 已经在"只有思考没有答案"时返回失败并说明原因，这里照常
                    // 走下面的空内容分支如实报错即可。
                }
            }
            else
            {
                var r = await _llm.ChatAsync(cfg, LlmClient.TagGlossSystemPrompt,
                                             $"Tag: {tag}", ct).ConfigureAwait(false);
                if (!r.Ok)
                    return new Outcome(false, "", false, false, rounds, r.Error, "");
                content = r.Content;
            }

            var cn = ModelGlossStore.Clean(content);
            if (cn.Length == 0)
                return new Outcome(false, "", false, searched, rounds, "模型返回了空内容", "");

            if (ModelGlossStore.IsUnknown(cn))
                return new Outcome(true, "", true, searched, rounds, "",
                                   "模型无法确定这个标签的含义（未写入缓存）");

            return new Outcome(true, cn, false, searched, rounds, "", string.Join("；", notes));
        }

        return new Outcome(false, "", false, searched, rounds, "工具循环超出轮数上限", "");
    }
}
