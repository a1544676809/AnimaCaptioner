namespace AnimaCaptioner.Core;

/// <summary>
/// 输入框的候补与 Tab 补全。
///
/// 抽出来单独放，是因为这里面有两条只能靠数据验证的规则：
///
/// 1. **第一个候选必须就是回车会加进去的那个。** 否则用户按 Tab 补全、
///    再按回车，得到的会是另一个标签——补全反而成了陷阱。所以候选列表
///    的第一项取 <see cref="TagEdit.Resolve"/> 的结果（中文键、人工词表、
///    旧别名都在这里落地），后面才是 <see cref="VocabDb.Search"/> 的包含匹配。
/// 2. **Tab 只循环"列表里看得见的那几个"。** 补全列表和 Tab 走的必须是同一份
///    候选，否则用户按 Tab 会跳到列表里没有的东西上。
///
/// 这个文件不引用任何 WinUI 类型，所以能被 net8 回归程序直接编译。
/// </summary>
public static class TagComplete
{
    /// <summary>候选上限。也是 Tab 能循环的格数——太长就没法"按 Tab 找"了。</summary>
    public const int Limit = 12;

    /// <summary>候选条目里标签与释义之间的分隔。列表显示与还原都用它，改一处即可。</summary>
    public const string Sep = "   —   ";

    public readonly record struct Candidate(string Tag, string Cn)
    {
        public bool HasCn => Cn.Length > 0;
    }

    /// <summary>一次 Tab 的结果。<paramref name="Fresh"/> 表示这是新起的一轮循环。</summary>
    public readonly record struct Step(Candidate Item, int Index, int Count, bool Fresh);

