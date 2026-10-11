using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnimaCaptioner.Core;

namespace CoreCheck;

/// <summary>
/// 临时探针：**走真实网络**，用生产代码路径验证能力获取与"关闭思考"是否真生效。
///
/// 第 2 条做成 A/B：同一个端点、同一条提问、同一个 max_tokens，分别发
/// 「改动前的请求体」（只有 chat_template_kwargs）和「改动后的请求体」
/// （ChatAsync 生产路径），直接看服务端返回的 reasoning_content 长度。
/// 只有对照组成立，"修好了"才不是一句自以为是。
///
/// 会读当前用户的 settings.json（含解密的 API Key）打真实请求，故不属于默认套件。
/// </summary>
public static class LlmLive
{
    private const string Sys = "Reply with exactly the word OK and nothing else.";
    private const string User = "say OK";

    public static int Run()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var cfg = AppSettings.Load();

        Console.WriteLine($"endpoint        = {cfg.ApiBaseUrl}");
        Console.WriteLine($"model           = {cfg.Model}");
        Console.WriteLine($"DisableThinking = {cfg.DisableThinking}   MaxTokens = {cfg.MaxTokens}");
        Console.WriteLine($"key             = {(cfg.ApiKey.Length > 0 ? $"已配置（{cfg.ApiKey.Length} 字符）" : "(空)")}");
        Console.WriteLine();

        var llm = new LlmClient();

        var caps = llm.EnsureCapsAsync(cfg).GetAwaiter().GetResult();
        if (caps is null)
        {
            Console.WriteLine("caps            = (未获取到：/models 没报或拉取失败)");
        }
        else
        {
            Console.WriteLine($"caps.name       = {caps.DisplayName}");
            Console.WriteLine($"上下文 / 输出上限 = {caps.ContextLength} / {caps.MaxOutputTokens}");
            Console.WriteLine($"输入模态        = {string.Join("/", caps.InputModalities ?? new List<string>())}");
            Console.WriteLine($"能吃图          = {caps.SupportsImage?.ToString() ?? "(服务端未声明)"}");
            Console.WriteLine($"思考档位        = {string.Join("/", caps.EffortLevels ?? new List<string>())} 默认={caps.DefaultEffort}");
            Console.WriteLine($"Describe()      = {caps.Describe()}");
        }

        var effort = LlmClient.ThinkingOffEffort(cfg.DisableThinking, caps?.SupportsEffort == true);
        Console.WriteLine($"决定            = ThinkingOffEffort({cfg.DisableThinking}, {caps?.SupportsEffort == true}) " +
                          $"→ {(effort is null ? "不发 reasoning_effort" : "reasoning_effort=" + effort)}");
        Console.WriteLine();

        // ---------- 对照组：改动前的请求体 ----------
        var oldReasoning = RawReasoning(cfg, withEffort: false);
        Console.WriteLine($"对照（旧请求体：只发 chat_template_kwargs）  reasoning 长度 = {oldReasoning}");

        // ---------- 实验组：走生产代码 ----------
        var r = llm.ChatAsync(cfg, Sys, User).GetAwaiter().GetResult();
        Console.WriteLine($"实验（ChatAsync 生产路径）                ok={r.Ok} content='{r.Content.Trim()}' reasoning 长度 = {r.Reasoning.Length}");
        if (!r.Ok) Console.WriteLine($"  error = {r.Error}");

        var pass = r.Ok && r.Reasoning.Length == 0;
        Console.WriteLine($"\n=== llm: {(pass ? "OK（关思考确实生效）" : "FAIL")} ===" +
                          (pass && oldReasoning == 0
                              ? "\n  注：对照组的 reasoning 也是 0，说明该端点本来就不思考，这次没能区分出差别。"
                              : ""));
        return pass ? 0 : 1;
    }

    /// <summary>照改动前的样子手工拼一个请求体，返回服务端给的 reasoning_content 长度。</summary>
    private static int RawReasoning(AppSettings cfg, bool withEffort)
    {
        try
        {
            var body = new JsonObject
            {
                ["model"] = cfg.Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = Sys },
                    new JsonObject { ["role"] = "user", ["content"] = User },
                },
                ["max_tokens"] = cfg.MaxTokens,
                ["temperature"] = cfg.Temperature,
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            };
            if (withEffort) body["reasoning_effort"] = "none";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var req = new HttpRequestMessage(HttpMethod.Post, cfg.ApiBaseUrl.TrimEnd('/') + "/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json")
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());

            using var resp = http.Send(req);
            var raw = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
            var doc = JsonDocument.Parse(raw);
            var msg = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
            return msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String
                ? rc.GetString()!.Length
                : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  对照组请求失败：" + ex.Message);
            return -1;
        }
    }
}
