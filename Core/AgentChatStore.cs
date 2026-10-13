using System.Text;
using System.Text.Json;

namespace AnimaCaptioner.Core;

/// <summary>
/// AI 助手对话的持久化。
///
/// **为什么独立一个文件**（而不是塞进 settings.json）：对话是随使用增长的数据，
/// 不是配置；而 <see cref="AppSettings.Save"/> 是整份内存对象覆盖写，
/// 把对话塞进去意味着每问一句就重写一遍全部设置。仿 <see cref="PromptLibraryStore"/>
/// 独立存放。
///
/// **只在用户点「清空对话」时才清**。早先的实现在换文档时整段丢掉，理由是
/// "历史里带着上一份文档的画面，跨文档继续问会让模型把 A 图的描述说到 B 图上"。
/// 那个顾虑是真的，但代价太大：切一下图片、甚至只是打开设置页回来，刚看到一半的
/// 回答和思考过程就没了。现在改成**不清空、而是在换图时插一条边界说明**
/// （<see cref="Kind.Note"/>）——连续性保住了，模型也仍然知道画面换了。
///
/// 思考内容与工具调用记录一并存下来，但**只用于显示**：它们永远不会被送进模型
/// （见 <see cref="ToModelHistory"/>），也永远不写进提示词。翻译那条路已经明确
/// 不许把思考当译文，这里是同一个道理。
/// </summary>
public sealed class AgentChatStore
{
    /// <summary>一条记录的来源。</summary>
    public enum Kind
    {
        /// <summary>用户说的话。</summary>
        User,
        /// <summary>模型的回复。</summary>
        Assistant,
        /// <summary>程序插的说明（调用失败等）。不作为任何一侧的发言显示。</summary>
        Note,
    }

    /// <summary>一条对话记录。字段都是公开可读写的 POCO，便于直接序列化。</summary>
    public sealed class Entry
    {
        /// <summary>"user" / "assistant" / "note"。存字符串是为了以后加种类时旧文件仍能读。</summary>
        public string Kind { get; set; } = "user";

        /// <summary>正文。送进模型的就是这一项。</summary>
        public string Text { get; set; } = "";

        /// <summary>思考内容。**只显示、不送模型**。</summary>
        public string Reasoning { get; set; } = "";

        /// <summary>工具调用记录。**只显示、不送模型**。</summary>
        public List<string> Log { get; set; } = new();

        /// <summary>写入时间（UTC，ISO 8601），仅作排查线索。</summary>
        public string At { get; set; } = "";

        public Kind KindOf() => Parse(Kind);
    }

    /// <summary>
    /// 保留的最大条数。超过就丢最旧的。
    ///
    /// 上限不是为了省磁盘，而是**必须**的：送给模型的上下文随轮次线性增长，
    /// 无上限总有一天会撞上模型的上下文窗口，那时表现为"突然每次请求都失败"，
    /// 而原因早就埋在几百轮之前。
    /// </summary>
    public const int MaxTurns = 200;

    /// <summary>单条思考内容的存储上限。显示层也有自己的上限，这里是防文件膨胀。</summary>
    public const int MaxReasoning = 20000;

    private readonly List<Entry> _items = new();
    private readonly object _lock = new();

    private string _path = FilePath;

    public static string FilePath => Path.Combine(AppSettings.Dir, "agent.json");

    /// <summary>本实例实际使用的位置。测试传临时文件，绝不碰用户真实的对话。</summary>
    public string StorePath => _path;

    public int Count { get { lock (_lock) return _items.Count; } }

    /// <summary>是否被上限裁剪过。界面据此提示"更早的对话已滚出"。</summary>
    public bool Trimmed { get; private set; }

    public static AgentChatStore Load() => Load(FilePath);

