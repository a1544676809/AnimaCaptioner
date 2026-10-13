using AnimaCaptioner.Core;
using System.Net;
using System.Text;

namespace CoreCheck;

/// <summary>
/// 自定义请求头的回归。
///
/// 第 1 节是纯逻辑（解析与校验），第 2 节起一个**本地假服务端**、走
/// <see cref="LlmClient.ChatAsync"/> 真实发一次请求，把收到的头逐个断言。
///
/// 为什么非要真发一次：`TryAddWithoutValidation` 有两个静默失败（非法头名返回
/// false、实体头加到 Headers 上被忽略），**两个都不抛异常**、只看代码看不出来。
/// 只有服务端实际收到什么才算数。
/// </summary>
public static class Headers
{
    public static int Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task<int> RunAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var fail = 0;
        void Check(string what, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!ok) fail++;
        }

        Console.WriteLine("=== 1. 解析与校验（纯逻辑）===");
        {
            var r = CustomHeaders.Parse("x-opencode-session: ses_abc");
            Check("冒号分隔", r.Entries.Count == 1 && r.Entries[0].Name == "x-opencode-session"
                              && r.Entries[0].Value == "ses_abc");
            Check("无拒绝项", !r.HasRejected);

            r = CustomHeaders.Parse("X-Title=My App");
            Check("等号也认（便利写法）", r.Entries.Count == 1 && r.Entries[0].Value == "My App");

            r = CustomHeaders.Parse("X: a=b");
            Check("值里的等号不被切开", r.Entries.Count == 1 && r.Entries[0].Value == "a=b");

            r = CustomHeaders.Parse("# 注释\n\n  x-a: 1  \n");
            Check("注释行与空行忽略、值去空白",
                r.Entries.Count == 1 && r.Entries[0].Value == "1");

            // 下面这几条是**静默失败**的防线：不报出来的话，用户填了却毫无反馈。
            r = CustomHeaders.Parse("X-Bad Name: v");
            Check("头名含空格被拒并报出", r.Entries.Count == 0 && r.HasRejected, r.Rejected.FirstOrDefault() ?? "");

            r = CustomHeaders.Parse("X:Y: v");
            // 按**第一个**冒号切分是 HTTP 的标准行为：值本身可以含冒号
            // （`X-Forwarded-For: 1.2.3.4:80`、`Host: a:443` 都这样）。
            // 所以这里不能算非法——早先我把它当错误预期，是我想错了。
            Check("值里的冒号保留在值里（按第一个冒号切）",
                r.Entries.Count == 1 && r.Entries[0].Name == "X" && r.Entries[0].Value == "Y: v",
                r.Entries.Count == 1 ? $"[{r.Entries[0].Name}]='{r.Entries[0].Value}'" : "");

            r = CustomHeaders.Parse(": v");
            Check("空头名被拒", r.Entries.Count == 0 && r.HasRejected);

            r = CustomHeaders.Parse("X-Empty:");
            Check("空值被拒（要删就删行，不是留空）", r.Entries.Count == 0 && r.HasRejected);

            r = CustomHeaders.Parse("no-separator-here");
            Check("没有分隔符被拒", r.Entries.Count == 0 && r.HasRejected);

            // 同一个头填两次会拼成一个逗号连接的值（实测），必须挡住。
            r = CustomHeaders.Parse("X-A: 1\nx-a: 2");
            Check("重复头名（忽略大小写）被拒", r.Entries.Count == 1 && r.HasRejected);

            // Authorization 由 API Key 负责；这里再填会拼成坏值。
            r = CustomHeaders.Parse("Authorization: Bearer x");
            Check("手填 Authorization 被拒并说明原因",
                r.Entries.Count == 0 && r.HasRejected &&
                (r.Rejected[0].Contains("API Key") || r.Rejected[0].Contains("程序自己")),
                r.Rejected.FirstOrDefault() ?? "");

            r = CustomHeaders.Parse("Content-Type: text/plain");
            Check("Content-Type 被识别为实体头（要走 Content.Headers）",
                r.Entries.Count == 1 && r.Entries[0].IsContentHeader);

            r = CustomHeaders.Parse("x-a: 1\nX-B: 2\nx-c: 3");
            Check("多个头都保留", r.Entries.Count == 3 && !r.HasRejected);
        }

        Console.WriteLine("=== 2. 真实发一次请求，看服务端收到什么 ===");
        {
            var listener = new HttpListener();
            var port = 18431;
            // 端口可能被占，往上试几个。
            for (; port < 18441; port++)
            {
                try { listener.Prefixes.Clear(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start(); break; }
                catch { if (port == 18440) { Check("起本地假服务端", false); return fail; } }
            }

            var bodies = new List<string>();
            // 收满**发出的全部请求**（含 GET /models）。ChatAsync 首次会先拉一次
            // /models 探能力（EnsureCapsAsync），所以真实请求数比"聊天次数"多。
            // 第一版按聊天次数开服务端，于是余出来的那次 GET 被当成了下一次聊天，
            // 所有断言整体错位一格——**这就是那三个"失败"的真正原因**，
            // 不是产品的问题。
            const int requestCount = 4;
            var server = Task.Run(async () =>
            {
                for (var i = 0; i < requestCount; i++)
                {
                    var ctx = await listener.GetContextAsync();

                    // **必须把请求体读完再回复。** 不读的话，未消费的 POST 体留在
                    // keep-alive 连接上，被服务端当成下一个请求的起始字节，后续请求会错配。
                    using (var reader = new StreamReader(ctx.Request.InputStream))
                        await reader.ReadToEndAsync();

                    var sb = new StringBuilder();
                    sb.Append("### ").Append(ctx.Request.HttpMethod).Append(' ')
                      .Append(ctx.Request.Url?.AbsolutePath).Append('\n');
                    foreach (var k in ctx.Request.Headers.AllKeys)
                        sb.Append(k).Append(": ").Append(ctx.Request.Headers[k]).Append('\n');
                    // HttpListener 把实体头单独摘出来了（ContentType / ContentLength 等
                    // 不在 Headers 集合里）。不显式补上就永远看不到 Content-Type。
                    if (ctx.Request.ContentType is { Length: > 0 } ct)
                        sb.Append("Content-Type: ").Append(ct).Append('\n');
                    bodies.Add(sb.ToString());
                    var body = Encoding.UTF8.GetBytes(
                        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}," +
                        "\"finish_reason\":\"stop\"}]}");
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.OutputStream.Write(body);
                    ctx.Response.Close();
                }
            });

            var cfg = new AppSettings
            {
                ApiBaseUrl = $"http://127.0.0.1:{port}/v1",
                Model = "test-model",
                ApiKey = "sk-real-key",
                TimeoutSeconds = 20,
                ReasoningEffort = "none"
            };
            cfg.ActiveProfile!.CustomHeaders =
                "# 注释\n" +
                "x-opencode-session: ses_abc123\n" +
                "x-custom-thing = value-with-equals\n" +
                "X-Bad Name: nope\n" +                    // 非法名：必须被拒
                "Authorization: Bearer rejected\n";       // 必须被拒

            var llm = new LlmClient();
            var chat = llm.ChatAsync(cfg, "sys", "hello").GetAwaiter().GetResult();
            Check("请求成功（假服务端返回了 choices）", chat.Ok, chat.Error);
            Check("回复解析正确", chat.Content == "ok", chat.Content);

            // 第二次：验证 Content-Type 这类实体头能否被覆盖（走 Content.Headers 才有效）。
            cfg.ActiveProfile!.CustomHeaders = "Content-Type: application/json";
            var chat2 = llm.ChatAsync(cfg, "sys", "hello").GetAwaiter().GetResult();
            Check("带 Content-Type 自定义头时请求仍成功", chat2.Ok, chat2.Error);

            // 第三次：清空后要能回到默认（证明不是"一旦设过就永久生效"）。
            cfg.ActiveProfile!.CustomHeaders = "";
            var chat3 = llm.ChatAsync(cfg, "sys", "hello").GetAwaiter().GetResult();
            Check("清空自定义头后请求仍成功", chat3.Ok, chat3.Error);

            Check("服务端收满了全部请求", await WaitForRequests(bodies, requestCount),
                $"{bodies.Count}/{requestCount}");
            try { await server; } catch { }
            listener.Stop();

            // **按路径与序号筛选**，不靠"第几个包"硬猜：ChatAsync 会先发一次
            // GET /models 探能力，混在里面会让下标整体错位。
            var posts = bodies.Where(b => b.Contains("### POST")).ToList();
            var gets = bodies.Where(b => b.Contains("### GET")).ToList();
            var p1 = posts.ElementAtOrDefault(0) ?? "";
            var p2 = posts.ElementAtOrDefault(1) ?? "";
            var p3 = posts.ElementAtOrDefault(2) ?? "";

            Console.WriteLine($"    服务端共收到 {bodies.Count} 个请求" +
                              $"（GET {gets.Count} / POST {posts.Count}）");
            Console.WriteLine("    第 1 次 POST 收到的头：");
            foreach (var line in p1.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Console.WriteLine("      " + line);
            Console.WriteLine("    第 2 次 POST Content-Type：" + HeaderLine(p2, "Content-Type"));
            Console.WriteLine("    第 3 次 POST Content-Type：" + HeaderLine(p3, "Content-Type"));

            Check("探测能力的 GET /models 也带上了自定义头（三条路径都要覆盖）",
                gets.Count > 0 && gets[0].Contains("ses_abc123"),
                gets.Count > 0 ? "" : "没有 GET 请求");

            Check("自定义头真的送到了（x-opencode-session）", p1.Contains("ses_abc123"));
            Check("等号写法的值也送到了", p1.Contains("value-with-equals"));
            Check("Authorization 用的是 API Key 那把", p1.Contains("sk-real-key"));
            Check("手填的 Authorization 没有生效", !p1.Contains("rejected"));
            Check("非法头名没有上线路", !p1.Contains("X-Bad Name"));

            // 必须**精确**比：用 Contains("application/json") 会让
            // "application/json; charset=utf-8" 也算通过，那条断言就永远绿、
            // 什么也证明不了（第一版就是这么写的）。
            Check("Content-Type 被精确覆盖为 application/json（无 charset）",
                HeaderValue(p2, "Content-Type") == "application/json",
                HeaderLine(p2, "Content-Type"));
            Check("清空后 Content-Type 回到默认（带 charset）",
                HeaderValue(p3, "Content-Type") == "application/json; charset=utf-8",
                HeaderLine(p3, "Content-Type"));
            Check("清空后自定义头确实不再发送", !p3.Contains("ses_abc123"));

            // 界面上要能看到被拒的行，否则用户填了却毫无反馈。
            cfg.ActiveProfile!.CustomHeaders = "X-Bad Name: v\nAuthorization: Bearer x";
            var problems = LlmClient.DescribeCustomHeaderProblems(cfg);
            Check("DescribeCustomHeaderProblems 报出两个问题",
                problems.Contains("X-Bad Name") && problems.Contains("Authorization"), problems);

            cfg.ActiveProfile!.CustomHeaders = "x-ok: 1";
            Check("没有问题时返回空串", LlmClient.DescribeCustomHeaderProblems(cfg).Length == 0);
        }

        Console.WriteLine("=== 3. 「测试连接 / 获取列表」也要带头（设置页用临时 AppSettings 探）===");
        {
            // 这是一条**已经发生过的 bug** 的回归：设置页那个 probe 是新建的
            // AppSettings，只搬了地址和密钥，头没搬——于是"测试连接"在**没带专属头**
            // 的状态下打过去，对需要头的网关回 400/401。用户看到的是"按钮说连不上"，
            // 而保存之后又能用，最容易让人以为地址或密钥填错了。
            var listener = new HttpListener();
            var port = 18521;
            for (; port < 18531; port++)
            {
                try
                {
                    listener.Prefixes.Clear();
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    listener.Start();
                    break;
                }
                catch { if (port == 18530) { Check("起本地假服务端", false); return fail; } }
            }

            var got = new List<string>();
            var server = Task.Run(async () =>
            {
                var ctx = await listener.GetContextAsync();
                using (var r = new StreamReader(ctx.Request.InputStream)) await r.ReadToEndAsync();
                var sb = new StringBuilder();
                sb.Append(ctx.Request.HttpMethod).Append(' ')
                  .Append(ctx.Request.Url?.AbsolutePath).Append('\n');
                foreach (var k in ctx.Request.Headers.AllKeys)
                    sb.Append(k).Append(": ").Append(ctx.Request.Headers[k]).Append('\n');
                got.Add(sb.ToString());
                var body = Encoding.UTF8.GetBytes("{\"object\":\"list\",\"data\":[{\"id\":\"m1\"}]}");
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(body);
                ctx.Response.Close();
            });

            // 照抄设置页 FetchModelsAsync 里构造 probe 的写法（含那次修复）。
            var probe = new AppSettings
            {
                ApiBaseUrl = $"http://127.0.0.1:{port}/v1",
                TimeoutSeconds = 20,
            };
            probe.ApiKey = "sk-from-editor";
            probe.ActiveProfile!.CustomHeaders = "x-opencode-session: ses_from_editor";

            var llm2 = new LlmClient();
            var (ok, models, err) = await llm2.ListModelsAsync(probe);
            Check("ListModelsAsync 成功（假服务端给了 1 个模型）", ok && models.Count == 1, err);

            try { await server; } catch { }
            listener.Stop();

            var text = got.Count > 0 ? got[0] : "";
            Check("走的是 GET /v1/models", text.Contains("GET /v1/models"), text.Split('\n')[0]);
            Check("自定义头跟着 probe 一起发出去（这条就是那个 bug 的回归）",
                text.Contains("ses_from_editor"));
            Check("Authorization 也在", text.Contains("sk-from-editor"));
        }

        Console.WriteLine("=== 4. 站点深拷贝带上自定义头（设置页在副本上编辑）===");
        {
            var p = new ModelProfile { BaseUrl = "http://x/v1", Model = "m" };
            p.CustomHeaders = "x-a: 1";
            var c = p.Clone();
            Check("Clone 保留 CustomHeaders", c.CustomHeaders == "x-a: 1", c.CustomHeaders);
            c.CustomHeaders = "x-a: 2";
            Check("改副本不影响原件（取消才能真的取消）", p.CustomHeaders == "x-a: 1", p.CustomHeaders);

            var json = new AppSettings { }.ToJson();
            Check("默认序列化里没有多余的自定义头", !json.Contains("x-a"));

            // 落盘再读回：字段名写错的话，用户填完保存、下次打开就没了，
            // 而症状只是"那个平台还是连不上"，很难追到这里。
            var cfg2 = new AppSettings();
            cfg2.ActiveProfile!.CustomHeaders = "x-opencode-session: ses_round\r\nx-b: 2";
            var back = AppSettings.FromJson(cfg2.ToJson());
            Check("自定义头能落盘再读回",
                back?.ActiveProfile?.CustomHeaders == "x-opencode-session: ses_round\r\nx-b: 2",
                back?.ActiveProfile?.CustomHeaders ?? "(null)");
            Check("读回后仍解析得出两个头",
                CustomHeaders.Parse(back?.ActiveProfile?.CustomHeaders).Entries.Count == 2);

            // 顶层视图要把 CustomHeaders 代理到活动站点（和别的字段一致的形状）。
            var cfg3 = new AppSettings();
            cfg3.ActiveProfile!.CustomHeaders = "x-proxy: 1";
            var clone = cfg3.Profiles.Select(p => p.Clone()).ToList();
            Check("站点表能整体复制（设置页在副本上编辑）",
                clone.All(p => p.CustomHeaders == "x-proxy: 1"));
        }

        Console.WriteLine(fail == 0 ? "\n自定义请求头：全部通过" : $"\n自定义请求头：{fail} 项失败");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>等假服务端收到至少 n 个请求（带超时，别让测试挂住）。</summary>
    private static async Task<bool> WaitForRequests(List<string> bodies, int n)
    {
        for (var i = 0; i < 100; i++)
        {
            if (bodies.Count >= n) return true;
            await Task.Delay(50);
        }
        return false;
    }

    /// <summary>从抓到的头文本里取某一行的完整内容（含名字）。</summary>
    private static string HeaderLine(string blob, string name) =>
        blob.Split('\n').FirstOrDefault(l =>
            l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?.Trim() ?? "(无)";

    /// <summary>只取值部分。精确比较用，避免 Contains 的子串误判。</summary>
    private static string HeaderValue(string blob, string name)
    {
        var line = HeaderLine(blob, name);
        if (line == "(无)") return "(无)";
        var idx = line.IndexOf(':');
        return idx < 0 ? line : line[(idx + 1)..].Trim();
    }
}
