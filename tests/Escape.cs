using System.Globalization;
using System.Text;
using AnimaCaptioner.Core;
using Microsoft.Data.Sqlite;

namespace CoreCheck;

/// <summary>
/// 标签转义（<see cref="TagEscape"/>）的回归。
///
/// <para>关键在于它**移植了 ComfyUI 的解析器**（<c>comfy/sd1_clip.py</c> 的
/// <c>parse_parentheses</c> / <c>token_weights</c> / <c>escape_important</c> /
/// <c>unescape_important</c>）当参考实现。只断言 <c>Escape</c>/<c>Unescape</c>
/// 互为逆是不够的——那只能证明两个函数自洽，证明不了它们对得上 ComfyUI 的真实行为。</para>
///
/// <para>这些函数在这里是**逐行照抄**的移植，不是"意思差不多"的重写：
/// <c>token_weights</c> 里 <c>float()</c> 失败时保留 <c>weight *= 1.1</c> 且不截断
/// 文本这一条细节，正是 <c>2b (nier:automata)</c> 能被正确还原的原因。</para>
/// </summary>
public static class Escape
{
    // ===================== ComfyUI 解析器（参考实现） =====================

    private static List<string> ParseParentheses(string s)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var nesting = 0;
        foreach (var ch in s)
        {
            if (ch == '(')
            {
                if (nesting == 0)
                {
                    if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                    current.Append('(');
                }
                else current.Append(ch);
                nesting++;
            }
            else if (ch == ')')
            {
                nesting--;
                if (nesting == 0) { current.Append(')'); result.Add(current.ToString()); current.Clear(); }
                else current.Append(ch);
            }
            else current.Append(ch);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static List<(string Text, double Weight)> TokenWeights(string s, double currentWeight)
    {
        var outList = new List<(string, double)>();
        foreach (var piece in ParseParentheses(s))
        {
            var x = piece;
            var weight = currentWeight;
            if (x.Length >= 2 && x[^1] == ')' && x[0] == '(')
            {
                x = x[1..^1];
                var colon = x.LastIndexOf(':');
                weight *= 1.1;
                if (colon > 0
                    && double.TryParse(x[(colon + 1)..], NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out var w))
                {
                    weight = w;
                    x = x[..colon];
                }
                outList.AddRange(TokenWeights(x, weight));
            }
            else outList.Add((x, currentWeight));
        }
        return outList;
    }

    // ComfyUI 用 "\0\1" / "\0\2" 当哨兵；C# 里 \1 不是合法转义，改用等价的单字符哨兵
    // （控制字符不可能出现在提示词里，所以两者行为一致）。
    private static string EscapeImportant(string t) =>
        t.Replace("\\)", "\u0001").Replace("\\(", "\u0002");

    private static string UnescapeImportant(string t) =>
        t.Replace("\u0001", ")").Replace("\u0002", "(");

    /// <summary>编码器实际收到的正文，以及每段的权重（权重只有 T5 分支会用）。</summary>
    private static (string Text, List<(string Text, double Weight)> Segs) ComfySees(string prompt)
    {
        var segs = TokenWeights(EscapeImportant(prompt), 1.0);
        var sb = new StringBuilder();
        foreach (var (t, _) in segs) sb.Append(UnescapeImportant(t));
        return (sb.ToString(), segs);
    }

    // ===================== 测试 =====================

    public static int Run(string? dbPath, string? seedPath)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var fail = 0;

        void Check(string label, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!ok) fail++;
        }

        VocabDb? vocab = null;
        if (dbPath is not null && File.Exists(dbPath))
        {
            vocab = new VocabDb();
            try { vocab.Load(dbPath, seedPath ?? ""); }
            catch (Exception ex) { Console.WriteLine("vocab load failed: " + ex.Message); return 2; }
        }

        // 覆盖各种边界形状：多层括号、字面反斜杠、方括号、@ 画师、括号外冒号、
        // 真实词库里的灾难形状（:1999 被当权重）。
        var corpus = new[]
        {
            "", "1girl", "solo, smile", "a(b)c", "((x))", "(a)(b)", "(chibi:2)",
            "\\(^o^)/", "\\\\n/", "\\m/", "\\n/", "\\||/", "double \\m/",
            "00 qan[t]", "@big chungus", "009 re:cyborg", "girls' frontline", ";d",
            "210 (reverse:1999)", "\"snow on the hearth\" (genshin impact)",
            "37 (down in the grotto) (reverse:1999)", "(a (b) c)", "k-on!", "<o> <o>",
        };

