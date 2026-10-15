using System.Text;
using System.Text.Json.Nodes;
using AnimaCaptioner.Core;

namespace CoreCheck;

/// <summary>
/// 真实网络的探针：**在带工具的多轮对话里，后插的一条图文 user 消息，模型到底看不看得见？**
///
/// 为什么非要先问这一句：`view_region` 的做法是"裁完往 messages 里插一条带图的 user 消息"
/// （工具结果只能是文本，这是 OpenAI 兼容协议的形状）。但有的网关会把这种消息当成
/// "用户新说的话"、有的干脆忽略图片部分。**不验证就写，很容易做出一个"看着像实现了、
/// 模型其实没看见图"的东西**——症状是它照样回答，只是答得和没给图一样，最难分辨。
///
/// 三个场景，判据全是"模型说出的字符串对不对"：
///   1. 图挂在**第一条** user 消息上（既有功能，作为对照组）。
///   2. 图挂在**后插的一条** user 消息上（view_region 的形状）← 真正要回答的问题。
///   3. 一条消息里**两张图**（tiles 的形状）。问它第二张写的是什么——
///      这能区分"全看见了"和"只看了第一张"。
///
/// 探针图片由 tools/imgprobe 之外的一个小脚本生成（四象限纯色 + 白底大黑字），
/// 目录默认 %TEMP%\ac-imgprobe，可用参数或 AC_PROBEIMG 指定。
/// 会读 settings.json（含解密的 key）打真实请求，所以不属于默认套件。
/// </summary>
public static class RegionProbe
{
    private const string SystemPrompt =
        "You are a test subject. Read the text that is written in the image you are shown. " +
        "Answer with that text only — no sentence, no punctuation, no explanation. " +
        "Never call a tool: there is nothing to call.";

