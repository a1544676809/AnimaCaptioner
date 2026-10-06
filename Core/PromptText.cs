namespace AnimaCaptioner.Core;

/// <summary>
/// 提示词原文的结构：**标签区 + 自然语言尾巴**。
///
/// Anima 的提示词不是"一串标签"，而是标签和英文散文的混合体。官方模型卡
/// （circlestone-labs/Anima，Prompting）：
///
///   "You can mix tags and natural language in arbitrary order."
///   "If using pure natural langauge, more descriptive is better.
///    Aim for at least 2 sentences."
///
/// 官方工作流模板里的正向提示词是**一整段英文散文、没有任何标签**；官方
/// example.png 里那次生成的提示词是「质量前缀 + 四句散文」，前缀以句号收尾：
///
///   masterpiece, best quality, score_7, safe. An anime girl wearing a ...
///
/// 所以解析规则就是：**标签区在前，散文尾巴在后**，尾巴从第一个"句边界"
/// 开始，一直吃到结尾。
///
/// 为什么按"尾巴"切而不是逐段判断：实测 679 个真实标签里，
/// 含句末标点的 **0 个**、首字母大写的 **0 个**，而这两个信号在英文散文里
/// 到处都是。连续的尾巴同时也正是官方写法（质量前缀 + 散文）和本程序
/// 「把英文描述追加到末尾」的产物，所以规则可预测，而不是逐片段的启发式猜测。
///
/// <see cref="ProseTail"/> **保留它的前导分隔符**（". " 或 " "），
/// 于是 <c>string.Join(", ", Tags) + ProseTail</c> 逐字节还原原文——
/// 官方那种"前缀以句号收尾"的写法能被原样保住，而不是被改写。
///
/// 这个文件不引用任何 WinUI 类型，所以能被 net8 回归程序直接编译。
/// </summary>
public static class PromptText
{
    public sealed record Parts(List<string> Tags, string ProseTail)
    {
        public bool HasProse => ProseTail.Length > 0;

        /// <summary>标签区的文本形态（不含散文尾巴）。</summary>
        public string TagText(string separator = ", ") => string.Join(separator, Tags);
    }

    /// <summary>着色区间：在原文里的绝对 [Start, Start+Length)。</summary>
    public readonly record struct ColorSpan(int Start, int Length, string Section);

    /// <summary>
    /// 训练侧的 token 预算。
    ///
    /// 这条约束来自训练器源码，不是估计值：
    ///   - `library/anima_train_utils.py` 第 101-110 行，两个分词器默认都是 512；
    ///   - `library/strategy_anima.py` 第 57-77 行把这两个值传给分词器时带
    ///     `truncation=True, padding="max_length"`。
    ///
    /// **`truncation=True` 意味着超出部分被直接砍掉，不报错、不警告。** 而且后果
    /// 有方向性：散文排在最后，所以超长时被丢掉的正好是散文，标签全都留着——
    /// 表现为「训练正常跑完」，但模型从没读到那段描述。
    ///
    /// 换算率实测自 Anima 自带的 T5 分词器（`comfy/text_encoders/t5_tokenizer`）：
    /// 标签密集的 caption 是 **3.23 字符/token**，纯散文约 4.0–4.3。
    /// 这里取标签密度——它是更保守的一侧（同样字符数下 token 更多）。
    /// </summary>
    public const int MaxTokens = 512;

    /// <summary>标签密集文本的字符/token（实测 53 个真实 caption 得 3.234）。</summary>
    public const double CharsPerTokenTagged = 3.23;

    /// <summary>
    /// 建议的字符软上限。留一成余量，因为这里的估算是按字符密度做的，
    /// 而标点、连字符、数字都会让实际 token 数偏离平均值。
    /// </summary>
    public static int SoftCharBudget => (int)(MaxTokens * 0.9 * CharsPerTokenTagged);

