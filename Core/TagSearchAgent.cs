using System.Text;
using System.Text.Json.Nodes;

namespace AnimaCaptioner.Core;

/// <summary>
/// 「问模型找标签」的执行体：用户用自然语言提问，模型**查本地词库**，
/// 再把查到的标签连同理由反馈给用户。
///
/// 和 <see cref="TagTranslator"/> 的关键区别，也是这个类的设计要点：
/// 那边是"你不知道才去搜"（模型已经会的东西不该白搜一遍），这边正相反——
/// **必须每次都查库**。理由是这里要的是"库里真实存在的标签名"，
/// 而模型凭记忆写出来的标签名极可能是它编的（Danbooru 标签是精确字符串，
/// 差一个词就是另一个标签或根本不存在）。所以：
///
/// 1. 系统提示要求**先调工具再回答**，并禁止凭记忆报标签名；
/// 2. 界面上那串"可以点的标签"一律取自**工具实际返回的行**，
///    不解析模型的话——模型写错了也不会把错标签塞进训练集；
/// 3. 仍然对模型回答里反引号包起来的标签名做一次**存在性核对**，
///    对不上的如实标出来（见 <see cref="TagSearch.SuspiciousMentions"/>）。
///
/// 第 3 条不是多余：小模型很爱把相近的词拼成一个"看起来对"的标签。
/// </summary>
public sealed class TagSearchAgent
{
    private readonly LlmClient _llm;

    public TagSearchAgent(LlmClient llm) => _llm = llm;

    /// <summary>
    /// <paramref name="Found"/> 是工具实际返回过的全部命中（去重后交给界面）；
    /// <paramref name="Log"/> 是给人看的检索记录；<paramref name="Suspicious"/> 是
    /// 模型提到但词库里查不到的标签名。
    /// </summary>
    public sealed record Outcome(bool Ok, string Answer, List<TagSearch.Hit> Found,
                                 List<string> Log, List<string> Suspicious,
                                 int Rounds, string Error);

    /// <summary>工具循环轮数上限。每轮都是一次真实请求，太多会让等待时间失控。</summary>
    private const int MaxRounds = 3;

    /// <summary>
    /// 这一路的预算。**带工具时思考链是开着的**（见 ChatWithToolsAsync 的注释：
    /// 关掉思考模型根本不发 tool_calls），而实测思考单独就能写几百 token，
    /// 所以这里必须比"只写答案"的额度宽松得多，否则第二轮就 length 截断。
    /// </summary>
    private const int Budget = 1600;

    private const string ToolName = "search_tags";

    /// <summary>
    /// 工具说明。刻意写"每次都要先查"——和翻译那条路的"不知道才查"正好相反，
    /// 因为这里的目标是**库里的真实标签名**，不是模型的知识。
    /// </summary>
    private const string ToolDescription =
        "Search the local Danbooru/Anima tag database. Call this FIRST, before answering, " +
        "for every question: the database is the only source of valid tag names and you " +
        "must not name tags from memory. Use SHORT queries of one or two words " +
        "(e.g. \"white dress\", \"lace\", \"shy\"), not a whole sentence: each query is " +
        "matched against tag names and their Chinese meanings. The query may be English, " +
        "Chinese, or a character/series/artist name. If a long query returns nothing, " +
        "split it into separate concepts and search them one by one.";

    /// <summary>
    /// 系统提示。三条都是实测逼出来的，不是套话：
    ///
    /// 1. **必须先查、不许凭记忆报标签名。** Danbooru 标签是精确字符串，
    ///    `blush` 与 `blushing` 是两个不同的东西（后者根本不存在）。让模型自由发挥，
    ///    用户拿到的就是一批加进去也不生效的标签。
    /// 2. **只抄 `tag=` 后面那一段。** 第一版工具结果是没有字段名的裸竖线，
    ///    实测 8B 把整行当成了标签名（`` `hatsune miku|初音未来（VOCALOID）` ``）。
    /// 3. **查询要短、要拆。** 实测「白色蕾丝连衣裙」这种一口气写完的中文，
    ///    模型原样当查询发了两次，一条都查不到就放弃了——而拆成
    ///    「白色连衣裙」+「蕾丝」两个查询，库里明明有 `white dress` 和 `lace`。
    ///    现在词库那边也加了复合查询兜底，但让模型自己拆一次更准。
    ///
    /// 反引号的要求也不是装饰——它是回答里标签名存在性核对能落地的前提
    /// （见 <see cref="TagSearch.SuspiciousMentions"/>）。
    /// </summary>
    public const string SystemPrompt =
        "You help a Chinese-speaking artist find tags in a local Danbooru/Anima tag database.\n" +
        "Rules:\n" +
        "- ALWAYS call search_tags before answering. Never answer from memory: tag names " +
        "are exact strings and a tag you write from memory is probably not the real one.\n" +
        "- Search with SHORT queries of one or two words, one concept at a time. Never " +
        "pass a whole sentence or a compound phrase such as \"white lace dress\": split " +
        "it into \"white dress\" and \"lace\" and search each separately.\n" +
        "- If a query returns nothing, try different or more general words before giving up.\n" +
        "- Only mention tags that came back from search_tags.\n" +
        "- Each result line looks like: 1. tag=<name> | cn=<meaning> | section=<category> " +
        "| posts=<count>. The tag name is ONLY the text between \"tag=\" and the next " +
        "\" | \". Copy exactly that text, never the whole line.\n" +
        "- Answer in Simplified Chinese. At most about 4 short lines.\n" +
        "- Wrap every tag name you mention in backticks.\n" +
        "- Say briefly what each recommended tag is for. When several tags fit, prefer " +
        "the one with the higher posts count.\n" +
        "- If nothing fits, say so plainly and suggest what to search instead. Do not " +
        "invent a tag to fill the gap.";