    /// <summary>从指定路径载入。测试传临时文件。</summary>
    public static AgentChatStore Load(string path)
    {
        var s = new AgentChatStore { _path = path };
        try
        {
            if (!File.Exists(path)) return s;
            var json = File.ReadAllText(path, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<List<Entry>>(json);
            if (data is null) return s;
            foreach (var e in data)
            {
                if (e is null) continue;
                e.Text = (e.Text ?? "").Trim();
                // 空正文的记录没有意义；但 assistant 的空回复在失败路径上可能出现，
                // 一并丢掉（它没有信息量，留着只会在界面里多一个空气泡）。
                if (e.Text.Length == 0) continue;
                if (e.Reasoning.Length > MaxReasoning) e.Reasoning = e.Reasoning[..MaxReasoning];
                e.Log ??= new List<string>();
                s._items.Add(e);
            }
            s.Trim();
            Log.Write($"agent chat loaded: {s._items.Count} entries from {path}");
        }
        catch (Exception ex)
        {
            // 文件坏了不该让程序起不来：丢掉重建，代价只是丢掉对话。
            Log.Write("agent chat load failed (will rebuild): " + ex.Message);
        }
        return s;
    }

    public void Save()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            List<Entry> copy;
            lock (_lock) copy = new List<Entry>(_items);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(copy,
                new JsonSerializerOptions { WriteIndented = false }), new UTF8Encoding(false));
            // 先写临时文件再替换：写一半崩掉不会留下一份半截对话。
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // 存不下不该打断对话。
            Log.Write("agent chat save failed: " + ex.Message);
        }
    }

    /// <summary>追加一条并落盘。</summary>
    public void Add(Kind kind, string text, string reasoning = "",
                    IEnumerable<string>? log = null)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;

        reasoning = (reasoning ?? "").Trim();
        if (reasoning.Length > MaxReasoning) reasoning = reasoning[..MaxReasoning];

        var e = new Entry
        {
            Kind = kind switch
            {
                Kind.Assistant => "assistant",
                Kind.Note => "note",
                _ => "user",
            },
            Text = text,
            Reasoning = reasoning,
            Log = log?.ToList() ?? new List<string>(),
            At = DateTime.UtcNow.ToString("o"),
        };

        lock (_lock)
        {
            _items.Add(e);
            TrimLocked();
        }
        Save();
    }

    /// <summary>用户按了「清空对话」——这是**唯一**会清空的地方。</summary>
    public void Clear()
    {
        lock (_lock) _items.Clear();
        Trimmed = false;
        Save();
    }

    /// <summary>只读快照，按时间正序。</summary>
    public List<Entry> Snapshot()
    {
        lock (_lock) return new List<Entry>(_items);
    }

    public bool IsEmpty { get { lock (_lock) return _items.Count == 0; } }

    /// <summary>最后一条记录的来源；没有记录时返回 null。</summary>
    public Kind? LastKind
    {
        get
        {
            lock (_lock) return _items.Count == 0 ? null : _items[^1].KindOf();
        }
    }

    /// <summary>
    /// 送给模型的历史。**思考与工具记录不在其中**（它们只给用户看），
    /// 说明条目（<see cref="Kind.Note"/>）折成 user 消息——它本来就是"告诉模型
    /// 情况变了"，而且**相邻的 user 会合并成一条**，免得出现连续两条 user
    /// 消息：有些服务端对角色交替比对得比较严。
    /// </summary>
    public List<AgentTurn> ToModelHistory()
    {
        var outp = new List<AgentTurn>();
        lock (_lock)
        {
            foreach (var e in _items)
            {
                var fromUser = e.KindOf() != Kind.Assistant;
                if (fromUser && outp.Count > 0 && outp[^1].FromUser)
                    outp[^1] = outp[^1] with { Text = outp[^1].Text + "\n" + e.Text };
                else
                    outp.Add(new AgentTurn(fromUser, e.Text));
            }
        }
        return outp;
    }

    private void Trim()
    {
        lock (_lock) TrimLocked();
    }

    private void TrimLocked()
    {
        if (_items.Count <= MaxTurns) return;
        var drop = _items.Count - MaxTurns;
        _items.RemoveRange(0, drop);
        Trimmed = true;
        Log.Write($"agent chat trimmed: dropped {drop} oldest entries (cap {MaxTurns})");
    }

    private static Kind Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "assistant" => Kind.Assistant,
        "note" => Kind.Note,
        _ => Kind.User,
    };
}
