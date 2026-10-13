using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AnimaCaptioner.Core;

/// <summary>一次构建的结果。数值直接来自刚建好的库，不是估算。</summary>
public sealed record VocabBuildResult(
    string DbPath,
    long DbBytes,
    int DistinctTags,
    int WithChinese,
    int AnimaIndex,
    int InAnima,
    int AliasCount,
    int Deprecated,
    int Banned,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed)
{
    public double ChinesePercent => DistinctTags == 0 ? 0 : WithChinese * 100.0 / DistinctTags;
}

/// <summary>
/// 从上游素材构建 <c>vocab.sqlite</c>。
///
/// 这是原先两个 Python 脚本（<c>build_vocab.py</c> + <c>add_anima_index.py</c>）的合并移植。
/// 两者必须一起跑：<c>build_vocab.py</c> 只产出 <c>tags</c>/<c>best</c>/<c>alias</c>，
/// 而 <see cref="VocabDb"/> 判定"这个标签合法吗"靠的是 <c>anima_index</c> 和
/// <c>best.in_anima</c>——那两张表由第二步补上。分开两次跑的结果是：词库看着建好了，
/// 但 Anima 独有的 12,125 个标签全查不到（<see cref="VocabDb"/> 里那句
/// "anima_index 缺失（旧版词库？）"的日志就是当年踩过的坑）。所以这里合成了一个过程，
/// 不存在"只跑了第一步"这种状态。
///
/// 关键语义（都已对着既有库逐行核对过，不是照抄注释）：
///
/// 1. <c>best</c> 是**物化表**，不是视图。做成视图时窗口函数会在每次查询上把 33 万行
///    重算一遍，查询直接卡死。改数据必须重跑构建，这是刻意的代价。
/// 2. <c>in_anima</c> 用 <c>best.tag = anima_index.name</c> 做**大小写敏感**的精确匹配。
///    这一条很容易写错：字符名如 <c>Aether (Genshin Impact)</c> 在 <c>best</c> 里是大写，
///    在 Anima 索引里是小写。用忽略大小写去配会多标 28 条、少 15 条别名——看着"差不多"，
///    而用错大小写语义正是这种错误最难发现的地方。
/// 3. 废弃与下架都是**标记，不是过滤**。Anima 训练用的是更早的 Danbooru 快照，
///    退役标签它仍然认识；删掉会让旧提示词查不到。
/// </summary>
public static class VocabBuilder
{
    /// <summary>Danbooru 数字类别 → 名字。与 Python 参考实现一致。</summary>
    private static readonly Dictionary<int, string> CatNames = new()
    {
        [0] = "general",
        [1] = "artist",
        [3] = "copyright",
        [4] = "character",
        [5] = "meta",
        [6] = "anima-cat6",
        [-1] = "anima-cat-1",
    };

    /// <summary>同名标签出现两次时谁说了算。社区精选包压过全量表。</summary>
    private static readonly Dictionary<string, int> Priority = new(StringComparer.Ordinal)
    {
        ["anima"] = 100, ["base"] = 50, ["characters"] = 50,
        ["clothing"] = 50, ["poses"] = 50, ["camera"] = 50, ["danbooru-cn"] = 10,
    };

    /// <summary>素材目录里还缺哪些必需文件。空数组 = 可以构建。</summary>
    public static IReadOnlyList<string> MissingRequired(string sourcesDir) =>
        VocabSources.RequiredFiles()
            .Where(f => !File.Exists(Path.Combine(sourcesDir, f)))
            .ToList();

