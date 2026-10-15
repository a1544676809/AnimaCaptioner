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

        Console.WriteLine("\n=== 11. 轮数用完：收尾只给 propose（不能再搜，但仍能提案）===");
        var host6 = new FakeHost();
        // 一直查到轮数用光（上限见 TagAgent.MaxRounds），随后是收尾请求。
        // **收尾那一轮现在只给 propose**——不是空工具列表。原来的空列表导致模型
        // 只能把标签写成一段话，用户看到"由于工具调用已用尽，无法再调用 propose，
        // 请手动录入"，而提案正是这一路唯一的产出。
        for (var i = 0; i < 8; i++)
            host6.Turn(Tool("c" + i, AgentKit.SearchToolName, """{"query":"loop"}"""));
        host6.Turn(new LlmClient.ToolTurn(true, "词库里只有 loop 相关的几种写法。", "", new(), "", ""));
        var image = new LlmClient.ChatImage("image/jpeg", new byte[] { 1, 2, 3, 4 });
        var o6 = Run(host6, "一直查下去", image, maxRounds: 8);
        Eq("8 次带工具的请求 + 1 次收尾", host6.SeenTools.Count, 9);
        Eq("收尾那次只给 propose（不能再搜，但仍能提案）",
           string.Join("|", host6.SeenTools[^1]), "propose");
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

        Console.WriteLine("\n=== 14. 思考过程：攒起来给界面看，但绝不参与判定 ===");
        var host9 = new FakeHost();
        host9.Known = IsKnown;
        host9.Results["white dress"] = new()
        {
            new TagSearch.Hit("white dress", "白色连衣裙", "general", 12000, true, false, "general"),
        };
        // 两轮各带一段思考，验证是**拼接**而不是只留最后一段。
        host9.Turn(new LlmClient.ToolTurn(true, "我先查一下词库。", "第一轮思考：先查白裙子。",
            new List<LlmClient.ToolCall> { new("c1", AgentKit.SearchToolName, """{"query":"white dress"}""") },
            "", ""))
             .Turn(new LlmClient.ToolTurn(true, "补好了。", "第二轮思考：查到了，可以提案。",
            new List<LlmClient.ToolCall>
            {
                new("c2", AgentKit.ProposeToolName, """{"add_tags":["1girl","white dress"]}"""),
            }, "", ""));
        var o9 = Run(host9, "照这张图补标签");

        True("两轮思考都带出来了",
             o9.Reasoning.Contains("第一轮思考") && o9.Reasoning.Contains("第二轮思考"));
        True("两段之间有分隔", o9.Reasoning.Contains("────────"));
        True("工具调用也记下来了（界面要用它显示步骤）",
             o9.Log.Any(l => l.Contains("white dress")));
        Eq("回复里**没有**思考内容（结论与过程分离）", o9.Reply, "补好了。");
        True("思考不会混进提案内容",
             o9.Proposal is not null &&
             !string.Join("|", o9.Proposal.AddTags).Contains("思考"));

        // 假 host 的历史里也不该出现思考：把它当 assistant 消息回灌会白白多付 token，
        // 而且会让模型把"自己想过但没说的话"当成说过的话。
        True("思考没被塞回对话历史（不占下一轮的 token）",
             host9.SeenMessages.All(ms => ms.All(m =>
                 (m["content"]?.ToString() ?? "").IndexOf("第一轮思考", StringComparison.Ordinal) < 0)));

        Console.WriteLine("\n=== 15. 没有思考时不硬造 ===");
        var host10 = new FakeHost();
        host10.Turn(new LlmClient.ToolTurn(true, "就是 solo。", "", new(), "", ""));
        var o10 = Run(host10, "solo 是什么");
        Eq("思考内容为空串（界面据此不加那个折叠块）", o10.Reasoning, "");

        Console.WriteLine("\n=== 16. 对话持久化：换页不清、只有「清空」才清 ===");
        {
            var dir = Path.Combine(Path.GetTempPath(), "ac-agent-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "agent.json");
            try
            {
                var s = AgentChatStore.Load(path);
                Eq("空文件起步是空的", s.Count, 0);

                s.Add(AgentChatStore.Kind.User, "这张图缺什么");
                s.Add(AgentChatStore.Kind.Assistant, "缺一个背景描述。",
                      reasoning: "先看画面再查词库。", log: new[] { "查「background」→ 3 条" });
                Eq("存了两条", s.Count, 2);

                // 重新载入：模拟重启
                var back = AgentChatStore.Load(path);
                Eq("重启后条数还在（这就是「不清空」）", back.Count, 2);
                Eq("用户那条正文对", back.Snapshot()[0].Text, "这张图缺什么");
                Eq("助手那条正文对", back.Snapshot()[1].Text, "缺一个背景描述。");
                Eq("思考内容也存下来了",
                   back.Snapshot()[1].Reasoning, "先看画面再查词库。");
                Eq("工具调用记录也存下来了",
                   string.Join("|", back.Snapshot()[1].Log), "查「background」→ 3 条");
                Eq("种类区分正确（user）", back.Snapshot()[0].KindOf(), AgentChatStore.Kind.User);
                Eq("种类区分正确（assistant）", back.Snapshot()[1].KindOf(), AgentChatStore.Kind.Assistant);

                // 送模型的必须**只有正文**：思考和工具记录是给用户看的，
                // 混进去既多花 token，又可能让模型把"想过但没说的话"当成说过了。
                var hist = back.ToModelHistory();
                Eq("送模型的历史条数", hist.Count, 2);
                True("用户那条是 FromUser", hist[0].FromUser);
                True("助手那条不是 FromUser", !hist[1].FromUser);
                True("送模型的内容里没有思考",
                     hist.All(h => !h.Text.Contains("先看画面再查词库")));
                True("送模型的内容里没有工具记录",
                     hist.All(h => !h.Text.Contains("查「background」")));

                // 换图说明：折成 user 消息，并与**紧跟其后**的 user 合并。
                // 注意总数是 3 而不是 2：说明前面是 assistant，所以它自己起一条
                // user 消息，随后的问题并进那一条（合并的是相邻 user，不是跨越 assistant）。
                back.Add(AgentChatStore.Kind.Note, "已切换到另一张图：B.png。");
                back.Add(AgentChatStore.Kind.User, "那这张呢");
                var h2 = back.ToModelHistory();
                Eq("总条数 = 3（user / assistant / user）", h2.Count, 3);
                Eq("第 1 条是 user", h2[0].FromUser, true);
                Eq("第 2 条是 assistant", h2[1].FromUser, false);
                Eq("第 3 条是 user", h2[2].FromUser, true);
                True("说明与紧跟其后的问题合并成了一条",
                     h2[2].Text.Contains("B.png") && h2[2].Text.Contains("那这张呢"));
                True("没有出现两条相邻的 user（有些服务端对角色的交替比对得严）",
                     !h2.Where((t, i) => i > 0 && t.FromUser && h2[i - 1].FromUser).Any());

                // 清空是唯一的清空路径
                back.Clear();
                Eq("Clear 之后条数为 0", back.Count, 0);
                Eq("Clear 也落了盘（重启不会又冒出来）", AgentChatStore.Load(path).Count, 0);

                // 空正文不入库（失败路径上会出现空回复）
                var s2 = AgentChatStore.Load(path);
                s2.Add(AgentChatStore.Kind.Assistant, "   ");
                Eq("空正文被丢掉", s2.Count, 0);

                // 坏文件不该让程序起不来
                File.WriteAllText(path, "{ this is not json");
                var s3 = AgentChatStore.Load(path);
                Eq("坏文件被容忍（重建为空）", s3.Count, 0);
                s3.Add(AgentChatStore.Kind.User, "重建后还能用");
                Eq("重建后能继续写", AgentChatStore.Load(path).Count, 1);

                // 上限：防止送模型的上下文无限增长
                var s4 = AgentChatStore.Load(path);
                for (var i = 0; i < AgentChatStore.MaxTurns + 20; i++)
                    s4.Add(AgentChatStore.Kind.User, "第 " + i + " 句");
                True($"条数被压到上限内（{s4.Count} ≤ {AgentChatStore.MaxTurns}）",
                     s4.Count <= AgentChatStore.MaxTurns);
                True("Trimmed 标记为真（界面据此提示更早的被移出）", s4.Trimmed);
                Eq("留下的是最新的那批", s4.Snapshot()[^1].Text,
                   "第 " + (AgentChatStore.MaxTurns + 19) + " 句");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        Console.WriteLine("\n=== 17. 图片工具：current_image / select_image ===");
        {
            // 有数据集时两个工具都要下发
            var h = new FakeHost();
            h.Dataset.AddRange(new[]
            {
                new AgentImage("a", 0, true, 12),
                new AgentImage("b", 1, false, 0, IsCurrent: true),
                new AgentImage("c", 2, true, 5),
            });
            h.Turn(Tool("i1", AgentKit.CurrentImageToolName, "{}"))
             .Turn(Tool("p1", AgentKit.ProposeToolName, """{"add_tags":[]}"""));
            var o = Run(h, "现在这张是什么");
            True("有数据集时下发了 current_image 与 select_image",
                 h.SeenTools[0].Contains(AgentKit.CurrentImageToolName) &&
                 h.SeenTools[0].Contains(AgentKit.SelectImageToolName));

            var toolMsg = h.SeenMessages[1]
                .First(m => m["role"]?.GetValue<string>() == "tool")["content"]!.GetValue<string>();
            True("工具结果说清了当前是哪张图", toolMsg.Contains("b (2 of 3)"));
            True("工具结果说了还没标", toolMsg.Contains("no caption yet"));
            True("工具结果列出了邻居（模型才可能切过去）",
                 toolMsg.Contains("- a") && toolMsg.Contains("- c"));
            True("标出了哪个是当前项", toolMsg.Contains("(current)"));

            // 没有数据集时不该下发那两个工具：让模型调必然失败的工具是白烧预算
            var h2 = new FakeHost();
            h2.Turn(Tool("p2", AgentKit.ProposeToolName, """{"add_tags":[]}"""));
            Run(h2, "随便");
            True("没有数据集时不下发图片工具",
                 !h2.SeenTools[0].Contains(AgentKit.CurrentImageToolName) &&
                 !h2.SeenTools[0].Contains(AgentKit.SelectImageToolName));

            // 切图：真的调到了 host，并且进了日志
            var h3 = new FakeHost();
            h3.Dataset.AddRange(new[]
            {
                new AgentImage("a", 0, true, 1),
                new AgentImage("b", 1, false, 0, IsCurrent: true),
                new AgentImage("c", 2, true, 5),
            });
            h3.Turn(Tool("s1", AgentKit.SelectImageToolName, """{"name":"c"}"""))
              .Turn(Tool("p3", AgentKit.ProposeToolName, """{"add_tags":[]}"""));
            var imgO3 = Run(h3, "切到 c");
            Eq("切图请求传给了 host", string.Join("|", h3.SelectRequests), "c");
            True("日志里有切图记录", imgO3.Log.Any(l => l.Contains("切到「c」")));
            True("切完之后 current_image 能反映新位置",
                 h3.Dataset.First(x => x.IsCurrent).Name == "c");

            // 空名字：要说清并要求重试，而不是拿用户的整句话去猜
            var h4 = new FakeHost();
            h4.Dataset.Add(new AgentImage("a", 0, true, 1, IsCurrent: true));
            h4.Turn(Tool("s2", AgentKit.SelectImageToolName, """{"name":"   "}"""))
              .Turn(Tool("p4", AgentKit.ProposeToolName, """{"add_tags":[]}"""));
            var imgO4 = Run(h4, "换一张");
            Eq("空名字没有真的去切", h4.SelectRequests.Count, 0);
            True("日志说明了是空名字并要求重试",
                 imgO4.Log.Any(l => l.Contains("空名字")));

            // 名字不存在：把失败原样回给模型，它会改
            var h5 = new FakeHost();
            h5.Dataset.Add(new AgentImage("a", 0, true, 1, IsCurrent: true));
            h5.SelectSucceeds = false;
            h5.Turn(Tool("s3", AgentKit.SelectImageToolName, """{"name":"nope"}"""))
              .Turn(Tool("p5", AgentKit.ProposeToolName, """{"add_tags":[]}"""));
            var imgO5 = Run(h5, "切到 nope");
            Eq("失败也照样问了 host", string.Join("|", h5.SelectRequests), "nope");
            True("失败被记进日志", imgO5.Log.Any(l => l.Contains("切图失败")));
            var failMsg = h5.SeenMessages[1]
                .First(m => m["role"]?.GetValue<string>() == "tool")["content"]!.GetValue<string>();
            True("失败原因回给了模型", failMsg.Contains("No image named"));
        }

        Console.WriteLine("\n=== 18. 完全权限：默认必须是关的 ===");
        {
            // 它推翻的是"助手只出提案、落盘由人点一下"这条承重原则
            // （见 TagAgent 类注释），所以默认值本身就是一条要守的约定：
            // 一旦哪天有人把它写成 true，所有现有用户的保证都会被悄悄撤掉。
            var fresh = new AppSettings();
            Eq("默认关闭", fresh.AgentFullAccess, false);

            var on = AppSettings.FromJson(new AppSettings { AgentFullAccess = true }.ToJson());
            Eq("打开后能落盘再读回", on?.AgentFullAccess, true);

            var off = AppSettings.FromJson(new AppSettings().ToJson());
            Eq("默认值经序列化往返仍是关", off?.AgentFullAccess, false);

            // 轮数也是用户可配的（默认 32）
            Eq("轮数默认 32", fresh.AgentMaxRounds, 32);
            var r = AppSettings.FromJson(new AppSettings { AgentMaxRounds = 7 }.ToJson());
            Eq("轮数能落盘再读回", r?.AgentMaxRounds, 7);

            Console.WriteLine("     （开启后写入仍只算一个撤销点，见 ApplyAgentProposal 的 PushUndo）");
        }

        Console.WriteLine("\n=== 19. 流式累积：分片边界（真发请求覆盖不到）===");
        {
            string Chunk(string deltaJson) =>
                "data: {\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"choices\":[{\"index\":0," +
                "\"delta\":" + deltaJson + ",\"finish_reason\":null}]}";

            // 思考链逐段下发（实测就是这个形状）
            var s = new ToolStream();
            s.Feed(Chunk("{\"role\":\"assistant\",\"content\":null,\"reasoning_content\":\"先看\"}"));
            s.Feed(Chunk("{\"content\":null,\"reasoning_content\":\"画面\"}"));
            s.Feed(Chunk("{\"content\":\"好\"}"));
            s.Feed(Chunk("{\"content\":\"的\"}"));
            Eq("思考链累积", s.Reasoning, "先看画面");
            Eq("正文累积", s.Content, "好的");

            // 非 data 行要忽略
            s.Feed("event: ping");
            s.Feed("");
            s.Feed(": keep-alive");
            Eq("忽略非 data 行后内容不变", s.Content, "好的");

            // 坏分片不该毁掉整轮
            s.Feed("data: {不是 json");
            Eq("坏分片被跳过", s.Content, "好的");

            // tool_calls 是碎片化的：名字 1 片、参数多片、id 只在第 1 片
            var t = new ToolStream();
            t.Feed(Chunk("{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\"," +
                         "\"function\":{\"name\":\"search_tags\",\"arguments\":\"\"}}]}"));
            t.Feed(Chunk("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"query\\\":\"}}]}"));
            t.Feed(Chunk("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"white\"}}]}"));
            t.Feed(Chunk("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\" dress\\\"}\"}}]}"));
            Eq("碎片按 index 拼成 1 次调用", t.Calls().Count, 1);
            Eq("名字拼回来了", t.Calls()[0].Name, "search_tags");
            Eq("参数拼回来了", t.Calls()[0].Arguments, "{\"query\":\"white dress\"}");
            Eq("id 没有后来被空值覆盖", t.Calls()[0].Id, "call_1");

            // 两个并发调用：不同 index 必须分开
            var t2 = new ToolStream();
            t2.Feed(Chunk("{\"tool_calls\":[{\"index\":0,\"id\":\"a\",\"function\":{\"name\":\"search_tags\"," +
                          "\"arguments\":\"{}\"}},{\"index\":1,\"id\":\"b\",\"function\":{\"name\":\"propose\"," +
                          "\"arguments\":\"\"}}]}"));
            t2.Feed(Chunk("{\"tool_calls\":[{\"index\":1,\"function\":{\"arguments\":\"{\\\"add_tags\\\":[]}\"}}]}"));
            Eq("两次调用分开", t2.Calls().Count, 2);
            Eq("第 1 次名字", t2.Calls()[0].Name, "search_tags");
            Eq("第 2 次参数补齐", t2.Calls()[1].Arguments, "{\"add_tags\":[]}");

            // finish_reason 与 [DONE]
            var t3 = new ToolStream();
            t3.Feed("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"x\"},\"finish_reason\":\"tool_calls\"}]}");
            Eq("收尾原因被记住", t3.FinishReason, "tool_calls");
            True("还没结束", !t3.Done);
            t3.Feed("data: [DONE]");
            True("[DONE] 置结束位", t3.Done);

            // 声明了 stream 却回完整响应的网关也要能读
            var t4 = new ToolStream();
            t4.Feed("data: {\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\"," +
                   "\"content\":\"整段答案\",\"reasoning_content\":\"整段思考\"},\"finish_reason\":\"stop\"}]}");
            Eq("非流式形状的正文", t4.Content, "整段答案");
            Eq("非流式形状的思考", t4.Reasoning, "整段思考");
            True("非流式形状直接算结束", t4.Done);

            // **关键的一条**：还原成响应 JSON 后必须走和非流式完全相同的判定
            var t5 = new ToolStream();
            t5.Feed(Chunk("{\"tool_calls\":[{\"index\":0,\"id\":\"z\",\"function\":{\"name\":\"propose\"," +
                          "\"arguments\":\"{\\\"add_tags\\\":[\\\"1girl\\\"]}\"}}]}"));
            t5.Feed("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}");
            var parsed = ParseTurn(t5.ToResponseJson());
            Eq("还原后仍解析出 1 次调用", parsed.Calls.Count, 1);
            Eq("还原后参数一致", parsed.Calls[0].Arguments, "{\"add_tags\":[\"1girl\"]}");
            Eq("还原后 ok", parsed.Ok, true);
        }

        Console.WriteLine("\n=== 20. 助手确实要求走流式（不回退成「生成完一次性蹦出来」）===");
        {
            var h = new FakeHost();
            h.Turn(Tool("p", AgentKit.ProposeToolName, """{"add_tags":["solo"]}"""));
            var got = new List<(string R, string C)>();
            RunStreaming(h, "试试", (r, c) => got.Add((r, c)));

            True("TagAgent 把流式回调透传给了 host", h.StreamRequested > 0);
            Eq("有请求的轮都带上了（这一轮只发 1 次）", h.StreamRequested, 1);
            True("进度回调真的收到了东西", got.Count > 0);
            True("回调里能看到逐步累积的思考（而不是最后一次性给全）",
                 got.Any(p => p.R == "思考片段A") && got.Any(p => p.R == "思考片段A思考片段B"));

            // 对照：不给回调时必须**不**要求流式（既有行为一字不变）
            var h2 = new FakeHost();
            h2.Turn(Tool("p2", AgentKit.ProposeToolName, """{"add_tags":[]}"""));
            Run(h2, "试试");
            Eq("不给回调时不走流式", h2.StreamRequested, 0);
        }

        Console.WriteLine("\n=== 21. 区域数学：归一化坐标 → 像素矩形 ===");
        {
            // 坐标一律归一化：模型看不到原始分辨率，让它填像素等于让它猜。
            Eq("整幅", ImageRegion.Crop(1000, 500, 0, 0, 1, 1), new PixelRect(0, 0, 1000, 500));
            Eq("右半", ImageRegion.Crop(1000, 500, 0.5, 0, 0.5, 1), new PixelRect(500, 0, 500, 500));
            Eq("右下四分之一", ImageRegion.Crop(1000, 500, 0.5, 0.5, 0.5, 0.5),
               new PixelRect(500, 250, 500, 250));
            Eq("超出边界夹回来（模型给 1.4 的意思是「到边上」）",
               ImageRegion.Crop(1000, 500, 0.9, 0.9, 0.5, 0.5), new PixelRect(900, 450, 100, 50));
            Eq("宽高给负数 = 方向反了（不是报错）",
               ImageRegion.Crop(1000, 500, 0.6, 0.2, -0.2, 0.2), new PixelRect(400, 100, 200, 100));
            Eq("退化成一个像素也不塌成 0 宽",
               ImageRegion.Crop(1000, 500, 1, 1, 0, 0), new PixelRect(999, 499, 1, 1));
            Eq("非法图尺寸", ImageRegion.Crop(0, 100, 0, 0, 1, 1), default(PixelRect));
        }

        Console.WriteLine("\n=== 22. tiles：均分、不重叠、块数封顶 ===");
        {
            Eq("3x3 允许", ImageRegion.TileProblem(3, 3), "");
            Eq("1x9 允许（正好上限）", ImageRegion.TileProblem(1, 9), "");
            True("4x4 被拒，并说明上限是 9",
                 ImageRegion.TileProblem(4, 4).Contains("9") && ImageRegion.TileProblem(4, 4).Contains("16"));
            True("0 行被拒", ImageRegion.TileProblem(3, 0).Length > 0);

            var t9 = ImageRegion.Tiles(9, 9, 3, 3);
            Eq("3x3 切出 9 块", t9.Count, 9);
            True("每块都是 3x3", t9.All(r => r.Width == 3 && r.Height == 3));
            Eq("第一块在左上", t9[0], new PixelRect(0, 0, 3, 3));
            Eq("阅读顺序：第 3 块在第 1 行最右", t9[2], new PixelRect(6, 0, 3, 3));
            Eq("第 4 块换到第 2 行最左", t9[3], new PixelRect(0, 3, 3, 3));

            // 不能整除时**不许有缝、不许重叠**：缝会让模型看不到一条内容却以为看全了
            var t6 = ImageRegion.Tiles(100, 50, 3, 2);
            Eq("3x2 切出 6 块", t6.Count, 6);
            Eq("第 1 块按向下取整", t6[0], new PixelRect(0, 0, 33, 25));
            Eq("最后一块补足余数（33+33+34=100）", t6[2], new PixelRect(66, 0, 34, 25));
            Eq("每行宽度之和正好等于图宽", t6.Take(3).Sum(r => r.Width), 100);
            Eq("每列高度之和正好等于图高", t6.Where(r => r.X == 0).Sum(r => r.Height), 50);

            Eq("1x1 就是整幅", ImageRegion.Tiles(640, 480, 1, 1).Single(),
               new PixelRect(0, 0, 640, 480));
            Eq("图比块数还小时把块数压到像素数（免得 0 宽的块）",
               ImageRegion.Tiles(2, 2, 3, 3).Count, 4);
        }

        Console.WriteLine("\n=== 23. EXIF 反查：模型的坐标 → 解码器原始像素的坐标 ===");
        {
            // 原始 600x400，EXIF 6 → 转正后是 400x600 的竖图。
            // 这一组数字错一个，模型要的那块就会跑到别处去，而它自己看不出来。
            var r = new PixelRect(10, 20, 30, 40);
            Eq("方向 1 原样", ImageRegion.ToRaw(r, 600, 400, 1), r);
            Eq("方向 2 左右镜像", ImageRegion.ToRaw(r, 600, 400, 2), new PixelRect(560, 20, 30, 40));
            Eq("方向 3 旋转 180", ImageRegion.ToRaw(r, 600, 400, 3), new PixelRect(560, 340, 30, 40));
            Eq("方向 4 上下镜像", ImageRegion.ToRaw(r, 600, 400, 4), new PixelRect(10, 340, 30, 40));
            Eq("方向 5 转置（宽高对调）", ImageRegion.ToRaw(r, 600, 400, 5), new PixelRect(20, 10, 40, 30));
            Eq("方向 6 顺时针 90", ImageRegion.ToRaw(r, 600, 400, 6), new PixelRect(20, 360, 40, 30));
            Eq("方向 7 副对角线", ImageRegion.ToRaw(r, 600, 400, 7), new PixelRect(540, 360, 40, 30));
            Eq("方向 8 逆时针 90", ImageRegion.ToRaw(r, 600, 400, 8), new PixelRect(540, 10, 40, 30));

            // 组合：模型要"转正后的右上角"，原始像素里就是左上角那一块
            var tr = ImageRegion.Crop(400, 600, 0.5, 0, 0.5, 0.5);
            Eq("转正后的右上角", tr, new PixelRect(200, 0, 200, 300));
            Eq("反查到原始像素 = 左上角", ImageRegion.ToRaw(tr, 600, 400, 6),
               new PixelRect(0, 0, 300, 200));
        }

        Console.WriteLine("\n=== 24. 像素方向变换：8 个方向逐像素核 ===");
        {
            // 3x2 的测试图：第 n 个像素的 B = n（其余字节固定），于是"某个像素搬对了没有"
            // 可以直接比数字。宽高不相等才看得出 5-8 到底有没有转置。
            const int W = 3, H = 2;
            var src = ImagePixels.TestImage(W, H);

            int N(byte[] buf, int w, int x, int y) => ImagePixels.Pixel(buf, w, x, y).B;

            var (ob1, wb1, hb1) = ImagePixels.Orient(src, W, H, 1);
            Eq("方向 1 尺寸不变", (wb1, hb1), (W, H));
            Eq("方向 1 是原图", N(ob1, wb1, 2, 1), 1 * W + 2);

            var (ob2, wb2, _) = ImagePixels.Orient(src, W, H, 2);
            Eq("方向 2 左右镜像", N(ob2, wb2, 0, 0), 2);
            Eq("方向 2 末列来自首列", N(ob2, wb2, 2, 1), 1 * W + 0);

            var (ob3, wb3, _) = ImagePixels.Orient(src, W, H, 3);
            Eq("方向 3 旋转 180：左上来自右下", N(ob3, wb3, 0, 0), 1 * W + 2);

            var (ob4, wb4, _) = ImagePixels.Orient(src, W, H, 4);
            Eq("方向 4 上下镜像：左上来自左下", N(ob4, wb4, 0, 0), 1 * W + 0);

            var (ob5, wb5, hb5) = ImagePixels.Orient(src, W, H, 5);
            Eq("方向 5 宽高对调", (wb5, hb5), (H, W));
            Eq("方向 5 转置", N(ob5, wb5, 1, 2), 1 * W + 2);

            var (ob6, wb6, hb6) = ImagePixels.Orient(src, W, H, 6);
            Eq("方向 6 宽高对调", (wb6, hb6), (H, W));
            Eq("方向 6 顺时针 90：显示左上来自原始左下", N(ob6, wb6, 0, 0), (H - 1) * W + 0);
            Eq("方向 6 显示右上来自原始左上", N(ob6, wb6, 1, 0), 0 * W + 0);

            var (ob7, wb7, _) = ImagePixels.Orient(src, W, H, 7);
            Eq("方向 7 副对角线", N(ob7, wb7, 0, 0), (H - 1) * W + (W - 1));

            var (ob8, wb8, _) = ImagePixels.Orient(src, W, H, 8);
            Eq("方向 8 逆时针 90", N(ob8, wb8, 0, 0), 0 * W + (W - 1));

            // 行距大于 宽x4（解码器真的会这样，尤其奇数宽度）：按 宽x4 去索引会逐行错位
            const int stride = W * 4 + 12;
            var padded = ImagePixels.TestImage(W, H, stride);
            var (obp, wbp, hbp) = ImagePixels.Orient(padded, W, H, 6, stride);
            Eq("带行距时尺寸照旧", (wbp, hbp), (H, W));
            Eq("带行距时第 1 个像素仍来自原始左下", N(obp, wbp, 0, 0), (H - 1) * W);
            Eq("带行距与不带行距结果逐字节相同",
               Convert.ToHexString(obp), Convert.ToHexString(ob6));

            // 缓冲不够时必须抛，不能返回一张方向不对的图
            var threw = false;
            try { ImagePixels.Orient(new byte[10], W, H, 6); }
            catch (ArgumentException) { threw = true; }
            True("像素缓冲不足时抛异常（而不是悄悄发错图）", threw);
        }

        Console.WriteLine("\n=== 25. 图像工具的声明、参数解析与预算 ===");
        {
            var withRegion = string.Join("|",
                AgentKit.Tools(true, withImages: true, canViewRegion: true).Select(t => t.Name));
            Eq("带图 + 可放大时的工具集", withRegion,
               "current_image|image_info|search_tags|web_search|select_image|view_region|propose");

            var noRegion = string.Join("|",
                AgentKit.Tools(true, withImages: true).Select(t => t.Name));
            True("没带图时不下发 view_region（放大靠插图，没图就没法放大）",
                 !noRegion.Contains(AgentKit.ViewRegionToolName));
            Eq("没带图时其余图片工具照旧", noRegion,
               "current_image|image_info|search_tags|web_search|select_image|propose");

            var noImages = string.Join("|", AgentKit.Tools(true).Select(t => t.Name));
            True("没有数据集时 image_info / view_region 都不下发",
                 !noImages.Contains(AgentKit.ImageInfoToolName) &&
                 !noImages.Contains(AgentKit.ViewRegionToolName));

            True("view_region 的参数是对象", AgentKit.ViewRegionTool.Parameters["type"]!.GetValue<string>() == "object");
            True("mode 只有 crop / tiles 两个取值",
                 AgentKit.ViewRegionTool.Parameters["properties"]!["mode"]!["enum"] is JsonArray e &&
                 e.Count == 2);
            True("说明里写明块数上限", AgentKit.ViewRegionTool.Description.Contains("9"));

            var p = AgentKit.ParseRegion("""{"mode":"crop","x":0.4,"y":0.1,"w":0.2,"h":0.3}""");
            True("crop 解析成功", p.Ok);
            Eq("模式", p.Mode, "crop");
            Eq("坐标", (p.X, p.Y, p.W, p.H), (0.4, 0.1, 0.2, 0.3));
            Eq("crop 只要一张图", p.ImageCount, 1);

            var t = AgentKit.ParseRegion("""{"mode":"tiles","cols":3,"rows":3}""");
            True("tiles 解析成功", t.Ok);
            Eq("tiles 要 9 张图", t.ImageCount, 9);

            True("缺 mode 时按字段推断为 crop",
                 AgentKit.ParseRegion("""{"x":0.1,"y":0.1,"w":0.1,"h":0.1}""").Mode == "crop");
            True("缺 mode 时按字段推断为 tiles",
                 AgentKit.ParseRegion("""{"cols":2,"rows":2}""").Mode == "tiles");
            True("数字写成字符串也收（小模型真会这么写）",
                 AgentKit.ParseRegion("""{"mode":"crop","x":"0.5","y":"0","w":"1","h":"1"}""").Ok);

            True("w=0 被拒，并说清 w/h 是尺寸",
                 AgentKit.ParseRegion("""{"mode":"crop","x":0,"y":0,"w":0,"h":0.5}""")
                          .Problem.Contains("greater than 0"));
            True("4x4 被拒（16 块会顶爆上下文）",
                 AgentKit.ParseRegion("""{"mode":"tiles","cols":4,"rows":4}""")
                          .Problem.Contains("9"));
            True("坏 JSON 被拒", !AgentKit.ParseRegion("not json").Ok);
            True("未知 mode 被拒",
                 !AgentKit.ParseRegion("""{"mode":"zoomin"}""").Ok);
            Eq("参数读不懂时预算估算为 0（不拦，让它去撞真正的报错）",
               AgentKit.PlannedRegionImages("not json"), 0);
            Eq("tiles 的预算估算", AgentKit.PlannedRegionImages("""{"mode":"tiles","cols":3,"rows":2}"""), 6);
        }

        Console.WriteLine("\n=== 26. TagAgent：放大后的图插成一条 user 消息 ===");
        {
            var h = new FakeHost();
            h.Dataset.Add(new AgentImage("a", 0, true, 3, IsCurrent: true));
            h.ViewImageCount = 2;
            h.Turn(Tool("v1", AgentKit.ViewRegionToolName, """{"mode":"crop","x":0.1,"y":0.1,"w":0.2,"h":0.2}"""))
             .Turn(Tool("p1", AgentKit.ProposeToolName, """{"add_tags":[]}"""));

            var o = Run(h, "把左上角放大给我看", image);
            True("成功", o.Ok);
            Eq("参数原样交给了 host", string.Join("|", h.ViewRequests),
               """{"mode":"crop","x":0.1,"y":0.1,"w":0.2,"h":0.2}""");
            Eq("日志里记了放大", string.Join("|", o.Log.Where(l => l.Contains("放大"))), "放大 → 2 张图");

            // 关键形状：工具结果之后**紧跟**一条带图的 user 消息
            var second = h.SeenMessages[1];
            Eq("第 2 次请求的消息条数（原问 + 工具调用 + 工具结果 + 图文消息）", second.Count, 4);
            Eq("第 1 条是用户原问", second[0]["role"]!.GetValue<string>(), "user");
            Eq("第 2 条是 assistant 的工具调用", second[1]["role"]!.GetValue<string>(), "assistant");
            Eq("第 3 条是工具结果（必须紧跟要它的那条 assistant）",
               second[2]["role"]!.GetValue<string>(), "tool");
            Eq("第 4 条是带图的 user 消息", second[3]["role"]!.GetValue<string>(), "user");

            var cropParts = second[3]["content"] as JsonArray;
            Eq("parts 数 = 文本 + 2 张图", cropParts?.Count ?? -1, 3);
            Eq("文本在前", cropParts![0]!["type"]!.GetValue<string>(), "text");
            Eq("第 1 张图", cropParts[1]!["type"]!.GetValue<string>(), "image_url");
            Eq("第 2 张图", cropParts[2]!["type"]!.GetValue<string>(), "image_url");
            var note = cropParts[0]!["text"]!.GetValue<string>();
            True("文案说清这是放大区域、不是用户新说的话",
                 note.Contains("enlarged region") && note.Contains("Look at"));

            // 没有带图时：工具不下发、host 也不会被调用
            var h2 = new FakeHost();
            h2.Dataset.Add(new AgentImage("a", 0, true, 3, IsCurrent: true));
            h2.Turn(Tool("p2", AgentKit.ProposeToolName, """{"add_tags":[]}"""));
            var noImgOutcome = Run(h2, "随便", image: null);
            True("没带图时不下发 view_region",
                 !h2.SeenTools[0].Contains(AgentKit.ViewRegionToolName));
            True("没带图时它连 image_info 都还在（那个是纯文本、没有成本）",
                 h2.SeenTools[0].Contains(AgentKit.ImageInfoToolName));
            True("没带图那次也正常收尾", noImgOutcome.Ok);

            // 裁剪失败：不能凭空插一条图文消息，失败原样回给模型
            var h3 = new FakeHost();
            h3.Dataset.Add(new AgentImage("a", 0, true, 3, IsCurrent: true));
            h3.ViewSucceeds = false;
            h3.Turn(Tool("v3", AgentKit.ViewRegionToolName, """{"mode":"crop","x":0,"y":0,"w":1,"h":1}"""))
              .Turn(Tool("p3", AgentKit.ProposeToolName, """{"add_tags":[]}"""));
            var failOutcome = Run(h3, "放大看看", image);
            True("失败也照样问了 host", h3.ViewRequests.Count == 1);
            True("日志如实记失败", failOutcome.Log.Any(l => l.Contains("放大失败")));
            Eq("失败时消息数 = 3（没有那条图文消息）", h3.SeenMessages[1].Count, 3);
            var failText = h3.SeenMessages[1][2]["content"]!.GetValue<string>();
            True("失败原因回给了模型", failText.Contains("could not produce that crop"));

            // 图片预算：3x3 用掉 9 张之后，再来一次 3x3 要被拦下（**不许白裁一遍**）
            var h4 = new FakeHost();
            h4.Dataset.Add(new AgentImage("a", 0, true, 3, IsCurrent: true));
            h4.ViewImageCount = 9;
            h4.Turn(Tool("v4", AgentKit.ViewRegionToolName, """{"mode":"tiles","cols":3,"rows":3}"""))
              .Turn(Tool("v5", AgentKit.ViewRegionToolName, """{"mode":"tiles","cols":3,"rows":3}"""))
              .Turn(Tool("p4", AgentKit.ProposeToolName, """{"add_tags":[]}"""));
            var budgetOutcome = Run(h4, "整张图逐块看", image);
            Eq("只真的裁了一次（第二次被预算拦下，没有白裁）", h4.ViewRequests.Count, 1);
            True("被拦时如实说明用完了",
                 h4.SeenMessages[2].Any(m => m["role"]?.GetValue<string>() == "tool" &&
                     (m["content"]?.GetValue<string>() ?? "").Contains("maximum of 12")));
            True("被拦之后仍然拿到了提案", budgetOutcome.Proposal is not null);
        }

        Console.WriteLine("\n=== 27. image_info：纯文本、原样回给模型 ===");
        {
            var h = new FakeHost();
            h.Dataset.Add(new AgentImage("a", 0, true, 3, IsCurrent: true));
            h.InfoText = "Current image: a (1 of 1).\nFile: image/jpeg, 900 B";
            h.Turn(Tool("i1", AgentKit.ImageInfoToolName, "{}"))
             .Turn(Tool("p1", AgentKit.ProposeToolName, """{"add_tags":[]}"""));
            var o = Run(h, "这张图多大", image);

            True("日志里记了这次查询", o.Log.Any(l => l.Contains("查图像信息")));
            var back = h.SeenMessages[1].First(m => m["role"]?.GetValue<string>() == "tool")["content"]!
                .GetValue<string>();
            Eq("事实原样回给了模型", back, h.InfoText);
            True("工具调用记录里有它（界面要显示这一步）",
                 o.ToolCalls is not null && o.ToolCalls.Any(c => c.Name == AgentKit.ImageInfoToolName));

            // 文案本身（纯逻辑，可以逐句钉）
            var facts = new ImageFacts("pic", "image/jpeg", 4_300_000, 4000, 6500, 1,
                                       Attached: true, Index: 4, Total: 53, CaptionTags: 12);
            var text = ImageTools.Info(facts);
            True("说了文件名与位置", text.Contains("pic (4 of 53)"));
            True("说了原始尺寸", text.Contains("4000x6500"));
            True("说了被缩到多小（模型据此判断细节有没有丢）", text.Contains("630x1024"));
            True("说了已有多少标签", text.Contains("12 tag"));
            True("让它用 view_region 而不是猜", text.Contains("view_region"));

            var noAttach = ImageTools.Info(facts with { Attached = false });
            True("没带图时明确说看不见，且不许要裁剪",
                 noAttach.Contains("NO copy") && noAttach.Contains("cannot see it"));

            var upright = ImageTools.Info(facts with { StoredWidth = 800, StoredHeight = 600 });
            True("原图就小于上限时如实说没有丢细节", upright.Contains("full resolution"));

            // 倍率：整图 630x1024 里，一块 1/25 面积的区域约 126x205，放大到 1024 长边约 5 倍
            var small = facts with { StoredWidth = 2500, StoredHeight = 2500 };
            var f2 = ImageTools.DetailFactor(small, new PixelRect(0, 0, 500, 500), 1024, 1024);
            True($"倍率算得出来（{f2:0.0}x）", f2 is > 4 and < 6);
            True("倍率接近 1 时如实说「没变大」",
                 ImageTools.DetailFactor(facts with { StoredWidth = 800, StoredHeight = 600 },
                                         new PixelRect(0, 0, 400, 300), 400, 300) < 1.15);
        }

        Console.WriteLine("\n=== 28. 覆盖标签：propose 必须有删除通道 ===");
        {
            // 用户报过的问题：连让助手"覆盖"两次，它每次都说覆盖了，而标签列表看着没变。
            // 根因是 propose 原先只有 add_tags —— 模型**没有手段**表达"删掉那些"，
            // 于是"覆盖"只能退化成"往里加"，而它照样在 note 里写"已替换"。
            // 复现与现场证据见 tests/OverwriteProbe.cs（corecheck overwrite）。

            var knownSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "solo", "smile", "upper body", "blonde hair", "twintails" };
            bool IsK(string t) => knownSet.Contains(t);

            var before = new[] { "1girl", "solo", "blonde hair", "twintails", "blue eyes" };

            // 模型说"覆盖成这三条"时该发的形状
            var p = AgentKit.ParseProposal("""
                {"remove_tags":["1girl","blonde hair","twintails","blue eyes"],
                 "add_tags":["solo","smile","upper body"],
                 "note":"已覆盖为三条"}
                """, IsK);
            True("解析出删除通道", p.HasRemovals);
            Eq("要删的个数", p.Remove.Count, 4);
            Eq("要删的内容", string.Join("|", p.Remove), "1girl|blonde hair|twintails|blue eyes");
            Eq("要加的个数", p.AddTags.Count, 3);
            True("有删除时 IsEmpty 为假", !p.IsEmpty);

            // 落地：现有 ∪ 加 − 删，应当正好等于用户要的三条
            var after = before
                .Where(t => !p.Remove.Contains(t, StringComparer.OrdinalIgnoreCase))
                .Concat(p.AddTags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Eq("落地后正好是用户要的三条", string.Join("|", after), "solo|smile|upper body");

            // 没有 remove_tags 时行为一字不变（既有调用方不必改）
            var old = AgentKit.ParseProposal("""{"add_tags":["solo"]}""", IsK);
            True("缺 remove_tags 时删除列表为空", !old.HasRemovals);
            Eq("Remove 永不为 null", old.Remove.Count, 0);
            True("纯加法仍然是 HasTags", old.HasTags);

            // 同一个标签既说删又说加：判给"加"（保守，免得手抖删掉东西）
            var both = AgentKit.ParseProposal("""
                {"add_tags":["solo"],"remove_tags":["solo","blonde hair"]}
                """, IsK);
            Eq("既删又加的判给加，不进删除列表", string.Join("|", both.Remove), "blonde hair");
            Eq("加的那条还在", string.Join("|", both.AddTags), "solo");

            // 大小写不同也算同一个标签
            var caseMix = AgentKit.ParseProposal("""
                {"add_tags":["Solo"],"remove_tags":["solo"]}
                """, IsK);
            Eq("忽略大小写判重", caseMix.Remove.Count, 0);

            // 手抖宽容：remove_tags 写成逗号分隔的字符串也要收
            var sloppy = AgentKit.ParseProposal("""{"remove_tags":"`1girl`, blonde hair"}""", IsK);
            Eq("字符串也按逗号拆", string.Join("|", sloppy.Remove), "1girl|blonde hair");

            // 工具声明里必须有这个字段，且说明写清"不许谎称替换过"
            var props = AgentKit.ProposeTool.Parameters["properties"]!;
            True("propose 声明了 remove_tags", props["remove_tags"] is not null);
            True("说明里写明只能靠它来删",
                 AgentKit.ProposeTool.Parameters["properties"]!["remove_tags"]!["description"]!
                     .GetValue<string>().Contains("REMOVE"));
            True("系统提示要求替换时必须列 remove_tags",
                 AgentKit.SystemPrompt(true, false).Contains("remove_tags"));

            // current_image 必须把现有标签**原样列出来**，否则模型无从知道该删哪些
            var h = new FakeHost();
            h.Dataset.Add(new AgentImage("a", 0, true, 5, IsCurrent: true,
                                         Tags: new[] { "1girl", "solo", "blonde hair" }));
            h.Turn(Tool("i", AgentKit.CurrentImageToolName, "{}"))
             .Turn(Tool("p", AgentKit.ProposeToolName, """{"add_tags":[]}"""));
            Run(h, "现在有哪些标签");
            var info = h.SeenMessages[1]
                .First(m => m["role"]?.GetValue<string>() == "tool")["content"]!.GetValue<string>();
            True("列出了现有标签名", info.Contains("1girl, solo, blonde hair"));
            True("告诉模型用 remove_tags 删", info.Contains("remove_tags"));

            // 只报数量是不够的（这正是原来的样子）
            var h2 = new FakeHost();
            h2.Dataset.Add(new AgentImage("a", 0, true, 5, IsCurrent: true));
            h2.Turn(Tool("i2", AgentKit.CurrentImageToolName, "{}"))
              .Turn(Tool("p2", AgentKit.ProposeToolName, """{"add_tags":[]}"""));
            Run(h2, "现在有哪些标签");
            var info2 = h2.SeenMessages[1]
                .First(m => m["role"]?.GetValue<string>() == "tool")["content"]!.GetValue<string>();
            True("没给标签名时不瞎编列表", !info2.Contains("Its current tags"));

            // 日志与工具结果都要体现删除（界面上那一步要看得见）
            var h3 = new FakeHost();
            h3.Dataset.Add(new AgentImage("a", 0, true, 5, IsCurrent: true));
            h3.Turn(Tool("p3", AgentKit.ProposeToolName,
                """{"add_tags":["solo"],"remove_tags":["blonde hair","twintails"]}"""));
            var overwriteOutcome = Run(h3, "覆盖成 solo");
            True("日志里写了删几个", overwriteOutcome.Log.Any(l => l.Contains("删除 2 个")));
            True("工具调用记录里也写了",
                 overwriteOutcome.ToolCalls is not null &&
                 overwriteOutcome.ToolCalls.Any(c => c.Result.Contains("删除 2 个")));
        }

        Console.WriteLine("\n=== 29. 覆盖的合并语义：先删后加、忽略大小写、既删又加判给加 ===");
        {
            // 第 28 节验的是"参数解析"，这一节验"解析出来的东西怎么合并进现有列表"
            // ——用户报的"感觉没变化"就发生在这一步与它的下游（保存）。
            var knownSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "solo", "smile", "upper body", "1girl", "blonde hair" };
            bool IsK2(string t) => knownSet2.Contains(t);

            var before = new[] { "1girl", "solo", "blonde hair", "twintails" };

            // 用户要的：覆盖成三条 → 结果必须正好是三条
            var p = AgentKit.ParseProposal("""
                {"remove_tags":["1girl","blonde hair","twintails"],
                 "add_tags":["solo","smile","upper body"]}
                """, IsK2);
            var (merged, added, removed) = AgentKit.MergeProposal(before, p);
            Eq("合并后正好三条", string.Join("|", merged), "solo|smile|upper body");
            Eq("删掉 3 个", removed, 3);
            Eq("新增 2 个（solo 本来就在）", added, 2);
            Eq("入参没被改动", string.Join("|", before), "1girl|solo|blonde hair|twintails");

            // 模型把手抖写出的"既删又加"一起发过来时（实测就这么干过：
            // 16 个全列进 remove、要留的三个又列进 add），结果仍然是对的
            var all = new[] { "a", "b", "c" };
            var messy = AgentKit.ParseProposal("""
                {"remove_tags":["a","b","c","A"],"add_tags":["a","b","c"]}
                """, IsK2);
            var (m2, add2, rem2) = AgentKit.MergeProposal(all, messy);
            Eq("既删又加不丢标签", string.Join("|", m2), "a|b|c");
            Eq("没有真的删", rem2, 0);
            Eq("也没有真的加", add2, 0);

            // 纯删除（用户说"把 X 去掉"）
            var del = AgentKit.ParseProposal("""{"remove_tags":["blonde hair"]}""", IsK2);
            var (m3, _, rem3) = AgentKit.MergeProposal(before, del);
            Eq("只删不加", string.Join("|", m3), "1girl|solo|twintails");
            Eq("删了一个", rem3, 1);

            // 删一个不在列表里的：如实算作没删到，不报错
            var miss = AgentKit.ParseProposal("""{"remove_tags":["nope"]}""", IsK2);
            var (m4, _, rem4) = AgentKit.MergeProposal(before, miss);
            Eq("没删到就一个都不动", string.Join("|", m4), "1girl|solo|blonde hair|twintails");
            Eq("计数如实为 0", rem4, 0);

            // 大小写不同算同一个标签
            var ci = AgentKit.ParseProposal("""{"remove_tags":["1GIRL"]}""", IsK2);
            var (m5, _, rem5) = AgentKit.MergeProposal(before, ci);
            Eq("忽略大小写删掉", string.Join("|", m5), "solo|blonde hair|twintails");
            Eq("算删到了", rem5, 1);

            // 空提案不动任何东西（只有一个 note 的那次实测）
            var note = AgentKit.ParseProposal("""{"add_tags":[],"remove_tags":[],"note":"这次不改"}""", IsK2);
            var (m6, add6, rem6) = AgentKit.MergeProposal(before, note);
            Eq("空提案不改列表", string.Join("|", m6), string.Join("|", before));
            Eq("空提案计数为 0", (add6, rem6), (0, 0));
        }

        Console.WriteLine($"\n=== agent: {(bad == 0 ? "OK" : bad + " FAILED")} ===");
        return bad == 0 ? 0 : 1;
    }

    private static LlmClient.ToolTurn Tool(string id, string name, string args) =>
        new(true, "", "", new List<LlmClient.ToolCall> { new(id, name, args) }, "", "");

    /// <summary>
    /// 调私有的 ParseToolTurn。用反射而不是把它改成 internal/public——**判据不该
    /// 为了测试方便而改变产品可见性**（tests/Vocab.cs 里调 AnimaFormat 也是这么做的）。
    /// </summary>
    private static LlmClient.ToolTurn ParseTurn(string json)
    {
        var m = typeof(LlmClient).GetMethod("ParseToolTurn",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (LlmClient.ToolTurn)m.Invoke(null, new object[] { json })!;
    }

    /// <summary>同步包一层，免得每一节都写 async/await 样板。</summary>
    /// <param name="maxRounds">0 = 用产品默认值（32）。要考"轮数用尽"那条路时显式给个小值。</param>
    private static TagAgent.Outcome Run(FakeHost host, string text,
                                        LlmClient.ChatImage? image = null, int maxRounds = 0) =>
        maxRounds > 0
            ? new TagAgent().RunAsync(host, new[] { new AgentTurn(true, text) }, image,
                                      CancellationToken.None, null, null, maxRounds)
                            .GetAwaiter().GetResult()
            : new TagAgent().RunAsync(host, new[] { new AgentTurn(true, text) }, image)
                            .GetAwaiter().GetResult();

    /// <summary>
    /// 带进度回调地跑一轮——用来验证 TagAgent 会把流式回调透传到 host。
    /// 不给回调时它就是非流式，这正是既有那几节的行为。
    /// </summary>
    private static TagAgent.Outcome RunStreaming(FakeHost host, string text,
                                                 Action<string, string> onProgress) =>
        new TagAgent().RunAsync(host, new[] { new AgentTurn(true, text) }, null,
                                CancellationToken.None, onProgress)
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

        /// <summary>被要求走流式的轮数。</summary>
        public int StreamRequested { get; private set; }

        /// <summary>收到过的进度回调（已累积思考, 已累积正文）。</summary>
        public List<(string Reasoning, string Content)> Progress { get; } = new();

        /// <summary>假数据集。空数组 = 没有数据集 → 两个图片工具不下发。</summary>
        public List<AgentImage> Dataset { get; } = new();

        /// <summary>模型请求过的切图目标，按顺序记下来。</summary>
        public List<string> SelectRequests { get; } = new();

        /// <summary>切图是否成功；测试里可以设成 false 验证失败路径。</summary>
        public bool SelectSucceeds { get; set; } = true;

        /// <summary>`image_info` 回什么。默认给一句能认出来的话。</summary>
        public string InfoText { get; set; } = "facts: 1000x2000 png";

        /// <summary>记录 view_region 收到过的参数原文。</summary>
        public List<string> ViewRequests { get; } = new();

        /// <summary>view_region 回不回图；设成 false 验证"裁剪失败"那条路。</summary>
        public bool ViewSucceeds { get; set; } = true;

        /// <summary>view_region 每次回几张图（tiles 用得上）。</summary>
        public int ViewImageCount { get; set; } = 1;

        public IReadOnlyList<AgentImage> Images => Dataset;

        public Task<string> ImageInfo() => Task.FromResult(InfoText);

        public Task<(bool Ok, string Message, IReadOnlyList<LlmClient.ChatImage> Images)>
            ViewRegion(string arguments)
        {
            ViewRequests.Add(arguments);
            if (!ViewSucceeds)
                return Task.FromResult((false, "could not produce that crop",
                                        (IReadOnlyList<LlmClient.ChatImage>)Array.Empty<LlmClient.ChatImage>()));

            var images = new List<LlmClient.ChatImage>();
            for (var i = 0; i < ViewImageCount; i++)
                images.Add(new LlmClient.ChatImage("image/jpeg", new byte[] { 9, 9, 9, (byte)i }));
            return Task.FromResult((true, "enlarged region attached", (IReadOnlyList<LlmClient.ChatImage>)images));
        }

        public (bool Ok, string Message) SelectImage(string name)
        {
            SelectRequests.Add(name);
            if (!SelectSucceeds) return (false, $"No image named \"{name}\".");
            // 切换当前项，并让下一次 current_image 能看到变化
            for (var i = 0; i < Dataset.Count; i++)
                Dataset[i] = Dataset[i] with { IsCurrent = Dataset[i].Name == name };
            return (true, $"Switched to \"{name}\".");
        }

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
                                                  CancellationToken ct,
                                                  Action<string, string>? onProgress = null)
        {
            SeenPrompts.Add(systemPrompt);
            SeenTools.Add(tools.Select(t => t.Name).ToList());
            SeenMessages.Add(messages.Select(m => (JsonObject)m.DeepClone()).ToList());
            // 有没有把流式回调透传下来，只有这里看得出来（第 19 节断言这个）
            if (onProgress is not null)
            {
                StreamRequested++;
                Progress.Add(("思考片段A", ""));
                onProgress("思考片段A", "");
                Progress.Add(("思考片段A思考片段B", "答案开头"));
                onProgress("思考片段A思考片段B", "答案开头");
            }

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
