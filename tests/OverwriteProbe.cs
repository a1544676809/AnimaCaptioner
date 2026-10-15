using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnimaCaptioner.Core;

namespace CoreCheck;

/// <summary>
/// 真实网络的探针：**用户说"把标签覆盖成 X"时，这条路到底做了什么？**
///
/// 起因是一个真实投诉：连着让助手"覆盖"两次，它每次都说覆盖了，而标签列表看着没变。
/// 用户机器上的日志已经指向一个方向（`agent applied: tags+=N` 只增不减），
/// 这个探针把**模型那一侧**也照出来：把它看到的工具结果、发出的 propose 参数原文、
/// 以及对用户的说法三样并排打出来。
///
/// 判据是三条事实，不是感觉：
///   1. 模型**能不能知道现在有哪些标签**（只知道"有 16 个"是没法做替换的）；
///   2. propose 里有没有"替换 / 删除"这个语义（现在只有 add_tags）；
///   3. 它的回复是否宣称做了它做不到的事。
///
/// 会读 settings.json（含解密的 key）打真实请求，故不属于默认套件。
/// </summary>
public static class OverwriteProbe
{
    /// <summary>照用户当时的现场：图里已有 16 个标签（他 caption 的真实内容）。</summary>
    private static readonly string[] ExistingTags =
    {
        "1girl", "solo", "blonde hair", "twintails", "blue eyes", "open mouth", "smile",
        "blush", "choker", "black corset", "cross-laced corset", "bat print", "bat wings",
        "stuffed cat", "upper body", "white background",
    };

    /// <summary>用户当时的原话（大意）。</summary>
    private const string Ask = "把这张图的标签覆盖成：solo, smile, upper body。原来那些不要了。";

    /// <summary>用户要的最终结果：只有这三个。</summary>
    private static readonly string[] Wanted = { "solo", "smile", "upper body" };

