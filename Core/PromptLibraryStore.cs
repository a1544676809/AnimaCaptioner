using System.Text;
using System.Text.Json;

namespace AnimaCaptioner.Core;

/// <summary>
/// 写标签模式的历史与预设库。
///
/// **为什么独立一个文件**（而不是塞进 settings.json）：这是会随使用增长的数据，
/// 不是配置。而 <see cref="AppSettings.Save"/> 是**整份内存对象覆盖写**——
/// 把历史塞进去意味着每复制一次提示词就重写一遍全部设置，条目长大后开销和
/// 冲突面都会放大。仿 <see cref="ModelGlossStore"/> 的做法独立存放。
///
/// 历史与预设**同库存放、用 <see cref="Entry.Pinned"/> 区分**，而不是两个文件：
/// 两者是同一种东西（一段提示词正文），只是保留策略不同。分开存会让"把历史里的
/// 一条钉成预设"变成跨文件的搬运，而那条路径正是最容易出现"两边都有/都没有"的。
///
/// 这个文件不引用任何 WinUI 类型，所以能被 net10 回归程序直接编译。
/// </summary>
public sealed class PromptLibraryStore
{
    /// <summary>
    /// 历史条目上限。超过就丢掉最旧的**未钉住**条目。
    ///
    /// 有上限是必须的：这一栏是"每次现场组装"，用久了会积累成百上千条，
    /// 而它每次都要整体读写一个 JSON。预设（Pinned）永不被这个上限清理——
    /// 用户明确标记过要留下的东西不该被自动丢掉。
    /// </summary>
    public const int MaxHistory = 300;

    public sealed class Entry
    {
        /// <summary>稳定 id，用于更新与选中。空 id 由 <see cref="Add"/> 生成。</summary>
        public string Id { get; set; } = "";

        /// <summary>显示标题。用户没命名时由正文派生（见 <see cref="DeriveTitle"/>）。</summary>
        public string Title { get; set; } = "";

        /// <summary>提示词正文（整行，含散文尾巴）。</summary>
        public string Body { get; set; } = "";

        /// <summary>最后写入时间（UTC，ISO 8601）。列表按它倒序。</summary>
        public string At { get; set; } = "";

        /// <summary>true = 预设（用户钉住，永不被历史上限清掉）；false = 历史。</summary>
        public bool Pinned { get; set; }

        /// <summary>用了多少次。常用于判断哪几条是真常用的。</summary>
        public int Uses { get; set; }
    }

    private readonly List<Entry> _items = new();
    private readonly object _lock = new();

    /// <summary>
    /// 本实例落盘用的路径。默认是 <see cref="FilePath"/>，可被测试替换成临时文件
    /// ——否则测试会去读写用户真实的提示词库。
    /// </summary>
    private string _path = FilePath;

    public static string FilePath => Path.Combine(AppSettings.Dir, "prompts.json");

    /// <summary>本实例实际使用的路径。名字刻意不叫 Path，避免与静态的
    /// <see cref="FilePath"/> 在静态上下文里混淆（编译器会报 CS0120）。</summary>
    public string StorePath => _path;

    public int Count { get { lock (_lock) return _items.Count; } }

    public int PinnedCount { get { lock (_lock) return _items.Count(e => e.Pinned); } }

    /// <summary>按最近使用排序的快照。预设与历史混排，但预设优先显示。</summary>
    public List<Entry> Snapshot()
    {
        lock (_lock)
            return _items
                .OrderByDescending(e => e.Pinned)
                .ThenByDescending(e => e.At, StringComparer.Ordinal)
                .ToList();
    }