    /// <summary>
    /// 构建词库。
    /// </summary>
    /// <param name="sourcesDir">上游素材所在目录（见 <see cref="VocabSources"/>）。</param>
    /// <param name="outDbPath">产出 <c>vocab.sqlite</c> 的完整路径。</param>
    /// <param name="seedMasterPath">随程序分发的种子映射母本；给了就复制到库旁边。</param>
    /// <param name="exportCsvDir">给了就额外导出四个 CSV（给 Excel/脚本看的，程序本体不用）。</param>
    /// <param name="log">进度回调。构建要几十秒，界面必须能看见它在动。</param>
    public static VocabBuildResult Build(
        string sourcesDir,
        string outDbPath,
        string? seedMasterPath = null,
        string? exportCsvDir = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        log ??= _ => { };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var warnings = new List<string>();

        var missing = MissingRequired(sourcesDir);
        if (missing.Count > 0)
            throw new FileNotFoundException(
                "素材不齐，缺：" + string.Join("、", missing) + "。先点「下载素材」或手工放进素材目录。");

        foreach (var opt in VocabSources.OptionalFiles())
            if (!File.Exists(Path.Combine(sourcesDir, opt)))
                warnings.Add("可选素材缺失，已跳过：" + opt);

        Directory.CreateDirectory(Path.GetDirectoryName(outDbPath)!);

        // 先建到临时文件，成功了再原子替换。**不能直接写目标**：构建中途失败
        // （或用户中途取消）会在原来的位置留下半截库，而那份半截库看起来是个正常文件，
        // 程序会照常加载然后给出莫名其妙的搜索结果——比直接报错难查得多。
        var tmp = outDbPath + ".building";
        // 同目录下的旧临时文件（上次崩溃留下的）会挡住写入。
        TryDelete(tmp);

        try
        {
            var result = BuildCore(sourcesDir, tmp, exportCsvDir, log, warnings, ct, sw);
            ct.ThrowIfCancellationRequested();

            // File.Move(overwrite) 在 Windows 上是原子的替换；若目标正被别的进程占用
            // 会抛 IOException，那时保留旧库不动才是对的。
            File.Move(tmp, outDbPath, overwrite: true);

            var seedWritten = CopySeedIfNeeded(seedMasterPath, Path.GetDirectoryName(outDbPath)!, log);

            sw.Stop();
            var final = result with
            {
                DbPath = outDbPath,
                DbBytes = new FileInfo(outDbPath).Length,
                Elapsed = sw.Elapsed,
                Warnings = warnings
            };
            if (!seedWritten && seedMasterPath is not null)
                log("种子映射已存在，未覆盖");

            log($"完成：{final.DistinctTags:N0} 条标签，中文覆盖 {final.ChinesePercent:F1}%，" +
                $"Anima 索引 {final.AnimaIndex:N0} 条，用时 {final.Elapsed.TotalSeconds:F1} 秒");
            return final;
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static VocabBuildResult BuildCore(
        string sourcesDir, string dbPath, string? exportCsvDir,
        Action<string> log, List<string> warnings, CancellationToken ct, System.Diagnostics.Stopwatch sw)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // 关掉连接池。默认是开的，`Dispose` 之后连接会回到池里、**文件句柄不释放**，
            // 于是下面那句原子替换（File.Move overwrite）会抛
            // "being used by another process"。库建完就交给只读加载，
            // 这里没有复用连接的需要。
            Pooling = false
        }.ToString();

        using var con = new SqliteConnection(cs);
        con.Open();

        // 构库比默认值吃 I/O：把日志模式调成 WAL 之外的内存式临时表能快不少，
        // 而且这个文件建完就交给只读加载，不需要崩溃恢复能力。
        Exec(con, "PRAGMA journal_mode=MEMORY");
        Exec(con, "PRAGMA synchronous=OFF");
        Exec(con, "PRAGMA temp_store=MEMORY");

        Exec(con, """
            CREATE TABLE tags (
                tag        TEXT    NOT NULL,
                danbooru   TEXT    NOT NULL,
                cn         TEXT,
                aliases    TEXT,
                category   TEXT,
                post_count INTEGER DEFAULT 0,
                source     TEXT,
                priority   INTEGER DEFAULT 0,
                PRIMARY KEY (tag, source)
            )
            """);
        Exec(con, """
            CREATE TABLE alias (
                alias  TEXT NOT NULL,
                raw    TEXT NOT NULL,
                tag    TEXT NOT NULL,
                target TEXT,
                PRIMARY KEY (alias, tag)
            )
            """);

        // ---- 1. 全量中英对照表 ----
        ct.ThrowIfCancellationRequested();
        log("读入全量中英对照表…");
        using (var tx = con.BeginTransaction())
        {
            using var src = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(sourcesDir, VocabSources.BulkDb),
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            src.Open();

            using var ins = con.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO tags VALUES ($t,$d,$c,$a,$cat,$pc,$s,$p)";
            var pt = ins.Parameters.Add("$t", SqliteType.Text);
            var pd = ins.Parameters.Add("$d", SqliteType.Text);
            var pc = ins.Parameters.Add("$c", SqliteType.Text);
            var pa = ins.Parameters.Add("$a", SqliteType.Text);
            var pcat = ins.Parameters.Add("$cat", SqliteType.Text);
            var ppc = ins.Parameters.Add("$pc", SqliteType.Integer);
            var ps = ins.Parameters.Add("$s", SqliteType.Text);
            var pp = ins.Parameters.Add("$p", SqliteType.Integer);

            // 顺序即优先级：Python 的 setdefault 是"先到者胜"，所以按 rowid 原序读，
            // 不排序。加了 ORDER BY 反而会改变去重结果。
            using var cmd = src.CreateCommand();
            cmd.CommandText = "SELECT name, category, cn_name, post_count FROM tags";
            using var r = cmd.ExecuteReader();
            var bulk = 0;
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                var name = r.IsDBNull(0) ? "" : r.GetString(0);
                var cat = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                pt.Value = AnimaFormat(name, cat);
                pd.Value = name;
                pc.Value = (r.IsDBNull(2) ? "" : r.GetString(2)).Trim();
                pa.Value = "";
                pcat.Value = CatNames.TryGetValue(cat, out var cn) ? cn : "other";
                ppc.Value = r.IsDBNull(3) ? 0 : r.GetInt32(3);
                ps.Value = "danbooru-cn";
                pp.Value = Priority["danbooru-cn"];
                ins.ExecuteNonQuery();
                bulk++;
            }
            tx.Commit();
            log($"  全量表 {bulk:N0} 条");
        }

