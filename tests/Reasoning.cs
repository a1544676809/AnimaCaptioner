using AnimaCaptioner.Core;

namespace CoreCheck;

/// <summary>
/// 「思考内容不许进译文」的回归。
///
/// 这条规则来自一个真实缺陷：`ParseResponse` 里原有一条兜底——"content 为空
/// 就把 reasoning 当结果返回"，注释写着"思考模式可能把答案塞进 reasoning_content"。
/// **那个判断是错的**：实测（远程 llama-server + Qwen3-8B，`--reasoning-format
/// deepseek`）开着思考时答案稳稳落在 `content`，`reasoning_content` 只有思考。
/// 当时看到 content 为空，真实原因是 `max_tokens` 太小——模型还在思考就被截断
/// （`finish_reason=length`）。
///
/// 后果很重：开思考 + 预算不足时，700 多字中文思考过程会被当成译文写进 caption
/// （"好的，用户让我翻译……"），而界面上看不出任何异常。
///
/// 第 1、2 节用的是**实测到的真实字段形状**，不是编造的样例。
/// 纯函数、不联网，clone 下来就能跑；给了数据集时会额外扫一遍真实 caption，
/// 确认剥标记的逻辑不会误删正常文本。
/// </summary>
public static class Reasoning
{
    /// <summary>
    /// 拼出尖括号标记。
    ///
    /// **必须拼，不能内联写死**：源码里出现完整的尖括号标记时，编辑工具链会把
    /// 它当成标记吃掉（实测把 `""` 变成了 `""`，于是源码里
    /// `IndexOf("")` 恒返回 0，`StripThinking` 把整段文本都截成了空串——
    /// 正是第 4、5 节这两条假阳性守卫抓到的）。
    /// </summary>
    private static string Tag(string name) => "<" + name + ">";

    /// <summary>实测：短句开思考时的答案（content，68 字符）。</summary>
    private const string ShortAnswer =
        "The character's cat tail has horizontal stripes of orange and brown.";

    /// <summary>实测：同一请求的 reasoning_content 开头（原样摘录）。</summary>
    private const string ReasoningHead =
        "好的，用户让我翻译关于角色猫尾的描述。首先，我需要仔细阅读用户的中文句子：" +
        "“该角色的猫尾具有橙棕相间的横条纹”。根据规则，我只能翻译用户提供的文字，" +
        "不能添加任何额外信息，比如颜色、材质等。";

    /// <summary>实测：同一请求的 reasoning_content 结尾（原样摘录）。</summary>
    private const string ReasoningTail = "然后，注意用户要求将角色名称放在前面，";

    /// <summary>实测：预算 150 被截断时，reasoning 写到一半的结尾（原样摘录）。</summary>
    private const string TruncatedTail =
        "的“横”指的是水平方向，所以翻译成“horizontal”是正确的。同时，用户提到“橙棕相间”，" +
        "也就是橙色和棕色交替出现，需要准确翻译为“orange and brown alternating”。";

    /// <summary>拼出实测长度的思考串（中间用实测片段接续，长度对齐真实观测）。</summary>
    private static string LongReasoning() =>
        ReasoningHead + "\n\n" + TruncatedTail + "\n\n" + ReasoningTail;

    public static int Run(string? dataset)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var bad = 0;

