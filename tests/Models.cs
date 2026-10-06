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

        Console.WriteLine($"\n=== models: {(bad == 0 ? "OK" : bad + " FAILED")} ===");
        return bad == 0 ? 0 : 1;
    }
}