    public Entry? Get(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_lock) return _items.FirstOrDefault(e => e.Id == id);
    }

    public static PromptLibraryStore Load() => Load(FilePath);

    /// <summary>从指定路径载入。测试传临时文件，绝不碰用户真实的库。</summary>
    public static PromptLibraryStore Load(string path)
    {
        var s = new PromptLibraryStore { _path = path };
        try
        {
            if (!File.Exists(path)) return s;
            var json = File.ReadAllText(path, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<List<Entry>>(json);
            if (data is null) return s;
            foreach (var e in data)
            {
                if (e is null) continue;
                e.Body = (e.Body ?? "").Trim();
                if (e.Body.Length == 0) continue;
                if (string.IsNullOrEmpty(e.Id)) e.Id = NewId();
                if (string.IsNullOrEmpty(e.Title)) e.Title = DeriveTitle(e.Body);
                s._items.Add(e);
            }
            Log.Write($"prompt library loaded: {s._items.Count} entries " +
                      $"({s.PinnedCount} pinned) from {path}");
        }
        catch (Exception ex)
        {
            // 库坏了不该让程序起不来：丢掉重建，代价只是丢掉历史。
            Log.Write("prompt library load failed (will rebuild): " + ex.Message);
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
            File.WriteAllText(tmp,
                JsonSerializer.Serialize(copy, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }),
                new UTF8Encoding(false));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write("prompt library save FAILED: " + ex.Message);
        }
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// 插一条（或按正文去重后更新已有那条并顶到最前）。返回落库的条目。
    ///
    /// 按**正文**去重而不是标题：标题可能重复（两段提示词都以 1girl 开头），
    /// 而正文相同就是同一条。否则"每次复制都加一条"会把历史刷成同一个内容的
    /// 几十份副本，反而找不到别的东西。
    /// </summary>
    public Entry Add(string body, bool pinned = false, string? title = null)
    {
        body = (body ?? "").Trim();
        lock (_lock)
        {
            var existing = _items.FirstOrDefault(
                e => string.Equals(e.Body, body, StringComparison.Ordinal));

            if (existing is not null)
            {
                existing.At = Now();
                existing.Uses++;
                if (pinned) existing.Pinned = true;
                if (!string.IsNullOrWhiteSpace(title)) existing.Title = title!.Trim();
                return existing;
            }

            var e = new Entry
            {
                Id = NewId(),
                Title = string.IsNullOrWhiteSpace(title) ? DeriveTitle(body) : title!.Trim(),
                Body = body,
                At = Now(),
                Pinned = pinned,
                Uses = 1,
            };
            _items.Add(e);
            Prune();
            return e;
        }
    }

    /// <summary>更新一条的正文/标题，并顶到最前。已钉住的保持钉住。</summary>
    public Entry? Update(string id, string body, string? title = null)
    {
        body = (body ?? "").Trim();
        lock (_lock)
        {
            var e = _items.FirstOrDefault(x => x.Id == id);
            if (e is null) return null;
            e.Body = body;
            if (!string.IsNullOrWhiteSpace(title)) e.Title = title!.Trim();
            else if (e.Title.Length == 0) e.Title = DeriveTitle(body);
            e.At = Now();
            return e;
        }
    }

    public bool SetPinned(string id, bool pinned)
    {
        lock (_lock)
        {
            var e = _items.FirstOrDefault(x => x.Id == id);
            if (e is null) return false;
            e.Pinned = pinned;
            // 刻意**不改标题**：用户可能已经给这条起过名字（重命名），
            // 用派生标题覆盖它等于把用户起的名字静默丢掉。
            // 只有标题还空着时才补一个派生的。
            if (e.Title.Length == 0) e.Title = DeriveTitle(e.Body);
            return true;
        }
    }

    public bool Remove(string id)
    {
        lock (_lock) return _items.RemoveAll(e => e.Id == id) > 0;
    }

    /// <summary>清掉历史但**保留预设**。菜单上"清空历史"用这个语义。</summary>
    public int ClearHistory()
    {
        lock (_lock) return _items.RemoveAll(e => !e.Pinned);
    }

    public void Clear()
    {
        lock (_lock) _items.Clear();
    }

    /// <summary>标题或正文包含关键词。空关键词返回全部。</summary>
    public List<Entry> Search(string? q)
    {
        var k = (q ?? "").Trim();
        if (k.Length == 0) return Snapshot();
        lock (_lock)
            return _items
                .Where(e => e.Title.Contains(k, StringComparison.OrdinalIgnoreCase)
                         || e.Body.Contains(k, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Pinned)
                .ThenByDescending(e => e.At, StringComparer.Ordinal)
                .ToList();
    }

    /// <summary>
    /// 超出上限时丢掉最旧的未钉住条目。在锁内调用。
    /// 先丢未钉住的，再按时间从旧到新——预设永远不参与淘汰。
    /// </summary>
    private void Prune()
    {
        var over = _items.Count - MaxHistory;
        if (over <= 0) return;

        var victims = _items
            .Where(e => !e.Pinned)
            .OrderBy(e => e.At, StringComparer.Ordinal)
            .Take(over)
            .ToList();

        foreach (var v in victims) _items.Remove(v);
        if (victims.Count > 0) Log.Write($"prompt library pruned: {victims.Count} oldest history entries");
    }

    private static string Now() => DateTime.UtcNow.ToString("o");

    /// <summary>
    /// 从正文派生一个短标题。
    ///
    /// 取前几个**标签**（逗号分隔）而不是开头的字符数：提示词常以
    /// `masterpiece, best quality, score_7, safe` 这类通用前缀开头，按字符截
    /// 会让一堆条目的标题长得一模一样，列表就不具备辨识度了。所以跳过
    /// 已知的头部控制词，从第一个"有信息量"的标签开始取。
    /// </summary>
    public static string DeriveTitle(string? body, int maxTags = 3, int maxLen = 48)
    {
        var text = (body ?? "").Trim();
        if (text.Length == 0) return "(空)";

        // 标签区：遇到句边界就停（后面是散文）
        var head = text;
        foreach (var mark in new[] { ". ", "! ", "? " })
        {
            var i = head.IndexOf(mark, StringComparison.Ordinal);
            if (i > 0) { head = head[..i]; break; }
        }

        var tags = head.Split(',', StringSplitOptions.RemoveEmptyEntries)
                       // 标题是给人看的：正文可能带着生图用的括号转义（`210 \(reverse:1999\)`），
                       // 不还原的话标题里会挂着反斜杠。
                       .Select(t => TagEscape.Unescape(t.Trim()))
                       .Where(t => t.Length > 0)
                       .ToList();

        // 跳过头部的通用控制词，它们不提供辨识度
        var start = 0;
        while (start < tags.Count && IsHeadNoise(tags[start])) start++;

        var picked = tags.Skip(start).Take(maxTags).ToList();
        if (picked.Count == 0) picked = tags.Take(maxTags).ToList();
        if (picked.Count == 0)
        {
            // 整段都是散文：取头几个词
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6);
            var s = string.Join(' ', words);
            return s.Length <= maxLen ? s : s[..(maxLen - 1)] + "…";
        }

        var title = string.Join(", ", picked);
        if (start + picked.Count < tags.Count) title += " …";
        return title.Length <= maxLen ? title : title[..(maxLen - 1)] + "…";
    }

    /// <summary>
    /// 标题派生时要跳过的**样板词**：质量 / 元信息 / 年代 / 安全四类。
    ///
    /// 刻意**不含数量段**（`1girl`、`solo`、`2boys`）：那几类是所有提示词共用的
    /// 样板前缀（`masterpiece, best quality, score_7, safe`），留着毫无辨识度；
    /// 而数量词描述的是画面主体，正是最该出现在标题里的信息。
    /// 用 TryHeadSection 取段名而不是"属于任一头段"——后者会把 1girl 也跳掉，
    /// 于是一串只有控制词的提示词会派生出 `masterpiece, best quality, …` 这种
    /// 彼此完全一样的标题。
    /// </summary>
    private static bool IsHeadNoise(string tag)
    {
        if (!TagSections.TryHeadSection(tag, out var section, out _)) return false;
        return section is TagSections.Quality or TagSections.Meta
                      or TagSections.Period or TagSections.Safety;
    }
}
