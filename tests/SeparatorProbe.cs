using AnimaCaptioner.Core;

namespace CoreCheck;

/// <summary>
/// 探针：**标签与自然语言之间是逗号还是句号？** 官方依据在哪？
///
/// 起因是用户的一个观察："自然语言和普通提示词之间好像也是用逗号分隔的，
/// 只是自然语言永远在最后面。"
///
/// 结论要分两半说，因为**观察对了一半、推论反了一半**：
///   - 对的一半：逗号确实是**标签之间**的分隔符（官方 Full tag example 全是逗号）。
///   - 反的一半：标签与自然语言之间**不是**逗号，而是句号（句边界）。
///     官方两个例子都是 `... @big chungus. An anime girl ...`。
///
/// 而且官方还有一条比这更根本的：**"You can mix tags and natural language in
/// arbitrary order"** —— 自然语言**不必**在最后面。本程序把它固定成"尾巴"
/// 是实现上的取舍（见 PromptText 的类注释），不是模型的要求。
///
/// 依据（online，2026-10 取的官方 model card）：
///   https://huggingface.co/circlestone-labs/Anima
///   本机走代理抓的原文存在 %TEMP%\anima-card.md，下面按引用核对。
/// </summary>
public static class SeparatorProbe
{
    /// <summary>官方原文里的两句判据（逐字，便于核对抓下来的那份没被人改过）。</summary>
    private const string CardOrder = "You can mix tags and natural language in arbitrary order.";
    private const string CardPrefix = "You can put quality / artist tags at the beginning of a natural language prompt.";
    private const string CardExample = "\"masterpiece, best quality, @big chungus. An anime girl with medium-length blonde hair is...\"";

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public static int Run(string? cardPath, string? vocabPath = null, string? seedPath = null)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var bad = 0;

        void Check(string label, bool ok)
        {
            if (!ok) bad++;
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {label}");
        }

        // **必须配一份真词库**：交界判据第 1 条要求"句号前那截是**已知标签**"，
        // 没有词库时 `safe` 不是已知标签，整条规则不成立，测出来的行为是假的。
        VocabDb? vocab = null;
        var vocabNote = "(未提供词库)";
        if (vocabPath is not null && File.Exists(vocabPath))
        {
            try
            {
                vocab = new VocabDb();
                vocab.Load(vocabPath, seedPath ?? "");
                vocabNote = $"词库 {vocab.TagCount:N0} 条";
            }
            catch (Exception ex) { vocabNote = "词库加载失败：" + ex.Message; }
        }
        Console.WriteLine($"词库：{vocabNote}");
        Console.WriteLine();

        // ---------- 1. 官方依据本身 ----------
        Console.WriteLine("=== 1. 官方 model card 怎么说的 ===");
        var card = cardPath is not null && File.Exists(cardPath)
            ? File.ReadAllText(cardPath) : null;
        if (card is null)
        {
            Console.WriteLine($"  （没找到 model card：{cardPath}，只跑解析器那几节）");
        }
        else
        {
            Check("官方允许标签与自然语言任意顺序（不是必须放最后）", card.Contains(CardOrder));
            Check("官方允许把质量/画师标签放在自然语言开头", card.Contains(CardPrefix));
            Check("官方例子的交界是句号（\". \"）而不是逗号", card.Contains(CardExample));
            Console.WriteLine($"       {CardOrder}");
            Console.WriteLine($"       {CardExample}");
        }

        // ---------- 2. 逗号 vs 句号：解析器分别怎么判 ----------
        Console.WriteLine();
        Console.WriteLine("=== 2. 交界处用逗号还是句号，解析结果完全不同 ===");
        const string dotSep =
            "safe, 1girl, solo, blue eyes. A black cat plush sits on her shoulder, and a small " +
            "panel is visible at the lower left of the image.";
        const string commaSep =
            "safe, 1girl, solo, blue eyes, A black cat plush sits on her shoulder, and a small " +
            "panel is visible at the lower left of the image.";

        var dot = PromptText.Parse(dotSep, vocab);
        var comma = PromptText.Parse(commaSep, vocab);

        Console.WriteLine($"  句号交界：{dot.Tags.Count} 个标签 + 散文 {dot.ProseTail.Length} 字" +
                          $"  -> tags=[{string.Join("|", dot.Tags)}]");
        Console.WriteLine($"      尾巴 = \"{Cut(dot.ProseTail.Trim(), 60)}\"");
        Console.WriteLine($"  逗号交界：{comma.Tags.Count} 个标签" +
                          $"，散文={(comma.HasProse ? comma.ProseTail.Length + " 字" : "没有")}" +
                          $"  -> tags=[{string.Join("|", comma.Tags)}]");
        Console.WriteLine($"      尾巴 = \"{Cut(comma.ProseTail.Trim(), 60)}\"");