        void Eq<T>(string label, T actual, T expected)
        {
            var ok = EqualityComparer<T>.Default.Equals(actual, expected);
            if (!ok) bad++;
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {label,-44} {actual}" +
                              (ok ? "" : $"   (期望 {expected})"));
        }

        void True(string label, bool cond, string detail = "")
        {
            if (!cond) bad++;
            Console.WriteLine($"  {(cond ? "ok  " : "FAIL")} {label,-44} {detail}");
        }

        var redacted = Tag("redacted_thinking");
        var redactedClose = "</" + "redacted_thinking" + ">";

        // ---------- 1. 真实形状：思考与答案分开 ----------
        Console.WriteLine("=== 1. 正常响应：答案在 content，思考在 reasoning_content ===");
        var normal = LlmClient.InterpretChoice(ShortAnswer, LongReasoning(), "stop", "{}");
        Eq("Ok", normal.Ok, true);
        Eq("Content 就是答案本身", normal.Content, ShortAnswer);
        True("Content 里没有中文思考", !ContainsCjk(normal.Content), normal.Content);
        True("思考仍单独保留（只供排查，谁都不能当译文用）",
             normal.Reasoning.Length > 0, $"{normal.Reasoning.Length} 字符");

        // ---------- 2. 真实形状：思考烧光预算（核心回归） ----------
        Console.WriteLine("\n=== 2. 截断：只有思考、没有答案（实测 content 为空 + length） ===");
        var cut = LlmClient.InterpretChoice("", LongReasoning(), "length", "{}");
        Eq("Ok 必须为 false", cut.Ok, false);
        // 这条是整份测试的核心：早先这里会把整段中文思考当成译文返回
        Eq("Content 必须为空（绝不退回 reasoning）", cut.Content, "");
        True("错误信息点明是预算问题", cut.Error.Contains("预算"), cut.Error);
        True("错误信息没有把思考正文倒出来",
             !cut.Error.Contains("用户让我翻译"), cut.Error.Length + " 字符");

        // 同一形状但 finish_reason=stop（思考正常结束、答案却没写）——同样不许退回
        var onlyThink = LlmClient.InterpretChoice("", LongReasoning(), "stop", "{}");
        Eq("只有思考（stop）也不许当译文", onlyThink.Content, "");
        Eq("Ok 为 false", onlyThink.Ok, false);

        // content 空 + 完全没有思考：如实报"空内容"，不要编原因
        var empty = LlmClient.InterpretChoice("", "", "stop", "{}");
        Eq("空内容 Ok", empty.Ok, false);
        Eq("空内容报错", empty.Error, "模型返回了空内容");

        // 截断但连思考都没有：要提到 finish_reason，便于定位
        var cutNoThink = LlmClient.InterpretChoice("", "", "length", "{}");
        True("截断且无内容时提到 length", cutNoThink.Error.Contains("length"),
             cutNoThink.Error);

        // ---------- 3. 内联思考标记的剥离 ----------
        Console.WriteLine("\n=== 3. 内联思考标记（换服务端 / 云端时的兜底） ===");
        Eq("成对 redacted_thinking 块被剥掉",
           LlmClient.StripThinking(redacted + "思考中……" + redactedClose + ShortAnswer),
           ShortAnswer);
        Eq("未闭合 redacted_thinking 从标记处删到结尾（标记前的正文保留）",
           LlmClient.StripThinking(ShortAnswer + redacted + "思考中……"),
           ShortAnswer);
        Eq("成对 thinking 标记被剥掉",
           LlmClient.StripThinking(Tag("thinking") + "hmm</thinking>" + ShortAnswer),
           ShortAnswer);
        Eq("成对 thought 标记被剥掉",
           LlmClient.StripThinking(Tag("thought") + "hmm</thought>" + ShortAnswer),
           ShortAnswer);
        Eq("未闭合 reasoning 标记删到结尾",
           LlmClient.StripThinking(ShortAnswer + Tag("reasoning") + "hmm"),
           ShortAnswer);
        Eq("残留的闭合标记被清掉",
           LlmClient.StripThinking("</thinking>" + ShortAnswer),
           ShortAnswer);
        Eq("空串", LlmClient.StripThinking(""), "");
        Eq("只有空白", LlmClient.StripThinking("   "), "");

        // ---------- 4. 假阳性守卫：正常英文不许被改动 ----------
        Console.WriteLine("\n=== 4. 假阳性守卫：正常文本必须原样返回 ===");
        // 刻意放进会撞上正则的字符：尖括号、冒号、括号、下划线、引号，
        // 以及 thinking 这个词本身（只有带尖括号的标记才该被剥）。
        string[] corpus =
        {
            ShortAnswer,
            "2b (nier:automata) standing in the rain, upper body",
            "masterpiece, best quality, score_7, safe. An anime girl holding a sign.",
            "The sign in her hands reads \"ANIMA\".",
            "She is thinking about the answer, " + Tag("not a tag") + ".",
            "honkai: star rail, 1girl, solo",
            "a " + Tag(" b and c ") + " d",
            "hair ornament shaped like the number 3",
            "worst quality, low quality, score_1, score_2, score_3, blurry",
        };
        var changed = 0;
        foreach (var s in corpus)
        {
            var got = LlmClient.StripThinking(s);
            if (got != s)
            {
                changed++;
                Console.WriteLine($"      被改动: {s}");
                Console.WriteLine($"      变成:   {got}");
            }
        }
        Eq("语料里被改动的条数", changed, 0);

        // ---------- 5. 真实 caption 上不许误删（可选，给了数据集才跑） ----------
        Console.WriteLine("\n=== 5. 真实 caption 上的假阳性扫描 ===");
        if (string.IsNullOrWhiteSpace(dataset) || !Directory.Exists(dataset))
        {
            Console.WriteLine("  跳过：未提供数据集（AC_DATASET 或第 1 个参数）");
        }
        else
        {
            var files = Directory.EnumerateFiles(dataset, "*.txt").ToList();
            var hits = 0;
            foreach (var f in files)
            {
                var text = File.ReadAllText(f);
                // caption 文件以换行收尾，而 StripThinking 会 Trim——那是预期的。
                // 所以比的是"除了首尾空白，有没有内容被删掉"。
                if (LlmClient.StripThinking(text) != text.Trim()) hits++;
            }
            Eq($"扫过 {files.Count} 个 caption，被改动的个数", hits, 0);
        }

        Console.WriteLine(bad == 0
            ? "\nreasoning: 全部通过"
            : $"\nreasoning: {bad} 项失败");
        return bad == 0 ? 0 : 1;
    }

    private static bool ContainsCjk(string s) =>
        s.Any(c => c >= 0x4E00 && c <= 0x9FFF);
}
