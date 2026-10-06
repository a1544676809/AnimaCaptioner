using System.Text;
using System.Text.Json;

namespace AnimaCaptioner.Core;

/// <summary>
/// 模型翻译的本地缓存 + 每条译文"是否官方"的判定。
///
/// 存在的理由有两个：
///
/// 1. **词库查不到不等于没有中文**。实测你的 53 个 caption 里，扣掉控制词
///    还有约 93 个不同标签（147 个实例）在词库里没有中文——大多是 Anima 原生
///    标签（`cat smile`、`ass grab`）和颜色+名词的复合词（`white back bow`）。
///    这些正是需要模型补的地方。
/// 2. **模型译出来的东西绝不能和官方释义混在一起**。词库的中文来自 Danbooru
///    对照表，是"官方"的；模型译的是推断，可能错。UI 上必须一眼可分，
///    所以这里的每条记录都带 <see cref="GlossEntry.Source"/>，且不覆盖词库值。
///
/// 存成独立 sidecar 文件而不是塞进 settings.json：它是会随使用增长的数据，
/// 不是配置；而且清掉它不该影响任何设置。
/// </summary>
public sealed class ModelGlossStore
{
    /// <summary>译文来源。UI 与导出都靠它区分可信度。</summary>
    public const string SourceModel = "model";

    public sealed class GlossEntry
    {
        public string Cn { get; set; } = "";
        public string Source { get; set; } = SourceModel;

        /// <summary>产生这条译文的模型名，便于判断是不是换了小模型后该重译。</summary>
        public string Model { get; set; } = "";

        /// <summary>产生时间（UTC），便于排序和排查。</summary>
        public string At { get; set; } = "";

        /// <summary>是否据本次查询联网搜过。UI 可以据此说明"这条查过资料"。</summary>
        public bool Searched { get; set; }
    }

    private readonly Dictionary<string, GlossEntry> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _map.Count; } }

    public static string FilePath => Path.Combine(AppSettings.Dir, "model-gloss.json");

    public static ModelGlossStore Load()
    {
        var s = new ModelGlossStore();
        try
        {
            if (!File.Exists(FilePath)) return s;
            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<Dictionary<string, GlossEntry>>(json);
            if (data is null) return s;
            foreach (var kv in data)
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value?.Cn))
                    s._map[kv.Key] = kv.Value!;
            Log.Write($"model gloss loaded: {s._map.Count} entries from {FilePath}");
        }
        catch (Exception ex)
        {
            // 缓存坏了不该让程序起不来：丢掉重建即可，代价只是重新翻译。
            Log.Write("model gloss load failed (will rebuild): " + ex.Message);
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            Dictionary<string, GlossEntry> copy;
            lock (_lock) copy = new Dictionary<string, GlossEntry>(_map, StringComparer.OrdinalIgnoreCase);

            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp,
                JsonSerializer.Serialize(copy, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }),
                new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write("model gloss save FAILED: " + ex.Message);
        }
    }

    public bool TryGet(string tag, out GlossEntry entry)
    {
        lock (_lock) return _map.TryGetValue(Norm(tag), out entry!);
    }

    /// <summary>写入一条模型译文。空译文或 UNKNOWN 一律不写——弃权就是弃权，不留半条记录。</summary>
    public void Put(string tag, string cn, string model, bool searched)
    {
        cn = (cn ?? "").Trim();
        if (cn.Length == 0 || IsUnknown(cn)) return;

        lock (_lock)
            _map[Norm(tag)] = new GlossEntry
            {
                Cn = cn,
                Source = SourceModel,
                Model = model ?? "",
                At = DateTime.UtcNow.ToString("o"),
                Searched = searched,
            };
    }

    public void Remove(string tag)
    {
        lock (_lock) _map.Remove(Norm(tag));
    }

    public void Clear()
    {
        lock (_lock) _map.Clear();
    }

    /// <summary>模型按提示词要求弃权时返回的哨兵值。</summary>
    public static bool IsUnknown(string s) =>
        s.Trim().Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase) ||
        s.Trim().Equals("未知", StringComparison.Ordinal) ||
        s.Trim().Equals("不确定", StringComparison.Ordinal);

    /// <summary>
    /// 清掉模型返回值里的包装。8B 有时会无视"只输出译文"，
    /// 写成「译文：xxx」、加引号、或附一句解释——这些都不该进词库。
    /// </summary>
    public static string Clean(string raw)
    {
        var s = (raw ?? "").Trim();

        // 去掉可能的 markdown 强调与成对引号
        s = s.Trim('*', '`', '"', '\'', '「', '」', '“', '”', '《', '》').Trim();

        // 去掉"译文："这类前缀
        foreach (var p in new[] { "译文：", "译文:", "翻译：", "翻译:", "中文：", "中文:",
                                  "Translation:", "translation:", "Chinese:" })
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            { s = s[p.Length..].Trim(); break; }

        // 只取第一行：附带的解释通常另起一行
        var nl = s.IndexOfAny(new[] { '\n', '\r' });
        if (nl > 0) s = s[..nl].Trim();

        // 仍然过长的一律当弃权：这一栏是短注释，长句必是跑偏了
        if (s.Length > 40) return "UNKNOWN";

        return s;
    }

    private static string Norm(string tag) => (tag ?? "").Trim().ToLowerInvariant();
}
