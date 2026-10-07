using System.Diagnostics;
using System.Text;
using AnimaCaptioner.Core;

namespace CoreCheck;

/// <summary>
/// 标签搜索窗口的回归。四条承重不变量，每条都对应一类会静默坑到用户的错法：
///
/// 1. **复合中文查询要查得到东西。** `白色蕾丝连衣裙` 整串查是 0 条（实测），
///    拆成「白色/蕾丝/连衣裙」才找得到 `white dress` 和 `lace-trimmed dress`。
///    用户和模型都习惯一口气写完，这条兜底直接决定窗口能不能用。
/// 2. **兜底的门槛不能放到 1。** 放到 1 时「量子计算机」会把
///    `icon (computing)`、`calculator` 这类只碰到「计算」二字组的标签排在前面，
///    而库里其实几乎没有对应标签——正确行为是少给结果、别给噪声。
/// 3. **回灌给模型的文本必须能"照着抄"。** 第一版是没有字段名的裸竖线，
///    实测 8B 把整行当成了标签名（`` `hatsune miku|初音未来（VOCALOID）` ``）。
/// 4. **模型提到的标签名要做存在性核对。** 实测它把
///    `quantum symbol (honkai: star rail)` 缩成了不存在的 `quantum symbol`，
///    也把 `ram (computer)` 缩成了 `ram`——核对必须抓得到，同时不能误报
///    `best quality`、`score_7` 这类合法控制词。
/// </summary>
public static class Search
{
    public static int Run(string dir, string dbPath, string seedPath)
    {
        var vocab = new VocabDb();
        try { vocab.Load(dbPath, seedPath); }
        catch (Exception ex)
        {
            Console.WriteLine("vocab load failed: " + ex.Message);
            return 2;
        }

        var fail = 0;

        // ---------- 1. 切词 ----------
        Console.WriteLine("=== 1. BuildTerms ===");
        var termCases = new (string Query, string[] Must, string[] MustNot)[]
        {
            ("白色蕾丝连衣裙", new[] { "白色", "蕾丝", "连衣", "衣裙" }, new[] { "白色蕾丝连衣裙" }),
            ("white dress", new[] { "white", "dress" }, new[] { "white dress" }),
            ("white_lace", new[] { "white lace" }, Array.Empty<string>()),
            ("量子计算机", new[] { "量子", "计算" }, Array.Empty<string>()),
            ("a", Array.Empty<string>(), new[] { "a" }),
            ("", Array.Empty<string>(), Array.Empty<string>()),
        };
        foreach (var (q, must, mustNot) in termCases)
        {
            var terms = TagSearch.BuildTerms(q);
            var miss = must.Where(t => !terms.Contains(t, StringComparer.Ordinal)).ToList();
            var extra = mustNot.Where(t => terms.Contains(t, StringComparer.Ordinal)).ToList();
            var ok = miss.Count == 0 && extra.Count == 0;
            Console.WriteLine($"  {q,-16} -> [{string.Join(", ", terms)}] {(ok ? "OK" : "BAD")}" +
                              (miss.Count > 0 ? $" 缺少 {string.Join(",", miss)}" : "") +
                              (extra.Count > 0 ? $" 不该有 {string.Join(",", extra)}" : ""));
            if (!ok) fail++;
        }

        // ---------- 2. 普通搜索：中英 + 下划线写法 ----------
        Console.WriteLine();
        Console.WriteLine("=== 2. plain search: Chinese / English / underscore form ===");
        foreach (var (q, wantAmong) in new[]
                 {
                     ("blush", "blush"),
                     ("微笑", "smile"),
                     ("^_^", "^ ^"),   // 词库存的是空格形式，下划线写法也要能找到它
                 })
        {
            var res = TagSearch.QueryDetailed(vocab, q, 50);
            var hasWant = res.Hits.Any(h => string.Equals(h.Tag, wantAmong, StringComparison.OrdinalIgnoreCase));
            var first = res.Hits.Count > 0 ? res.Hits[0].Tag : "(none)";
            Console.WriteLine($"  {q,-10} -> {res.Hits.Count,4} 条, first='{first}', " +
                              $"含 '{wantAmong}' {(hasWant ? "OK" : "MISSING")} split={res.SplitMatch}");
            if (!hasWant) fail++;
        }

        // ---------- 3. 复合查询：兜底必须生效 ----------
        Console.WriteLine();
        Console.WriteLine("=== 3. compound Chinese query needs the split fallback ===");
        const string compound = "白色蕾丝连衣裙";
        var plain = vocab.Search(compound, 50);
        var res3 = TagSearch.QueryDetailed(vocab, compound, 50);
        var tags3 = res3.Hits.Select(h => h.Tag).ToList();
        Console.WriteLine($"  VocabDb.Search('{compound}') 整串 -> {plain.Count} 条（这就是要兜底的原因）");
        Console.WriteLine($"  TagSearch.Query          -> {res3.Hits.Count} 条, split={res3.SplitMatch}");
        Console.WriteLine($"    top: {string.Join(", ", tags3.Take(6))}");
        if (plain.Count != 0) { Console.WriteLine("  !! 整串竟然能命中，这条断言的前提变了"); fail++; }
        if (!res3.SplitMatch) { Console.WriteLine("  !! 没有走兜底"); fail++; }
        foreach (var want in new[] { "white dress", "lace-trimmed dress" })
        {
            var hit = tags3.Contains(want, StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"  期望命中 '{want}': {(hit ? "OK" : "MISSING")}");
            if (!hit) fail++;
        }

        // ---------- 4. 兜底门槛：过半 vs 1 ----------
        Console.WriteLine();
        Console.WriteLine("=== 4. the majority threshold must actually filter ===");
        var terms4 = TagSearch.BuildTerms(compound);
        var need4 = Math.Max(1, (terms4.Count + 1) / 2);
        var loose = vocab.SearchTerms(terms4, 1, 400).Count;
        var strict = vocab.SearchTerms(terms4, need4, 400).Count;
        Console.WriteLine($"  '{compound}' 词数={terms4.Count} 门槛={need4}: " +
                          $"门槛1 -> {loose} 条, 门槛{need4} -> {strict} 条");
        if (strict >= loose || strict == 0) { Console.WriteLine("  !! 门槛没有起到过滤作用"); fail++; }

        // 库里几乎什么都没有的查询：宁可少给，也别把"计算"类噪声排到前面
        var termsQ = TagSearch.BuildTerms("量子计算机");
        var needQ = Math.Max(1, (termsQ.Count + 1) / 2);
        var looseQ = vocab.SearchTerms(termsQ, 1, 400).Count;
        var strictQ = vocab.SearchTerms(termsQ, needQ, 400).Count;
        Console.WriteLine($"  '量子计算机' 词数={termsQ.Count} 门槛={needQ}: " +
                          $"门槛1 -> {looseQ} 条, 门槛{needQ} -> {strictQ} 条");
        if (strictQ >= looseQ) { Console.WriteLine("  !! 门槛没有起到过滤作用"); fail++; }

        // ---------- 5. 回灌给模型的文本格式 ----------
        Console.WriteLine();
        Console.WriteLine("=== 5. tool content must be copy-safe ===");
        var sample = TagSearch.Query(vocab, "初音未来", 6);
        var tool = TagSearch.ToToolContent(sample, "初音未来", 2);
        var lines = tool.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var numbered = lines.Where(l => l.Length > 2 && char.IsDigit(l[0])).ToList();
        var allLabelled = numbered.Count > 0 && numbered.All(l => l.Contains("tag=", StringComparison.Ordinal));
        var tagCopyable = sample.All(h => tool.Contains("tag=" + h.Tag, StringComparison.Ordinal));
        Console.WriteLine($"  行数={lines.Length} 编号行={numbered.Count} 都带 tag= {(allLabelled ? "OK" : "BAD")}");
        Console.WriteLine($"  每个标签都能被原样抄到 {(tagCopyable ? "OK" : "BAD")}");
        Console.WriteLine($"  含剩余次数 {(tool.Contains("searches remaining:", StringComparison.Ordinal) ? "OK" : "BAD")}");
        if (!allLabelled || !tagCopyable) fail++;
        if (!tool.Contains("searches remaining:", StringComparison.Ordinal)) fail++;

        // 空结果要如实说"没有"，而不是给一段空列表让模型自由发挥
        var empty = TagSearch.ToToolContent(new List<TagSearch.Hit>(), "zzz", 1);
        var emptyOk = empty.Contains("no tags matched", StringComparison.Ordinal);
        Console.WriteLine($"  空结果文本 {(emptyOk ? "OK" : "BAD")}: {empty.Replace("\n", " | ")}");
        if (!emptyOk) fail++;

        // ---------- 6. 反引号解析 ----------
        Console.WriteLine();
        Console.WriteLine("=== 6. BacktickedTags ===");
        var btCases = new (string Text, int Want)[]
        {
            ("use `blush` and `shy`", 2),
            ("no backticks here", 0),
            ("unpaired `blush only", 0),
            ("empty `` pair", 0),
        };
        foreach (var (text, want) in btCases)
        {
            var got = TagSearch.BacktickedTags(text);
            Console.WriteLine($"  {text,-24} -> {got.Count} (want {want}) {(got.Count == want ? "OK" : "BAD")}");
            if (got.Count != want) fail++;
        }

        // ---------- 7. 存在性核对：抓幻觉，不误报控制词 ----------
        Console.WriteLine();
        Console.WriteLine("=== 7. SuspiciousMentions ===");
        var susCases = new (string Text, string[] WantSuspicious)[]
        {
            // 实测原样：模型把 `quantum symbol (honkai: star rail)` 缩成了不存在的名字
            ("推荐 `quantum protocol` 和 `quantum symbol`", new[] { "quantum symbol" }),
            // 实测原样：真实标签是 `ram (computer)`，它缩成了 `ram`
            ("可以用 `ram` 这个标签", new[] { "ram" }),
            // 真标签不许误报
            ("推荐 `blush` 和 `smile`", Array.Empty<string>()),
            ("`hatsune miku` 是核心标签", Array.Empty<string>()),
            // 控制词与风格触发器本来就合法但不在标签表里，误报会让每次回答都带警告
            ("前缀是 `best quality`、`score_7`、`safe`", Array.Empty<string>()),
            ("触发词 `@sayori style`", Array.Empty<string>()),
            // 模型建议的搜索词也会被标出来——措辞上要能覆盖这种情况
            ("试试用 `量子` 作为关键词", new[] { "量子" }),
        };
        foreach (var (text, want) in susCases)
        {
            var got = TagSearch.SuspiciousMentions(text, vocab);
            var ok = got.Count == want.Length &&
                     want.All(w => got.Contains(w, StringComparer.OrdinalIgnoreCase));
            Console.WriteLine($"  {text,-40} -> [{string.Join(", ", got)}] {(ok ? "OK" : "BAD")}");
            if (!ok) fail++;
        }

        // ---------- 8. 可点列表来自工具结果 ----------
        Console.WriteLine();
        Console.WriteLine("=== 8. RankForUser ===");
        var found = TagSearch.Query(vocab, "微笑", 40);
        var ranked = TagSearch.RankForUser(found, 10);
        var distinct = ranked.Select(h => h.Tag.ToLowerInvariant()).Distinct().Count() == ranked.Count;
        var ordered = ranked.Zip(ranked.Skip(1)).All(p => p.First.PostCount >= p.Second.PostCount);
        Console.WriteLine($"  {found.Count} 条 -> 去重后 {ranked.Count} 条（上限 10），" +
                          $"无重复 {(distinct ? "OK" : "BAD")}，引用数降序 {(ordered ? "OK" : "BAD")}");
        if (!distinct || !ordered || ranked.Count > 10) fail++;
        var noneRanked = TagSearch.RankForUser(new List<TagSearch.Hit>());
        Console.WriteLine($"  空输入 -> {noneRanked.Count} 条 {(noneRanked.Count == 0 ? "OK" : "BAD")}");
        if (noneRanked.Count != 0) fail++;

        // ---------- 9. Anima 原生标签必须能搜到 ----------
        Console.WriteLine();
        Console.WriteLine("=== 9. Anima-native tags (absent from the merged table) must be searchable ===");
        // 这一批约 12,125 个标签只在 anima_index 里，best 表没有。搜索如果只扫 best，
        // 用户就搜不到它们——而程序别处（校验/解析）都认它们，属于自相矛盾。
        // 下面这几个是实测确认"只在 anima_index 里"的，覆盖高/中/低引用三档。
        Console.WriteLine($"  词库报告 anima-only 数量 = {vocab.AnimaOnlyCount}");
        if (vocab.AnimaOnlyCount < 1000) { Console.WriteLine("  !! anima-only 没建起来"); fail++; }
        foreach (var t in new[] { "female focus", "black footwear", "topless", "cat smile", "vertical stripes" })
        {
            var hits = TagSearch.Query(vocab, t, 30);
            var self = hits.FirstOrDefault(h => string.Equals(h.Tag, t, StringComparison.OrdinalIgnoreCase));
            var isFirst = hits.Count > 0 && string.Equals(hits[0].Tag, t, StringComparison.OrdinalIgnoreCase);
            var status = vocab.Status(t);
            Console.WriteLine($"  {t,-18} -> {hits.Count,4} 条, first='{(hits.Count > 0 ? hits[0].Tag : "-")}', " +
                              $"含自身={(self is not null ? "是" : "否")} 排首位={(isFirst ? "是" : "否")} status={status}");
            if (self is null || status == "unknown") fail++;
        }

        // ---------- 10. 真实标签的自查 ----------
        Console.WriteLine();
        Console.WriteLine("=== 10. every real canonical tag: visible, and first when live ===");
        var files = Directory.EnumerateFiles(dir, "*.txt").OrderBy(x => x, StringComparer.Ordinal).ToList();
        var distinctTags = new List<string>();
        var seenTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            string text;
            try { text = File.ReadAllText(f, Encoding.UTF8).Trim(); }
            catch { continue; }
            foreach (var t in PromptText.Parse(text, vocab).Tags)
                if (seenTags.Add(t)) distinctTags.Add(t);
        }

