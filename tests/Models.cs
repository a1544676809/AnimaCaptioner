using AnimaCaptioner.Core;

namespace CoreCheck;

/// <summary>
/// /models 响应解析的回归。
///
/// 最有价值的一项是第 1 节：它对着**远程服务器真实返回的字节**断言，不是编造的
/// 样例。llama-server 的响应比标准 OpenAI 多两个数组（顶层 `models` 和
/// `data[].meta`），解析器必须挑对那个、且不能把两个数组都吃进来变成重复条目。
///
/// 纯函数、不联网，所以 clone 下来就能跑（不需要数据集或词库）。
/// </summary>
public static class Models
{
    /// <summary>
    /// 远程 192.168.1.4:8080 上 llama-server 的原样响应（Qwen3-8B Q4_K_M）。
    ///
    /// 三个容易踩的点都在这份数据里：
    /// - 顶层同时有 `models`（llama-server 自己的）和 `data`（OpenAI 的），名字相同；
    /// - `data[].meta.ftype` 是量化级别，而顶层 `models[].details` 里那个
    ///   `quantization_level` 是**空串**——挑错了就什么也看不到；
    /// - `aliases` 与 `id` 相同，若把别名也当独立模型就会多出一条。
    /// </summary>
    private const string RemoteLlamaServer = """
        {"models":[{"name":"qwen3-8b","model":"qwen3-8b","modified_at":"","size":"","digest":"","type":"model","description":"","tags":[""],"capabilities":["completion"],"parameters":"","details":{"parent_model":"","format":"gguf","family":"","families":[""],"parameter_size":"","quantization_level":""}}],"object":"list","data":[{"id":"qwen3-8b","aliases":["qwen3-8b"],"tags":[],"object":"model","created":1791285731,"owned_by":"llamacpp","meta":{"vocab_type":2,"n_vocab":151936,"n_ctx":40960,"n_ctx_train":40960,"n_embd":4096,"n_params":8190735360,"size":5021827072,"ftype":"Q4_K - Medium"}}]}
        """;

    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var bad = 0;

