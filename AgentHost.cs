using System.Text.Json.Nodes;
using AnimaCaptioner.Core;

namespace AnimaCaptioner;

/// <summary>
/// <see cref="TagAgent.IHost"/> 的真实实现：把 agent 循环需要的四件事接到
/// 真正的接口客户端、词库和搜索后端上。
///
/// 单独一个文件而不是塞进 MainWindow：它是"agent 与外界"的唯一边界，
/// 出问题时（图没被接受、搜索没配、词库没加载）症状都在这一层，
/// 放在 4600 行的窗体里就很难找。
/// </summary>
internal sealed class AgentHost : TagAgent.IHost
{
    private readonly AppSettings _cfg;
    private readonly LlmClient _llm;
    private readonly SearchClient _search;
    private readonly VocabDb? _vocab;

    /// <summary>
    /// 图片相关的三件事由窗口注入，而不是让 AgentHost 抓着 MainWindow：
    /// 它是"agent 与外界"的边界，边界上只该有**形状**（列表、当前项、切图），
    /// 不该有窗体的内部状态。接上这三个具体实现的是 MainWindow。
    /// </summary>
    private readonly Func<IReadOnlyList<AgentImage>> _images;
    private readonly Func<string, (bool Ok, string Message)> _selectImage;

    public AgentHost(AppSettings cfg, LlmClient llm, SearchClient search, VocabDb? vocab,
                     Func<IReadOnlyList<AgentImage>>? images = null,
                     Func<string, (bool Ok, string Message)>? selectImage = null)
    {
        _cfg = cfg;
        _llm = llm;
        _search = search;
        _vocab = vocab;
        _images = images ?? (() => Array.Empty<AgentImage>());
        _selectImage = selectImage ?? (_ => (false, "Switching images is not available here."));
    }

    /// <summary>写标签模式没有数据集 → 返回空 → 两个图片工具都不会下发。</summary>
    public IReadOnlyList<AgentImage> Images => _images();

    public (bool Ok, string Message) SelectImage(string name) => _selectImage(name);

    /// <summary>没配搜索后端时不下发这个工具：让模型调一个必然失败的工具是白烧一轮预算。</summary>
    public bool CanWebSearch => _cfg.SearchEnabled;

    public bool IsKnown(string tag) => TagSections.IsKnownTag(tag, _vocab);

    public string Search(string query, out List<TagSearch.Hit> hits, out bool splitMatch)
    {
        var res = TagSearch.QueryDetailed(_vocab, query, TagSearch.ToolLimit);
        hits = res.Hits;
        splitMatch = res.SplitMatch;

        // remaining=2 只是给模型的一句"还能再查几次"的提示；真正管轮数的是 TagAgent
        return TagSearch.ToToolContent(res.Hits, query, 2, res.SplitMatch);
    }

    public Task<LlmClient.ToolTurn> TurnAsync(string systemPrompt, IReadOnlyList<AgentTool> tools,
                                              List<JsonObject> messages, int maxTokens,
                                              CancellationToken ct) =>
        _llm.ChatWithToolsAsync(_cfg, systemPrompt, messages, tools, maxTokens, ct);

    public async Task<string> SearchWebAsync(string query, CancellationToken ct)
    {
        if (!_cfg.SearchEnabled) return "Web search is not enabled in the settings.";

        var res = await _search.SearchAsync(_cfg, query, ct).ConfigureAwait(false);
        if (!res.Ok) return "Web search failed: " + res.Error;

        Log.Write($"agent web search: 「{query}」→ {res.Hits.Count} 条");
        return SearchClient.ToToolContent(res, query);
    }
}
