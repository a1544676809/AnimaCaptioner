using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimaCaptioner.Core;

/// <summary>
/// OpenAI 流式响应（<c>stream: true</c> 的 SSE）的累积器。
///
/// **为什么单独抽一个类**：流式的难点全在"分片边界不保证落在语义边界上"，
/// 而这类错误只有拿合成的分片序列才测得出来——真发一次请求，分片怎么切是服务端
/// 说了算，覆盖不到边界情形。
///
/// 实测（拿 deepseek 官方接口打的）：
///
/// - <c>reasoning_content</c> 与 <c>content</c> 都是**逐增量**下发的，
///   前者是思考链，正是要实时显示的东西；
/// - <c>tool_calls</c> 是**碎片化**的：一次调用里名字可能只出现在第 1 片、
///   参数被切成十几片（实测名字 1 片、参数 10 片）。必须按 <c>index</c> 累加，
///   而且 <c>id</c> 也只在某一片里出现。
///
/// 另外顺手兼容一种不按牌理出牌的服务端：声明了 <c>stream</c> 却只回一个
/// 完整的非流式响应（<c>message</c> 而不是 <c>delta</c>）。不兼容的话那种网关
/// 上"开了流式就什么都没有"。
/// </summary>
public sealed class ToolStream
{
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();
    private readonly Dictionary<int, Partial> _partial = new();
    private readonly List<int> _order = new();

    private sealed class Partial
    {
        public string Id = "";
        public readonly StringBuilder Name = new();
        public readonly StringBuilder Args = new();
    }

    /// <summary>已累积的答案正文。</summary>
    public string Content => _content.ToString();

    /// <summary>已累积的思考链。**只用于显示**，绝不当成结果。</summary>
    public string Reasoning => _reasoning.ToString();

    /// <summary>服务端给的收尾原因（stop / tool_calls / length …）。</summary>
    public string FinishReason { get; private set; } = "";

    /// <summary>见到 <c>[DONE]</c> 或已经收到过完整响应。</summary>
    public bool Done { get; private set; }

    /// <summary>喂一行 SSE。非 data 行（event:、注释、空行）自动忽略。</summary>
    public void Feed(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var s = line.Trim();
        if (!s.StartsWith("data:", StringComparison.Ordinal)) return;

        var payload = s[5..].Trim();
        if (payload.Length == 0) return;
        if (payload == "[DONE]") { Done = true; return; }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(payload); }
        catch { return; }   // 坏分片跳过，不因为一片毁掉整轮
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return;

            var ch = choices[0];
            if (ch.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                FinishReason = fr.GetString() ?? "";

            // 非流式形状（有些网关声明了 stream 却照旧回完整响应）
            if (ch.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
            {
                AbsorbMessage(msg);
                Done = true;
                return;
            }

            if (!ch.TryGetProperty("delta", out var d) || d.ValueKind != JsonValueKind.Object) return;
            Append(d, "content", _content);
            Append(d, "reasoning_content", _reasoning);
            Append(d, "reasoning", _reasoning);   // 少数网关用这个名字

            if (d.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                foreach (var tc in tcs.EnumerateArray()) AbsorbCall(tc);
        }
    }

    private void AbsorbMessage(JsonElement msg)
    {
        if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            _content.Append(c.GetString());
        if (msg.TryGetProperty("reasoning_content", out var r) && r.ValueKind == JsonValueKind.String)
            _reasoning.Append(r.GetString());
        else if (msg.TryGetProperty("reasoning", out var r2) && r2.ValueKind == JsonValueKind.String)
            _reasoning.Append(r2.GetString());
        if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            foreach (var tc in tcs.EnumerateArray()) AbsorbCall(tc);
    }

    private static void Append(JsonElement obj, string name, StringBuilder sb)
    {
        if (obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var piece = v.GetString();
            if (!string.IsNullOrEmpty(piece)) sb.Append(piece);
        }
    }

    /// <summary>
    /// 收一片 tool_call。**按 index 归并**：同一个调用的名字/参数会分好几片到，
    /// 直接 append 成新调用会把一次调用拆成十几个。
    /// </summary>
    private void AbsorbCall(JsonElement tc)
    {
        if (tc.ValueKind != JsonValueKind.Object) return;
        var idx = tc.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number
            ? ix.GetInt32()
            : 0;

        if (!_partial.TryGetValue(idx, out var p))
        {
            p = new Partial();
            _partial[idx] = p;
            _order.Add(idx);
        }

        if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            var v = id.GetString() ?? "";
            // id 只在某一片出现；不要用后面的空值覆盖掉前面拿到的
            if (v.Length > 0) p.Id = v;
        }

        if (!tc.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object) return;
        if (fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
            p.Name.Append(nm.GetString());
        if (fn.TryGetProperty("arguments", out var ar) && ar.ValueKind == JsonValueKind.String)
            p.Args.Append(ar.GetString());
    }

    /// <summary>拼好的工具调用，按首次出现的 index 顺序。</summary>
    public List<LlmClient.ToolCall> Calls()
    {
        var list = new List<LlmClient.ToolCall>(_order.Count);
        foreach (var i in _order)
        {
            var p = _partial[i];
            list.Add(new LlmClient.ToolCall(p.Id, p.Name.ToString(), p.Args.ToString()));
        }
        return list;
    }

    /// <summary>
    /// 还原成一个**非流式形状**的响应 JSON。
    ///
    /// 这样做的理由是复用：解析与判定（没有工具调用时怎么判"只有思考没答案"、
    /// 怎么剥内联思考标记）全部交给已经测过的
    /// <see cref="LlmClient.ParseToolTurn"/>，流式这条路不另写一份——两份判定
    /// 迟早会漂移，而漂移的表现是"同一次回答，流式开关不同结论不同"。
    /// </summary>
    public string ToResponseJson()
    {
        var msg = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = Content,
        };
        if (Reasoning.Length > 0) msg["reasoning_content"] = Reasoning;

        var calls = Calls();
        if (calls.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var c in calls)
                arr.Add(new JsonObject
                {
                    ["id"] = c.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = c.Name,
                        ["arguments"] = c.Arguments,
                    },
                });
            msg["tool_calls"] = arr;
        }

        var root = new JsonObject
        {
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["message"] = msg,
                    ["finish_reason"] = FinishReason.Length > 0 ? FinishReason : "stop",
                }
            }
        };
        return root.ToJsonString();
    }
}
