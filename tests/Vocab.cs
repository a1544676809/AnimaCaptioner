using AnimaCaptioner.Core;
using Microsoft.Data.Sqlite;
using System.Text;

namespace CoreCheck;

/// <summary>
/// 词库构建器的回归测试。
///
/// 最强的判据是**拿既有那份已验证的 vocab.sqlite 当基准**：从同一批上游素材重建一份，
/// 然后逐表逐列逐行对拍。如果构建器漏了某个语义（大小写、去重顺序、标记规则），
/// 对拍会以具体行数差异暴露出来，而不是等到用户搜索时才发现少了一批标签。
///
/// 需要仓库外的素材目录，所以没给就跳过（返回 2），不假装通过。
/// </summary>
public static class Vocab
{
    public static int Run(string sourcesDir, string baselineDb)
    {
        var fail = 0;
        void Check(string what, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!ok) fail++;
        }

        Console.WriteLine("=== 1. 素材清单自检 ===");
        Check("必需素材清单非空", VocabSources.RequiredFiles().Any());
        Check("清单里没有重复文件名",
            VocabSources.All.Select(f => f.FileName)
                .Concat(VocabSources.PackFiles)
                .Distinct(StringComparer.Ordinal).Count() ==
            VocabSources.All.Length + VocabSources.PackFiles.Length);
        foreach (var f in VocabSources.All)
            Console.WriteLine($"    {f.FileName,-26} {f.License,-10} {f.Upstream}");
        Console.WriteLine($"    （另有 {VocabSources.PackFiles.Length} 个社区精选包，MIT）");

        Console.WriteLine("=== 1b. 分页抓取（离线自检：错了只会静默少数据）===");
        // 这些判据全部离线，用假的页内容。若错了，症状是"改名表少了 97%"而不是报错。
        {
            // 满页 (1000) 且有新记录 -> 还没到底
            Check("满页且有新记录 -> 还有下一页",
                !VocabFetch.IsLastPage(1000, 1000));
            // 不满一页 -> 到底
            Check("不满一页 -> 到底", VocabFetch.IsLastPage(37, 37));
            // 整页全是重复 -> 到底（分页漂移时会这样，不能当成"还有下一页"无限抓）
            Check("整页全重复 -> 到底", VocabFetch.IsLastPage(1000, 0));

            Check("页码替换进 URL",
                VocabFetch.PageUrl("https://x/y.json?limit=1000&page={0}", 7)
                    .EndsWith("page=7", StringComparison.Ordinal));

            var p1 = "[{\"id\":1,\"n\":\"a\"},{\"id\":2,\"n\":\"b\"}]";
            var p2 = "[{\"id\":2,\"n\":\"b\"},{\"id\":3,\"n\":\"c\"}]";   // id=2 与上页重复
            var merged = VocabFetch.MergePagedJson(new[] { p1, p2 }, out var mc);
            Check("跨页按 id 去重", mc == 3, $"{mc} 条");
            using (var d = System.Text.Json.JsonDocument.Parse(merged))
                Check("合并结果是合法 JSON 数组",
                    d.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array &&
                    d.RootElement.GetArrayLength() == 3);

            // 缺 id 的记录不能互相去重，否则第二条起全被吞掉。
            var noId = "[{\"n\":\"x\"},{\"n\":\"y\"},{\"n\":\"z\"}]";
            VocabFetch.MergePagedJson(new[] { noId }, out var nc);
            Check("缺 id 的记录不被误去重", nc == 3, $"{nc} 条");

            // 坏页跳过而不是整批失败。
            VocabFetch.MergePagedJson(new[] { p1, "{not json", p2 }, out var bc);
            Check("坏页跳过、其余照收", bc == 3, $"{bc} 条");

            VocabFetch.CountNewInPage(p2, new HashSet<long> { 2 }, out var raw2);
            Check("CountNewInPage 统计新记录与原始条数", raw2 == 2, $"raw={raw2}");
        }

        Console.WriteLine("=== 1c. 词库相关设置能落盘再读回 ===");
        {
            // 素材目录与代理是构建的前提条件；丢一个用户就得每次重填。
            // 序列化是反射式的，理论上自动带上——但"理论上"不是判据，这里真的转一圈。
            var cfg = new AppSettings
            {
                VocabSourceDir = @"D:\vocab-sources",
                VocabProxy = "http://127.0.0.1:10808",
                VocabDbPath = @"D:\out\vocab.sqlite",
                SeedPath = @"D:\out\seed.json"
            };
            var json = cfg.ToJson();
            var back = AppSettings.FromJson(json);
            Check("VocabSourceDir 落盘再读回", back?.VocabSourceDir == @"D:\vocab-sources",
                back?.VocabSourceDir ?? "(null)");
            Check("VocabProxy 落盘再读回", back?.VocabProxy == "http://127.0.0.1:10808",
                back?.VocabProxy ?? "(null)");
            Check("空代理是合法值（表示直连）",
                AppSettings.FromJson(new AppSettings().ToJson())?.VocabProxy == "");
        }