        // 两种写法被判成同一个结果 —— 说明**交界不是由标点决定的**。
        // 真正的判据是那条"句子形状"规则：段首大写 + ≥3 个词 + 不是已知标签。
        // （尾巴本身差一个前导分隔符 ". "，那是 ProseTail 刻意保留的，剥掉再比。）
        static string Body(string tail) => tail.TrimStart('.', '!', '?', ';', ' ').TrimEnd();
        Check("两种写法给出同样的解析（交界由「句子形状」判据决定，不看标点）",
              dot.Tags.Count == comma.Tags.Count &&
              string.Equals(Body(dot.ProseTail), Body(comma.ProseTail), StringComparison.Ordinal));
        Check("句号写法：标签归标签、散文归散文", dot.Tags.Count == 4 && dot.HasProse);
        Check("逗号写法也一样分得开（这正是用户观察到的那个现象）",
              comma.Tags.Count == 4 && comma.HasProse);

        // 反证：把散文换成"不像句子"的写法，逗号就真的会把它吃成标签
        const string lowerCaseProse =
            "safe, 1girl, solo, blue eyes, black cat plush on her shoulder, plain white backdrop";
        var lower = PromptText.Parse(lowerCaseProse, vocab);
        Console.WriteLine();
        Console.WriteLine("  反证 —— 散文写成小写、不像句子时：");
        Console.WriteLine($"    \"{Cut(lowerCaseProse, 70)}\"");
        Console.WriteLine($"    -> {lower.Tags.Count} 个标签，散文={(lower.HasProse ? "有" : "没有")}");
        Check("小写且不像句子时，逗号两侧全被当成标签", !lower.HasProse && lower.Tags.Count == 6);
        Console.WriteLine("    ↑ 所以逗号**确实**不构成边界：分不分得开，取决于那一段像不像句子。");

        // ---------- 3. 那"逗号"这个印象是从哪来的 ----------
        Console.WriteLine();
        Console.WriteLine("=== 3. 逗号确实是分隔符 —— 但分隔的是**标签之间** ===");
        const string officialTags =
            "year 2025, newest, safe, 1girl, oomuro sakurako, yuru yuri, @nnn yryr, smile, " +
            "brown hair, solo, white background";
        var many = PromptText.Parse(officialTags, vocab);
        Console.WriteLine($"  官方 Full tag example 那一串：{many.Tags.Count} 个标签，0 字散文" +
                          $"（全用逗号分隔）");
        Check("纯标签串被整串当标签", many.Tags.Count == 11 && !many.HasProse);

        // ---------- 4. 自然语言**内部**的逗号不受影响 ----------
        Console.WriteLine();
        Console.WriteLine("=== 4. 自然语言内部的逗号照常出现（因为尾巴是整段吞下去的） ===");
        var inner = dot.ProseTail.Count(c => c == ',');
        Console.WriteLine($"  尾巴里 {inner} 个逗号，解析不受影响：尾巴是「从第一个句边界吃到结尾」");
        Check("尾巴内含逗号也照旧", dot.HasProse && inner > 0);

        // ---------- 5. 官方允许任意顺序，本程序只支持尾巴 ----------
        Console.WriteLine();
        Console.WriteLine("=== 5. 一个**实现取舍**：官方允许任意顺序，本程序只认「尾巴」 ===");
        const string proseFirst =
            "An anime girl with blonde twintails stands in a white room. 1girl, solo, smile";
        var odd = PromptText.Parse(proseFirst, vocab);
        Console.WriteLine($"  自然语言在前、标签在后：tags={odd.Tags.Count} " +
                          $"prose={(odd.HasProse ? odd.ProseTail.Length + " 字" : "没有")}");
        Console.WriteLine("  -> 整段都被判成散文（第 3 条「句子形状」从第 0 个字就命中），");
        Console.WriteLine("     于是后面的标签也一并被吞进散文。官方允许这么写，本程序不保证还原。");

        // 那就得看看：这种正文万一真进了程序，**加一个标签会不会把正文粘坏**。
        // 尾巴以"句号+空格"开头时没问题（Rebuild 直接接上），但散文在最前面时
        // ProseStart 返回的下标是 0，尾巴**不带前导分隔符**——接上去就粘成一坨。
        var rebuilt = PromptText.Rebuild(new[] { "masterpiece" }, odd.ProseTail);
        Console.WriteLine();
        Console.WriteLine($"  在散文开头的正文上加一个标签，Rebuild 得到：");
        Console.WriteLine($"    \"{Cut(rebuilt, 90)}\"");
        var glued = rebuilt.StartsWith("masterpieceAn", StringComparison.Ordinal);
        Console.WriteLine($"    {(glued ? "**坏掉了**：标签与散文之间没有分隔符，粘成一个词" : "分隔正常")}");
        Check("散文开头的正文，加标签后仍有分隔符（拼接不粘）", !glued);

        Console.WriteLine();
        Console.WriteLine($"=== separator: {(bad == 0 ? "OK（官方依据 + 解析行为都对上了）" : bad + " FAILED")} ===");
        return bad == 0 ? 0 : 1;
    }
}
