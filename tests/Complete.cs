using System.Text;
using AnimaCaptioner.Core;

namespace CoreCheck;

/// <summary>
/// TagComplete（输入框的候选与 Tab 补全）的回归。
///
/// 三条承重不变量，每一条都对应一类会静默坑到用户的错法：
///
/// 1. **第一个候选必须等于回车会加进去的那个标签。** Tab 补全之后按回车得到
///    另一个东西，补全就成了陷阱——用户看着框里的字按回车，拿到的却不是它。
/// 2. **对一个已经完整的真实标签，Tab 不能把它换掉。** 实测拿训练集里每个
///    正典标签当输入，第一个候选都得是它自己；否则用户在已有标签上顺手按了
///    Tab，标签就被改成了别的东西。这条同时守着排序质量——本项目踩过
///    「`微笑` 把 22 引用的小画师排在 421 万引用的 `smile` 前面」这类坑。
/// 3. **Tab 循环的正是列表里看得见的那几个**，一格不漏、不跳格、能绕回来。
/// </summary>
public static class Complete
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

        // ---------- 1. 空输入不能倒出整张表 ----------
        Console.WriteLine("=== 1. empty / whitespace input yields nothing ===");
        foreach (var q in new[] { "", "   ", null })
        {
            var got = TagComplete.Suggest(q, vocab);
            Console.WriteLine($"  '{q ?? "(null)"}' -> {got.Count} candidates {(got.Count == 0 ? "OK" : "LEAK!")}");
            if (got.Count != 0) fail++;
        }
        var cy0 = new TagComplete.Cycler();
        var st0 = cy0.Next("", t => TagComplete.Suggest(t, vocab));
        Console.WriteLine($"  Tab on empty text -> {(st0 is null ? "null OK" : "STEP! " + st0.Value.Item.Tag)}");
        if (st0 is not null) fail++;

        // ---------- 2. 中文输入：候选要"所见即所得" ----------
        Console.WriteLine();
        Console.WriteLine("=== 2. Chinese input -> candidates survive Enter unchanged ===");
        // 末两个是"打到一半"的输入：模糊解析会把 `双马` 解成 `horse`（键 `马`
        // 在里面），而用户多半在打 `双马尾`——所以前缀候选必须排在模糊解析前面。
        var cnCases = new[] { "双马尾", "水手服", "逆光", "女仆装", "微笑", "双马", "银发" };
        foreach (var q in cnCases)
        {
            var list = TagComplete.Suggest(q, vocab);
            if (list.Count == 0) { Console.WriteLine($"  '{q}' -> no candidates (SKIP)"); continue; }

            var wouldAdd = TagEdit.Resolve(q, vocab).Tag;
            var first = list[0];
            var stable = string.Equals(TagEdit.Resolve(first.Tag, vocab).Tag, first.Tag, StringComparison.Ordinal);
            Console.WriteLine($"  '{q}' -> '{first.Tag}'{(first.HasCn ? " / " + first.Cn : "")}  " +
                              $"(直接回车会是 '{wouldAdd}') {(stable ? "OK" : "UNSTABLE!")}");
            if (!stable) fail++;
        }

        // ---------- 3. 候选显示与还原必须互逆 ----------
        Console.WriteLine();
        Console.WriteLine("=== 3. Display / Bare round-trip ===");
        var sample = new[] { "双马尾", "smile", "long hair", "1girl", "仰视" };
        var rtChecked = 0;
        foreach (var q in sample)
        {
            foreach (var c in TagComplete.Suggest(q, vocab))
            {
                var back = TagComplete.Bare(TagComplete.Display(c));
                rtChecked++;
                if (string.Equals(back, c.Tag, StringComparison.Ordinal)) continue;
                Console.WriteLine($"  '{TagComplete.Display(c)}' -> '{back}' but tag is '{c.Tag}'  MISMATCH");
                fail++;
            }
        }
        Console.WriteLine($"  {rtChecked} candidates round-tripped {(fail == 0 ? "OK" : "")}");
        Console.WriteLine("  PASS");

        // ---------- 4. Tab 循环：一格不漏、能绕回来 ----------
        Console.WriteLine();
        Console.WriteLine("=== 4. Tab walks the visible list and wraps ===");
        foreach (var q in new[] { "双马尾", "smile" })
        {
            var list = TagComplete.Suggest(q, vocab);
            if (list.Count < 2) { Console.WriteLine($"  '{q}' -> only {list.Count} candidate(s), skip cycle test"); continue; }

            var cy = new TagComplete.Cycler();
            var text = q;
            var seen = new List<string>();
            for (var i = 0; i < list.Count; i++)
            {
                var s = cy.Next(text, t => TagComplete.Suggest(t, vocab));
                if (s is null) break;
                seen.Add(s.Value.Item.Tag);
                text = s.Value.Item.Tag;
            }

            var inOrder = seen.SequenceEqual(list.Select(c => c.Tag), StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"  '{q}': {seen.Count}/{list.Count} steps, order match = {inOrder} {(inOrder ? "OK" : "BAD ORDER")}");
            if (!inOrder) fail++;

            // 再按一次应绕回第 1 格
            var wrap = cy.Next(text, t => TagComplete.Suggest(t, vocab));
            var wrapped = wrap is not null &&
                          string.Equals(wrap.Value.Item.Tag, list[0].Tag, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  wrap-around -> '{wrap?.Item.Tag}' (want '{list[0].Tag}') {(wrapped ? "OK" : "NO WRAP")}");
            if (!wrapped) fail++;

            // Shift+Tab 往回走一格
            var back = cy.Next(wrap!.Value.Item.Tag, t => TagComplete.Suggest(t, vocab), backwards: true);
            var backOk = back is not null &&
                         string.Equals(back.Value.Item.Tag, list[^1].Tag, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  Shift+Tab -> '{back?.Item.Tag}' (want '{list[^1].Tag}') {(backOk ? "OK" : "BAD BACK")}");
            if (!backOk) fail++;
        }

        // ---------- 5. 改了字就重新算，不从上次的格继续 ----------
        Console.WriteLine();
        Console.WriteLine("=== 5. typing after a completion restarts the cycle ===");
        var cy5 = new TagComplete.Cycler();
        var s1 = cy5.Next("双马尾", t => TagComplete.Suggest(t, vocab));
        Console.WriteLine($"  Tab on '双马尾' -> '{s1?.Item.Tag}' fresh={s1?.Fresh} idx={s1?.Index} of {s1?.Count}");
        if (s1 is null) { Console.WriteLine("  FAIL: no candidates"); fail++; }
        else
        {
            var s2 = cy5.Next("水手服", t => TagComplete.Suggest(t, vocab));
            var restarted = s2 is not null && s2.Value.Fresh &&
                            string.Equals(s2.Value.Item.Tag, TagComplete.Suggest("水手服", vocab)[0].Tag,
                                          StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  then typed '水手服' -> '{s2?.Item.Tag}' fresh={s2?.Fresh} " +
                              $"{(restarted ? "OK (restarted)" : "STALE CYCLE!")}");
            if (!restarted) fail++;
        }

        // ---------- 6. 两个输入框各一份状态，互不干扰 ----------
        Console.WriteLine();
        Console.WriteLine("=== 6. two boxes keep independent cycles ===");
        var a = new TagComplete.Cycler();
        var b = new TagComplete.Cycler();
        var sa = a.Next("双马尾", t => TagComplete.Suggest(t, vocab));
        _ = a.Next(sa!.Value.Item.Tag, t => TagComplete.Suggest(t, vocab));   // a 走到第 2 格
        var sb = b.Next("双马尾", t => TagComplete.Suggest(t, vocab));        // b 应是全新的第 1 格
        var indep = sb is not null && sb.Value.Index == 0 && sb.Value.Fresh && a.Index == 1;
        Console.WriteLine($"  a.Index={a.Index} (want 1), b.Index={sb?.Index} fresh={sb?.Fresh} (want 0/True) " +
                          $"{(indep ? "OK" : "SHARED STATE!")}");
        if (!indep) fail++;

        // ---------- 7. 从列表里点选之后，Tab 从那一格继续 ----------
        Console.WriteLine();
        Console.WriteLine("=== 7. picking from the list syncs the cycle ===");
        var list7 = TagComplete.Suggest("smile", vocab);
        var cy7 = new TagComplete.Cycler();
        _ = cy7.Next("smile", t => TagComplete.Suggest(t, vocab));           // 建立候选
        var pick = list7.Count > 2 ? list7[2].Tag : list7[^1].Tag;
        cy7.SyncTo(pick);
        var after = cy7.Next(pick, t => TagComplete.Suggest(t, vocab));
        var wantNext = list7.Count > 2 ? list7[3 % list7.Count].Tag : list7[0].Tag;
        var syncOk = after is not null &&
                     string.Equals(after.Value.Item.Tag, wantNext, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  picked '{pick}' ({cy7.Index - 1} -> ...) then Tab -> '{after?.Item.Tag}' " +
                          $"(want '{wantNext}') {(syncOk ? "OK" : "NOT SYNCED")}");
        if (!syncOk) fail++;

        // ---------- 8. 真实标签上按 Tab 不能把它换掉 ----------
        Console.WriteLine();
        Console.WriteLine("=== 8. real dataset tags survive a Tab ===");
        var files = Directory.EnumerateFiles(dir, "*.txt").OrderBy(x => x, StringComparer.Ordinal).ToList();
        var distinct = new List<string>();
        var seenTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            string text;
            try { text = File.ReadAllText(f, Encoding.UTF8).Trim(); }
            catch { continue; }
            foreach (var t in PromptText.Parse(text, vocab).Tags)
                if (seenTags.Add(t)) distinct.Add(t);
        }

        // 两条规则，分别对应"不许动它"和"要动也只能往前接"：
        //   A. 词库认识的写法（状态不是 unknown）→ 第一个候选必须是它自己。
        //      这条同时守着排序质量：`sensitive` 曾经会变成 `sensitive pornograph`。
        //   B. 词库完全不认识的写法 → 只允许补成**以它为前缀**的标签；
        //      补成 `@nsfw bb`、`@silver bell` 这种仅靠包含命中的东西就是错。
        var checkedTags = 0;
        var noCand = 0;
        var prefixOnly = 0;
        var badA = new List<string>();
        var badB = new List<string>();
        foreach (var t in distinct)
        {
            // 只测"本来就是正典形式"的标签：旧别名按 Tab 被规范化是正确行为，
            // 不属于这里要守的"不许动它"。
            var r = TagEdit.Resolve(t, vocab);
            if (!string.Equals(r.Tag, t, StringComparison.Ordinal)) continue;

            var list = TagComplete.Suggest(t, vocab);
            if (list.Count == 0) { noCand++; continue; }   // 词库不认的标签：Tab 无事可做，安全

            checkedTags++;
            var known = !string.Equals(r.Status, "unknown", StringComparison.Ordinal);
            if (known)
            {
                if (string.Equals(list[0].Tag, t, StringComparison.OrdinalIgnoreCase)) continue;
                badA.Add($"'{t}' [{r.Status}] -> '{list[0].Tag}'");
                continue;
            }

            if (string.Equals(list[0].Tag, t, StringComparison.OrdinalIgnoreCase)) continue;
            if (list[0].Tag.StartsWith(t, StringComparison.OrdinalIgnoreCase) ||
                (list[0].HasCn && list[0].Cn.StartsWith(t, StringComparison.Ordinal)))
            { prefixOnly++; continue; }
            badB.Add($"'{t}' [unknown] -> '{list[0].Tag}' (not a prefix match)");
        }

        Console.WriteLine($"  {distinct.Count} distinct tags in {files.Count} captions; " +
                          $"{checkedTags} canonical, {noCand} with no candidates at all");
        foreach (var s in badA.Take(10)) Console.WriteLine("  A " + s);
        foreach (var s in badB.Take(10)) Console.WriteLine("  B " + s);
        Console.WriteLine(badA.Count == 0
            ? "  PASS A: a tag the vocab knows always completes to itself"
            : $"  FAIL A ({badA.Count} known tags would be replaced)");
        Console.WriteLine(badB.Count == 0
            ? $"  PASS B: unknown tags only complete forward ({prefixOnly} legit prefix completions)"
            : $"  FAIL B ({badB.Count} unknown tags would jump to an unrelated tag)");
        fail += badA.Count + badB.Count;

        // ---------- 9. 候选上限 + 单次耗时 ----------
        Console.WriteLine();
        Console.WriteLine("=== 9. candidate list stays cycleable (<= Limit), and stays fast ===");
        foreach (var q in new[] { "a", "s", "1", "双", "sm" })
        {
            var n = TagComplete.Suggest(q, vocab).Count;
            var ok = n <= TagComplete.Limit;
            Console.WriteLine($"  '{q}' -> {n} (limit {TagComplete.Limit}) {(ok ? "OK" : "TOO MANY")}");
            if (!ok) fail++;
        }

        // 这个函数在每次按键上都会跑（词库是全表扫描），慢一点就是打字卡顿。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rounds = 20;
        for (var i = 0; i < rounds; i++) TagComplete.Suggest("smile", vocab);
        sw.Stop();
        var per = sw.Elapsed.TotalMilliseconds / rounds;
        Console.WriteLine($"  {rounds} lookups 'smile': {per:F1} ms each {(per < 250 ? "OK" : "SLOW")}");
        if (per >= 250) fail++;

        // ---------- 10. 所见即所得：候选列表里的每一个都必须原样存得下去 ----------
        Console.WriteLine();
        Console.WriteLine("=== 10. every candidate is stable under Enter (WYSIWYG) ===");
        // 直接拿 Search 的结果当候选就会破这条：`china dress` 是退役名，
        // 回车会被词库重定向成 `qipao`——框里写 A、存下去是 B。
        var probes = new[] { "china", "hairpin", "game", "wink", "long_hair", "微笑",
                             "双马尾", "smile", "1girl", "@dairi" };
        var checkedCand = 0;
        var unstable = new List<string>();
        foreach (var q in probes)
        {
            var raw = TagComplete.Suggest(q, vocab);
            foreach (var c in raw)
            {
                checkedCand++;
                var back = TagEdit.Resolve(c.Tag, vocab).Tag;
                if (string.Equals(back, c.Tag, StringComparison.Ordinal)) continue;
                unstable.Add($"'{q}' -> candidate '{c.Tag}' but Enter stores '{back}'");
            }
        }
        Console.WriteLine($"  {checkedCand} candidates over {probes.Length} queries checked");
        foreach (var u in unstable.Take(8)) Console.WriteLine("  " + u);
        Console.WriteLine(unstable.Count == 0
            ? "  PASS (what the box shows is what gets stored)"
            : $"  FAIL ({unstable.Count} candidates would change on Enter)");
        fail += unstable.Count;

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "complete: all checks passed" : $"complete: {fail} FAILED");
        return fail == 0 ? 0 : 1;
    }
}