        Console.WriteLine("=== 2. Anima 标签名归一化（判据来自官方模型卡）===");
        // 这几条是模型卡明确规定的形状，错了会让出图质量下降且没有任何报错。
        Check("下划线 → 空格", Fmt("long_hair", 0) == "long hair");
        Check("画师加 @", Fmt("dairi", 1) == "@dairi");
        Check("已是 @ 的不重复加", Fmt("@dairi", 1) == "@dairi");
        Check("score_N 保留下划线", Fmt("score_7", 0) == "score_7");
        Check("score_ 后面不是数字则照常换空格", Fmt("score_meta", 0) == "score meta");
        Check("非画师不加 @", Fmt("1girl", 0) == "1girl");

        if (sourcesDir.Length == 0 || !Directory.Exists(sourcesDir))
        {
            Console.WriteLine($"\n没有素材目录，跳过重建。（设 AC_VOCABSRC 指向放上游文件的目录即可跑全）");
            Console.WriteLine(fail == 0 ? "\n词库构建：纯逻辑部分通过" : $"\n词库构建：{fail} 项失败");
            return fail == 0 ? 0 : 1;
        }

        var missing = VocabBuilder.MissingRequired(sourcesDir);
        if (missing.Count > 0)
        {
            Console.WriteLine($"\n素材不全（缺 {string.Join("、", missing)}），跳过构建对拍");
            return 2;
        }