    /// <summary>一个只声明、不该被调用的工具。带上它才构成"带工具的多轮对话"这个前提。</summary>
    private static readonly AgentTool DummyTool = new(
        "noop",
        "This tool does nothing. Never call it.",
        new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });

    public static int Run(string? imgDir)
    {
        Console.OutputEncoding = Encoding.UTF8;

        imgDir ??= Environment.GetEnvironmentVariable("AC_PROBEIMG");
        if (string.IsNullOrWhiteSpace(imgDir))
            imgDir = Path.Combine(Path.GetTempPath(), "ac-imgprobe");
        Console.WriteLine("图片目录: " + imgDir);

        var s1 = Path.Combine(imgDir, "secret1.jpg");
        var s2 = Path.Combine(imgDir, "secret2.jpg");
        if (!File.Exists(s1) || !File.Exists(s2))
        {
            Console.WriteLine($"找不到探针图片（需要 secret1.jpg / secret2.jpg）。");
            Console.WriteLine("先跑 make.py 生成，或用 AC_PROBEIMG 指向那个目录。");
            return 2;
        }

        var cfg = AppSettings.Load();
        var pick = Environment.GetEnvironmentVariable("AC_PROFILE");

        // 选站点：AC_PROFILE 可以给 id 前缀、名字或序号；不给就用当前活动站点。
        if (!string.IsNullOrWhiteSpace(pick))
        {
            var hit = cfg.Profiles.FirstOrDefault(p =>
                          p.Id.StartsWith(pick, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(p.DisplayName, pick, StringComparison.OrdinalIgnoreCase))
                      ?? (int.TryParse(pick, out var idx) && idx >= 0 && idx < cfg.Profiles.Count
                          ? cfg.Profiles[idx] : null);
            if (hit is null) { Console.WriteLine($"AC_PROFILE={pick} 没匹配到站点。"); return 2; }
            cfg.ActiveProfileId = hit.Id;
        }

        Console.WriteLine($"站点      = {cfg.ActiveProfile?.DisplayName} {cfg.ApiBaseUrl}");
        Console.WriteLine($"模型      = {cfg.Model}");

        var llm = new LlmClient();
        var caps = llm.EnsureCapsAsync(cfg).GetAwaiter().GetResult();
        Console.WriteLine($"能吃图    = {(caps is null ? "(服务端未声明)" : caps.SupportsImage?.ToString() ?? "(服务端未声明)")}" +
                          $"，输入模态 = {(caps?.InputModalities is null ? "(未报)" : string.Join("/", caps.InputModalities))}");
        Console.WriteLine();

        var img1 = new LlmClient.ChatImage("image/jpeg", File.ReadAllBytes(s1));
        var img2 = new LlmClient.ChatImage("image/jpeg", File.ReadAllBytes(s2));

        var bad = 0;
        bad += Scenario(llm, cfg, "1. 图挂在第一条 user 消息上（对照组）",
                        FirstMessages(img1), "PANGOLIN-42");
        bad += Scenario(llm, cfg, "2. 图挂在后插的一条 user 消息上（view_region 的形状）",
                        LaterMessages(img2), "WOMBAT-77");
        bad += Scenario(llm, cfg, "3. 一条消息里两张图，问它第二张（tiles 的形状）",
                        TwoImageMessages(img1, img2), "WOMBAT-77");
        bad += ProductionLoop(llm, cfg, imgDir);

        Console.WriteLine($"\n=== region: {(bad == 0 ? "OK（三种形状模型都看得见图，真实循环里也调得动）" : bad + " 项 FAILED")} ===");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>
    /// 第 4 个场景：**走产品代码**的完整一轮。
    ///
    /// 前三节是手拼报文——它们证明"网关看得见图"，但证明不了"产品拼出来的报文
    /// 网关也看得见"。这一节把真正的 <see cref="TagAgent"/> 跑起来（真实的系统提示、
    /// 真实的工具声明、真实的插入时机），只把"裁剪"换成读一个现成的文件：
    ///
    /// - 原图是一张**纯白**图，上面一个字都没有；
    /// - 模型要的那个区域回的是写好 "WOMBAT-77" 的图（尺寸按真实裁剪的目标给）。
    ///
    /// 所以它要是答出 WOMBAT-77，那只可能来自那条后插的图文 user 消息。
    /// 另外还直接检查发出去的报文里确实有那一条（模型不听话时也能看出是哪一层断的）。
    /// </summary>
    private static int ProductionLoop(LlmClient llm, AppSettings cfg, string imgDir)
    {
        Console.WriteLine("── 4. 真实 TagAgent 循环：模型自己调 view_region 去认字");

        var blank = Path.Combine(imgDir, "blank.jpg");
        var crop = Path.Combine(imgDir, "secret2-crop.jpg");
        if (!File.Exists(blank) || !File.Exists(crop))
        {
            Console.WriteLine("   FAIL 缺 blank.jpg / secret2-crop.jpg（先跑 make_probe_images.py）");
            return 1;
        }

        var host = new LiveHost(llm, cfg)
        {
            Crop = new LlmClient.ChatImage("image/jpeg", File.ReadAllBytes(crop)),
        };
        host.Dataset.Add(new AgentImage("probe", 0, false, 0, IsCurrent: true));

        var image = new LlmClient.ChatImage("image/jpeg", File.ReadAllBytes(blank));
        var outcome = new TagAgent().RunAsync(
            host, new[] { new AgentTurn(true, "Look at the picture. What text is written on the tiny sign? " +
                                             "If you cannot read it, zoom in on it first.") },
            image, CancellationToken.None).GetAwaiter().GetResult();

        var inserted = host.SeenMessages
            .SelectMany(ms => ms)
            .Where(m => m["role"]?.GetValue<string>() == "user" && m["content"] is JsonArray)
            .Any(m => ((JsonArray)m["content"]!)
                      .OfType<JsonObject>()
                      .Any(p => p["type"]?.GetValue<string>() == "text" &&
                                (p["text"]?.GetValue<string>() ?? "")
                                    .Contains("enlarged region", StringComparison.OrdinalIgnoreCase)));

        // 逐轮把"这一轮报文里有几张图、模型调了什么、回了什么"打出来。
        // 出问题时（模型没看见放大的图）只有这张表能指出断在哪一层：
        // 是图没插进报文（产品的错），还是插进去了模型没看（模型的事）。
        for (var i = 0; i < host.SeenMessages.Count; i++)
        {
            var imgs = host.SeenMessages[i]
                .Where(m => m["content"] is JsonArray)
                .Sum(m => ((JsonArray)m["content"]!).OfType<JsonObject>()
                    .Count(p => p["type"]?.GetValue<string>() == "image_url"));
            var calls = i < host.SeenCalls.Count
                ? string.Join(", ", host.SeenCalls[i].Select(c => c.Name))
                : "(无)";
            var text = i < host.SeenContent.Count ? host.SeenContent[i].Replace("\n", " ") : "";
            Console.WriteLine($"   轮 {i + 1}: 报文里 {imgs} 张图，调用[{calls}]，正文=\"{Cut(text, 60)}\"");
        }

        Console.WriteLine($"   view_region 调用 = {host.ViewRequests.Count}；" +
                          $"报文中带「enlarged region」的图文消息 = {(inserted ? "有" : "没有")}");
        Console.WriteLine($"   回答 = \"{Cut(outcome.Reply.Replace("\n", " "), 120)}\"");

        var ok = true;
        if (host.ViewRequests.Count == 0)
        {
            Console.WriteLine("   FAIL 模型没调 view_region——这个场景没被真正执行（换模型或重跑）");
            ok = false;
        }
        if (!inserted)
        {
            Console.WriteLine("   FAIL 发出去的报文里没有后插的那条图文消息" +
                              "（就是这条让模型看见放大结果）");
            ok = false;
        }
        if (!outcome.Reply.Contains("WOMBAT-77", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("   FAIL 没读出放大图里的字（期望回答里有 WOMBAT-77）");
            ok = false;
        }
        if (ok) Console.WriteLine("   ok   真实循环里模型调了工具、拿到了图、读出了字");
        Console.WriteLine();
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 假 host 里唯一"真"的东西就是网络：<see cref="TagAgent.IHost.TurnAsync"/> 直接
    /// 打真实接口。其余三件（词库、搜索、裁图）都换成可控的实现——
    /// 这一节要验的是"产品拼出来的报文"，不是词库。
    /// </summary>
    private sealed class LiveHost : TagAgent.IHost
    {
        private readonly LlmClient _llm;
        private readonly AppSettings _cfg;

        public LiveHost(LlmClient llm, AppSettings cfg) { _llm = llm; _cfg = cfg; }

        public List<AgentImage> Dataset { get; } = new();
        public List<string> ViewRequests { get; } = new();
        public List<List<JsonObject>> SeenMessages { get; } = new();

        /// <summary>每一轮模型发起的调用名，以及它写下的正文——用来定位断在哪一层。</summary>
        public List<List<LlmClient.ToolCall>> SeenCalls { get; } = new();
        public List<string> SeenContent { get; } = new();

        public LlmClient.ChatImage? Crop { get; set; }
        /// <summary>探针里用一个定值（产品路径按服务端能力解析）。</summary>
        public int MaxTokensPerTurn => 2400;

        /// <summary>探针不开写盘能力。</summary>
        /// <summary>探针里不提供批量打标。</summary>
        /// <summary>探针里不开标签编辑能力。</summary>
        /// <summary>探针不过滤工具开关。</summary>
        /// <summary>探针里不提供批量补标签。</summary>
        public (int Changed, string Message) AddTagsToImages(IReadOnlyList<string> tags, bool includeUnlabeled, int limit) =>
            (0, "bulk add tags is off in this probe");
        public AppSettings? Settings => null;
        public bool CanEditTags => false;

        public string DescribeTags() => "tags are off in this probe";

        public (int Changed, string Message) SetTagSections(IReadOnlyList<string> tags, string? section) =>
            (0, "setting sections is off in this probe");

        public (bool Changed, string Message) SortTags() => (false, "sorting is off in this probe");
        public bool CanBatchLabel => false;

        // 这个探针验的正是"切图之后模型看不看得见新图"，所以切图必须可用。
        // 但它不逐张走：逐张走要"当场落盘"，这个探针没有那套界面状态。
        public bool CanSwitchImages => true;
        public bool CanApplyNow => false;

        public (string Message, bool Ok) ApplyProposalNow(AgentProposal proposal) =>
            ("applying immediately is off in this probe", false);

        public Task<(string Message, bool Cancelled)> LabelImagesAsync(string arguments, CancellationToken ct) =>
            Task.FromResult(("bulk labelling is off in this probe", false));

        public void ReportBatchProgress(string text) { }
        public bool CanSave => false;

        public (bool Ok, string Message) Save() => (false, "saving is off in this probe");

        public bool CanWebSearch => false;
        public IReadOnlyList<AgentImage> Images => Dataset;

        public bool IsKnown(string tag) => true;

        public string Search(string query, out List<TagSearch.Hit> hits, out bool splitMatch)
        {
            hits = new List<TagSearch.Hit>();
            splitMatch = false;
            return $"query={query} hits=0";
        }

        public async Task<LlmClient.ToolTurn> TurnAsync(string systemPrompt,
                                                        IReadOnlyList<AgentTool> tools,
                                                        List<JsonObject> messages, int maxTokens,
                                                        CancellationToken ct,
                                                        Action<string, string>? onProgress = null)
        {
            SeenMessages.Add(messages.Select(m => (JsonObject)m.DeepClone()).ToList());
            var turn = await _llm.ChatWithToolsAsync(_cfg, systemPrompt, messages, tools,
                                                     maxTokens, ct).ConfigureAwait(false);
            SeenCalls.Add(turn.Calls);
            SeenContent.Add(turn.Content ?? "");
            return turn;
        }

        public Task<string> SearchWebAsync(string query, CancellationToken ct) =>
            Task.FromResult("web search is off in this probe");
        /// <summary>切图：探针里返回"成功但没有图"。</summary>
        public Task<(bool Ok, string Message, LlmClient.ChatImage? Image)> SelectImageAsync(
            string name, CancellationToken ct) =>
            Task.FromResult<(bool, string, LlmClient.ChatImage?)>(
                (true, $"Switched to \"{name}\".", null));

        public Task<string> ImageInfo() => Task.FromResult(
            "Current image: probe (1 of 1). File: image/jpeg, 12 KB, stored 1200x800 " +
            "(no EXIF rotation). A copy is attached, scaled to 1024x683: anything smaller " +
            "than about 2 original pixels is not resolvable. Use view_region.");

        public Task<(bool Ok, string Message, IReadOnlyList<LlmClient.ChatImage> Images)>
            ViewRegion(string arguments)
        {
            ViewRequests.Add(arguments);
            if (Crop is null)
                return Task.FromResult((false, "no crop available",
                    (IReadOnlyList<LlmClient.ChatImage>)Array.Empty<LlmClient.ChatImage>()));

            return Task.FromResult((true,
                "Enlarged region attached below as a NEW user message: x=0.5 y=0 w=0.5 h=0.25 " +
                "of \"probe\". Look at the new user message before you answer.",
                (IReadOnlyList<LlmClient.ChatImage>)new[] { Crop }));
        }
    }

    /// <summary>跑一个场景并判定。判据是"回答里有没有那个字符串"——不猜、不解释。</summary>
    private static int Scenario(LlmClient llm, AppSettings cfg, string label,
                                List<JsonObject> messages, string expect)
    {
        Console.WriteLine("── " + label);
        var turn = llm.ChatWithToolsAsync(cfg, SystemPrompt, messages,
                                          new[] { DummyTool }, 2000)
                      .GetAwaiter().GetResult();

        var answer = (turn.Content ?? "").Trim().Replace("\n", " ");
        Console.WriteLine($"   调用工具 = {turn.Calls.Count}，回答 = \"{Cut(answer, 120)}\"");
        if (!turn.Ok)
        {
            Console.WriteLine($"   FAIL 请求失败：{turn.Error}");
            return 1;
        }
        if (turn.Calls.Count > 0)
        {
            Console.WriteLine("   FAIL 它去调工具了，这个场景没被真正执行（重跑或换个模型）");
            return 1;
        }

        var ok = answer.Contains(expect, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine(ok ? $"   ok   看见了（说对了 {expect}）"
                             : $"   FAIL 没看见：期望回答里含 {expect}");
        Console.WriteLine();
        return ok ? 0 : 1;
    }

    /// <summary>图挂在第一条 user 消息上——既有功能的样子。</summary>
    private static List<JsonObject> FirstMessages(LlmClient.ChatImage image) => new()
    {
        LlmClient.UserMessage("What exact text is written in this image? Reply with the text only.",
                              image),
    };

    /// <summary>
    /// view_region 的形状：assistant 要工具 → tool 回一句文字 → **再插一条带图的 user 消息**。
    /// 顺序不能变：工具结果必须紧跟在要它的 assistant 消息后面。
    /// </summary>
    private static List<JsonObject> LaterMessages(LlmClient.ChatImage image) => new()
    {
        new JsonObject { ["role"] = "user", ["content"] = "Please enlarge a region of the picture for me." },
        LlmClient.AssistantMessage("", new List<LlmClient.ToolCall>
        {
            new("probe1", "noop", "{}"),
        }),
        LlmClient.ToolMessage("probe1", "The enlarged region is attached as a new user message."),
        LlmClient.UserMessage("Here is the enlarged region you asked for. What EXACT text is " +
                              "written in THIS attached image? Reply with the text only.", image),
    };

    /// <summary>tiles 的形状：一条 user 消息里两张图。问第二张，区分"只看了第一张"。</summary>
    private static List<JsonObject> TwoImageMessages(LlmClient.ChatImage a, LlmClient.ChatImage b) =>
        new()
        {
            LlmClient.UserMessage("Two images are attached: the first, then the second. What EXACT " +
                                  "text is written in the SECOND image only? Reply with the text only.",
                                  new[] { a, b }),
        };

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
