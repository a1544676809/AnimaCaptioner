namespace AnimaCaptioner.Core;

/// <summary>
/// 按**文档身份**分桶的撤销历史：训练集里每一张图、提示词库里每一条草稿
/// 各自拥有一条独立的撤销 / 重做记录。
///
/// <para>**为什么按实例分，而不是共用一个栈。** 共用栈时换文档必须清空
/// （见 <see cref="PromptHistory.Clear"/> 的说明）：撤销点里存的是上一份文档的
/// 标签，跨文档撤销会把那些标签显示在当前文档里，用户接着一保存就写错了文件。
/// 但"记得清空"是个靠纪律维持的安全属性——漏掉任何一个换文档的路径，症状是
/// 静默写坏一个 caption，不是崩溃。按身份分桶之后，一份快照**只能被它自己的
/// 文档取用**，这个属性变成结构上不可能违反。</para>
///
/// <para>顺带换回了本该有的能力：标完第 3 张翻到第 7 张，想起第 3 张改错了，
/// 回到第 3 张时撤销栈还在。共用栈时它是空的。</para>
///
/// <para>**离开时记状态、打开时验一致**。桶里存着"上次离开这个文档时它是什么
/// 内容"。重新打开时如果盘上 / 库里的内容和它**对不上**，说明这份文档被外部
/// 改过（别的编辑器、脚本、另一个实例），旧撤销点指向的是已经不存在的内容——
/// 这时必须丢掉这个桶：否则 Ctrl+Z 会把外部改动退掉，而离开时的自动保存
/// 又会把它永久写死。</para>
///
/// <para>纯逻辑、不碰 WinUI，所以 net8 的 corecheck 能直接编译真实源码来测。</para>
/// </summary>
public sealed class PromptHistoryStore
{
    /// <summary>
    /// 最多同时保留多少份文档的历史（超出按最久未用淘汰）。
    ///
    /// 一步快照约 250 字节（20 个标签；<see cref="PromptSnapshot"/> 复制的是
    /// 字符串**引用**，字符串不可变，所以跨快照共享是安全的），默认 100 步
    /// 即每份约 25 KB，256 份约 6.4 MB——上限的作用是"数据集上千张时别无限涨"，
    /// 而不是"内存吃紧"。
    /// </summary>
    public const int DefaultMaxDocuments = 256;

    /// <summary>一份文档的历史桶。</summary>
    private sealed class Entry
    {
        public PromptHistory History { get; } = new();

        /// <summary>是否记过"离开时的内容"。没记过就没有校验依据。</summary>
        public bool HasKnown;

        /// <summary>离开时的标签序列（字面量口径，与 _pendingTags 一致）。</summary>
        public List<string> KnownTags = new();

        public string KnownProse = "";

        /// <summary>点开过但一次都没编辑、也没记过状态——淘汰时优先丢它。</summary>
        public bool Idle =>
            History.UndoCount == 0 && History.RedoCount == 0 && !HasKnown;
    }

    private readonly Dictionary<string, Entry> _buckets = new(StringComparer.Ordinal);

    /// <summary>最近使用顺序，队尾 = 最近。淘汰时从队首找。</summary>
    private readonly List<string> _lru = new();

    private int _maxSteps = PromptHistory.DefaultMaxSteps;
    private int _maxDocuments = DefaultMaxDocuments;

    /// <summary>
    /// 每份文档的最大撤销步数。改小会立刻把**所有**已有的桶裁到新上限，
    /// 改大不会凭空补出历史。
    /// </summary>
    public int MaxSteps
    {
        get => _maxSteps;
        set
        {
            _maxSteps = Math.Clamp(value, 0, PromptHistory.MaxStepsLimit);
            foreach (var e in _buckets.Values) e.History.MaxSteps = _maxSteps;
        }
    }

    /// <summary>最多保留多少份文档的历史。设小会立刻淘汰最久未用的那些。</summary>
    public int MaxDocuments
    {
        get => _maxDocuments;
        set
        {
            _maxDocuments = Math.Max(1, value);
            Trim();
        }
    }

    /// <summary>当前保留着历史的文档份数（日志与测试用）。</summary>
    public int DocumentCount => _buckets.Count;

    /// <summary>
    /// 一份**不入桶**的空栈，用于"当前没有文档"（图片列表为空、数据集刚清空）。
    /// 每次返回新实例：它会被当成当前栈使用，共享一个实例迟早互相污染。
    /// </summary>
    public PromptHistory NewDetached()
    {
        var h = new PromptHistory();
        h.MaxSteps = _maxSteps;
        return h;
    }

