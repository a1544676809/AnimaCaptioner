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
    /// 带思考的翻译要留足预算：实测思考链单独就能写几百 token，
    /// 给 300 时第二轮直接 length 截断（既没正文也没 tool_calls）。
    /// </summary>
    private const int ToolBudget = 1600;

    private const string ToolName = "web_search";

    /// <summary>
    /// 工具说明。刻意强调"你已经知道的就别查"——一律强制先搜会把它已经会的
    /// 也搜一遍，既慢又容易被无关结果带偏（实测过：同一个模型对 `cat smile`
    /// 直接给答案，对 `kimekomi` 才发起搜索，这个判断该由它自己做）。
    /// </summary>
    private const string ToolDescription =
        "Search the web for the meaning of an image tag. Use this when you do not " +
        "know what a tag means, especially for character names, artist names, memes, " +
        "or unusual garment and pose terms. Do not use it for tags you already know.";

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
                    cfg, LlmClient.TagGlossSystemPrompt, messages,
                    ToolName, ToolDescription, ToolBudget, ct).ConfigureAwait(false);

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
                            cfg, LlmClient.TagGlossSystemPrompt, messages, ToolName,
                            ToolDescription, ToolBudget, ct).ConfigureAwait(false);
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
