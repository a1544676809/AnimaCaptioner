using System.Text.Json.Nodes;
using AnimaCaptioner.Core;

namespace CoreCheck;

/// <summary>
/// AI 助手（读图 / 查库 / 提案）的回归。
///
/// 三块都是**纯逻辑**，所以不需要网络和词库：
///   1. <see cref="ImageSizing"/>：发图前的尺寸数学。比例算错会让模型看到的图被
///      拉扁，而界面上完全看不出来。
///   2. <see cref="AgentKit"/>：工具声明、系统提示、把模型的 propose 参数解析成
///      提案。第 3 节对着**真实会出现的畸形参数**断言（小模型很爱把多个标签塞进
///      一个字符串、或者漏掉字段）。
///   3. <see cref="TagAgent"/> 的循环控制流：何时停、轮数用完怎么办、未知工具名
///      怎么办。用一个假 host 驱动，不需要服务器。
/// </summary>
public static class Agent
{
    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var bad = 0;

        void Eq<T>(string label, T actual, T expected)
        {
            var ok = EqualityComparer<T>.Default.Equals(actual, expected);
            if (!ok) bad++;
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {label,-46} {actual}" +
                              (ok ? "" : $"   (期望 {expected})"));
        }

        void True(string label, bool cond)
        {
            if (!cond) bad++;
            Console.WriteLine($"  {(cond ? "ok  " : "FAIL")} {label}");
        }

        // ---------- 1. 发图前的尺寸 ----------
        Console.WriteLine("=== 1. ImageSizing：长边 1024，比例不能变 ===");
        Eq("训练集竖图 3258x3978 → 长边 1024", ImageSizing.Fit(3258, 3978), (839, 1024));
        Eq("横图 4000x3000 → 长边 1024", ImageSizing.Fit(4000, 3000), (1024, 768));
        Eq("正方形 4096x4096", ImageSizing.Fit(4096, 4096), (1024, 1024));
        Eq("已经小于上限的原样返回（不放大）", ImageSizing.Fit(800, 600), (800, 600));
        Eq("恰好等于上限", ImageSizing.Fit(1024, 500), (1024, 500));
        Eq("极端窄图不塌成 0", ImageSizing.Fit(8000, 3), (1024, 1));
        Eq("非法尺寸", ImageSizing.Fit(0, 100), (0, 0));

        Console.WriteLine("\n=== 2. EXIF 方向：5–8 要先把宽高换过来 ===");
        Eq("方向 1（正常）不换", ImageSizing.Oriented(4000, 3000, 1), (4000, 3000));
        Eq("方向 6（顺时针 90°）换", ImageSizing.Oriented(4000, 3000, 6), (3000, 4000));
        Eq("方向 8（逆时针 90°）换", ImageSizing.Oriented(4000, 3000, 8), (3000, 4000));
        Eq("方向 3（180°）不换", ImageSizing.Oriented(4000, 3000, 3), (4000, 3000));
        // 组合：竖拍图的 EXIF 让它自称 4000x3000，换过来才是真的竖图
        var (ow, oh) = ImageSizing.Oriented(4000, 3000, 6);
        Eq("换过之后再缩，长边落在高度上", ImageSizing.Fit(ow, oh), (768, 1024));

        // ---------- 3. 工具集 ----------
        Console.WriteLine("\n=== 3. 工具集 ===");
        Eq("不联网时不发 web_search", string.Join("|", AgentKit.Tools(false).Select(t => t.Name)),
           "search_tags|propose");
        Eq("联网时三件都在，propose 在最后", string.Join("|", AgentKit.Tools(true).Select(t => t.Name)),
           "search_tags|web_search|propose");
        True("search_tags 与「问模型找标签」共用同一段说明",
             AgentKit.SearchTool.Description == TagSearchAgent.ToolDescription);
        True("每个工具都有 object 参数块",
             AgentKit.Tools(true).All(t => t.Parameters["type"]?.GetValue<string>() == "object"));

        // ---------- 4. propose 参数解析 ----------
        Console.WriteLine("\n=== 4. ParseProposal：模型的手抖都要接住 ===");
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "1girl", "solo", "silver hair" };
        bool IsKnown(string t) => known.Contains(t);

        var p1 = AgentKit.ParseProposal("""
            {"add_tags":["1girl","silver hair"],"prose":"She stands under a tree.",
             "replace_prose":false,"note":"按画面补的"}
            """, IsKnown);
        Eq("标签数", p1.AddTags.Count, 2);
        Eq("词库认得的分到 Known", string.Join("|", p1.Known), "1girl|silver hair");
        Eq("Unknown 为空", p1.Unknown.Count, 0);
        Eq("散文", p1.Prose, "She stands under a tree.");
        Eq("替换标志", p1.ReplaceProse, false);
        Eq("说明", p1.Note, "按画面补的");
        True("HasTags / HasProse", p1.HasTags && p1.HasProse);

        // 小模型很爱把多个标签塞进一个字符串，甚至带上反引号或项目符号
        var p2 = AgentKit.ParseProposal("""
            {"add_tags":"- `1girl`, *solo*, `@syrstyle`","prose":"","note":""}
            """, IsKnown);
        Eq("单个字符串按逗号拆开", string.Join("|", p2.AddTags), "1girl|solo|@syrstyle");
        Eq("查不到的进 Unknown，不丢", string.Join("|", p2.Unknown), "@syrstyle");
        True("@ 开头的触发词保留为标签（不当散文）", p2.Unknown.Contains("@syrstyle"));

        var p3 = AgentKit.ParseProposal("""
            {"add_tags":["1girl","1GIRL"," 1girl "],"prose":"  x  ","replace_prose":true}
            """, IsKnown);
        Eq("忽略大小写去重", p3.AddTags.Count, 1);
        Eq("散文两端空白被去掉", p3.Prose, "x");
        Eq("replace_prose=true 被读到", p3.ReplaceProse, true);

        var p4 = AgentKit.ParseProposal("""{"note":"我只想说句话"}""", IsKnown);
        Eq("字段缺失 → 空提案", p4.IsEmpty, true);
        Eq("字段缺失 → 说明仍在", p4.Note, "我只想说句话");

        var p5 = AgentKit.ParseProposal("这不是 JSON", IsKnown);
        Eq("参数不是 JSON → 空提案（绝不退回解析模型的话）", p5.IsEmpty, true);

        var p6 = AgentKit.ParseProposal("""{"add_tags":[],"prose":""}""", IsKnown);
        Eq("空数组 + 空散文 → 空提案", p6.IsEmpty, true);

        var p7 = AgentKit.ParseProposal("""{"add_tags":["1girl"],"prose":"a","replace_prose":"yes"}""", IsKnown);
        Eq("replace_prose 不是布尔就当没给", p7.ReplaceProse, false);

        var p8 = AgentKit.ParseProposal("""{"add_tags":["1girl"],"prose":"a"}""", null);
        Eq("没有词库时全部算 Unknown（不崩）", string.Join("|", p8.Unknown), "1girl");

        // ---------- 5. 系统提示 ----------
        Console.WriteLine("\n=== 5. 系统提示：几条承重规则必须在 ===");
        var withImage = AgentKit.SystemPrompt(hasImage: true, canWebSearch: true);
        var noImage = AgentKit.SystemPrompt(hasImage: false, canWebSearch: false);
        True("要求先查 search_tags", withImage.Contains("search_tags"));
        True("要求收尾调 propose", withImage.Contains(AgentKit.ProposeToolName));
        True("禁止凭记忆写标签名", withImage.Contains("from memory"));
        True("没有对应标签时走散文", withImage.Contains("DO NOT invent a tag"));
        True("@ 开头留在标签里", withImage.Contains("artist trigger word"));
        True("带图时要求只写看得见的", withImage.Contains("ACTUALLY see"));
        True("不带图时禁止描述没见过的画面", noImage.Contains("never describe a picture you have not seen"));
        True("不带图时不会说图已附上", !noImage.Contains("image from the user's dataset is attached"));
        True("没开搜索时明确说没有这个工具", noImage.Contains("web search tool is not available"));
        True("开了搜索时不发那句", !withImage.Contains("web search tool is not available"));
        True("质量/分数/安全词不擅自加", withImage.Contains("masterpiece"));

        // ---------- 6. 循环控制流（假 host） ----------
        Console.WriteLine("\n=== 6. TagAgent 循环：先查再提案 ===");
        var host = new FakeHost();
        host.Known = IsKnown;
        host.Results["white dress"] = new()
        {
            new TagSearch.Hit("white dress", "白色连衣裙", "general", 12000, true, false, "general"),
        };
        host.Turn(Tool("c1", AgentKit.SearchToolName, """{"query":"white dress"}"""))
            .Turn(Tool("c2", AgentKit.ProposeToolName,
                """{"add_tags":["1girl","white dress"],"prose":"She stands under a tree.","note":"看画面补的"}"""));

        var outcome = Run(host, "照这张图补标签");
        Eq("成功", outcome.Ok, true);
        Eq("轮数 = 2", outcome.Rounds, 2);
        True("拿到提案", outcome.Proposal is not null);
        Eq("提案标签", string.Join("|", outcome.Proposal!.AddTags), "1girl|white dress");
        Eq("查过一次", string.Join("|", host.Searches), "white dress");
        Eq("两次请求都带上了工具", string.Join(";", host.SeenTools.Select(t => string.Join("+", t))),
           "search_tags+web_search+propose;search_tags+web_search+propose");
        True("回复用模型这句话", outcome.Reply.Length > 0);

        Console.WriteLine("\n=== 7. 提案就停：同一轮里后面的调用不再执行 ===");
        var host2 = new FakeHost();
        host2.Turn(new LlmClient.ToolTurn(true, "", "", new List<LlmClient.ToolCall>
        {
            new("a", AgentKit.ProposeToolName, """{"add_tags":["1girl"]}"""),
            new("b", AgentKit.SearchToolName, """{"query":"never"}"""),
        }, "", ""));
        var o2 = Run(host2, "随便");
        Eq("只发了一次请求", host2.SeenTools.Count, 1);
        Eq("没有执行后面的查询", host2.Searches.Count, 0);
        True("仍然拿到了提案", o2.Proposal is not null);
        True("回复是程序补的（模型没写正文）", o2.Reply.Contains("1 个标签"));

        Console.WriteLine("\n=== 8. 只回答、不提案 ===");
        var host3 = new FakeHost();
        host3.Turn(new LlmClient.ToolTurn(true, "这个词库里没有，建议改成 `solo`。", "", new(), "", ""));
        var o3 = Run(host3, "solo 有吗");
        Eq("成功", o3.Ok, true);
        Eq("没有提案", o3.Proposal is null, true);
        Eq("回答原样带出", o3.Reply, "这个词库里没有，建议改成 `solo`。");

        Console.WriteLine("\n=== 9. 未知工具名：如实回一句，让它下一轮改对 ===");
        var host4 = new FakeHost();
        host4.Turn(Tool("x", "lookup_pixiv", """{"query":"x"}"""))
             .Turn(Tool("y", AgentKit.ProposeToolName, """{"add_tags":["solo"]}"""));
        var o4 = Run(host4, "试试");
        True("还是成功拿到了提案", o4.Ok && o4.Proposal is not null);
        var unknownReply = host4.SeenMessages[1]
            .First(m => m["role"]?.GetValue<string>() == "tool")["content"]!.GetValue<string>();
        True("回给模型的消息里列出了可用工具",
             unknownReply.Contains("Unknown tool") && unknownReply.Contains("search_tags") &&
             unknownReply.Contains("propose"));

        Console.WriteLine("\n=== 10. 空查询：不拿用户的整句话去查 ===");
        var host5 = new FakeHost();
        host5.Turn(Tool("q", AgentKit.SearchToolName, """{"query":"   "}"""))
             .Turn(Tool("r", AgentKit.ProposeToolName, """{"add_tags":[]}"""));
        var o5 = Run(host5, "一整句话的描述，不该被当成查询发出去");
        Eq("没有真的查过词库", host5.Searches.Count, 0);
        True("成功了（要求重试后拿到提案）", o5.Ok);

        Console.WriteLine("\n=== 11. 轮数用完：收尾那一次不带工具，但图还在 ===");
        var host6 = new FakeHost();
        // 前 4 轮一直查（把轮数用光），第 5 次是收尾请求——它不带工具，所以
        // 假 host 这里排的是一句纯文本回答，和真实模型的行为一致。
        for (var i = 0; i < 4; i++)
            host6.Turn(Tool("c" + i, AgentKit.SearchToolName, """{"query":"loop"}"""));
        host6.Turn(new LlmClient.ToolTurn(true, "词库里只有 loop 相关的几种写法。", "", new(), "", ""));
        var image = new LlmClient.ChatImage("image/jpeg", new byte[] { 1, 2, 3, 4 });
        var o6 = Run(host6, "一直查下去", image);
        Eq("4 次带工具的请求 + 1 次收尾", host6.SeenTools.Count, 5);
        Eq("收尾那次工具列表为空", host6.SeenTools[^1].Count, 0);
        Eq("收尾那次的文本成为回答", o6.Reply, "词库里只有 loop 相关的几种写法。");
        Eq("收尾那次成功", o6.Ok, true);
        var first = host6.SeenMessages[^1][0]["content"];
        True("收尾请求里第一轮的图还在（content 是 parts 数组）",
             first is JsonArray parts && parts.Count == 2 &&
             parts[1]!["type"]!.GetValue<string>() == "image_url");
        True("收尾请求最后一条是要求它作答的 user 消息",
             host6.SeenMessages[^1][^1]["role"]!.GetValue<string>() == "user");

        Console.WriteLine("\n=== 12. 带图请求的形状 ===");
        var host7 = new FakeHost();
        host7.Turn(Tool("z", AgentKit.ProposeToolName, """{"add_tags":[]}"""));
        Run(host7, "这张图里有什么", image);
        var userContent = host7.SeenMessages[0][0]["content"];
        Eq("首条 user 消息是 parts 数组", userContent is JsonArray ? "yes" : "no", "yes");
        var arr = (JsonArray)userContent!;
        Eq("parts 数", arr.Count, 2);
        Eq("text 在前", arr[0]!["type"]!.GetValue<string>(), "text");
        Eq("image_url 在后", arr[1]!["type"]!.GetValue<string>(), "image_url");
        var uri = arr[1]!["image_url"]!["url"]!.GetValue<string>();
        True("data URI 前缀正确", uri.StartsWith("data:image/jpeg;base64,"));
        Eq("base64 解回来是原来的字节", Convert.ToBase64String(image.Bytes),
           uri["data:image/jpeg;base64,".Length..]);
        True("带图时系统提示按带图那一份走", host7.SeenPrompts[0].Contains("ACTUALLY see"));

        Console.WriteLine("\n=== 13. 失败如实报出来 ===");
        var host8 = new FakeHost();
        host8.Turn(new LlmClient.ToolTurn(false, "", "", new(), "", "HTTP 400 Bad Request"));
        var o8 = Run(host8, "试试");
        Eq("不成功", o8.Ok, false);
        Eq("错误原样带出", o8.Error, "HTTP 400 Bad Request");
        Eq("没有提案", o8.Proposal is null, true);

        Console.WriteLine($"\n=== agent: {(bad == 0 ? "OK" : bad + " FAILED")} ===");
        return bad == 0 ? 0 : 1;
    }

    private static LlmClient.ToolTurn Tool(string id, string name, string args) =>
        new(true, "", "", new List<LlmClient.ToolCall> { new(id, name, args) }, "", "");

    /// <summary>同步包一层，免得每一节都写 async/await 样板。</summary>
    private static TagAgent.Outcome Run(FakeHost host, string text,
                                        LlmClient.ChatImage? image = null) =>
        new TagAgent().RunAsync(host, new[] { new AgentTurn(true, text) }, image)
                      .GetAwaiter().GetResult();

    /// <summary>
    /// 假 host：把每一轮要回什么都先排好，同时把"agent 到底发了什么"记下来——
    /// 第 11、12 节断言的就是这个（带图和不带工具的收尾请求最容易在重构里被弄丢）。
    /// </summary>
    private sealed class FakeHost : TagAgent.IHost
    {
        private readonly Queue<LlmClient.ToolTurn> _turns = new();

        public List<List<string>> SeenTools { get; } = new();
        public List<List<JsonObject>> SeenMessages { get; } = new();
        public List<string> SeenPrompts { get; } = new();
        public List<string> Searches { get; } = new();
        public Dictionary<string, List<TagSearch.Hit>> Results { get; } = new();
        public Func<string, bool> Known { get; set; } = _ => true;

        public bool CanWebSearch { get; set; } = true;

        public FakeHost Turn(LlmClient.ToolTurn turn)
        {
            _turns.Enqueue(turn);
            return this;
        }

        public bool IsKnown(string tag) => Known(tag);

        public string Search(string query, out List<TagSearch.Hit> hits, out bool splitMatch)
        {
            Searches.Add(query);
            hits = Results.TryGetValue(query, out var h) ? h : new List<TagSearch.Hit>();
            splitMatch = false;
            return $"query={query} hits={hits.Count}";
        }

        public Task<LlmClient.ToolTurn> TurnAsync(string systemPrompt,
                                                  IReadOnlyList<AgentTool> tools,
                                                  List<JsonObject> messages, int maxTokens,
                                                  CancellationToken ct)
        {
            SeenPrompts.Add(systemPrompt);
            SeenTools.Add(tools.Select(t => t.Name).ToList());
            SeenMessages.Add(messages.Select(m => (JsonObject)m.DeepClone()).ToList());

            // 脚本用完就当成一次失败：能立刻看出"循环多发了一次请求"，
            // 而不是拿最后一条反复应答把问题掩盖过去。
            return Task.FromResult(_turns.Count > 0
                ? _turns.Dequeue()
                : new LlmClient.ToolTurn(false, "", "", new(), "", "fake host: no scripted turn left"));
        }

        public Task<string> SearchWebAsync(string query, CancellationToken ct) =>
            Task.FromResult("web:" + query);
    }
}