    public async Task<Outcome> AskAsync(AppSettings cfg, VocabDb? vocab, string question,
                                        CancellationToken ct = default)
    {
        var found = new List<TagSearch.Hit>();
        var log = new List<string>();
        var searched = new List<(string Query, List<TagSearch.Hit> Hits, bool Split)>();

        if (vocab is null)
            return new Outcome(false, "", found, log, new List<string>(), 0, "词库未加载");

        var q = (question ?? "").Trim();
        if (q.Length == 0)
            return new Outcome(false, "", found, log, new List<string>(), 0, "问题是空的");

        var messages = new List<JsonObject>
        {
            new() { ["role"] = "user", ["content"] = q }
        };

        var rounds = 0;
        while (rounds < MaxRounds)
        {
            rounds++;

            var turn = await _llm.ChatWithToolsAsync(
                cfg, SystemPrompt, messages, ToolName, ToolDescription, Budget, ct)
                .ConfigureAwait(false);

            if (!turn.Ok)
                return new Outcome(false, "", found, log, new List<string>(), rounds, turn.Error);

            // 每轮都记一行：这条路径要发好几次请求、每次十几秒，
            // 出问题时（模型不调工具、预算被思考吃光）只有这行能看出是哪一轮。
            Log.Write($"tag search ask: round={rounds} calls={turn.Calls.Count} " +
                      $"content={turn.Content.Length} reasoning={turn.Reasoning.Length}");

            if (turn.Calls.Count == 0)
            {
                var answer = (turn.Content ?? "").Trim();
                if (answer.Length == 0)
                    return new Outcome(false, "", found, log, new List<string>(), rounds,
                                       "模型没有给出回答");

                return new Outcome(true, answer, TagSearch.RankForUser(found), log,
                                   TagSearch.SuspiciousMentions(answer, vocab), rounds, "");
            }

            messages.Add(LlmClient.AssistantMessage(turn.Content, turn.Calls));

            foreach (var call in turn.Calls)
            {
                var query = LlmClient.QueryFromArguments(call.Arguments);
                if (query.Length == 0) query = q;   // 参数没解析出来就用原问题

                var res = TagSearch.QueryDetailed(vocab, query, TagSearch.ToolLimit);
                var hits = res.Hits;
                found.AddRange(hits);
                searched.Add((query, hits, res.SplitMatch));
                log.Add(hits.Count > 0 ? $"查「{query}」→ {hits.Count} 条" : $"查「{query}」→ 无结果");

                // tool_call_id 必须原样回灌，否则小模型会对不上号而反复调用同一个工具
                messages.Add(LlmClient.ToolMessage(
                    call.Id, TagSearch.ToToolContent(hits, query, MaxRounds - rounds, res.SplitMatch)));
            }
        }

        // 轮数用完了还在搜：把已有结果整理成一段文字，**不带工具**再问一次。
        // 不带工具是关键——上一轮它还会继续调工具，而 ChatAsync 这条路上
        // 根本没有工具可调，所以它只能给答案。这比"再发一次带工具的请求、
        // 求它别调了"可靠。
        var digest = new StringBuilder();
        digest.Append("User question: ").Append(q).Append("\n\nSearch results so far:\n");
        foreach (var (query, hits, split) in searched)
            digest.Append(TagSearch.ToToolContent(hits, query, 0, split)).Append('\n');
        digest.Append("\nGive your final answer now, following the same rules. " +
                      "Do not mention any tag that is not in the results above.");

        var final = await _llm.ChatAsync(cfg, SystemPrompt, digest.ToString(), ct).ConfigureAwait(false);
        if (!final.Ok)
            return new Outcome(false, "", found, log, new List<string>(), rounds, final.Error);

        var text = (final.Content ?? "").Trim();
        if (text.Length == 0)
            return new Outcome(false, "", found, log, new List<string>(), rounds,
                               "工具循环超出轮数上限，且模型没有给出最终回答");

        return new Outcome(true, text, TagSearch.RankForUser(found), log,
                           TagSearch.SuspiciousMentions(text, vocab), rounds, "");
    }
}
