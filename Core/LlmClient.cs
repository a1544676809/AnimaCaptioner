using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimaCaptioner.Core;

/// <summary>
/// 标准 OpenAI 兼容接口的客户端。本地 llama-server 和任何云端服务走同一套代码：
/// 只换 base url、key、model。
///
/// 两处刻意的设计：
/// - chat_template_kwargs.enable_thinking 只在显式要求时下发。本地 llama-server
///   认这个字段，云端多数服务不认；不认的服务会直接 400，所以默认不发。
/// - 思考链可能把 content 吃空（实测 Qwen3 在 enable_thinking=true 时答案落在
///   reasoning_content）。所以两个字段都读，content 为空才退回 reasoning_content。
/// </summary>
public sealed class LlmClient : IDisposable
{
    private readonly HttpClient _http = new();

    /// <summary>
    /// 上一次设进 HttpClient 的超时值。
    ///
    /// 踩过的坑：`HttpClient.Timeout` **只能在发出第一个请求之前设置**，之后再设会抛
    /// `InvalidOperationException: This instance has already started one or more requests`。
    /// 所以"夹中文要重试一次"这条路径第一次真跑起来就炸了（日志里能看到
    /// `llm request failed: ... CheckDisposedOrStarted`）。这里记住当前值，
    /// 只有真的要变时才设——同一个超时值重复赋值同样会抛。
    /// </summary>
    private TimeSpan _currentTimeout = Timeout.InfiniteTimeSpan;

    public string LastError { get; private set; } = "";

    /// <summary>拆关键词用的系统提示。要求只出中文短词，不要英文、不要整句。</summary>
    public const string KeywordSystemPrompt =
        "你是提示词分析助手。把用户的中文画面描述拆解成一组简短的中文关键词，" +
        "每个关键词对应画面中的一个具体元素：人物数量、发型、发色、瞳色、表情、姿势、" +
        "服装部件、动作、镜头景别、背景环境、光线。" +
        "只输出关键词，用中文逗号分隔，不要英文，不要解释，不要整句。";

    /// <summary>
    /// 中文描述 → 英文自然语言（Anima 的散文通道）。
    ///
    /// 这条提示的设计依据是 Anima 官方模型卡（circlestone-labs/Anima，Prompting）：
    ///
    ///   "If using pure natural langauge, more descriptive is better.
    ///    Aim for at least 2 sentences. Extremely short prompts can give
    ///    unexpected results."
    ///   "Name a character, then describe their basic appearance."
    ///     - "This is extra important when prompting for multiple characters."
    ///   "Follow standard English capitalization rules for character and series names."
    ///
    /// 所以约束是：写成**连贯的英文散文**（不是标签串），至少两句，
    /// 先点人物再描写外貌，人名/作品名按英文正字法大写。
    ///
    /// 刻意不要求它输出 Danbooru 标签：实测 8B 直接写英文标签会写成散文
    /// （`black and white maid outfit`），用 GBNF 语法约束又会在含义上选错词
    /// （`少女` → 某个光之美少女角色）。标签那条路由词库负责，模型只负责它
    /// 真正擅长的部分——读懂描述并写成通顺的英文。
    /// </summary>
    public const string ProseSystemPrompt =
        "You are an English prompt writer for the Anima text-to-image model. " +
        "Translate the user's Chinese description into ONE English paragraph of " +
        "flowing natural language.\n" +
        "Rules:\n" +
        "- Write at least 2 complete sentences, and be descriptive. Do not produce a " +
        "comma-separated tag list.\n" +
        "- Name the subject first, then describe appearance, clothing, pose, " +
        "background and lighting.\n" +
        "- Capitalize character and series names following standard English rules.\n" +
        "- Do not add quality words such as masterpiece or best quality, and do not " +
        "add score tags; those are handled separately.\n" +
        "- Output ONLY the English paragraph. No Chinese, no quotes, no explanation, " +
        "no markdown.";

    public sealed record ChatResult(bool Ok, string Content, string Reasoning, string Raw, string Error);

    /// <summary>模型要求调用的一次工具。</summary>
    public sealed record ToolCall(string Id, string Name, string Arguments);

    /// <summary>一轮带工具的响应：既有最终文本，也可能带若干待执行的工具调用。</summary>
    public sealed record ToolTurn(bool Ok, string Content, string Reasoning,
                                  List<ToolCall> Calls, string Raw, string Error);