        Console.WriteLine("=== 1. Escape / Unescape 互为逆 ===");
        var notRoundTrip = corpus.Where(s => TagEscape.Unescape(TagEscape.Escape(s)) != s).ToList();
        Check($"{corpus.Length} 个样本往返恒等", notRoundTrip.Count == 0,
              notRoundTrip.Count == 0 ? "" : $"首个不等: '{notRoundTrip[0]}'");

        // 单趟扫描而不是两次 Replace —— 后者在 `\\(` 这种"字面反斜杠 + 转义括号"的
        // 串上顺序敏感，会多吞一个反斜杠。`\(^o^)/` 正好是这个形状。
        Check("字面反斜杠 + 括号（\\(^o^)/ 形状）不失真",
              TagEscape.Unescape(TagEscape.Escape("\\(^o^)/")) == "\\(^o^)/",
              $"'{TagEscape.Escape("\\(^o^)/")}' -> '{TagEscape.Unescape(TagEscape.Escape("\\(^o^)/"))}'");

        Console.WriteLine();
        Console.WriteLine("=== 2. 只插入反斜杠，绝不改动别的字符 ===");
        var altered = corpus.Where(s =>
            TagEscape.Escape(s).Replace("\\", "") != s.Replace("\\", "")).ToList();
        Check("除反斜杠外字符完全不变", altered.Count == 0,
              altered.Count == 0 ? "" : $"首个: '{altered[0]}'");

        // 明确钉住"不该转义的符号一个都不碰"——这是最容易照 A1111 习惯做错的地方：
        // ComfyUI 只解析圆括号，方括号/花括号是字面量。
        Check("方括号是字面量，不转义", TagEscape.Escape("00 qan[t]") == "00 qan[t]");
        Check("花括号不转义", TagEscape.Escape("{x}") == "{x}");
        Check("@ 是画师前缀，不转义", TagEscape.Escape("@big chungus") == "@big chungus");
        Check("括号外的冒号不转义", TagEscape.Escape("009 re:cyborg") == "009 re:cyborg");
        Check("撇号 / 分号 / 感叹号不转义",
              TagEscape.Escape("girls' frontline; k-on!") == "girls' frontline; k-on!");
        Check("无括号时 NeedsEscaping 为假",
              !TagEscape.NeedsEscaping("1girl, solo") && TagEscape.NeedsEscaping("a(b)"));

        Console.WriteLine();
        Console.WriteLine("=== 3. 与 ComfyUI 解析器对拍：转义后必须原样送达 ===");
        var parenTags = corpus.Where(TagEscape.NeedsEscaping).ToList();

        var mangled = parenTags.Where(t => ComfySees(t).Text != t).ToList();
        Check($"未转义时确实被改写（{mangled.Count}/{parenTags.Count}）", mangled.Count > 0,
              mangled.Count > 0 ? $"例: '{mangled[0]}' -> '{ComfySees(mangled[0]).Text}'" : "");

        var notRestored = parenTags.Where(t => ComfySees(TagEscape.Escape(t)).Text != t).ToList();
        Check($"转义后全部精确还原（{parenTags.Count} 个）", notRestored.Count == 0,
              notRestored.Count == 0 ? "" :
              $"首个: '{notRestored[0]}' -> '{ComfySees(TagEscape.Escape(notRestored[0])).Text}'");

        // 权重也要回到 1.0：转义顺带把误加的注意力权重一起去掉
        var stray = parenTags
            .Where(t => ComfySees(TagEscape.Escape(t)).Segs.Any(s => Math.Abs(s.Weight - 1.0) > 1e-9))
            .ToList();
        Check("转义后不残留任何误加的权重", stray.Count == 0,
              stray.Count == 0 ? "" : $"首个: '{stray[0]}'");

        // 最严重的那一类：:1999 被当成权重，年份整个从提示词里消失
        var (eatenSeen, eatenSegs) = ComfySees("210 (reverse:1999)");
        Check("未转义时年份被当权重吃掉（这就是必须修的那 100 多个）",
              eatenSeen == "210 reverse" && eatenSegs.Any(s => Math.Abs(s.Weight - 1999.0) < 1e-9),
              $"sees='{eatenSeen}' weights=[{string.Join(",", eatenSegs.Select(s => s.Weight))}]");
        Check("转义后年份还在",
              ComfySees(TagEscape.Escape("210 (reverse:1999)")).Text == "210 (reverse:1999)");