        // ---- 2. 社区精选包 ----
        ct.ThrowIfCancellationRequested();
        log("读入社区精选包…");
        using (var tx = con.BeginTransaction())
        {
            using var ins = con.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO tags VALUES ($t,$d,$c,$a,$cat,$pc,$s,$p)";
            var pt = ins.Parameters.Add("$t", SqliteType.Text);
            var pd = ins.Parameters.Add("$d", SqliteType.Text);
            var pc = ins.Parameters.Add("$c", SqliteType.Text);
            var pa = ins.Parameters.Add("$a", SqliteType.Text);
            var pcat = ins.Parameters.Add("$cat", SqliteType.Text);
            var ppc = ins.Parameters.Add("$pc", SqliteType.Integer);
            var ps = ins.Parameters.Add("$s", SqliteType.Text);
            var pp = ins.Parameters.Add("$p", SqliteType.Integer);

            foreach (var fn in VocabSources.PackFiles.OrderBy(x => x, StringComparer.Ordinal))
            {
                var path = Path.Combine(sourcesDir, fn);
                if (!File.Exists(path)) continue;

                using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("tags", out var tags) ||
                    tags.ValueKind != JsonValueKind.Array)
                    continue;

                // pack id 取自文件里的 pack.id，退回文件名——与 Python 一致。
                var packId = fn[..^5];
                if (root.TryGetProperty("pack", out var pack) &&
                    pack.ValueKind == JsonValueKind.Object &&
                    pack.TryGetProperty("id", out var idEl) &&
                    idEl.ValueKind == JsonValueKind.String)
                    packId = idEl.GetString() ?? packId;

                var n = 0;
                foreach (var t in tags.EnumerateArray())
                {
                    var en = (Str(t, "english") ?? "").Trim();
                    if (en.Length == 0) continue;
                    var cn = (Str(t, "chinese") ?? "").Trim();

                    // 别名里去掉与主译名重复的那条，否则同一行里会出现两遍。
                    var aliasList = new List<string>();
                    if (t.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Array)
                        foreach (var a in al.EnumerateArray())
                        {
                            var s = (a.GetString() ?? "").Trim();
                            if (s.Length > 0 && s != cn) aliasList.Add(s);
                        }

                    pt.Value = en;
                    pd.Value = en.Replace(' ', '_');
                    pc.Value = cn;
                    pa.Value = string.Join("|", aliasList);
                    pcat.Value = Str(t, "category") is { Length: > 0 } c2 ? c2 : "general";
                    ppc.Value = 0;
                    ps.Value = packId;
                    pp.Value = Priority.TryGetValue(packId, out var pr) ? pr : 20;
                    ins.ExecuteNonQuery();
                    n++;
                }
                log($"  {packId}: {n} 条");
            }
            tx.Commit();
        }

        // ---- 3. best 物化表 ----
        // 每个标签取"最权威的身份"（引用数最高的真实 Danbooru 标签），同时尽量保留
        // 社区精选包里更好的中文译名与分类。
        //
        // 用真表而不是视图是刻意的：做成视图时窗口函数会在**每一次查询**上把 33 万行
        // 重算一遍，查询直接挂住（当年的原话）。代价是改数据必须重建。
        ct.ThrowIfCancellationRequested();
        log("生成去重表 best（这一步最慢）…");
        Exec(con, """
            CREATE TABLE best AS
            WITH ranked AS (
                SELECT *,
                       ROW_NUMBER() OVER (
                           PARTITION BY tag
                           ORDER BY (post_count > 0) DESC, post_count DESC, priority DESC
                       ) AS rn_canon,
                       ROW_NUMBER() OVER (
                           PARTITION BY tag
                           ORDER BY (source <> 'danbooru-cn') DESC, priority DESC
                       ) AS rn_cur
                FROM tags
            )
            SELECT c.tag,
                   c.danbooru,
                   COALESCE(NULLIF(c.cn, ''), NULLIF(u.cn, ''))             AS cn,
                   COALESCE(NULLIF(u.aliases, ''), NULLIF(c.aliases, ''))   AS aliases,
                   CASE WHEN u.source IS NOT NULL AND u.source <> 'danbooru-cn'
                        THEN u.category ELSE c.category END                 AS category,
                   c.post_count,
                   c.source
            FROM (SELECT * FROM ranked WHERE rn_canon = 1) c
            LEFT JOIN (SELECT * FROM ranked WHERE rn_cur = 1 AND source <> 'danbooru-cn') u
                   ON u.tag = c.tag
            """);

        foreach (var ix in new[]
                 {
                     "CREATE INDEX idx_b_tag ON best(tag)",
                     "CREATE INDEX idx_b_cn  ON best(cn)",
                     "CREATE INDEX idx_b_cat ON best(category)",
                     "CREATE INDEX idx_b_pc  ON best(post_count DESC)"
                 })
            Exec(con, ix);

        // ---- 4. 改名表：退役写法仍要能查到现用标签 ----
        ct.ThrowIfCancellationRequested();
        log("合并 Danbooru 改名表…");
        var aliasesPath = Path.Combine(sourcesDir, VocabSources.AliasesJson);
        if (File.Exists(aliasesPath))
        {
            // 用 best.danbooru（带下划线的原始名）做连接键：画师行在这一列里刻意不带 @。
            // 同名多条时后者覆盖前者——与 Python 的字典推导一致。
            var known = new Dictionary<string, (string Tag, string Cat)>(StringComparer.Ordinal);
            using (var q = con.CreateCommand())
            {
                q.CommandText = "SELECT tag, danbooru, category FROM best";
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    var tagv = r.IsDBNull(0) ? "" : r.GetString(0);
                    var dbv = r.IsDBNull(1) ? "" : r.GetString(1);
                    var catv = r.IsDBNull(2) ? "" : r.GetString(2);
                    if (dbv.Length > 0) known[dbv] = (tagv, catv);
                }
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(aliasesPath, Encoding.UTF8));
            using var tx = con.BeginTransaction();
            using var ins = con.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO alias VALUES ($a,$r,$t,$tg)";
            var pa = ins.Parameters.Add("$a", SqliteType.Text);
            var pr = ins.Parameters.Add("$r", SqliteType.Text);
            var pt2 = ins.Parameters.Add("$t", SqliteType.Text);
            var ptg = ins.Parameters.Add("$tg", SqliteType.Text);

            var used = 0;
            foreach (var a in doc.RootElement.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();
                var ante = Str(a, "antecedent_name");
                var cons = Str(a, "consequent_name");
                if (string.IsNullOrEmpty(ante) || string.IsNullOrEmpty(cons) || ante == cons) continue;
                if (!known.TryGetValue(cons, out var hit)) continue;   // 目标不在词库里，留着没用

                pa.Value = AnimaFormat(ante, hit.Cat == "artist" ? 1 : 0);
                pr.Value = ante;
                pt2.Value = hit.Tag;
                ptg.Value = cons;
                ins.ExecuteNonQuery();
                used++;
            }
            tx.Commit();
            log($"  改名映射采用 {used:N0} 条");
        }
        else
        {
            warnings.Add("没有改名表（" + VocabSources.AliasesJson + "）：旧写法查不到新标签");
        }

        // ---- 5. 废弃 / 下架标记 ----
        // 两者都是**标记而非过滤**，理由见类注释第 3 条。
        ct.ThrowIfCancellationRequested();
        Exec(con, "ALTER TABLE best ADD COLUMN deprecated INTEGER DEFAULT 0");
        Exec(con, "ALTER TABLE best ADD COLUMN banned INTEGER DEFAULT 0");

        var depPath = Path.Combine(sourcesDir, VocabSources.DeprecatedJson);
        if (File.Exists(depPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(depPath, Encoding.UTF8));
            using var tx = con.BeginTransaction();
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE best SET deprecated=1 WHERE danbooru=$d";
            var pd = cmd.Parameters.Add("$d", SqliteType.Text);
            var n = 0;
            foreach (var d in doc.RootElement.EnumerateArray())
            {
                var name = Str(d, "name");
                if (string.IsNullOrEmpty(name)) continue;
                pd.Value = name;
                n += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            log($"  废弃标记 {n:N0} 条");
        }

        // 既是正典行、又是活跃改名源的，按定义就是退役名：Danbooru 仍在重定向它。
        // ffdkj 的快照滞后，还会拿旧引用数继续推荐这些名字（如 china_dress 显示 7.6 万，
        // 实际 Danbooru 已报 0，现名是 qipao）。抽检 50 条对 Danbooru API 全是 0，
        // 所以整批标掉是安全的。
        Exec(con, """
            UPDATE best SET deprecated=1
            WHERE danbooru IN (SELECT raw FROM alias WHERE alias <> tag)
            """);

        var bannedPath = Path.Combine(sourcesDir, VocabSources.BannedCsv);
        if (File.Exists(bannedPath))
        {
            using var tx = con.BeginTransaction();
            using var cmd = con.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE best SET banned=1 WHERE danbooru=$d";
            var pd = cmd.Parameters.Add("$d", SqliteType.Text);
            var n = 0;
            foreach (var line in ReadCsv(bannedPath, hasHeader: true))
            {
                if (line.Length == 0 || line[0].Length == 0) continue;
                pd.Value = line[0];     // 只取 tag 列
                n += cmd.ExecuteNonQuery();
            }
            tx.Commit();
            log($"  下架标记 {n:N0} 条");
        }

        // ---- 6. Anima 自己的标签索引（原本是第二个脚本，必须一起跑）----
        ct.ThrowIfCancellationRequested();
        log("建立 Anima 标签索引…");
        Exec(con, """
            CREATE TABLE anima_index (
                name       TEXT NOT NULL,
                raw        TEXT NOT NULL,
                category   TEXT,
                post_count INTEGER DEFAULT 0,
                aliases    TEXT
            )
            """);

        var csvPath = Path.Combine(sourcesDir, VocabSources.AnimaIndexCsv);
        using (var tx = con.BeginTransaction())
        {
            using var ins = con.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO anima_index VALUES ($n,$r,$c,$p,$a)";
            var pn = ins.Parameters.Add("$n", SqliteType.Text);
            var pr2 = ins.Parameters.Add("$r", SqliteType.Text);
            var pc2 = ins.Parameters.Add("$c", SqliteType.Text);
            var pp2 = ins.Parameters.Add("$p", SqliteType.Integer);
            var pa2 = ins.Parameters.Add("$a", SqliteType.Text);

            var n = 0;
            foreach (var row in ReadCsv(csvPath, hasHeader: false))
            {
                // 列序：raw, numeric category, post_count, aliases
                if (row.Length < 4 || row[0].Length == 0) continue;
                if (!int.TryParse(row[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cat))
                    cat = 0;
                int.TryParse(row[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var posts);

                pn.Value = row[0].Replace('_', ' ');   // Anima 要的是空格形式
                pr2.Value = row[0];                   // 原始（下划线）形式
                pc2.Value = CatNames.TryGetValue(cat, out var cnm) ? cnm : "other";
                pp2.Value = posts;
                pa2.Value = (row[3] ?? "").Trim();
                ins.ExecuteNonQuery();
                n++;
            }
            tx.Commit();
            log($"  Anima 索引 {n:N0} 条");
        }

        Exec(con, "CREATE INDEX idx_ai_name ON anima_index(name)");

        // in_anima / anima_aliases：**大小写敏感**的精确匹配（类注释第 2 条）。
        // SQLite 的默认 BINARY 排序规则就是大小写敏感的，正是我们要的语义。
        Exec(con, "ALTER TABLE best ADD COLUMN in_anima INTEGER DEFAULT 0");
        Exec(con, "ALTER TABLE best ADD COLUMN anima_aliases TEXT");
        Exec(con, """
            UPDATE best SET
                in_anima = CASE WHEN EXISTS (
                    SELECT 1 FROM anima_index a WHERE a.name = best.tag) THEN 1 ELSE 0 END,
                anima_aliases = COALESCE((
                    SELECT a.aliases FROM anima_index a WHERE a.name = best.tag), '')
            """);

        // ---- 7. 统计 + 可选导出 ----
        if (exportCsvDir is not null)
        {
            ct.ThrowIfCancellationRequested();
            log("导出 CSV…");
            Directory.CreateDirectory(exportCsvDir);
            ExportCsv(con, exportCsvDir);
        }

        var distinct = Scalar(con, "SELECT COUNT(*) FROM best");
        var withCn = Scalar(con, "SELECT COUNT(*) FROM best WHERE cn IS NOT NULL AND cn<>''");
        var animaN = Scalar(con, "SELECT COUNT(*) FROM anima_index");
        var inAnimaN = Scalar(con, "SELECT COUNT(*) FROM best WHERE in_anima=1");
        var aliasN = Scalar(con, "SELECT COUNT(*) FROM alias");
        var depN = Scalar(con, "SELECT COUNT(*) FROM best WHERE deprecated=1");
        var banN = Scalar(con, "SELECT COUNT(*) FROM best WHERE banned=1");

        // VACUUM 会把文件收紧（构建期插入了大量临时页）。同时它也让文件成为
        // 一份紧凑的只读库——程序加载时不需要回放 WAL。
        Exec(con, "VACUUM");

        return new VocabBuildResult(
            dbPath, 0, distinct, withCn, animaN, inAnimaN, aliasN, depN, banN,
            warnings, sw.Elapsed);
    }

    /// <summary>
    /// Danbooru 标签名 → Anima 训练时用的确切形式。
    /// 下划线换空格；画师加 <c>@</c>；<c>score_N</c> 是唯一保留下划线的例外。
    /// </summary>
    private static string AnimaFormat(string name, int category)
    {
        var n = (name ?? "").Trim();
        var isScore = n.StartsWith("score_", StringComparison.Ordinal) &&
                      n.Length > 6 && n[6..].All(char.IsDigit);
        if (!isScore) n = n.Replace('_', ' ');
        if (category == 1 && !n.StartsWith('@')) n = "@" + n;
        return n;
    }

    /// <summary>
    /// 复制种子映射到库旁边。返回是否真的写了。
    ///
    /// <c>seed.json</c> 是人工整理的"中文概念 → 标签"补充映射（全量表里没有精确中文键的
    /// 那些常用说法，例如「数位板」这类）。它是运行期单独读的文件，不烤进 sqlite，
    /// 所以构建完必须把它放到 <see cref="AppSettings.SeedPath"/> 指的位置。
    /// 已存在就不覆盖——用户可能自己补过条目。
    /// </summary>
    private static bool CopySeedIfNeeded(string? master, string targetDir, Action<string> log)
    {
        if (master is null || !File.Exists(master)) return false;
        var dest = Path.Combine(targetDir, "seed.json");
        if (File.Exists(dest)) return false;
        File.Copy(master, dest);
        log("种子映射已写入：" + dest);
        return true;
    }

    private static void ExportCsv(SqliteConnection con, string dir)
    {
        WriteCsv(Path.Combine(dir, "tags.csv"),
            "tag,danbooru,cn,aliases,category,post_count,source,deprecated,banned",
            con, "SELECT tag,danbooru,cn,aliases,category,post_count,source,deprecated,banned " +
                 "FROM best ORDER BY post_count DESC, tag");

        WriteCsv(Path.Combine(dir, "anima-core.csv"),
            "tag,cn,aliases,category,source",
            con, "SELECT tag,cn,aliases,category,source FROM tags " +
                 "WHERE source<>'danbooru-cn' ORDER BY source, category, tag");

        WriteCsv(Path.Combine(dir, "aliases.csv"),
            "retired_name,danbooru_name,canonical_tag,canonical_danbooru",
            con, "SELECT alias,raw,tag,target FROM alias ORDER BY tag, alias");

        WriteCsv(Path.Combine(dir, "banned-artists.csv"),
            "tag,cn,post_count",
            con, "SELECT tag,cn,post_count FROM best WHERE banned=1 ORDER BY post_count DESC");
    }

    private static void WriteCsv(string path, string header, SqliteConnection con, string sql)
    {
        // UTF-8 BOM：Excel 直接双击打开时才不会把中文当乱码。
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine(header);
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            for (var i = 0; i < r.FieldCount; i++)
            {
                if (i > 0) w.Write(',');
                if (r.IsDBNull(i)) continue;
                var s = Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
                    w.Write('"' + s.Replace("\"", "\"\"") + '"');
                else
                    w.Write(s);
            }
            w.WriteLine();
        }
    }

    /// <summary>
    /// 极简 CSV 读取。只用来读上游给的固定格式文件，所以刻意不处理跨行引号字段——
    /// 那两种文件里都没有。省掉一个依赖。
    ///
    /// <paramref name="hasHeader"/> 必须由调用方**明确指定**，不能靠猜：
    /// <c>anima-1.0.csv</c> 只有数据行、没有表头（第一行就是 <c>1girl,0,7868012,…</c>），
    /// 而 <c>banned_tags.csv</c> 有表头。用"第一行像不像表头"去判断会把 Anima 索引的
    /// 第一条 <c>1girl</c> 吃掉——而它恰好是引用数最高的标签，少了它不会报错，
    /// 只会表现为"最常用的标签查不到"。
    /// </summary>
    private static IEnumerable<string[]> ReadCsv(string path, bool hasHeader)
    {
        using var r = new StreamReader(path, Encoding.UTF8, true);
        var first = true;
        while (r.ReadLine() is { } line)
        {
            if (first && hasHeader) { first = false; continue; }
            first = false;
            if (line.Length == 0) continue;
            yield return SplitCsv(line);
        }
    }

    private static string[] SplitCsv(string line)
    {
        var outp = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { outp.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        outp.Add(sb.ToString());
        return outp.ToArray();
    }

    private static string? Str(JsonElement obj, string prop) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static void Exec(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int Scalar(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 删不掉就让它去 */ }
    }
}
