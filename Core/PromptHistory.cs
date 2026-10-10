namespace AnimaCaptioner.Core;

/// <summary>
/// 一个撤销点：某一刻的**整份文档状态**。
///
/// 存的是状态而不是"反向操作"，因为这个文档的状态空间很小（一串标签 + 一段散文），
/// 而操作种类很多（加/删/移/改名/粘贴/规范/重排/翻译/清空/拖拽）。
/// 反向操作要为每一种各写一份逆运算，而且粘贴、规范化这类会**批量**改动，
/// 逆运算还要记住每个标签原来的下标——状态快照没有这些负担。
/// </summary>
public sealed class PromptSnapshot
{
    public PromptSnapshot(IReadOnlyList<string> tags, string proseTail, string label)
    {
        // 必须复制：调用方传进来的就是它自己正在用的那个列表，
        // 之后会被原地改写（_pendingTags[i] = newTag / Add / AddRange）。
        // 不复制的话撤销点会跟着一起变，撤销就撤销到"现在"了。
        Tags = new List<string>(tags);
        ProseTail = proseTail ?? "";
        Label = label ?? "";
    }

    /// <summary>标签序列。存的是**字面量**（不带转义反斜杠），和 _pendingTags 同一口径。</summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>英文自然语言那段（含前面的 ". " 分隔符），没有则为空串。</summary>
    public string ProseTail { get; }

    /// <summary>产生这个撤销点的操作名，用于「已撤销：添加标签」这类提示。</summary>
    public string Label { get; }

    /// <summary>状态是否与另一份相同。用于挡掉"没改动却记了一个撤销点"。</summary>
    public bool SameAs(IReadOnlyList<string> tags, string proseTail)
    {
        if (Tags.Count != tags.Count) return false;
        for (var i = 0; i < Tags.Count; i++)
            if (!string.Equals(Tags[i], tags[i], StringComparison.Ordinal)) return false;
        return string.Equals(ProseTail, proseTail ?? "", StringComparison.Ordinal);
    }
}

/// <summary>
/// 标签编辑的撤销 / 重做栈。
///
/// <para>**用法是"操作前记一笔"**：每个会改动文档的命令在真正动手之前调
/// <see cref="Push"/> 把当前状态存下来。于是撤销就是把上一个状态拿回来，
/// 重做就是把当前状态再推回去——两边共用同一个栈结构，没有单独的逆运算。</para>
///
/// <para>**只改内存，绝不落盘**。撤销本身不写 .txt、不写提示词库：它只把界面
/// 状态退回去，落盘仍旧由保存那条路负责（标注模式的 Ctrl+S / 写标签模式的入库）。
/// 这是刻意的——否则撤销一次就会静默改掉训练集里的一个 caption。</para>
///
/// <para>纯逻辑、不碰 WinUI，所以 net8 的 corecheck 能直接编译真实源码来测。</para>
/// </summary>
public sealed class PromptHistory
{
    /// <summary>默认步数。100 步够覆盖"改错了想退回去"的实际需要，内存代价可忽略。</summary>
    public const int DefaultMaxSteps = 100;

    /// <summary>步数上限。再大没有实际意义，只会让设置里能填进一个荒唐的值。</summary>
    public const int MaxStepsLimit = 1000;

    private readonly List<PromptSnapshot> _undo = new();
    private readonly List<PromptSnapshot> _redo = new();
    private int _max = DefaultMaxSteps;

    /// <summary>
    /// 最大撤销步数。0 表示关闭撤销（Push 不再记任何东西，菜单里两项恒灰）。
    /// 调小时会立刻裁掉超出的旧撤销点；调大不会凭空补出历史。
    /// </summary>
    public int MaxSteps
    {
        get => _max;
        set
        {
            _max = Math.Clamp(value, 0, MaxStepsLimit);
            Trim();
        }
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>栈里现有的撤销点数（测试与日志用）。</summary>
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    /// <summary>下一次撤销会退掉的那个操作名；没有则为空串。</summary>
    public string UndoLabel => _undo.Count > 0 ? _undo[^1].Label : "";

    /// <summary>下一次重做会重放的那个操作名；没有则为空串。</summary>
    public string RedoLabel => _redo.Count > 0 ? _redo[^1].Label : "";

    /// <summary>
    /// 记一个撤销点。**在改动文档之前调**，传入的是"改动前"的状态。
    ///
    /// 返回是否真的记下了：步数为 0、或状态与上一个撤销点完全相同（说明这个操作
    /// 其实没改动任何东西）时返回 false，调用方据此不必刷新菜单。
    /// </summary>
    public bool Push(IReadOnlyList<string> tags, string proseTail, string label)
    {
        if (_max <= 0) return false;

        // 没改动就不记：否则撤销一次看起来"什么都没发生"，用户会以为功能坏了。
        // （正常情况下各命令都在自己的提前返回之后才 Push，这里是兜底。）
        if (_undo.Count > 0 && _undo[^1].SameAs(tags, proseTail)) return false;

        _undo.Add(new PromptSnapshot(tags, proseTail, label));
        Trim();

        // 新操作一旦发生，原来那条"未来"就作废了——这是撤销栈的常规语义，
        // 不这样的话重做会重放出一条与当前状态对不上的历史。
        _redo.Clear();
        return true;
    }

    /// <summary>
    /// 撤销。传入**当前**状态（它会被推到重做栈上），返回要恢复到的那个状态；
    /// 没有可撤销的则返回 null。
    /// </summary>
    public PromptSnapshot? Undo(IReadOnlyList<string> tags, string proseTail)
    {
        if (_undo.Count == 0) return null;

        var snap = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        // 标签沿用被撤销的那一步：撤销完再重做，提示语两边说的是同一件事。
        _redo.Add(new PromptSnapshot(tags, proseTail, snap.Label));
        return snap;
    }

    /// <summary>重做。语义与 <see cref="Undo"/> 对称。</summary>
    public PromptSnapshot? Redo(IReadOnlyList<string> tags, string proseTail)
    {
        if (_redo.Count == 0) return null;

        var snap = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        _undo.Add(new PromptSnapshot(tags, proseTail, snap.Label));
        Trim();
        return snap;
    }

    /// <summary>
    /// 清空两个栈。**换文档时必须调**：撤销点里存的是"某张图的标签"，
    /// 而撤销只改内存不写盘，跨文档撤销会让上一张图的标签出现在当前文档里，
    /// 用户接着一保存就写错了文件。
    /// </summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private void Trim()
    {
        while (_undo.Count > _max) _undo.RemoveAt(0);
        // 重做栈不按步数裁：它的深度天然不超过撤销栈曾经到过的深度。
    }
}