    /// <summary>
    /// 单个标签翻译的系统提示。
    ///
    /// 两个刻意的设计：
    /// - **只输出译文本身**。以前让它解释，结果译文里混进了大段说明文字，
    ///   而这一栏是给人看的短注释，不是对话。
    /// - **允许它说"不知道"**。词库里查不到的标签大多是生僻的 Anima 原生标签
    ///   （`cat smile`、`ass grab`）或颜色+名词的复合词（`white back bow`）。
    ///   逼它猜会编出像模像样的错译，而错译会污染训练集——这条路径的价值
    ///   恰恰在于"比沉默多说一点"，所以宁可让它弃权。
    /// </summary>
    public const string TagGlossSystemPrompt =
        "You translate Danbooru/Anima image-generation tags into Simplified Chinese " +
        "for a Chinese-speaking artist.\n" +
        "Rules:\n" +
        "- Output ONLY the Chinese translation. No English, no quotes, no explanation, " +
        "no markdown, no pinyin.\n" +
        "- Keep it short: a noun phrase, at most about 12 Chinese characters. " +
        "This is a glossary entry, not a sentence.\n" +
        "- Use the standard term an artist would use. For garments and poses prefer " +
        "the common Chinese art term over a literal word-for-word rendering.\n" +
        "- If the tag is a character name, an artist name, a series name, or a meme " +
        "you are not sure about, answer exactly: UNKNOWN\n" +
        "- If you are not confident of the meaning, answer exactly: UNKNOWN\n" +
        "Do not guess.";

    /// <summary>带联网搜索时的附加说明。工具描述本身也让模型知道该在什么时候查。</summary>
    private const string SearchToolDescription =
        "Search the web for the meaning of an image tag. Use this when you do not " +
        "know what a tag means, especially for character names, artist names, memes, " +
        "or unusual garment and pose terms. Do not use it for tags you already know.";