    /// <summary>
    /// 打开一份文档：返回它那一桶的撤销栈（没有就新建）。
    ///
    /// <paramref name="tags"/> / <paramref name="prose"/> 是**刚从盘上 / 库里读出来**
    /// 的当前内容，用来校验旧历史是否还有效；对不上就丢掉旧桶、从空历史开始。
    /// </summary>
    public PromptHistory Attach(string? id, IReadOnlyList<string> tags, string prose)
    {
        if (string.IsNullOrEmpty(id)) return NewDetached();

        if (!_buckets.TryGetValue(id, out var entry))
        {
            entry = new Entry();
            entry.History.MaxSteps = _maxSteps;
            _buckets[id] = entry;
        }
        else if (entry.HasKnown && !SameAsKnown(entry, tags, prose))
        {
            // 内容与上次离开时对不上 = 被外部改过。旧撤销点指向的是已经不存在
            // 的内容，留着会让 Ctrl+Z 把外部改动退掉，而离开时的自动保存
            // 又会把它写死到盘上。所以丢掉历史，但**保留桶**（它还是这份文档的桶）。
            //
            // 记日志是必要的：不然用户只会看到"撤销怎么没了"，无从判断是
            // 外部改了文件，还是程序把它弄丢了。
            Log.Write($"history dropped (content changed outside): {id} " +
                      $"known={entry.KnownTags.Count}tags now={tags.Count}tags");
            Forget(entry);
        }

        Touch(id);
        Trim();
        return entry.History;
    }

    /// <summary>
    /// 记下"离开这份文档时它是什么内容"，供下次 <see cref="Attach"/> 校验。
    ///
    /// <para>**只需要记离开这一刻**，因为盘上 / 库里的内容正是这一刻写下去的，
    /// 所以它就是唯一正确的基线；在编辑过程中反复记同一件事没有额外价值。</para>
    ///
    /// <para>没有桶的文档直接忽略：一次都没编辑过 = 没有历史要守，
    /// 不该因为"点开看了一眼"就占一个桶（那会让浏览一遍数据集就把真正编辑过的
    /// 实例挤掉）。</para>
    /// </summary>
    public void Remember(string? id, IReadOnlyList<string> tags, string prose)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (!_buckets.TryGetValue(id, out var entry)) return;

        entry.HasKnown = true;
        entry.KnownTags = new List<string>(tags);
        entry.KnownProse = prose ?? "";

        Touch(id);
        Trim();
    }

    /// <summary>
    /// 把一份历史搬到另一个身份上。
    ///
    /// <para>必须有这一步，因为**新草稿共用一个身份**（<see cref="DraftDoc.NewIdentity"/>）：
    /// 一份新草稿在首次入库拿到 id 之前，历史都记在那个共用键底下。不搬的话，
    /// 那份草稿从库里重新打开时历史凭空消失，而**下一份全新草稿会继承它的撤销栈**
    /// ——按 Ctrl+Z 会把上一份草稿的标签退进来。</para>
    /// </summary>
    public void Migrate(string from, string to)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return;
        if (string.Equals(from, to, StringComparison.Ordinal)) return;
        if (!_buckets.TryGetValue(from, out var entry)) return;

        // 目标已有桶时以目标为准（来源是刚入库的那一份，重新打开时还会再建），
        // 绝不覆盖掉一个已经存在、可能带着真实历史的桶。
        Drop(from);
        if (_buckets.ContainsKey(to)) { Touch(to); return; }

        _buckets[to] = entry;
        Touch(to);
        Trim();
        Log.Write($"history moved: {from} -> {to}");
    }

    /// <summary>丢掉一份文档的历史（库条目被删除、身份失效时用）。</summary>
    public void Drop(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _buckets.Remove(id);
        _lru.Remove(id);
    }

    /// <summary>清空全部历史（换了一个训练集时用）。</summary>
    public void Clear()
    {
        // 连每个桶里的栈一起清：调用方（MainWindow）手上还握着当前那一桶的引用
        // （_history），只把字典清掉的话那个引用会留着一份没人管的历史，
        // 「撤销（添加标签）」这类菜单文案就停在旧状态上了。
        foreach (var e in _buckets.Values) Forget(e);
        _buckets.Clear();
        _lru.Clear();
    }

    /// <summary>丢掉一个桶里的历史，但保留桶本身（走的是 <see cref="PromptHistory.Clear"/>）。</summary>
    private static void Forget(Entry e)
    {
        e.History.Clear();
        e.HasKnown = false;
        e.KnownTags = new List<string>();
        e.KnownProse = "";
    }

    private void Touch(string id)
    {
        _lru.Remove(id);
        _lru.Add(id);
    }

    private void Trim()
    {
        while (_buckets.Count > _maxDocuments && _lru.Count > 0)
        {
            // 优先丢"点开过但从未编辑"的桶：浏览一遍数据集（或来回翻图）不该
            // 把早先真正编辑过的那几张的历史挤出去。找不到这种才按最久未用丢。
            string? victim = null;
            foreach (var k in _lru)
            {
                if (_buckets.TryGetValue(k, out var e) && e.Idle) { victim = k; break; }
            }

            Drop(victim ?? _lru[0]);
        }
    }

    private static bool SameAsKnown(Entry e, IReadOnlyList<string> tags, string prose)
    {
        if (e.KnownTags.Count != tags.Count) return false;
        for (var i = 0; i < tags.Count; i++)
        {
            if (!string.Equals(e.KnownTags[i], tags[i], StringComparison.Ordinal)) return false;
        }

        // 散文尾巴按 Trim 比较：草稿入库时正文会 Trim，重新解析回来可能差一个
        // 尾随空白——那不是"被外部改过"，不该因此把历史丢掉。
        return string.Equals(e.KnownProse.Trim(), (prose ?? "").Trim(), StringComparison.Ordinal);
    }
}