        Console.WriteLine();
        Console.WriteLine("=== 4. 判据是词库认不认，不是长得像不像权重 ===");
        // 用假词库把判据单独钉住（真实词库在第 5 节）。这两条方向相反，缺一不可：
        //   `210 (reverse:1999)` 形状像权重但词库认得 -> 必须转义
        //   `(chibi:2)`           形状像权重且词库不认 -> 必须保留
        bool FakeKnown(string t) => t is "210 (reverse:1999)" or "2b (nier:automata)";
        Check("词库认得的括号标签 -> 转义",
              TagEscape.ShouldEscape("210 (reverse:1999)", FakeKnown));
        Check("手写权重 (chibi:2) -> 保留（转义会毁掉权重）",
              !TagEscape.ShouldEscape("(chibi:2)", FakeKnown));
        Check("手写注意力 (chibi) girl -> 保留",
              !TagEscape.ShouldEscape("(chibi) girl", FakeKnown));
        Check("无括号 -> 一律不动", !TagEscape.ShouldEscape("1girl", FakeKnown));
        Check("IsWeightExpression 认得 (chibi:2) / (x:1.5)",
              TagEscape.IsWeightExpression("(chibi:2)") && TagEscape.IsWeightExpression("(x:1.5)"));
        Check("IsWeightExpression 不认 210 (reverse:1999)（不是纯权重）",
              !TagEscape.IsWeightExpression("210 (reverse:1999)"));
        Check("IsWeightExpression 不认 (a (b) c)",
              !TagEscape.IsWeightExpression("(a (b) c)"));

        var mixed = new List<string> { "210 (reverse:1999)", "(chibi:2)", "1girl", "@big chungus" };
        var escapedMixed = TagEscape.EscapeAll(mixed, FakeKnown);
        Check("EscapeAll 只动该动的那个",
              escapedMixed[0] == "210 \\(reverse:1999\\)" && escapedMixed[1] == "(chibi:2)"
              && escapedMixed[2] == "1girl" && escapedMixed[3] == "@big chungus",
              string.Join(" | ", escapedMixed));
        Check("CountEscaped 与 EscapeAll 一致",
              TagEscape.CountEscaped(mixed, FakeKnown) == 1,
              $"count={TagEscape.CountEscaped(mixed, FakeKnown)}");
        Check("EscapeAll 不改原列表", mixed[0] == "210 (reverse:1999)");

        // 幂等性是"复制时再规范化一遍"这条路的前提（见 MainWindow.NormalizeEscapes）：
        // 已经转义过的标签带着反斜杠、词库查不到，所以不会再补一层。
        // 不幂等的话，第二次复制会得到 `\\\(`，ComfyUI 那边看到的就是字面反斜杠。
        var once = TagEscape.EscapeAll(new List<string> { "210 (reverse:1999)" }, FakeKnown);
        var twice = TagEscape.EscapeAll(once, FakeKnown);
        Check("EscapeAll 幂等（复制时再跑一遍不会加第二层反斜杠）",
              once[0] == twice[0] && twice[0] == "210 \\(reverse:1999\\)",
              $"once='{once[0]}' twice='{twice[0]}'");

        Console.WriteLine();
        Console.WriteLine("=== 5. 配置默认值与标题派生 ===");
        var cfg = new AppSettings();
        Check("转义开关默认开", cfg.EscapeTagParens);

        // 标题是给人看的。库里存的是线上格式（复制走的那份），派生标题时必须还原，
        // 否则左栏那条提示词的名字会挂着反斜杠。
        var titleRaw = PromptLibraryStore.DeriveTitle("210 (reverse:1999), 1girl, solo");
        var titleEsc = PromptLibraryStore.DeriveTitle("210 \\(reverse:1999\\), 1girl, solo");
        Check("派生标题不挂反斜杠", !titleEsc.Contains('\\'), $"'{titleEsc}'");
        Check("转义与否派生出同一个标题", titleRaw == titleEsc,
              $"'{titleRaw}' vs '{titleEsc}'");

        if (vocab is null)
        {
            Console.WriteLine();
            Console.WriteLine("=== 6-7. 真实词库与解析层：跳过（未提供词库）===");
            Console.WriteLine();
            Console.WriteLine(fail == 0 ? "=== escape: 全部通过 ===" : $"=== escape: {fail} 项失败 ===");
            return fail == 0 ? 0 : 1;
        }

        Console.WriteLine();
        Console.WriteLine("=== 6. 真实词库：Anima 自带索引里的全部括号标签 ===");
        var names = new List<string>();
        using (var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "select name from anima_index where name like '%(%'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
        }
        Console.WriteLine($"  索引里含 ( 的标签: {names.Count}");

