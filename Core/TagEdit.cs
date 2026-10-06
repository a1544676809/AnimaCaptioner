namespace AnimaCaptioner.Core;

/// <summary>
/// 单个标签的编辑与规范化：把用户敲进来的任意写法（中文、下划线、旧别名、
/// 缺 @ 的画师名）解析成词库认可的标签。
///
/// 单独抽出来是因为这条逻辑有两个必须守住的边界，而且都值得脱离界面验证：
///
/// 1. **认不出来也要原样保留。** 词库不是全集（实测 679 个真实标签里有 74 个
///    词库不认，比如 `2 fingers`、`bangs`），用户写了新角色名或自造短语时
///    静默丢掉是最坏的结果——那会悄悄改掉训练集。所以认不出就原样留下，
///    只在提示里说明"不在任何索引中"。
/// 2. **不许产生重复。** 改名之后可能撞上列表里已有的标签（`wink` 规范成
///    `one eye closed`，而列表里本来就有它），这时要拒绝并说明，而不是让
///    两个一样的标签并排存在。
///
/// 这个文件不引用任何 WinUI 类型，所以能被 net8 回归程序直接编译。
/// </summary>
public static class TagEdit
{
    /// <summary>解析结果。Status 沿用词库的状态词（anima/alias/retired/unknown…）。</summary>
    public readonly record struct Resolved(string Tag, string Status)
    {
        /// <summary>词库是否认得这个写法。不认得时 <see cref="Tag"/> 是原样的输入。</summary>
        public bool Found => Status is not ("unknown" or "no-usage" or "empty");
    }

    /// <summary>一次编辑的结果。<see cref="Ok"/> 为 false 时 <see cref="Tag"/> 是"保持不变"的值。</summary>
    public readonly record struct Result(bool Ok, string Tag, string Status, string Message);

    public readonly record struct PasteResult(
        List<string> Tags, string ProseTail, int Skipped, bool ProseOnly);

    public readonly record struct NormalizeResult(
        List<string> Tags, int Changed, List<(string From, string To)> Pairs, int Skipped);

    /// <summary>状态词 → 人话。措辞和「添加标签」那条路保持一致，避免两处说法不同。</summary>
    public static string DescribeStatus(string status) => status switch
    {
        "anima" => "Anima 索引",
        "valid" => "词库",
        "exact" => "词库中文键",
        "seed" => "人工词表",
        "seed-unverified" => "人工词表（未在索引中验证）",
        "fuzzy" => "词库模糊匹配",
        "alias" => "旧别名 → 正典名",
        "underscore-fixed" => "去掉下划线",
        "retired" => "已废弃",
        "no-usage" => "无引用",
        _ => "不在任何索引中"
    };

    /// <summary>
    /// 任意写法 → 标签。顺序与「添加标签」一致：先当英文标签解析，
    /// 再当中文关键词，最后原样保留。
    ///
    /// Anima 索引优先是刻意的：Danbooru 会把 `see-through` 重定向到
    /// `see-through clothes`，而 Anima 自带 `see-through`（160,050 帖）。
    /// </summary>
    public static Resolved Resolve(string? input, VocabDb? vocab)
    {
        var raw = (input ?? "").Trim();
        if (raw.Length == 0) return new Resolved("", "empty");
        if (vocab is null) return new Resolved(raw, "unknown");

        var (en, enSt) = vocab.ResolveEnglish(raw);
        if (en is not null && enSt is not ("unknown" or "no-usage"))
            return new Resolved(en, enSt);

        var (cn, cnSt) = vocab.ResolveChinese(raw);
        if (cn is not null) return new Resolved(cn, cnSt);

        // 词库认得拼写但没有引用（Danbooru 里 post_count = 0）：仍然收下规范拼写，
        // 它比用户敲的变体更可能被模型认出。
        if (en is not null && enSt != "empty") return new Resolved(en, enSt);

        return new Resolved(raw, "unknown");
    }