    /// <summary>按实测换算率估算 token 数。</summary>
    public static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / CharsPerTokenTagged);

    /// <summary>是否可能超过训练侧的 512 截断点。</summary>
    public static bool OverBudget(string? text) => EstimateTokens(text) > MaxTokens;

    private static readonly char[] SentenceEnd = { '.', '!', '?', ';', '\u3002', '\uff01', '\uff1f' };

    private static readonly string[] InnerBoundaries = { ". ", "! ", "? ", "...", "\u3002" };

    private static bool WordCountAtLeast(string s, int n)
    {
        var count = 0;
        var inWord = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { inWord = false; continue; }
            if (inWord) continue;
            inWord = true;
            if (++count >= n) return true;
        }
        return false;
    }

    /// <summary>
    /// 散文尾巴在原文里的起始下标；没有尾巴返回 -1。
    ///
    /// 三个触发条件，按顺序判：
    /// 1. 段内出现 <c>". "</c> 这类句边界，且**分号之前那截是已知标签**
    ///    （官方写法 <c>safe. An anime girl …</c> 就是这种：`safe` 是标签，
    ///    句号是标签区的终结符）。若分号前那截不是标签，则整段都是散文。
    /// 2. 段以句末标点收尾且去掉标点后是已知标签（<c>@big chungus.</c>——
    ///    官方模型卡自己的例子就是这个形状）。
    /// 3. 段首字母大写、≥3 个词、且不是已知标签 —— 读起来就是句子。
    /// </summary>
    private static int ProseStart(string text, VocabDb? vocab)
    {
        var segStart = 0;
        while (true)
        {
            var comma = text.IndexOf(',', segStart);
            var segEnd = comma < 0 ? text.Length : comma;

            var s = segStart;
            var e = segEnd;
            while (s < e && char.IsWhiteSpace(text[s])) s++;
            while (e > s && char.IsWhiteSpace(text[e - 1])) e--;
            var seg = text[s..e];

            if (seg.Length > 0)
            {
                // 1. 段内句边界
                foreach (var mark in InnerBoundaries)
                {
                    var i = seg.IndexOf(mark, StringComparison.Ordinal);
                    if (i < 0) continue;
                    var head = seg[..i].TrimEnd();
                    // 句号前那截是标签 -> 边界落在句号上（标签区含句号，散文含其后空格）
                    if (head.Length > 0 && TagSections.IsKnownTag(head, vocab)) return s + i;
                    // 否则整段都算散文
                    return s;
                }

                // 2. 以句末标点收尾
                if (seg.EndsWith(SentenceEnd))
                {
                    var head = seg[..^1].TrimEnd();
                    if (head.Length > 0 && TagSections.IsKnownTag(head, vocab)) return s + seg.Length - 1;
                    return s;
                }

                // 3. 句子形状且不是标签
                if (char.IsUpper(seg[0]) && WordCountAtLeast(seg, 3)
                    && !TagSections.IsKnownTag(seg, vocab))
                    return s;
            }

            if (comma < 0) return -1;
            segStart = comma + 1;
        }
    }

    /// <summary>
    /// 解析提示词原文。<paramref name="text"/> 里的换行会先归一成空格的语义
    /// （caption 是单行格式），但不改动传回的 <see cref="Parts.ProseTail"/>——
    /// 它按原文切片，保证还能逐字节拼回去。
    /// </summary>
    public static Parts Parse(string? text, VocabDb? vocab)
    {
        if (string.IsNullOrEmpty(text)) return new Parts(new List<string>(), "");

        var p = ProseStart(text, vocab);
        if (p < 0)
            return new Parts(SplitTags(text), "");

        var tags = SplitTags(text[..p]);
        return new Parts(tags, text[p..]);
    }

    /// <summary>只按逗号切标签，去空白、去空段。</summary>
    public static List<string> SplitTags(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

    /// <summary>
    /// 用新的标签序列重建提示词，散文尾巴原样接回。
    /// 这是所有标签操作（上移/下移/删除/规整/添加）的落盘路径。
    /// </summary>
    public static string Rebuild(IReadOnlyList<string> tags, string? proseTail,
                                 string separator = ", ")
    {
        var head = string.Join(separator, tags.Where(t => t.Length > 0));
        var tail = proseTail ?? "";
        if (tail.Length == 0) return head;
        if (head.Length == 0) return tail.TrimStart();
        return head + tail;
    }

    /// <summary>
    /// 把一段英文散文并进已有提示词。
    ///
    /// 官方 example.png 的写法是标签区用**句号**收尾再接散文，所以这里也按
    /// 句号接，而不是逗号——用逗号会把散文粘成"一个超长标签段"，着色和校验
    /// 都会跟着错。已有内容以句末标点收尾时就不再补。
    /// </summary>
    public static string AppendProse(string? existing, string prose)
    {
        prose = (prose ?? "").Trim();
        if (prose.Length == 0) return existing ?? "";

        var head = (existing ?? "").TrimEnd();
        if (head.Length == 0) return prose;

        var last = head[^1];
        if (last is '.' or '!' or '?' or ';' || SentenceEnd.Contains(last))
            return head + " " + prose;
        return head + ". " + prose;
    }

    /// <summary>
    /// 着色区间：只给标签区里**确实能识别成标签**的部分算区间。
    /// 散文尾巴一个区间都不产生，于是保持无底色——这是必须的：
    /// 实测官方那段散文按逗号切是 13/13 全不是标签，一律染成 general
    /// 会把整段散文涂成标签色。
    /// </summary>
    public static List<ColorSpan> ColorSpans(string? text, VocabDb? vocab)
    {
        var spans = new List<ColorSpan>();
        var parts = Parse(text, vocab);

        // 标签区的长度 = 原文长度 - 尾巴长度。尾巴按原文切片，所以这个换算是准的。
        var tagRegionLength = text is null ? 0 : text.Length - parts.ProseTail.Length;

        var from = 0;
        while (true)
        {
            var comma = text!.IndexOf(',', from);
            var end = comma < 0 ? text.Length : comma;
            if (from >= tagRegionLength) break;

            var s = from;
            var e = Math.Min(end, tagRegionLength);
            while (s < e && char.IsWhiteSpace(text[s])) s++;
            while (e > s && char.IsWhiteSpace(text[e - 1])) e--;

            if (e > s)
            {
                var body = text[s..e];
                if (TagSections.IsKnownTag(body, vocab))
                    spans.Add(new ColorSpan(s, e - s, TagSections.Classify(body, vocab)));
            }

            if (comma < 0 || comma >= tagRegionLength) break;
            from = comma + 1;
        }
        return spans;
    }
}