        // 两条分开的不变量，混在一起会互相掩盖：
        //   A. **可见性**：词库认得的写法必须出现在自己的搜索结果里。
        //      搜不到自己意味着这个标签在窗口里根本找不到。
        //   B. **排序**：引用正常的标签必须排第一。`no-usage`（Danbooru 里
        //      post_count=0 的退役残桩，如 `bangs`）排在活跃前缀匹配之后是
        //      **正确行为**——它不是能用的标签，所以不适用这条。
        var checkedTags = 0;
        var skipped = 0;
        var unknown = 0;
        var missing = new List<string>();
        var notFirst = new List<string>();
        foreach (var t in distinctTags)
        {
            // 控制词和风格触发器不是 Danbooru 标签，搜不到自己是正常的
            if (VocabDb.IsControlToken(t) || VocabDb.IsStyleTrigger(t)) { skipped++; continue; }
            // 只测本来就是正典形式的标签（旧别名搜出正典名是正确行为）
            var r = TagEdit.Resolve(t, vocab);
            if (!string.Equals(r.Tag, t, StringComparison.Ordinal)) { skipped++; continue; }
            // 词库完全不认识的写法（如 `white back bow`、`silver bell` 这类
            // 颜色+名词复合短语）只能给出部分匹配，那不是缺陷，跳过。
            if (string.Equals(r.Status, "unknown", StringComparison.Ordinal)) { unknown++; continue; }

            // 用界面同样的上限来查可见性：退役标签（score 被罚 +10）会排在
            // 所有活跃匹配之后，用小上限（如 30）截断会把它们切掉——那是
            // "列表太长"的问题，不是"搜不到"。
            var hits = TagSearch.Query(vocab, t, TagSearch.UiLimit);
            var self = hits.FirstOrDefault(h => string.Equals(h.Tag, t, StringComparison.OrdinalIgnoreCase));
            if (self is null) { missing.Add(t); continue; }

            checkedTags++;
            if (string.Equals(r.Status, "no-usage", StringComparison.Ordinal)) continue;
            if (hits.Count > 0 && string.Equals(hits[0].Tag, t, StringComparison.OrdinalIgnoreCase)) continue;
            notFirst.Add($"'{t}' [{r.Status}] -> '{hits[0].Tag}'");
        }
        Console.WriteLine($"  检查 {checkedTags} 个词库认识的标签" +
                          $"（跳过 {skipped} 个控制词/别名，{unknown} 个词库不认）");
        Console.WriteLine($"  A. 搜不到自己：{missing.Count} 个");
        foreach (var b in missing.Take(10)) Console.WriteLine("    " + b);
        Console.WriteLine($"  B. 不排在首位：{notFirst.Count} 个");
        foreach (var b in notFirst.Take(10)) Console.WriteLine("    " + b);
        if (missing.Count > 0) fail++;
        if (notFirst.Count > 0) fail++;

        // ---------- 11. 耗时预算 ----------
        Console.WriteLine();
        Console.WriteLine("=== 11. timing budget (the window debounces at 200ms) ===");
        var swPlain = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++) TagSearch.Query(vocab, "blush", TagSearch.UiLimit);
        swPlain.Stop();
        var swSplit = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++) TagSearch.Query(vocab, compound, TagSearch.UiLimit);
        swSplit.Stop();
        var plainMs = swPlain.ElapsedMilliseconds / 5.0;
        var splitMs = swSplit.ElapsedMilliseconds / 3.0;
        Console.WriteLine($"  普通查询 {plainMs:F0} ms/次（预算 150），" +
                          $"拆词兜底 {splitMs:F0} ms/次（预算 900）");
        if (plainMs > 150) { Console.WriteLine("  !! 普通查询超预算"); fail++; }
        if (splitMs > 900) { Console.WriteLine("  !! 拆词兜底超预算"); fail++; }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "search: all checks passed" : $"search: {fail} FAILED");
        return fail == 0 ? 0 : 1;
    }
}