        // 全量跑，不抽样——抽样会漏掉边界形状（多层括号、括号内含冒号数字等）
        var stillBad = new List<string>();
        var weightEaten = 0;
        foreach (var t in names)
        {
            var fixedUp = ComfySees(TagEscape.Escape(t)).Text;
            if (fixedUp != t && stillBad.Count < 5) stillBad.Add($"{t} -> {fixedUp}");
            if (ComfySees(t).Segs.Any(s =>
                    Math.Abs(s.Weight - 1.0) > 1e-9 && Math.Abs(s.Weight - 1.1) > 1e-9))
                weightEaten++;
        }
        Check($"全部 {names.Count} 个转义后精确还原", stillBad.Count == 0,
              stillBad.Count == 0 ? "" : string.Join(" | ", stillBad));
        Check($"其中 {weightEaten} 个原本会被当成权重吃掉", weightEaten > 0, $"count={weightEaten}");

        var unknown = names.Where(t => !TagSections.IsKnownTag(t, vocab)).ToList();
        Check("全部被词库认出来（否则开关根本不会生效）", unknown.Count == 0,
              unknown.Count == 0 ? "" : $"首个未知: '{unknown[0]}'");

        // 大写开头 + ≥3 词的括号标签：ProseStart 第 3 条（句子形状）会把它们判成散文，
        // 整行从标签列表里消失。实测 Anima 索引里 **0 个**，所以这条不可达——
        // 打印出来是为了让"以后真出现了"这件事在测试输出里看得见。
        var upper = names.Where(t => char.IsUpper(t[0]) && t.Count(c => c == ' ') >= 2).ToList();
        Console.WriteLine($"  （大写开头 + ≥3 词的括号标签: {upper.Count} 个，实测 0 —— 见第 7 节的机制测试）");

        Console.WriteLine();
        Console.WriteLine("=== 7. 解析层：转义必须对 Parse / 着色 / 状态栏透明 ===");
        var lit = "210 (reverse:1999)";
        var esc = TagEscape.Escape(lit);

        var withFlag = PromptText.Parse(esc, vocab, unescapeTags: true);
        Check("回读时还原成字面量", withFlag.Tags.Count == 1 && withFlag.Tags[0] == lit,
              string.Join(" | ", withFlag.Tags));

        var noFlag = PromptText.Parse(esc, vocab, unescapeTags: false);
        Check("不传 flag 就漏（证明这一层是必需的）",
              noFlag.Tags.Count == 1 && noFlag.Tags[0] == esc,
              string.Join(" | ", noFlag.Tags));

        // 散文边界：ProseStart 第 1 条要求"句号前那截是已知标签"。转义后它查不到，
        // 于是整行被判成散文 —— 标签会从中间列表里整个消失，这是静默破坏。
        var prose = "210 \\(reverse:1999\\). An anime girl stands here.";
        var ok = PromptText.Parse(prose, vocab, unescapeTags: true);
        var bad = PromptText.Parse(prose, vocab, unescapeTags: false);
        Check("转义标签后接散文时仍按标签切分",
              ok.Tags.Count == 1 && ok.Tags[0] == lit,
              $"tags={ok.Tags.Count} prose='{ok.ProseTail}'");
        Check("不还原则整行被判成散文（缺陷形状）",
              bad.Tags.Count == 0 && bad.ProseTail.Length > 0,
              $"tags={bad.Tags.Count} prose='{bad.ProseTail}'");

        // 着色：区间按原文下标（因为框里是转义后的文本），分类按字面量
        var spans = PromptText.ColorSpans(prose, vocab, unescapeTags: true);
        Check("着色区间落在转义后的原文上",
              spans.Count == 1 && prose.Substring(spans[0].Start, spans[0].Length) == esc,
              spans.Count == 1 ? $"'{prose.Substring(spans[0].Start, spans[0].Length)}'" : $"spans={spans.Count}");
        Check("分类用的是字面量",
              spans.Count == 1 && spans[0].Section == TagSections.Classify(lit, vocab),
              spans.Count == 1 ? spans[0].Section : "-");
        Check("不还原则不着色（缺陷形状）",
              PromptText.ColorSpans(prose, vocab, unescapeTags: false).Count == 0);

        Check("状态栏标签数与字面量口径一致",
              PromptText.Summarize(esc, vocab, true) == PromptText.Summarize(lit, vocab, false),
              $"'{PromptText.Summarize(esc, vocab, true)}' vs '{PromptText.Summarize(lit, vocab, false)}'");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "=== escape: 全部通过 ===" : $"=== escape: {fail} 项失败 ===");
        return fail == 0 ? 0 : 1;
    }
}