        void Eq<T>(string label, T actual, T expected)
        {
            var ok = EqualityComparer<T>.Default.Equals(actual, expected);
            if (!ok) bad++;
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {label,-42} {actual}" +
                              (ok ? "" : $"   (期望 {expected})"));
        }

        // ---------- 1. 远程真实响应 ----------
        Console.WriteLine("=== 1. 远程 llama-server 的真实响应（原样字节） ===");
        var remote = LlmClient.ParseModels(RemoteLlamaServer);
        Eq("模型个数（两个数组不能重复计入）", remote.Count, 1);
        if (remote.Count == 1)
        {
            var m = remote[0];
            Eq("id", m.Id, "qwen3-8b");
            Eq("量化级别（取自 meta.ftype）", m.Quantization, "Q4_K - Medium");
            Eq("上下文（取自 meta.n_ctx）", m.ContextLength, 40960);
            Eq("体积（取自 meta.size）", m.SizeBytes, 5021827072L);
            Eq("别名原样保留", string.Join(",", m.Aliases ?? new List<string>()), "qwen3-8b");
            Eq("Describe()", m.Describe(), "Q4_K - Medium · 上下文 40960 · 4.68 GB");
        }

        // ---------- 2. 标准 OpenAI 形状：去重 + 排序 ----------
        Console.WriteLine("\n=== 2. 标准 OpenAI 形状：排序与去重 ===");
        var std = LlmClient.ParseModels("""
            {"object":"list","data":[
              {"id":"zeta","object":"model"},
              {"id":"Alpha","object":"model"},
              {"id":"alpha","object":"model"},
              {"id":"  beta  ","object":"model"},
              {"object":"model"},
              {"id":"","object":"model"},
              {"id":"alpha","object":"model"}]}
            """);
        Eq("去重后个数", std.Count, 3);
        Eq("忽略大小写排序", string.Join("|", std.Select(x => x.Id)), "Alpha|beta|zeta");
        Eq("无扩展字段时 Describe 为空", std[0].Describe(), "");

        // ---------- 3. 只有 models[].name 的网关 ----------
        Console.WriteLine("\n=== 3. 只给 models[].name 的网关 ===");
        var only = LlmClient.ParseModels("""{"models":[{"name":"llama3"},{"model":"mixtral"}]}""");
        Eq("个数", only.Count, 2);
        Eq("name 与 model 都认", string.Join("|", only.Select(x => x.Id)), "llama3|mixtral");

        // ---------- 4. 裸数组 ----------
        Console.WriteLine("\n=== 4. 裸数组 ===");
        var bare = LlmClient.ParseModels("""["b","a"]""");
        Eq("字符串数组", string.Join("|", bare.Select(x => x.Id)), "a|b");
        var bareObj = LlmClient.ParseModels("""[{"id":"x"}]""");
        Eq("对象数组", bareObj.Count == 1 ? bareObj[0].Id : "(空)", "x");

        // ---------- 5. 畸形与边界：必须是"认不出来"，不是"空列表" ----------
        Console.WriteLine("\n=== 5. 畸形输入 ===");
        foreach (var (label, raw) in new[]
                 {
                     ("空串", ""),
                     ("只有空白", "   "),
                     ("不是 JSON", "Internal Server Error"),
                     ("被截断的 JSON", "{\"data\":[{\"id\":\"a\""),
                     ("根是数字", "42"),
                     ("根是 null", "null"),
                 })
        {
            var t = LlmClient.TryParseModels(raw, out var parsed);
            Eq($"{label} → 认不出来", t, false);
            Eq($"{label} → 列表为空", parsed.Count, 0);
        }

        // data 存在但不是数组：认得出形状，但里面没有模型
        var oddData = LlmClient.TryParseModels("""{"data":"nope"}""", out var oddList);
        Eq("data 不是数组 → 形状仍可识别", oddData, true);
        Eq("data 不是数组 → 0 个模型", oddList.Count, 0);

        // 合法的空列表：HTTP 成功但没模型，调用方据此提示而不是报故障
        var emptyList = LlmClient.ParseModels("""{"object":"list","data":[]}""");
        Eq("合法空列表 → 0 个模型", emptyList.Count, 0);

        // ---------- 6. 体积格式化 ----------
        Console.WriteLine("\n=== 6. FormatSize ===");
        Eq("1 GiB", LlmClient.FormatSize(1L << 30), "1.00 GB");
        Eq("4.68 GB", LlmClient.FormatSize(5021827072L), "4.68 GB");
        Eq("1 MiB", LlmClient.FormatSize(1L << 20), "1.0 MB");
        Eq("512 B", LlmClient.FormatSize(512L), "512 B");

        // ---------- 7. DeepSeek：能力是服务端报的，不是猜的 ----------
        Console.WriteLine("\n=== 7. DeepSeek 的真实响应（能力字段） ===");
        var ds = LlmClient.ParseModels(DeepSeek);
        Eq("个数", ds.Count, 2);
        if (ds.Count == 2)
        {
            var flash = ds[0];
            Eq("id", flash.Id, "deepseek-flash");
            Eq("显示名（name 与 id 不同才记）", flash.DisplayName, "DeepSeek-V4.1-Flash");
            Eq("上下文取自 context_window", flash.ContextLength, 1048576);
            Eq("输出上限", flash.MaxOutputTokens, 393216);
            Eq("能吃图（input_modalities 含 image）", flash.SupportsImage, (bool?)true);
            Eq("思考档位取自 effort.supported_levels",
               string.Join("/", flash.EffortLevels ?? new List<string>()), "low/high/max");
            Eq("默认档位", flash.DefaultEffort, "high");
            Eq("Describe()", flash.Describe(),
               "DeepSeek-V4.1-Flash · 上下文 1048576 · 输出上限 393216 · 支持图像输入 · 思考 low/high/max（默认 high）");

            var pro = ds[1];
            Eq("只报 text 的模型判为不能吃图", pro.SupportsImage, (bool?)false);
            Eq("Describe() 写明仅文本", pro.Describe(),
               "DeepSeek-V4-Pro · 上下文 1048576 · 输出上限 393216 · 仅文本输入 · 思考 low/high/max（默认 high）");
        }

        // ---------- 8. 「不知道」不等于「不支持」 ----------
        Console.WriteLine("\n=== 8. 老网关不报能力时必须是未知 ===");
        // llama-server 的 meta 里没有 input_modalities：这一条守着"别把未知当不支持"，
        // 一旦判成 false，本来能读图的配置会被界面拦下来。
        Eq("llama-server → 图像能力未知", remote[0].SupportsImage, (bool?)null);
        Eq("llama-server → 没有思考档位", remote[0].SupportsEffort, false);
        Eq("标准 OpenAI → 图像能力未知", std[0].SupportsImage, (bool?)null);
        Eq("Describe() 里不出现图像字样", std[0].Describe().Contains("图像"), false);

        // ---------- 9. 推理档位（真值表） ----------
        Console.WriteLine("\n=== 9. 档位词表 / IsThinkingOff / EffortToSend ===");
        // 词表来自服务端 422 的原话（见 tests/EffortProbe 那组实测）：supported_levels
        // 只是"有意义的那几档"，允许的取值范围是这八个。
        Eq("词表就是服务端列出的那八个",
           string.Join("/", LlmClient.EffortVocabulary),
           "none/minimal/low/medium/high/xhigh/ultra/max");

        Eq("空串不是关思考（自动=服务端默认，仍会思考）", LlmClient.IsThinkingOff(""), false);
        Eq("null 不是关思考", LlmClient.IsThinkingOff(null), false);
        Eq("none 是关思考", LlmClient.IsThinkingOff("none"), true);
        Eq("大小写与空白都算 none", LlmClient.IsThinkingOff("  NONE "), true);
        Eq("low 不是关思考", LlmClient.IsThinkingOff("low"), false);

        Eq("自动 + 支持 → 不发（跟随服务端默认）", LlmClient.EffortToSend("", true), null);
        Eq("自动 + 不支持 → 不发", LlmClient.EffortToSend("", false), null);
        Eq("none + 支持 → 发 none", LlmClient.EffortToSend("none", true), "none");
        // llama-server 没有这个参数，没声明就不发——它靠 enable_thinking=false 关思考
        Eq("none + 未声明 → 不发", LlmClient.EffortToSend("none", false), null);
        Eq("high + 支持 → 发 high", LlmClient.EffortToSend("high", true), "high");
        Eq("high + 未声明 → 不发", LlmClient.EffortToSend("high", false), null);
        Eq("规范化：'  HIGH ' → high", LlmClient.EffortToSend("  HIGH ", true), "high");
        // 实测：flash 的 supported_levels 只报 low/high/max，但 medium 会被 200 接受，
        // 所以这里**不**拿 supported_levels 当门槛（拿它当门槛会白白丢掉用户的选择）。
        Eq("medium（模型未声明）仍发", LlmClient.EffortToSend("medium", true), "medium");
        // 反过来，不在全局词表里的值会被 422（实测 bogus），所以挡住不打红请求。
        Eq("词表外的值 → 不发", LlmClient.EffortToSend("bogus", true), null);
        Eq("手改出的怪值也挡下", LlmClient.EffortToSend("high; rm -rf", true), null);
        // 八个都必须能发出去，否则界面上就会出现"选了却没反应"的档
        Eq("八个档位全部可下发",
           LlmClient.EffortVocabulary.All(l => LlmClient.EffortToSend(l, true) == l), true);

        // ---------- 10. 能不能带图（真值表） ----------
        Console.WriteLine("\n=== 10. CanAttachImage ===");
        Eq("能读图 + 有图 → 带", LlmClient.CanAttachImage(true, true), true);
        Eq("能读图 + 没图 → 不带", LlmClient.CanAttachImage(true, false), false);
        Eq("明确只吃文本 + 有图 → 拦下", LlmClient.CanAttachImage(false, true), false);
        Eq("明确只吃文本 + 没图 → 不带", LlmClient.CanAttachImage(false, false), false);
        // 这一条是重点：llama-server 不报 input_modalities，绝不能因此判成"不支持"。
        Eq("服务端没报 + 有图 → 放行", LlmClient.CanAttachImage(null, true), true);
        Eq("服务端没报 + 没图 → 不带", LlmClient.CanAttachImage(null, false), false);

        Console.WriteLine($"\n=== models: {(bad == 0 ? "OK" : bad + " FAILED")} ===");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>
    /// `GET https://api.deepseek.com/models` 的原样响应（2026-10 实测，逐字节）。
    ///
    /// 与 llama-server 那份的核心差别：**它在 `data[]` 里直接报了模型能力**
    /// （`input_modalities` / `context_window` / `max_output_tokens` / `effort`），
    /// 而这些**不是标准 OpenAI 的字段**——标准只保证 `id`。所以解析必须是
    /// "有就取、没有就留空"，不能假设任何一项存在。
    ///
    /// 两个模型的 `input_modalities` 恰好一个含 image 一个不含，正好用来钉住
    /// 三态里的 true 与 false。
    /// </summary>
    private const string DeepSeek = """
        {"object":"list","data":[{"id":"deepseek-flash","object":"model","owned_by":"deepseek","name":"DeepSeek-V4.1-Flash","context_window":1048576,"max_output_tokens":393216,"input_modalities":["text","image"],"output_modalities":["text"],"effort":{"supported_levels":["low","high","max"],"default_level":"high"},"api_capabilities":{"anthropic_messages":{"system_prompt_update":"in-history"}}},{"id":"deepseek-v4-pro","object":"model","owned_by":"deepseek","name":"DeepSeek-V4-Pro","context_window":1048576,"max_output_tokens":393216,"input_modalities":["text"],"output_modalities":["text"],"effort":{"supported_levels":["low","high","max"],"default_level":"high"},"api_capabilities":{"anthropic_messages":{"system_prompt_update":"leading-only"}}}]}
        """;
}