    /// <summary>发一次 chat/completions。永不抛异常，错误放在返回值里，便于 UI 直接显示。</summary>
    public async Task<ChatResult> ChatAsync(AppSettings cfg, string systemPrompt, string userPrompt,
                                            CancellationToken ct = default)
    {
        LastError = "";
        if (string.IsNullOrWhiteSpace(cfg.ApiBaseUrl))
            return Fail("未配置接口地址");

        var url = cfg.ApiBaseUrl.TrimEnd('/') + "/chat/completions";

        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(cfg.Model) ? "local" : cfg.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }
            },
            ["max_tokens"] = cfg.MaxTokens,
            ["temperature"] = cfg.Temperature
        };

        // 只有本地 llama-server 认这个字段；不下发以免云端服务 400。
        // 但仍给一个开关，因为有些自建网关也支持。
        if (cfg.DisableThinking)
            body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json")
            };
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());

            SetTimeout(cfg.TimeoutSeconds);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                                          .ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var msg = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                LastError = msg;
                Log.Write($"llm {msg} url={url} body={Truncate(raw, 600)}");
                return new ChatResult(false, "", "", raw, msg + "：" + Truncate(raw, 400));
            }

            return ParseResponse(raw);
        }
        catch (TaskCanceledException)
        {
            LastError = "请求超时";
            return new ChatResult(false, "", "", "", "请求超时（" + cfg.TimeoutSeconds + " 秒）。" +
                "本地模型首次请求要编译 SYCL/CUDA kernel，可能较慢，可调大超时。");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Write("llm request failed: " + ex);
            return new ChatResult(false, "", "", "", ex.Message);
        }
    }

    private static ChatResult ParseResponse(string raw)    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.GetArrayLength() == 0)
                return new ChatResult(false, "", "", raw, "响应里没有 choices");

            var msg = choices[0].GetProperty("message");
            var content = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "" : "";
            var reasoning = msg.TryGetProperty("reasoning_content", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? "" : "";

            // 思考模式可能把答案塞进 reasoning_content 而 content 为空
            if (content.Trim().Length == 0 && reasoning.Trim().Length > 0)
                return new ChatResult(true, reasoning.Trim(), reasoning, raw, "");

            if (content.Trim().Length == 0)
                return new ChatResult(false, "", reasoning, raw, "模型返回了空内容");

            return new ChatResult(true, content.Trim(), reasoning, raw, "");
        }
        catch (Exception ex)
        {
            return new ChatResult(false, "", "", raw, "响应解析失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 带工具的一轮对话。与 <see cref="ChatAsync"/> 的区别是：它会把 `tools`
    /// 下发给服务端，并把返回里的 `tool_calls` 解析出来交给调用方执行。
    ///
    /// 两个实测得出的要点：
    ///
    /// 1. **绝不能设 `max_tokens` 太小**。Qwen3 在思考模式下会先写一大段
    ///    reasoning 再决定调不调工具；预算不够时第一轮就把 token 烧光，
    ///    响应变成 `finish_reason: "length"` + 空 content + 没有 tool_calls，
    ///    表现为"模型不听话"，其实是预算不够。所以这里给一个宽松的默认值。
    /// 2. **`tool_call_id` 必须原样回灌**。小模型靠它对上号，对不上会反复
    ///    调用同一个工具直到预算耗尽——表现是死循环而不是报错。
    /// </summary>
    public async Task<ToolTurn> ChatWithToolsAsync(
        AppSettings cfg, string systemPrompt, List<JsonObject> messages,
        string toolName, string toolDescription, int maxTokens,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cfg.ApiBaseUrl))
            return new ToolTurn(false, "", "", new(), "", "未配置接口地址");

        var url = cfg.ApiBaseUrl.TrimEnd('/') + "/chat/completions";

        var tools = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = toolName,
                    ["description"] = toolDescription,
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["query"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "The search query.",
                            }
                        },
                        ["required"] = new JsonArray { "query" },
                    },
                },
            }
        };

        var msgs = new JsonArray();
        msgs.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        foreach (var m in messages) msgs.Add(m.DeepClone());

        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(cfg.Model) ? "local" : cfg.Model,
            ["messages"] = msgs,
            ["tools"] = tools,
            ["tool_choice"] = "auto",
            ["max_tokens"] = maxTokens,
            ["temperature"] = cfg.Temperature,
        };

        // **这一路刻意不下发 enable_thinking=false**，即使全局关着思考也一样。
        //
        // 实测（llama-server b11393 + Qwen3-8B）：关掉思考链后模型**不会**发出
        // tool_calls，只把标签原样吐回来——搜索工具等于失效。而全局那个开关是为
        // 散文翻译设的（那边思考纯属浪费 token），两件事的正确取值相反。
        // 所以这里按本次调用的需要决定，不跟着全局走：工具循环总是保留思考。
        //
        // （ChatAsync 那条路仍然照旧下发关闭标记，行为不变。）

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json")
            };
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());

            SetTimeout(cfg.TimeoutSeconds);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                                          .ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var msg = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                Log.Write($"llm(tools) {msg} body={Truncate(raw, 600)}");
                return new ToolTurn(false, "", "", new(), raw, msg + "：" + Truncate(raw, 400));
            }

            return ParseToolTurn(raw);
        }
        catch (TaskCanceledException)
        {
            return new ToolTurn(false, "", "", new(), "", "请求超时（" + cfg.TimeoutSeconds + " 秒）");
        }
        catch (Exception ex)
        {
            Log.Write("llm(tools) request failed: " + ex);
            return new ToolTurn(false, "", "", new(), "", ex.Message);
        }
    }

    private static ToolTurn ParseToolTurn(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.GetArrayLength() == 0)
                return new ToolTurn(false, "", "", new(), raw, "响应里没有 choices");

            var choice = choices[0];
            var finish = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
                ? fr.GetString() ?? "" : "";
            var msg = choice.GetProperty("message");

            var content = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "" : "";
            var reasoning = msg.TryGetProperty("reasoning_content", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? "" : "";

            var calls = new List<ToolCall>();
            if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    if (!tc.TryGetProperty("function", out var fn)) continue;
                    calls.Add(new ToolCall(
                        tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                            ? id.GetString() ?? "" : "",
                        fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                            ? nm.GetString() ?? "" : "",
                        fn.TryGetProperty("arguments", out var ar) && ar.ValueKind == JsonValueKind.String
                            ? ar.GetString() ?? "" : ""));
                }
            }

            // 预算被思考链烧光：既没有 tool_calls 也没有正文，还报 length。
            // 这跟"模型不想回答"表现一样，但成因完全不同，必须分开报。
            if (calls.Count == 0 && content.Trim().Length == 0 && finish == "length")
                return new ToolTurn(false, "", reasoning, calls, raw,
                    $"模型在思考阶段就用完了 token 预算（{finish}）。请调大 max_tokens，" +
                    "或改用更短的系统提示。");

            return new ToolTurn(true, content.Trim(), reasoning, calls, raw, "");
        }
        catch (Exception ex)
        {
            return new ToolTurn(false, "", "", new(), raw, "响应解析失败：" + ex.Message);
        }
    }

    /// <summary>从 tool_call 的 arguments 里取 query 字段。取不到时返回空串，由调用方决定怎么办。</summary>
    public static string QueryFromArguments(string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String)
                return q.GetString() ?? "";
        }
        catch { /* 参数不是合法 JSON：退回原文，总比丢掉这次调用好 */ }
        return "";
    }

    /// <summary>构造一条 assistant 消息（原样回灌 tool_calls，id 不能改）。</summary>
    public static JsonObject AssistantMessage(string content, List<ToolCall> calls)
    {
        var arr = new JsonArray();
        foreach (var c in calls)
            arr.Add(new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
            });

        return new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content,
            ["tool_calls"] = arr,
        };
    }

    /// <summary>构造一条 tool 结果消息。tool_call_id 必须与请求一致。</summary>
    public static JsonObject ToolMessage(string callId, string content) => new()
    {
        ["role"] = "tool",
        ["tool_call_id"] = callId,
        ["content"] = content,
    };

    /// <summary>把模型返回的一段文本切成关键词，识别中英文逗号、顿号、换行。</summary>
    public static List<string> SplitKeywords(string content) =>        content.Split(new[] { ',', '，', '、', '\n', '\r', '；', ';' },
                      StringSplitOptions.RemoveEmptyEntries)
               .Select(k => k.Trim().Trim('.', '。', '-', '·', '"', '\''))
               .Where(k => k.Length > 0)
               .ToList();

    /// <summary>
    /// 服务端列出的一个模型。
    ///
    /// 后三项来自 llama-server 的扩展字段（`data[].meta`），标准 OpenAI 服务不提供。
    /// 但选模型时恰好最需要它们：同一个模型的不同量化在列表里只是一串相似的名字，
    /// 只有 ftype 能把 IQ4_XS / Q4_K_M / Q6_K 区分开。
    /// </summary>
    public sealed record ModelInfo(
        string Id,
        string Quantization = "",
        int ContextLength = 0,
        long SizeBytes = 0,
        IReadOnlyList<string>? Aliases = null)
    {
        /// <summary>
        /// 界面上的一行说明。三项扩展信息都没有时返回空串，由调用方决定不显示。
        /// （标准 OpenAI 服务只会给 id，那时这一行本来就是空的。）
        /// </summary>
        public string Describe()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Quantization)) parts.Add(Quantization);
            if (ContextLength > 0) parts.Add("上下文 " + ContextLength);
            if (SizeBytes > 0) parts.Add(FormatSize(SizeBytes));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>把字节数写成人类可读的大小。</summary>
    public static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("F2") + " GB"
        : bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("F1") + " MB"
        : bytes + " B";

    /// <summary>
    /// 拉取服务端可用模型列表（GET /models）。
    ///
    /// 注意 **HTTP 成功但列表为空是合法结果**，不是故障：有些网关压根不实现
    /// /models（返回 `{"object":"list","data":[]}`）。这时应当照常让用户手填
    /// 模型名，而不是报"连接失败"——所以 Ok 与 Models.Count 是两件事。
    /// </summary>
    public async Task<(bool Ok, List<ModelInfo> Models, string Error)> ListModelsAsync(
        AppSettings cfg, CancellationToken ct = default)
    {
        LastError = "";
        if (string.IsNullOrWhiteSpace(cfg.ApiBaseUrl)) return (false, new(), "未配置接口地址");

        var url = cfg.ApiBaseUrl.TrimEnd('/') + "/models";
        // 列模型是个轻量请求，不该等模型加载那种长超时；但也不要短到本地服务
        // 首次编译 kernel 时直接判失败。
        var timeout = Math.Min(30, Math.Max(5, cfg.TimeoutSeconds));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());
            SetTimeout(timeout);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var msg = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                // 401/403 的正文里常带着"key 不对"这类具体原因，值得直接给用户看
                if (!string.IsNullOrWhiteSpace(raw)) msg += "：" + Truncate(raw, 300);
                LastError = msg;
                Log.Write($"models {msg} url={url}");
                return (false, new(), msg);
            }

            if (!TryParseModels(raw, out var models))
                Log.Write("models: 响应不是可识别的形状 " + Truncate(raw, 300));

            Log.Write($"models listed: {models.Count} url={url}");
            return (true, models, "");
        }
        catch (TaskCanceledException)
        {
            LastError = "请求超时";
            return (false, new(), $"请求超时（{timeout} 秒）");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Write("models request failed: " + ex);
            return (false, new(), ex.Message);
        }
    }

    /// <summary>
    /// 解析 /models 的响应。**纯函数，不碰网络**，所以能在测试工程里直接对着真实
    /// 响应断言（tests/Models.cs）。解析不出来时返回空列表。
    /// </summary>
    public static List<ModelInfo> ParseModels(string raw) =>
        TryParseModels(raw, out var models) ? models : new List<ModelInfo>();

    /// <summary>
    /// 兼容四种真实存在的形状：
    ///   1. 标准 OpenAI —— `{"data":[{"id":"..."}]}`
    ///   2. llama-server —— 同样有 data，另带 `aliases` / `meta{n_ctx,ftype,size}`
    ///   3. 只有 models —— `{"models":[{"name":"..."}]}`
    ///   4. 裸数组 —— `["a","b"]` 或 `[{"id":"a"}]`
    /// 返回值表示"是不是一个能认出来的列表形状"，与"列表里有没有东西"无关。
    /// </summary>
    public static bool TryParseModels(string raw, out List<ModelInfo> models)
    {
        models = new List<ModelInfo>();
        if (string.IsNullOrWhiteSpace(raw)) return false;

        JsonDocument doc;
        // 只吞解析异常：里面的走查若出错应当暴露出来，而不是被悄悄当成"空列表"
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException) { return false; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                AddModels(root, models);
                return true;
            }
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                AddModels(data, models);          // 标准字段优先
            else if (root.TryGetProperty("models", out var ms) && ms.ValueKind == JsonValueKind.Array)
                AddModels(ms, models);

            return true;
        }
    }

    private static void AddModels(JsonElement array, List<ModelInfo> sink)
    {
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) sink.Add(new ModelInfo(s.Trim()));
                continue;
            }
            if (el.ValueKind != JsonValueKind.Object) continue;

            var id = Str(el, "id") ?? Str(el, "name") ?? Str(el, "model");
            if (string.IsNullOrWhiteSpace(id)) continue;

            var hasMeta = el.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object;
            sink.Add(new ModelInfo(
                id.Trim(),
                hasMeta ? Str(meta, "ftype") ?? "" : "",
                hasMeta ? Int(meta, "n_ctx") : 0,
                hasMeta ? Long(meta, "size") : 0L,
                StrArray(el, "aliases")));
        }
        DedupeModels(sink);
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static long Long(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    private static List<string>? StrArray(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;

        var list = new List<string>();
        foreach (var e in v.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) continue;
            var s = e.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// 按 id 去重（忽略大小写，保留先出现的写法）并排序。
    ///
    /// 比较一律用 OrdinalIgnoreCase，不用默认的区域敏感比较：模型名是标识符，
    /// 土耳其语 I/ı 那类区域规则只会让同一个列表在不同语言的机器上排出不同顺序。
    /// </summary>
    private static void DedupeModels(List<ModelInfo> list)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keep = new List<ModelInfo>(list.Count);
        foreach (var m in list)
            if (seen.Add(m.Id)) keep.Add(m);

        keep.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        list.Clear();
        list.AddRange(keep);
    }

    private static ChatResult Fail(string m) { return new ChatResult(false, "", "", "", m); }

    /// <summary>
    /// 改超时，只在真的变化时才写回。
    /// HttpClient.Timeout 在首个请求发出后就不可修改（会抛），而同一个值重复写
    /// 一样会抛，所以必须先比再设。
    /// </summary>
    private void SetTimeout(int seconds)
    {
        var want = TimeSpan.FromSeconds(Math.Max(10, seconds));
        if (want == _currentTimeout) return;
        try
        {
            _http.Timeout = want;
            _currentTimeout = want;
        }
        catch (InvalidOperationException)
        {
            // 已经有请求在飞：保持原超时继续用，不要让整次调用失败
            Log.Write($"HttpClient.Timeout 已锁定（已有请求在途），忽略 {want.TotalSeconds:F0}s");
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public void Dispose() => _http.Dispose();
}