    /// <summary>
    /// 编辑一个已存在的标签：把 <paramref name="input"/> 当成新名字。
    /// <paramref name="others"/> 是列表里**除它之外**的标签，用来防重复。
    /// </summary>
    public static Result Apply(string? input, string? oldTag,
                               IReadOnlyList<string> others, VocabDb? vocab)
    {
        var old = (oldTag ?? "").Trim();
        var raw = (input ?? "").Trim();

        if (raw.Length == 0)
            return new Result(false, old, "empty", "标签不能为空。要移除它请用「删除」。");

        // 一行只能是一个标签。用户多半是粘了一整行提示词进来——如实说清该用哪个入口，
        // 而不是把它切成好几个标签（那会在他没要求的情况下改动列表）。
        if (raw.Contains(','))
            return new Result(false, old, "comma",
                "一行只能放一个标签。要一次加多个，请用右键菜单的「粘贴」。");

        if (string.Equals(raw, old, StringComparison.Ordinal))
            return new Result(false, old, "unchanged", "没有改动。");

        return NormalizeOne(raw, old, others, vocab);
    }

    /// <summary>
    /// 规范化一个写法。与 <see cref="Apply"/> 的区别：这里不把"和原名一样"
    /// 当成放弃理由——右键菜单的「翻译 / 规范化」正是要对现有标签做这件事。
    /// </summary>
    public static Result NormalizeOne(string? input, string? oldTag,
                                      IReadOnlyList<string> others, VocabDb? vocab)
    {
        var old = (oldTag ?? "").Trim();
        var raw = (input ?? "").Trim();
        if (raw.Length == 0)
            return new Result(false, old, "empty", "标签不能为空。");

        var r = Resolve(raw, vocab);
        if (r.Tag.Length == 0)
            return new Result(false, old, "empty", "标签不能为空。");

        var via = DescribeStatus(r.Status);

        if (string.Equals(r.Tag, raw, StringComparison.Ordinal))
        {
            return r.Found
                ? new Result(false, raw, r.Status, $"「{raw}」已经是规范形式（{via}）。")
                : new Result(false, raw, r.Status,
                    $"「{raw}」不在任何索引中，保持原样（{via}）。");
        }

        foreach (var o in others)
        {
            if (string.Equals(o, r.Tag, StringComparison.OrdinalIgnoreCase))
                return new Result(false, old, "duplicate",
                    $"「{raw}」的规范形式是「{r.Tag}」，但列表里已经有它了。");
        }

        return new Result(true, r.Tag, r.Status, $"「{raw}」→「{r.Tag}」（{via}）");
    }

    /// <summary>
    /// 整行规范化：别名→正典名、去下划线、补 @、中文→英文标签。
    /// 段内相对次序原样保留（不重排），重复的合并掉并计入 Skipped。
    /// </summary>
    public static NormalizeResult Normalize(IReadOnlyList<string> tags, VocabDb? vocab)
    {
        var outp = new List<string>(tags.Count);
        var pairs = new List<(string From, string To)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;

        foreach (var t in tags)
        {
            var r = Resolve(t, vocab);
            var cand = r.Tag.Length > 0 ? r.Tag : t;
            if (cand.Length == 0) { skipped++; continue; }
            if (!seen.Add(cand)) { skipped++; continue; }

            outp.Add(cand);
            if (!string.Equals(cand, t, StringComparison.Ordinal))
                pairs.Add((t, cand));
        }

        return new NormalizeResult(outp, pairs.Count, pairs, skipped);
    }

    /// <summary>
    /// 解析剪贴板内容成一批标签。
    ///
    /// 换行先折成逗号：从文件或表格里复制出来的一列标签是换行分隔的，
    /// 而 <see cref="PromptText.Parse"/> 只认逗号，不折的话整列会变成一个
    /// 带换行的"标签"。
    ///
    /// 自然语言部分（如果有）走 <see cref="PromptText.Parse"/> 摘掉：
    /// 复制一整行提示词再粘回来时，散文不该被当成一堆标签塞进列表。
    /// </summary>
    public static PasteResult FromPaste(string? text, IReadOnlyList<string> existing, VocabDb? vocab)
    {
        var s = (text ?? "").Replace("\r\n", ", ").Replace('\r', ',').Replace('\n', ',');
        var parsed = PromptText.Parse(s, vocab);

        var outp = new List<string>();
        var skipped = 0;
        var seen = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        foreach (var piece in parsed.Tags)
        {
            var r = Resolve(piece, vocab);
            var cand = r.Tag.Length > 0 ? r.Tag : piece;
            if (cand.Length == 0) { skipped++; continue; }
            if (!seen.Add(cand)) { skipped++; continue; }
            outp.Add(cand);
        }

        var proseOnly = outp.Count == 0 && parsed.ProseTail.Trim().Length > 0;
        return new PasteResult(outp, parsed.ProseTail, skipped, proseOnly);
    }
}