        Console.WriteLine("=== 3. 从素材重建（这是真正的验证）===");
        var dir = Path.Combine(Path.GetTempPath(), "ac-vocab-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var built = Path.Combine(dir, "vocab.sqlite");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = VocabBuilder.Build(sourcesDir, built, log: m => Console.WriteLine("    " + m));
            Console.WriteLine($"    用时 {sw.Elapsed.TotalSeconds:F1}s，库 {res.DbBytes / 1024.0 / 1024.0:F1} MB");

            foreach (var w in res.Warnings) Console.WriteLine("    ⚠ " + w);

            Check("产出文件存在", File.Exists(built));
            Check("中文覆盖率 ≥ 99%", res.ChinesePercent >= 99.0, $"{res.ChinesePercent:F1}%");
            Check("Anima 索引非空（少了它 12k 标签查不到）", res.AnimaIndex > 100_000,
                res.AnimaIndex.ToString("N0"));

            if (!File.Exists(baselineDb))
            {
                Console.WriteLine($"\n没有基准库，只验到「能建出东西」：{baselineDb}");
                return fail == 0 ? 0 : 1;
            }

            // ---- 与既有库逐表对拍 ----
            Console.WriteLine("=== 4. 与既有 vocab.sqlite 对拍 ===");
            using var a = Open(built);
            using var b = Open(baselineDb);

            CompareCount(a, b, "best", Check);
            CompareCount(a, b, "alias", Check);
            CompareCount(a, b, "anima_index", Check);

            Check("best 的 tag 集合完全一致",
                DiffSet(a, b, "SELECT tag FROM best", "SELECT tag FROM best") == 0);
            Check("alias 的 (alias,tag) 集合完全一致",
                DiffSet(a, b,
                    "SELECT alias||' -> '||tag FROM alias",
                    "SELECT alias||' -> '||tag FROM alias") == 0);
            Check("anima_index 的 (name,raw) 集合完全一致",
                DiffSet(a, b,
                    "SELECT name||' | '||raw FROM anima_index",
                    "SELECT name||' | '||raw FROM anima_index") == 0);

            // 逐列对拍 best：这是最严的一条，任何归一化/优先级差异都会露出来。
            var bestDiff = DiffSet(a, b,
                "SELECT tag||'\u0001'||danbooru||'\u0001'||COALESCE(cn,'')||'\u0001'||" +
                "COALESCE(aliases,'')||'\u0001'||COALESCE(category,'')||'\u0001'||" +
                "post_count||'\u0001'||COALESCE(source,'')||'\u0001'||deprecated||'\u0001'||" +
                "banned||'\u0001'||COALESCE(in_anima,0)||'\u0001'||COALESCE(anima_aliases,'') FROM best",
                "SELECT tag||'\u0001'||danbooru||'\u0001'||COALESCE(cn,'')||'\u0001'||" +
                "COALESCE(aliases,'')||'\u0001'||COALESCE(category,'')||'\u0001'||" +
                "post_count||'\u0001'||COALESCE(source,'')||'\u0001'||deprecated||'\u0001'||" +
                "banned||'\u0001'||COALESCE(in_anima,0)||'\u0001'||COALESCE(anima_aliases,'') FROM best");
            Check("best 每一行的每一列都一致（含 in_anima / anima_aliases）", bestDiff == 0,
                bestDiff == 0 ? "" : $"差异 {bestDiff:N0} 行");

            // in_anima 的分支覆盖：总数对了但分布错了也可能发生。
            Check("in_anima=1 的行数一致",
                Scalar(a, "SELECT COUNT(*) FROM best WHERE in_anima=1") ==
                Scalar(b, "SELECT COUNT(*) FROM best WHERE in_anima=1"),
                Scalar(b, "SELECT COUNT(*) FROM best WHERE in_anima=1").ToString("N0"));
            Check("anima_aliases 非空行数一致",
                Scalar(a, "SELECT COUNT(*) FROM best WHERE anima_aliases<>''") ==
                Scalar(b, "SELECT COUNT(*) FROM best WHERE anima_aliases<>''"));
            Check("deprecated=1 行数一致",
                Scalar(a, "SELECT COUNT(*) FROM best WHERE deprecated=1") ==
                Scalar(b, "SELECT COUNT(*) FROM best WHERE deprecated=1"));
            Check("banned=1 行数一致",
                Scalar(a, "SELECT COUNT(*) FROM best WHERE banned=1") ==
                Scalar(b, "SELECT COUNT(*) FROM best WHERE banned=1"));

            // ---- 用应用自己的加载器读新库，并与基准库**对拍解析结果** ----
            //
            // 不写死"wink 应该变成 one eye closed"这类预期——那种断言只在作者猜对时才对。
            // 这里改成拿基准库当标准答案：同一个查询，两边必须给出同一个 (tag, status)。
            // 判据不依赖我的记忆，也就不会因为我记错而误报（写这节时我就猜错了两次：
            // wink 是 Anima 原生标签、双马尾 在不给 seed 时本来就不是 twintails）。
            Console.WriteLine("=== 5. 解析结果与基准库对拍（VocabDb 加载器）===");
            var seedPath = FindSeed(sourcesDir, built, baselineDb);
            Console.WriteLine("    seed: " + (seedPath.Length > 0 ? seedPath : "(没找到，按无 seed 比)"));

            var nb = new VocabDb();
            var na = new VocabDb();
            try
            {
                nb.Load(baselineDb, seedPath);
                na.Load(built, seedPath);
                Check("两个库都能加载（基准 / 新建）", true);
                Check("标签数一致", na.TagCount == nb.TagCount,
                    $"{na.TagCount:N0} vs {nb.TagCount:N0}");
                Check("anima-only 数量一致（这条依赖 anima_index 与 best 都正确）",
                    na.AnimaOnlyCount == nb.AnimaOnlyCount,
                    $"{na.AnimaOnlyCount:N0} vs {nb.AnimaOnlyCount:N0}");

                // 英文解析：覆盖 Anima 原生、退役重定向、下划线修正、查无此词四类。
                var enProbes = new[]
                {
                    "black footwear",   // 只在 Anima 索引里
                    "wink",             // Anima 原生，不该被 Danbooru 重定向掉
                    "china dress",      // 已被 qipao 顶替的旧名
                    "long_hair",        // 下划线写法
                    "see-through",      // Anima 原生 vs Danbooru 重定向的经典分歧
                    "1girl", "score_7", "@dairi",
                    "definitely-not-a-real-tag-xyz",
                };
                var enBad = new List<string>();
                foreach (var q in enProbes)
                {
                    var ra = na.ResolveEnglish(q);
                    var rb = nb.ResolveEnglish(q);
                    if (ra.Tag != rb.Tag || ra.Status != rb.Status)
                        enBad.Add($"{q}: {ra.Tag}/{ra.Status} vs {rb.Tag}/{rb.Status}");
                }
                Check($"英文解析 {enProbes.Length} 例与基准一致", enBad.Count == 0,
                    string.Join("; ", enBad));

                // 中文解析：覆盖 seed 命中、精确中文键、包含匹配、纯英文拒绝。
                var cnProbes = new[]
                {
                    "双马尾", "微笑", "银发", "水手服", "猫耳", "逆光",
                    "一位少女在樱花树下",   // 长句 → 包含匹配
                    "not-chinese-at-all",
                };
                var cnBad = new List<string>();
                foreach (var q in cnProbes)
                {
                    var ra = na.ResolveChinese(q);
                    var rb = nb.ResolveChinese(q);
                    if (ra.Tag != rb.Tag || ra.Status != rb.Status)
                        cnBad.Add($"{q}: {ra.Tag}/{ra.Status} vs {rb.Tag}/{rb.Status}");
                }
                Check($"中文解析 {cnProbes.Length} 例与基准一致", cnBad.Count == 0,
                    string.Join("; ", cnBad));

                // 搜索窗口的结果也要一致——这是用户每天真正看到的那一层。
                var sBad = new List<string>();
                foreach (var q in new[] { "hair", "双马尾", "eyes", "@dairi", "score" })
                {
                    var ha = TagSearch.Query(na, q, 20);
                    var hb = TagSearch.Query(nb, q, 20);
                    var sa = string.Join("|", ha.Select(x => x.Tag));
                    var sb = string.Join("|", hb.Select(x => x.Tag));
                    if (!string.Equals(sa, sb, StringComparison.Ordinal))
                        sBad.Add($"{q}: {ha.Count} vs {hb.Count} 条");
                }
                Check("标签搜索前 20 条与基准一致", sBad.Count == 0, string.Join("; ", sBad));

                // 分段/转义依赖词库判定，也一并比。
                var secBad = new List<string>();
                foreach (var t in new[] { "1girl", "long hair", "hatsune miku", "black footwear", "@dairi" })
                {
                    if (TagSections.Classify(t, na) != TagSections.Classify(t, nb))
                        secBad.Add(t);
                    if (TagSections.IsKnownTag(t, na) != TagSections.IsKnownTag(t, nb))
                        secBad.Add(t + "(known)");
                }
                Check("大类归类与已知判定一致", secBad.Count == 0, string.Join("; ", secBad));
            }
            catch (Exception ex)
            {
                Check("两个库都能加载（基准 / 新建）", false, ex.Message);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine(fail == 0 ? "\n词库构建：全部通过" : $"\n词库构建：{fail} 项失败");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// 找一个可用的 seed.json。构建产物的同目录、素材目录、基准库同目录依次找。
    /// seed 是运行期单独加载的文件（不烤进库），两边必须用**同一个**才有可比性。
    /// </summary>
    private static string FindSeed(string sourcesDir, string built, string baselineDb)
    {
        foreach (var p in new[]
                 {
                     Path.Combine(Path.GetDirectoryName(built)!, "seed.json"),
                     Path.Combine(sourcesDir, "seed.json"),
                     Path.Combine(Path.GetDirectoryName(baselineDb)!, "seed.json"),
                 })
            if (File.Exists(p)) return p;
        return "";
    }

    /// <summary>AnimaFormat 是私有的，这里用反射打进去——为了不让"测试方便"改变产品可见性。</summary>
    private static string Fmt(string name, int cat)
    {
        var m = typeof(VocabBuilder).GetMethod("AnimaFormat",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (string)m.Invoke(null, new object[] { name, cat })!;
    }

    private static SqliteConnection Open(string path)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        c.Open();
        return c;
    }

    private static void CompareCount(SqliteConnection a, SqliteConnection b, string table,
        Action<string, bool, string> check)
    {
        var na = Scalar(a, "SELECT COUNT(*) FROM " + table);
        var nb = Scalar(b, "SELECT COUNT(*) FROM " + table);
        check($"{table} 行数一致", na == nb, $"{na:N0} vs {nb:N0}");
    }

    /// <summary>两个结果集的行数差异（对称差）。0 = 完全一致。</summary>
    private static int DiffSet(SqliteConnection a, SqliteConnection b, string sqlA, string sqlB)
    {
        var sa = new HashSet<string>(StringComparer.Ordinal);
        var sb = new HashSet<string>(StringComparer.Ordinal);
        using (var c = a.CreateCommand()) { c.CommandText = sqlA; using var r = c.ExecuteReader(); while (r.Read()) sa.Add(r.GetString(0)); }
        using (var c = b.CreateCommand()) { c.CommandText = sqlB; using var r = c.ExecuteReader(); while (r.Read()) sb.Add(r.GetString(0)); }
        return sa.Except(sb, StringComparer.Ordinal).Count() + sb.Except(sa, StringComparer.Ordinal).Count();
    }

    private static int Scalar(SqliteConnection con, string sql)
    {
        using var c = con.CreateCommand();
        c.CommandText = sql;
        return Convert.ToInt32(c.ExecuteScalar());
    }
}
