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
/// 这个文件不引用任何 WinUI 类型，所以能被 net10 回归程序直接编译。
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

    /// <summary>
    /// 一段提示词的简短摘要，例如 <c>5 tags · ≈18 tokens</c>；超预算时附警告。
    ///
    /// 放在这里（而不是 UI 那一层）是为了让**列表行与状态栏共用同一个口径**：
    /// 两处各算一份的话，迟早出现"列表说没超、状态栏说超了"。它也是纯逻辑，
    /// 所以 net10 回归能直接测。
    ///
    /// 标签数走 <see cref="Parse"/>，与中栏列表、着色、校验**同一个解析器**。
    /// 这里踩过一个坑：曾经用"含空格的逗号段不算标签"来排除散文，结果
    /// `best quality` / `silver hair` 这类**合法的多词标签**被漏数，于是
    /// 状态栏左边报 3 tags、右边报 4 tags。散文边界只有解析器判得准（它看的是
    /// 句边界与已知标签），按空格猜一定会错。
    /// </summary>
    public static string Summarize(string? body, VocabDb? vocab = null, bool unescapeTags = false)
    {
        var text = (body ?? "").Trim();
        if (text.Length == 0) return "空";

        var tags = Parse(text, vocab, unescapeTags).Tags.Count;

        // token 预算要按**编码器实际看到**的文本算：ComfyUI 会先把 \( \) 的反斜杠
        // 吃掉再送分词器（escape_important -> unescape_important），所以转义后的正文
        // 直接拿去估算会凭空多算那些反斜杠。括号标签一多就会多出几个 token，
        // 严重时误报「超 512」——而 512 是训练侧的硬上限，误报会让人白删标签。
        var visible = unescapeTags ? TagEscape.Unescape(text) : text;
        var tokens = EstimateTokens(visible);
        var s = tags > 0 ? $"{tags} tags · " : "";
        s += $"≈{tokens} tokens";
        if (tokens > MaxTokens) s += $" ⚠ 超 {MaxTokens}";
        return s;
    }

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
    private static int ProseStart(string text, VocabDb? vocab, bool unescapeTags)
    {
        // 转义过的正文里标签带着 \( \) 的反斜杠，直接查库查不到。所有词库查询都必须
        // 走这个包装：**按原文定位、按字面量查库**——下标仍按原文算，所以着色与
        // 切片不受影响。漏掉任何一处，被转义的标签就会因为"不是已知标签"而
        // 掉进下面第 3 条（首字母大写 + ≥3 词）被判成散文，整行消失。
        bool Known(string tag) =>
            TagSections.IsKnownTag(unescapeTags ? TagEscape.Unescape(tag) : tag, vocab);

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
                    if (head.Length > 0 && Known(head)) return s + i;
                    // 否则整段都算散文
                    return s;
                }

                // 2. 以句末标点收尾
                if (seg.EndsWith(SentenceEnd))
                {
                    var head = seg[..^1].TrimEnd();
                    if (head.Length > 0 && Known(head)) return s + seg.Length - 1;
                    return s;
                }

                // 3. 句子形状且不是标签
                if (char.IsUpper(seg[0]) && WordCountAtLeast(seg, 3)
                    && !Known(seg))
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
    /// <param name="unescapeTags">
    /// 正文里的标签是否带着生图用的转义反斜杠（见 <see cref="TagEscape"/>）。
    /// 为 true 时**词库查询与返回的标签都用去掉反斜杠的字面量**，而散文边界
    /// 仍按原文下标计算——于是转义对解析、着色、校验完全透明。
    /// 标注模式一律传 false（训练集里没有转义）。
    /// </param>
    public static Parts Parse(string? text, VocabDb? vocab, bool unescapeTags = false)
    {
        if (string.IsNullOrEmpty(text)) return new Parts(new List<string>(), "");

        var p = ProseStart(text, vocab, unescapeTags);
        if (p < 0)
            return new Parts(SplitTags(text, unescapeTags), "");

        var tags = SplitTags(text[..p], unescapeTags);
        return new Parts(tags, text[p..]);
    }

    /// <summary>只按逗号切标签，去空白、去空段。<paramref name="unescape"/> 见 <see cref="Parse"/>。</summary>
    public static List<string> SplitTags(string text, bool unescape = false) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Select(t => unescape ? TagEscape.Unescape(t) : t)
            .ToList();

    /// <summary>
    /// 用新的标签序列重建提示词，散文尾巴原样接回。
    /// 这是所有标签操作（上移/下移/删除/规整/添加）的落盘路径。
    ///
    /// **尾巴与标签之间一定要有分隔符。** 正常情况尾巴自带（`. ` 或 ` `，见
    /// <see cref="Parse"/>），直接接上即可；但尾巴**不带**前导分隔符时有两种来源，
    /// 两种都会把词粘在一起：
    ///   - 官方允许"标签与自然语言任意顺序"，于是散文可能在**最前面**
    ///     （`An anime girl … . 1girl, solo`）。那时 <see cref="ProseStart"/> 返回 0、
    ///     尾巴就是整段正文，前面一个分隔符都没有。
    ///   - 手改的 caption 里标签区后面直接顶着散文、中间只有逗号。
    /// 实测（tests/SeparatorProbe.cs）：不补的话 `masterpiece` + `An anime girl…`
    /// 会拼成 `masterpieceAn anime girl…`，而这是**静默**的——标签列表看不出异常，
    /// 训练时读到的是一个不存在的词加上一段被毁掉的散文。
    ///
    /// 补什么：尾巴已经以标点或空白开头就不动；否则按标签区习惯补 `", "`——
    /// 尾巴当时是被逗号分出来的（或者它本来就是第一段），逗号是它原本的邻居。
    /// </summary>
    public static string Rebuild(IReadOnlyList<string> tags, string? proseTail,
                                 string separator = ", ")
    {
        var head = string.Join(separator, tags.Where(t => t.Length > 0));
        var tail = proseTail ?? "";
        if (tail.Length == 0) return head;
        if (head.Length == 0) return tail.TrimStart();
        if (NeedsSeparator(tail)) return head + separator + tail;
        return head + tail;
    }

    /// <summary>
    /// 尾巴前面要不要替它补一个分隔符。**判据只看第一个字符**：是标点或空白
    /// 就说明它自带边界（正常尾巴 `. ` / ` `），不需要补。
    /// </summary>
    private static bool NeedsSeparator(string tail) =>
        tail.Length > 0 && !char.IsWhiteSpace(tail[0]) &&
        !SentenceEnd.Contains(tail[0]) && tail[0] != ',';

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
    public static List<ColorSpan> ColorSpans(string? text, VocabDb? vocab, bool unescapeTags = false)
    {
        var spans = new List<ColorSpan>();
        var parts = Parse(text, vocab, unescapeTags);

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
                // 查库与分类都用字面量，但着色区间仍按原文下标——转义只是插入了
                // 反斜杠，区间本身没变，所以两者可以各取所需。
                var lookup = unescapeTags ? TagEscape.Unescape(body) : body;
                if (TagSections.IsKnownTag(lookup, vocab))
                    spans.Add(new ColorSpan(s, e - s, TagSections.Classify(lookup, vocab)));
            }

            if (comma < 0 || comma >= tagRegionLength) break;
            from = comma + 1;
        }
        return spans;
    }
}