    public static int Run()
    {
        Console.OutputEncoding = Encoding.UTF8;

        var cfg = AppSettings.Load();
        Console.WriteLine($"站点 = {cfg.ActiveProfile?.DisplayName}  {cfg.ApiBaseUrl}");
        Console.WriteLine($"模型 = {(string.IsNullOrWhiteSpace(cfg.Model) ? "(local)" : cfg.Model)}");
        Console.WriteLine($"图里现有 {ExistingTags.Length} 个标签：{string.Join(", ", ExistingTags)}");
        Console.WriteLine($"用户说：「{Ask}」");
        Console.WriteLine("用户要的结果：只剩 " + string.Join(", ", Wanted));
        Console.WriteLine();

        var host = new Host(cfg);
        var outcome = new TagAgent().RunAsync(
            host, new[] { new AgentTurn(true, Ask) }, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        Console.WriteLine("---- 模型这一轮实际调了什么 ----");
        for (var i = 0; i < host.Turns.Count; i++)
        {
            var calls = host.Turns[i].Calls;
            Console.WriteLine($"  轮 {i + 1}: " +
                (calls.Count == 0 ? "(没有调用)" : string.Join(", ", calls.Select(c => c.Name))));
            foreach (var c in calls)
            {
                var args = (c.Arguments ?? "").Replace("\n", " ");
                Console.WriteLine($"      {c.Name} {Cut(args, 320)}");
                if (c.Name == AgentKit.ProposeToolName)
                {
                    Console.WriteLine("      ↑ propose 的字段 = " + string.Join(", ", FieldNames(c.Arguments)));
                    Console.WriteLine("      ↑ 有没有 remove/replace 语义 = " + HasReplaceSemantics(c.Arguments));
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("---- 模型看到的工具结果（它做判断的全部依据）----");
        foreach (var t in host.ToolResults)
            Console.WriteLine("  · " + Cut(t.Replace("\n", " ⏎ "), 260));

        Console.WriteLine();
        Console.WriteLine("---- 模型对用户的说法 ----");
        Console.WriteLine("  " + Cut(outcome.Reply.Replace("\n", " "), 400));
        Console.WriteLine();
        Console.WriteLine($"ok={outcome.Ok} rounds={outcome.Rounds} " +
                          $"proposal={(outcome.Proposal is not null)}");

        var bad = 0;

        if (outcome.Proposal is not { } p)
        {
            Console.WriteLine();
            Console.WriteLine("  FAIL 这一轮没有产出提案——用例没被真正跑到（模型拒答或超时），");
            Console.WriteLine("       换一次再跑，别把「没跑到」当成「没问题」。");
            Console.WriteLine();
            Console.WriteLine("=== overwrite: 未能验证 ===");
            return 2;
        }

        {
            var merged = ExistingTags
                .Where(t => !p.Remove.Contains(t, StringComparer.OrdinalIgnoreCase))
                .Concat(p.AddTags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine();
            Console.WriteLine("---- 落地的结果 vs 用户要的 ----");
            Console.WriteLine($"  remove_tags         = {p.Remove.Count} 个：{string.Join(", ", p.Remove)}");
            Console.WriteLine($"  add_tags            = {p.AddTags.Count} 个：{string.Join(", ", p.AddTags)}");
            Console.WriteLine($"  (现有 − 删) ∪ 加     = {merged.Count} 个：{string.Join(", ", merged)}");
            Console.WriteLine($"  用户要的             = {Wanted.Length} 个：{string.Join(", ", Wanted)}");

            // 判据：最终集合**正好**是用户要的三条（既不多也不少）
            var same = merged.Count == Wanted.Length &&
                       merged.All(t => Wanted.Contains(t, StringComparer.OrdinalIgnoreCase));
            if (same)
            {
                Console.WriteLine();
                Console.WriteLine("  ok   覆盖成功：模型用 remove_tags 把该删的列了出来，");
                Console.WriteLine("       落地结果正好是用户要的三条。");
            }
            else
            {
                var extra = merged.Where(t => !Wanted.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                var missing = Wanted.Where(t => !merged.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                Console.WriteLine();
                Console.WriteLine($"  FAIL 对不上：多出 {extra.Count} 个（{string.Join(", ", extra.Take(6))}…），" +
                                  $"缺 {missing.Count} 个（{string.Join(", ", missing)}）");
                Console.WriteLine($"       而它对用户说：「{Cut(outcome.Reply.Replace("\n", " "), 80)}」");
                bad++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== overwrite: {(bad == 0 ? "OK（模型能表达替换，落地结果正确）" : "FAIL")} ===");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>propose 参数里有没有能表达"替换/删除"的字段。</summary>
    private static bool HasReplaceSemantics(string? args)
    {
        var names = FieldNames(args);
        return names.Contains("remove_tags") || names.Contains("replace_tags") ||
               names.Contains("set_tags") || names.Contains("overwrite");
    }

    private static List<string> FieldNames(string? args)
    {
        var names = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(args) ? "{}" : args);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var prop in doc.RootElement.EnumerateObject()) names.Add(prop.Name);
        }
        catch { }
        return names;
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>
    /// 照真实实现的样子接一张图。**关键是 <see cref="AgentImage"/> 只有 TagCount、
    /// 没有标签名**——所以 current_image 那条工具最多只能说"有 16 个标签"。
    /// 工具结果原文从 <c>messages</c> 里捞出来，那才是模型真正看到的。
    /// </summary>
    private sealed class Host : TagAgent.IHost
    {
        private readonly LlmClient _llm;
        private readonly AppSettings _cfg;
        private readonly HashSet<string> _seen = new();

        public Host(AppSettings cfg) { _cfg = cfg; _llm = new LlmClient(); }

        public List<AgentImage> Dataset { get; } = new()
        {
            // **把标签名一起给出去**，照 MainWindow.BuildImageList 现在的样子。
            // 原先这里（和产品一样）只给 TagCount，于是 current_image 只能说"有 16 个"，
            // 模型连该删哪些都列不出来——那正是要验的那个缺陷，探针必须跟着改。
            new AgentImage("优酱-智慧", 0, true, ExistingTags.Length, IsCurrent: true,
                           Tags: ExistingTags),
        };

        public List<LlmClient.ToolTurn> Turns { get; } = new();

        /// <summary>回给模型的工具结果原文（按出现顺序去重）。</summary>
        public List<string> ToolResults { get; } = new();
        /// <summary>探针里用一个定值（产品路径按服务端能力解析）。</summary>
        public int MaxTokensPerTurn => 2400;

        /// <summary>探针不开写盘能力。</summary>
        /// <summary>探针里不提供批量打标。</summary>
        public bool CanBatchLabel => false;

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
            foreach (var m in messages)
            {
                if (m["role"]?.GetValue<string>() != "tool") continue;
                var text = m["content"]?.GetValue<string>() ?? "";
                if (_seen.Add(text)) ToolResults.Add(text);
            }

            var turn = await _llm.ChatWithToolsAsync(_cfg, systemPrompt, messages, tools,
                                                     maxTokens, ct).ConfigureAwait(false);
            Turns.Add(turn);
            return turn;
        }

        public Task<string> SearchWebAsync(string query, CancellationToken ct) =>
            Task.FromResult("web search is off in this probe");

        public (bool Ok, string Message) SelectImage(string name) =>
            (false, "image switching is off in this probe");

        public Task<string> ImageInfo() => Task.FromResult(
            "Current image: 优酱-智慧 (1 of 1). File: image/png, 280 KB, stored 571x571 " +
            $"(no EXIF rotation). Caption: it already has {ExistingTags.Length} tag(s). No copy " +
            "of this image is attached to this conversation: you cannot see it.");

        public Task<(bool Ok, string Message, IReadOnlyList<LlmClient.ChatImage> Images)>
            ViewRegion(string arguments) => Task.FromResult((false,
                "The current image is NOT attached to this conversation.",
                (IReadOnlyList<LlmClient.ChatImage>)Array.Empty<LlmClient.ChatImage>()));
    }
}
