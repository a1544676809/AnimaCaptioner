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
    private readonly Func<string, (bool Ok, string Message, LlmClient.ChatImage? Image)> _selectImage;
    private readonly Func<Task<string>> _imageInfo;
    private readonly Func<string, Task<(bool Ok, string Message, IReadOnlyList<LlmClient.ChatImage> Images)>> _viewRegion;

    /// <summary>批量三件事也由窗口注入：边界上只该有形状（能不能、跑一次、报进度）。</summary>
    private readonly Func<bool>? _canBatchLabel;
    private readonly Func<string, CancellationToken, Task<(string Message, bool Cancelled)>>? _labelImages;
    private readonly Action<string>? _batchProgress;

    /// <summary>标签清单 / 类型 / 排序三件事也由窗口注入（边界上只该有形状）。</summary>
    private readonly Func<string>? _describeTags;
    private readonly Func<IReadOnlyList<string>, string?, (int Changed, string Message)>? _setTagSections;
    private readonly Func<(bool Changed, string Message)>? _sortTags;
    private readonly Func<IReadOnlyList<string>, bool, int, (int Changed, string Message)>? _addTagsToImages;

    public AgentHost(AppSettings cfg, LlmClient llm, SearchClient search, VocabDb? vocab,
                     Func<IReadOnlyList<AgentImage>>? images = null,
                     Func<string, (bool Ok, string Message, LlmClient.ChatImage? Image)>? selectImage = null,
                     Func<Task<string>>? imageInfo = null,
                     Func<string, Task<(bool Ok, string Message, IReadOnlyList<LlmClient.ChatImage> Images)>>? viewRegion = null,
                     Func<bool>? canBatchLabel = null,
                     Func<string, CancellationToken, Task<(string Message, bool Cancelled)>>? labelImages = null,
                     Action<string>? batchProgress = null,
                     Func<string>? describeTags = null,
                     Func<IReadOnlyList<string>, string?, (int Changed, string Message)>? setTagSections = null,
                     Func<(bool Changed, string Message)>? sortTags = null,
                     Func<IReadOnlyList<string>, bool, int, (int Changed, string Message)>? addTagsToImages = null)
    {
        _cfg = cfg;
        _llm = llm;
        _search = search;
        _vocab = vocab;
        _images = images ?? (() => Array.Empty<AgentImage>());
        _selectImage = selectImage ?? (_ => (false, "Switching images is not available here.", null));
        _canBatchLabel = canBatchLabel;
        _labelImages = labelImages;
        _batchProgress = batchProgress;
        _describeTags = describeTags;
        _setTagSections = setTagSections;
        _sortTags = sortTags;
        _addTagsToImages = addTagsToImages;

        // 两个图像工具的兜底都是"如实说没有"：没接上实现时不抛异常、也不假装成功，
        // 免得出现"工具调了、回了句看不懂的话、模型照着编"。
        _imageInfo = imageInfo ?? (() => Task.FromResult(ImageTools.NoImage));
        _viewRegion = viewRegion ?? (_ => Task.FromResult(
            (false, ImageTools.NotAttached, (IReadOnlyList<LlmClient.ChatImage>)Array.Empty<LlmClient.ChatImage>())));
    }

    /// <summary>
    /// 每一轮的 max_tokens。**0 = 自动**：按服务端 /models 声明的能力解析
    /// （<see cref="LlmClient.ResolveBudget"/>）。
    ///
    /// 现算而不是启动时算一次：能力是异步拿到的（<c>EnsureCapsAsync</c>），
    /// 启动那一刻往往还不知道，而"第一次请求就用了兜底值"会让用户以为自动没生效。
    /// </summary>
    public int MaxTokensPerTurn =>
        LlmClient.ResolveBudget(_cfg.AgentMaxTokens, _llm.Caps(_cfg), AppSettings.AgentAutoFallback);

    /// <summary>
    /// 「完全权限」= 允许模型要求写盘（它调 save_caption）。
    /// 真正的写在界面那边、在提案应用之后执行——见 <see cref="TagAgent.IHost.CanSave"/>。
    /// </summary>
    public bool CanSave => _cfg.AgentFullAccess;

    /// <summary>
    /// 能不能用 `label_images`。**由窗口注入**：只有它知道"现在有没有数据集、
    /// 是不是正在批量循环里"（后者防自我递归，见 <see cref="TagAgent.IHost.CanBatchLabel"/>）。
    /// 没接上时一律 false——不下发一个必然失败的工具。
    /// </summary>
    public bool CanBatchLabel => _canBatchLabel?.Invoke() == true;

    public Task<(string Message, bool Cancelled)> LabelImagesAsync(string arguments,
                                                                   CancellationToken ct) =>
        _labelImages is null
            ? Task.FromResult(("Bulk labelling is not available here.", false))
            : _labelImages(arguments, ct);

    /// <summary>给 TagAgent 过滤"设置里关掉的工具"用。</summary>
    public AppSettings? Settings => _cfg;

    /// <summary>
    /// 能不能读标签清单、改类型、规整顺序。与「完全权限」同一个开关：
    /// 改类型会**持久化**一条偏好、规整会改提示词顺序，两者都属于"它自己动手"。
    /// </summary>
    public bool CanEditTags => _cfg.AgentFullAccess;

    public string DescribeTags() =>
        _describeTags?.Invoke() ?? "The tag list is not available here.";

    public (int Changed, string Message) SetTagSections(IReadOnlyList<string> tags, string? section) =>
        _setTagSections is null
            ? (0, "Setting tag sections is not available here.")
            : _setTagSections(tags, section);

    public (bool Changed, string Message) SortTags() =>
        _sortTags is null ? (false, "Reordering is not available here.") : _sortTags();

    public (int Changed, string Message) AddTagsToImages(IReadOnlyList<string> tags,
                                                         bool includeUnlabeled, int limit) =>
        _addTagsToImages is null
            ? (0, "Adding tags across the dataset is not available here.")
            : _addTagsToImages(tags, includeUnlabeled, limit);

    public void ReportBatchProgress(string text)
    {
        try { _batchProgress?.Invoke(text); }
        catch (Exception ex) { Log.Write("batch progress failed: " + ex.Message); }
    }

    /// <summary>写标签模式没有数据集 → 返回空 → 两个图片工具都不会下发。</summary>
    public IReadOnlyList<AgentImage> Images => _images();

    public (bool Ok, string Message, LlmClient.ChatImage? Image) SelectImage(string name) => _selectImage(name);

    public Task<string> ImageInfo() => _imageInfo();

    public Task<(bool Ok, string Message, IReadOnlyList<LlmClient.ChatImage> Images)>
        ViewRegion(string arguments) => _viewRegion(arguments);

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

    /// <summary>
    /// 带工具的一轮。把进度回调透传给 <see cref="LlmClient.ChatWithToolsAsync"/>——
    /// 给了它就走流式，界面能实时看到思考链。
    ///
    /// 温度显式传助手那一档（<see cref="AppSettings.AgentTemperature"/>）：
    /// 这个方法是助手与翻译**共用**的，而两者的温度诉求不同（助手要照做、翻译要稳），
    /// 让它跟着 <c>cfg.Temperature</c> 走就会把翻译的设定渗进助手。
    /// </summary>
    public Task<LlmClient.ToolTurn> TurnAsync(string systemPrompt, IReadOnlyList<AgentTool> tools,
                                              List<JsonObject> messages, int maxTokens,
                                              CancellationToken ct,
                                              Action<string, string>? onProgress = null) =>
        _llm.ChatWithToolsAsync(_cfg, systemPrompt, messages, tools, maxTokens, ct, onProgress,
                                _cfg.AgentTemperature);

    public async Task<string> SearchWebAsync(string query, CancellationToken ct)
    {
        if (!_cfg.SearchEnabled) return "Web search is not enabled in the settings.";

        var res = await _search.SearchAsync(_cfg, query, ct).ConfigureAwait(false);
        if (!res.Ok) return "Web search failed: " + res.Error;

        Log.Write($"agent web search: 「{query}」→ {res.Hits.Count} 条");
        return SearchClient.ToToolContent(res, query);
    }
}