    /// <summary>
    /// 给一个输入串算候选。中文、英文、旧别名、带下划线的写法都吃。
    ///
    /// 排序分四段：解析结果 → 输入本身 → 强前缀匹配 → 其余。
    /// 三处都不能省：
    ///
    /// 1. **解析结果排第一**，因为它就是回车会加进去的那个标签（中文键、
    ///    人工词表、旧别名、下划线写法都在这条路上落成英文标签）。但"模糊"
    ///    解析不算数——`双马` 按包含匹配会解析成 `horse`（键 `马` 在里面），
    ///    而用户十有八九是在打 `双马尾` 的前两个字，所以模糊结果要让位给前缀匹配。
    /// 2. **强前缀匹配排在包含匹配前面。** 不能赖 Search 的分数：它的
    ///    "废弃/无引用 +10"会把一个前缀候选压到仅靠包含命中的候选后面，
    ///    于是 Tab 会跳到 `@nsfw bb`、`@silver bell` 这种只是碰巧含有输入串的东西上。
    /// 3. **每个候选都过一遍解析。** 候选列表里出现什么，回车就得加什么：
    ///    直接把 Search 的结果当候选，Tab 会补出 `china dress`，而回车加进去的是
    ///    `qipao`（词库会把退役名重定向到正典名）——框里写的和实际存的不一样。
    /// </summary>
    public static List<Candidate> Suggest(string? query, VocabDb? vocab, int limit = Limit)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0 || vocab is null || limit <= 0) return new List<Candidate>();

        var outp = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string tag, string cn)
        {
            if (tag.Length == 0 || outp.Count >= limit) return;
            if (!seen.Add(tag)) return;
            outp.Add(new Candidate(tag, cn));
        }

        // 标签本身以输入开头，或它的中文释义以输入开头，都算"强前缀"。
        // 中文输入时标签是英文，所以后半条才是中文补全真正依赖的信号。
        static bool StrongPrefix(Candidate c, string q) =>
            c.Tag.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
            (c.HasCn && c.Cn.StartsWith(q, StringComparison.Ordinal));

        var r = TagEdit.Resolve(q, vocab);
        var mapped = r.Tag.Length > 0 && !string.Equals(r.Tag, q, StringComparison.OrdinalIgnoreCase);
        var fuzzy = string.Equals(r.Status, "fuzzy", StringComparison.Ordinal);

        if (mapped && !fuzzy) Add(r.Tag, vocab.LookupCn(r.Tag));

        // 多要一些再自己排：Search 只按分数截断，前缀候选可能排在截断之外。
        var hits = vocab.Search(q, Math.Max(limit, 40));
        var stable = hits
            .Select(h =>
            {
                var t = TagEdit.Resolve(h.Tag, vocab).Tag;
                if (t.Length == 0) t = h.Tag;
                return new Candidate(t, vocab.LookupCn(t));
            })
            .ToList();

        var anyStrong = stable.Any(c => StrongPrefix(c, q));

        // 输入本身就是词库认识的写法（"unknown" 以外的任何状态，含 retired /
        // no-usage）时它必须排第一——否则在一个已经合法的标签上顺手按一下 Tab
        // 就会把它改成别的标签。只有"词库完全不认识、而且有强前缀候选可给"时
        // 才让位，否则 `twint` 这种打到一半的输入永远补不出 `twintails`。
        var selfKnown = string.Equals(r.Tag, q, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(r.Status, "unknown", StringComparison.Ordinal);
        if (outp.Count == 0 && (selfKnown || !anyStrong)) Add(q, vocab.LookupCn(q));

        foreach (var c in stable) if (StrongPrefix(c, q)) Add(c.Tag, c.Cn);
        if (mapped && fuzzy) Add(r.Tag, vocab.LookupCn(r.Tag));   // 模糊结果让位给前缀之后
        foreach (var c in stable) Add(c.Tag, c.Cn);

        return outp;
    }

    /// <summary>候选在列表里的显示文本。有中文时带上释义。</summary>
    public static string Display(Candidate c) => c.HasCn ? c.Tag + Sep + c.Cn : c.Tag;

    /// <summary>把显示文本还原成纯标签。候选框选中项交回来的是显示文本。</summary>
    public static string Bare(string? displayed) =>
        (displayed ?? "").Split(Sep, 2, StringSplitOptions.None)[0].Trim();

    /// <summary>补全结果的一句话说明，写进状态栏。</summary>
    public static string Status(Step s)
    {
        var head = s.Count > 1 ? $"补全 {s.Index + 1}/{s.Count}：{s.Item.Tag}" : $"补全：{s.Item.Tag}";
        if (s.Item.HasCn) head += $" — {s.Item.Cn}";
        if (s.Count > 1 && s.Fresh) head += "（Tab 切换，Shift+Tab 往回）";
        return head;
    }

    /// <summary>
    /// Tab 循环的状态机。每个输入框一个实例——两个框共用会让 Tab 互相跳格。
    ///
    /// "还在同一次循环里"的判据是**框里的文字正好等于上次填进去的那个候选**。
    /// 不能用"按过几次 Tab"来记：用户补全之后接着打字是最常见的动作，
    /// 那时他期望的是按新内容重新给候选，而不是从上次的第 5 格继续。
    /// </summary>
    public sealed class Cycler
    {
        private List<Candidate> _items = new();
        private int _index = -1;

        public int Count => _items.Count;
        public int Index => _index;
        public bool Active => _index >= 0 && _index < _items.Count;

        /// <summary>
        /// 当前这一轮的候选。给界面用来把下拉列表重新铺一遍——
        /// Tab 之后列表要留着（否则用户看不出还能往哪切），
        /// 而列表内容必须还是这同一份。
        /// </summary>
        public IReadOnlyList<Candidate> Items => _items;

        public void Reset()
        {
            _items = new List<Candidate>();
            _index = -1;
        }

        /// <summary>
        /// 框里的文字是不是"我上次填进去的那个候选"。
        ///
        /// 判断循环要不要作废**只能看内容，不能只看事件**：Tab 填完字之后，
        /// 给候选列表设 ItemsSource 还会再触发一次 TextChanged（实测），
        /// 那次事件看着像"用户改了字"，真去清循环就会让连按两次 Tab 卡在同一格。
        /// </summary>
        public bool At(string? text) =>
            Active && string.Equals(_items[_index].Tag, (text ?? "").Trim(),
                                    StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 走一步。<paramref name="lookup"/> 只在需要重算候选时调用
        /// （也就是用户改了字之后），所以它可以是稍贵的词库查询。
        /// 没有候选时返回 null，并清掉状态。
        /// </summary>
        public Step? Next(string? typed, Func<string, List<Candidate>> lookup, bool backwards = false)
        {
            var t = (typed ?? "").Trim();
            if (t.Length == 0) { Reset(); return null; }

            if (At(t))
            {
                var n = _items.Count;
                var next = backwards ? _index - 1 : _index + 1;
                if (next < 0) next = n - 1;
                if (next >= n) next = 0;
                _index = next;
                return new Step(_items[_index], _index, n, Fresh: false);
            }

            var items = lookup(t);
            if (items.Count == 0) { Reset(); return null; }

            _items = items;
            _index = backwards ? items.Count - 1 : 0;
            return new Step(_items[_index], _index, items.Count, Fresh: true);
        }

        /// <summary>
        /// 用户从下拉列表里点了一个候选之后调这个：把循环对到那一格上，
        /// 于是接着按 Tab 是从这里往后走，而不是从头重来。
        /// 点的那个不在当前候选里（改过字了）就清空状态。
        /// </summary>
        public void SyncTo(string? tag)
        {
            var t = (tag ?? "").Trim();
            for (var i = 0; i < _items.Count; i++)
            {
                if (!string.Equals(_items[i].Tag, t, StringComparison.OrdinalIgnoreCase)) continue;
                _index = i;
                return;
            }
            Reset();
        }
    }
}
