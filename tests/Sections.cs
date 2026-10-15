using System.Text;
using AnimaCaptioner.Core;

/// <summary>
/// Exercises the real TagSections code against the real training set.
///
/// The decisive test is idempotence on the 53 existing captions. Those were
/// hand-ordered and independently verified to already follow the official
/// section order, so if the classifier disagrees with the human ordering, this
/// prints exactly which tags it would move and where. That is the only way to
/// catch a misclassification without eyeballing 2,335 tag instances.
/// </summary>
internal static class Sections
{
    public static int Run(string dir)
    {
        var cfg = AppSettings.Load();
        var v = new VocabDb();
        v.Load(cfg.VocabDbPath, cfg.SeedPath);

        var files = Directory.EnumerateFiles(dir, "*.txt").OrderBy(x => x, StringComparer.Ordinal).ToList();
        Console.WriteLine($"section check over {files.Count} captions in {dir}");

        // ---- 1. every caption already canonical? ----
        var notIdempotent = new List<string>();
        foreach (var f in files)
        {
            var tags = CaptionFile.Load(f).Tags;
            var after = TagSections.Canonicalize(tags, v);
            if (!tags.SequenceEqual(after, StringComparer.Ordinal)) notIdempotent.Add(f);
        }
        Console.WriteLine($"\n[1] canonical order already: {files.Count - notIdempotent.Count}/{files.Count}");
        foreach (var f in notIdempotent)
        {
            Console.WriteLine("    would reorder: " + Path.GetFileName(f));
            var tags = CaptionFile.Load(f).Tags;
            var after = TagSections.Canonicalize(tags, v);
            for (var i = 0; i < tags.Count; i++)
            {
                if (i >= after.Count) break;
                if (!string.Equals(tags[i], after[i], StringComparison.Ordinal))
                {
                    Console.WriteLine($"      pos {i}: was [{tags[i]}] -> now [{after[i]}]");
                    break;
                }
            }
        }

        // ---- 2. classification of every distinct tag ----
        var distinct = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
            foreach (var t in CaptionFile.Load(f).Tags) distinct.Add(t);

        var bySection = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var s in TagSections.All) bySection[s.Key] = new List<string>();
        var fallback = new List<string>();
        foreach (var t in distinct)
        {
            var sec = TagSections.Classify(t, v);
            bySection[sec].Add(t);
            if (v.CategoryOf(t).Length == 0) fallback.Add(t);
        }

        Console.WriteLine($"\n[2] {distinct.Count} distinct tags classified");
        foreach (var s in TagSections.All)
            Console.WriteLine($"    {s.Title,-18} {bySection[s.Key].Count,4}");
        Console.WriteLine($"    (of which unknown to both indexes: {fallback.Count})");

        // ---- 3. the control tags MUST land in the head sections, not general ----
        Console.WriteLine("\n[3] control tags that must not fall into general:");
        // 官方第一段那个方括号里的四类现在各自成段，所以期望值也按类拆开了。
        // 这一改动的依据见 Core/TagSections.cs 顶部注释。
        var must = new (string Tag, string Want)[]
        {
            ("masterpiece", TagSections.Quality), ("best quality", TagSections.Quality),
            ("score_7", TagSections.Quality), ("safe", TagSections.Safety),
            ("explicit", TagSections.Safety), ("nsfw", TagSections.Safety),
            ("sensitive", TagSections.Safety), ("year 2025", TagSections.Period),
            ("newest", TagSections.Period), ("highres", TagSections.Meta),
            ("1girl", TagSections.Count), ("solo", TagSections.Count),
            ("absurdres", TagSections.Meta), ("jpeg artifacts", TagSections.Meta),
        };
        var bad = 0;
        foreach (var (t, want) in must)
        {
            var s = TagSections.Classify(t, v);
            var ok = s == want;
            if (!ok) bad++;
            Console.WriteLine($"    {(ok ? "ok  " : "BAD ")} {t,-18} -> {s}{(ok ? "" : $" want {want}")}");
        }
        Console.WriteLine(bad == 0 ? "    all correct" : $"    {bad} WRONG");

        // ---- 4. the model card's own example, in its own order ----
        Console.WriteLine("\n[4] model card example (already in official order) -> canonicalized:");
        var example = ("year 2025, newest, normal quality, score_5, highres, safe, 1girl, oomuro sakurako, "
            + "yuru yuri, @nnn yryr, smile, brown hair, hat, solo, fur-trimmed gloves, open mouth, "
            + "long hair, gift box, fang, skirt, red gloves, blunt bangs, gloves, one eye closed, "
            + "shirt, brown eyes, santa costume, red hat, skin fang, twitter username, white background, "
            + "holding bag, fur trim, simple background, brown skirt, bag, gift bag, looking at viewer, "
            + "santa hat, ;d, red shirt, box, gift, fur-trimmed headwear, holding, red capelet, "
            + "holding box, capelet").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        var exAfter = TagSections.Canonicalize(example, v);
        var exSame = example.SequenceEqual(exAfter, StringComparer.Ordinal);
        Console.WriteLine($"    idempotent on the official example: {(exSame ? "YES" : "NO")}");

        // 官方那个示例里唯一会被挪动的是 `solo`：示例把它放在 general 段中间
        // （smile, brown hair, hat, solo, ...），本程序把它归进「人物数量」段，
        // 和 1girl 放一起。这不是随手定的：
        //   - 官方给第二节的写法是 [1girl/1boy/1other etc]，社区指南一律称其为
        //     "subject count"，而 solo 正是数量语义；
        //   - 用户自己那 53 个 caption 全部把 solo 紧跟在 1girl 后面（实测 35 个
        //     含 solo 的 caption 里，它都在 index 2）。归到 general 会让这 35 个
        //     全部被重排，等于推翻用户已经定好的约定；
        //   - 官方明确说段内顺序随意，所以示例里 solo 的位置不构成"它属于
        //     general"的论据。
        // 所以这里保留 count，并把差异显式报出来而不是假装没这回事。
        var diffs = new List<int>();
        for (var i = 0; i < Math.Min(example.Count, exAfter.Count); i++)
            if (!string.Equals(example[i], exAfter[i], StringComparison.Ordinal)) diffs.Add(i);
        Console.WriteLine($"    positions differing from the card example: {diffs.Count}" +
                          (diffs.Count > 0 ? " -> " + string.Join(", ", diffs) : ""));
        if (diffs.Count > 0)
        {
            // 报出真正被挪动的那个标签（集合差集看不出"位置变了但仍在"的情况）
            var firstAt = diffs[0];
            Console.WriteLine($"    first displacement at index {firstAt}: " +
                              $"card has [{example[firstAt]}] ({TagSections.Classify(example[firstAt], v)}), " +
                              $"we put [{exAfter[firstAt]}] ({TagSections.Classify(exAfter[firstAt], v)})");
            var displaced = example.Where(x => !example.IndexOf(x).Equals(exAfter.IndexOf(x))).ToList();
            Console.WriteLine($"    displaced tags: [{string.Join(", ", displaced)}] -> " +
                string.Join(", ", displaced.Select(x => $"{x}={TagSections.Classify(x, v)}")));
        }
        Console.WriteLine("    resulting order:");
        foreach (var t in exAfter)
            Console.WriteLine($"      {TagSections.Classify(t, v),-10} {t}");

        // ---- 5. within-section order must survive ----
        Console.WriteLine("\n[5] within-section order preservation:");
        var mixed = new List<string> { "1girl", "smile", "solo", "long hair", "@sayori style", "safe" };
        var m1 = TagSections.Canonicalize(mixed, v);
        var m2 = TagSections.Canonicalize(m1, v);
        Console.WriteLine("    in : " + string.Join(", ", mixed));
        Console.WriteLine("    out: " + string.Join(", ", m1));
        Console.WriteLine("    idempotent: " + (m1.SequenceEqual(m2, StringComparer.Ordinal) ? "YES" : "NO"));

        // 打乱同一段内部的顺序，结果必须逐字保留那个新顺序（这才叫"段内可调"）
        var gen1 = new List<string> { "smile", "long hair", "blunt bangs", "cat ears", "twintails" };
        var gen2 = new List<string> { "twintails", "blunt bangs", "cat ears", "smile", "long hair" };
        var r1 = TagSections.Canonicalize(gen1, v);
        var r2 = TagSections.Canonicalize(gen2, v);
        var preserved = r1.SequenceEqual(gen1, StringComparer.Ordinal)
                     && r2.SequenceEqual(gen2, StringComparer.Ordinal);
        Console.WriteLine("    two different general-only orders both preserved verbatim: " +
                          (preserved ? "YES" : "NO"));
        Console.WriteLine("      a: " + string.Join(", ", r1));
        Console.WriteLine("      b: " + string.Join(", ", r2));
        if (!preserved) bad++;

        // 同一段内部的相对次序在跨段排序后也不能变
        var shuffled = new List<string> { "cat ears", "safe", "smile", "1girl", "twintails", "@sayori style" };
        var sorted = TagSections.Canonicalize(shuffled, v);
        var genOnly = sorted.Where(x => TagSections.Classify(x, v) == TagSections.General).ToList();
        var genWant = shuffled.Where(x => TagSections.Classify(x, v) == TagSections.General).ToList();
        var intraOk = genOnly.SequenceEqual(genWant, StringComparer.Ordinal);
        Console.WriteLine("    general tags keep their relative order after sorting: " +
                          (intraOk ? "YES" : "NO"));
        Console.WriteLine("      got : " + string.Join(", ", genOnly));
        Console.WriteLine("      want: " + string.Join(", ", genWant));
        if (!intraOk) bad++;

        // ---- 6. colour strings normalise and reject junk ----
        Console.WriteLine("\n[6] colour normalisation:");
        var badColor = 0;
        foreach (var s in TagSections.All)
        {
            var n = TagSections.NormalizeHex(s.DefaultHex);
            var ok = n == s.DefaultHex;
            if (!ok) badColor++;
            Console.WriteLine($"    {s.Key,-10} default {s.DefaultHex} -> {(n ?? "<null>")} {(ok ? "ok" : "BAD")}");
        }
        // 8 位要去掉 alpha；非法输入必须是 null，而不是静默变成黑色
        var cases = new (string? In, string? Want)[]
        {
            ("#4A90D9", "#4A90D9"), ("4a90d9", "#4A90D9"), ("#FF4A90D9", "#4A90D9"),
            ("zzz", null), ("#12345", null), ("", null), (null, null),
        };
        foreach (var (input, want) in cases)
        {
            var got = TagSections.NormalizeHex(input);
            var ok = got == want;
            if (!ok) badColor++;
            Console.WriteLine($"    norm({input ?? "<null>",-12}) -> {got ?? "<null>",-10} " +
                              $"{(ok ? "ok" : $"BAD want {want ?? "<null>"}")}");
        }
        Console.WriteLine(badColor == 0 ? "    all correct" : $"    {badColor} WRONG");

        // ---- 7. structural edits (TagOrder) ----
        Console.WriteLine("\n[7] structural edits:");
        // realistic caption: meta, count, artist, then general
        var cap = new List<string>
        {
            "nsfw", "1girl", "solo", "@sayori style",
            "looking at viewer", "smile", "short hair", "sidelocks", "cat ears",
        };
        var ord = 0;
        void Show(string label, IEnumerable<string> t) =>
            Console.WriteLine($"    {label,-26} {string.Join(", ", t)}");

        // (a) move a general tag up inside its own segment
        var a = TagOrder.Move(cap, new[] { 5 }, -1, v);      // "smile" up
        Show("smile up:", a.Tags);
        var aOk = a.Moved == 1 && a.Tags.IndexOf("smile") == 4 && a.Tags.IndexOf("looking at viewer") == 5;
        Console.WriteLine($"      moved={a.Moved} blocked={a.Blocked} -> {(aOk ? "ok" : "BAD")}");
        if (!aOk) ord++;

        // (b) moving the FIRST general tag up must be refused, not silently sorted away
        var b = TagOrder.Move(cap, new[] { 4 }, -1, v);      // "looking at viewer" is the segment head
        Show("segment head up (refused):", b.Tags);
        var bOk = b.Moved == 0 && b.Blocked == 1 && b.Tags.SequenceEqual(cap, StringComparer.Ordinal);
        Console.WriteLine($"      moved={b.Moved} blocked={b.Blocked} unchanged={b.Tags.SequenceEqual(cap, StringComparer.Ordinal)} -> {(bOk ? "ok" : "BAD")}");
        if (!bOk) ord++;

        // (c) multi-select move down must not trample itself
        var c = TagOrder.Move(cap, new[] { 5, 6 }, 1, v);    // smile, short hair
        Show("smile+short hair down:", c.Tags);
        var cOk = c.Moved == 2
               && c.Tags.IndexOf("smile") == 6 && c.Tags.IndexOf("short hair") == 7
               && c.Tags.IndexOf("sidelocks") == 5;
        Console.WriteLine($"      moved={c.Moved} -> {(cOk ? "ok" : "BAD")}");
        if (!cOk) ord++;

        // (d) crossing a section boundary is refused
        var d = TagOrder.Move(cap, new[] { 3 }, 1, v);       // @sayori style down into general
        var dOk = d.Moved == 0 && d.Blocked == 1;
        Console.WriteLine($"    artist down across boundary: moved={d.Moved} blocked={d.Blocked} -> {(dOk ? "ok" : "BAD")}");
        if (!dOk) ord++;

        // (e) delete by pending index, including a multi-select, must hit the right rows
        var e = TagOrder.Delete(cap, new[] { 5, 7 });
        Show("delete idx 5 and 7:", e);
        var eOk = !e.Contains("smile") && !e.Contains("sidelocks") && e.Count == cap.Count - 2;
        Console.WriteLine($"      -> {(eOk ? "ok" : "BAD")}");
        if (!eOk) ord++;

        // (f) delete with indices given out of order must still be correct
        var fDel = TagOrder.Delete(cap, new[] { 7, 5 });
        var fOk = fDel.SequenceEqual(e, StringComparer.Ordinal);
        Console.WriteLine($"    delete out-of-order indices matches: {(fOk ? "ok" : "BAD")}");
        if (!fOk) ord++;

        // (g) drop-in-place then canonicalize must be a no-op for an already-sorted caption
        var gList = TagOrder.Drop(cap, new[] { "sidelocks" }, "smile");
        var gSorted = TagSections.Canonicalize(gList, v);
        Show("drop sidelocks before smile:", gList);
        var gOk = gSorted.SequenceEqual(gList, StringComparer.Ordinal);
        Console.WriteLine($"      canonicalize is a no-op afterwards: {(gOk ? "ok" : "BAD")}");
        if (!gOk) ord++;

        // (h) the real thing: every caption must survive move-then-undo round-trip
        var rtBad = 0;
        foreach (var rtFile in files)
        {
            var tags = CaptionFile.Load(rtFile).Tags;
            for (var i = 0; i < tags.Count; i++)
            {
                var down = TagOrder.Move(tags, new[] { i }, 1, v);
                if (down.Moved != 1) continue;
                var back = TagOrder.Move(down.Tags, new[] { i + 1 }, -1, v);
                if (back.Moved != 1 || !back.Tags.SequenceEqual(tags, StringComparer.Ordinal))
                {
                    rtBad++;
                    if (rtBad <= 3)
                        Console.WriteLine($"      ROUND-TRIP BAD {Path.GetFileName(rtFile)} idx {i}");
                }
            }
        }
        Console.WriteLine($"    move-down-then-up restores every caption: " +
                          $"{(rtBad == 0 ? "ok (all 53)" : $"{rtBad} BAD")}");
        if (rtBad > 0) ord++;

        // ---- 8. 自动排序（Arrange）必须把元信息也当头部控制词 ----
        //
        // 这一条是**有意偏离 Python 参考实现**的地方，所以显式钉住：
        // animatag.py 的 HEAD_GROUPS 只有 [QUALITY, SCORE, YEAR, RATINGS, COUNTS]
        // 五组，**没有元信息组**，于是 highres / absurdres 会掉进 body、
        // 按 prior 混进一般标签里排。C# 侧现在从 TagSections 派生，自动补上了
        // 这一组，所以这里必须验证它们排在 general 之前。
        //
        // 注意 parity 的 golden 用例**没有**覆盖到这一点（那三个用例里一个
        // 元信息词都没有），所以只能在这里测——这也是它之前能一直藏着的原因。
        Console.WriteLine("\n[8] auto-arrange keeps meta tags in the head, before general:");
        var noPrior = new Dictionary<string, double>(StringComparer.Ordinal);
        var arrIn = new[] { "1girl", "highres", "absurdres", "smile", "safe", "masterpiece" };
        var arrOut = VocabDb.Arrange(arrIn, noPrior);
        Console.WriteLine("    in : " + string.Join(", ", arrIn));
        Console.WriteLine("    out: " + string.Join(", ", arrOut));
        var idxHigh = arrOut.IndexOf("highres");
        var idxSmile = arrOut.IndexOf("smile");
        var headOk = idxHigh >= 0 && idxSmile >= 0 && idxHigh < idxSmile
                  && arrOut.IndexOf("absurdres") < idxSmile;
        Console.WriteLine($"    highres/absurdres before general: {(headOk ? "ok" : "BAD")}");
        if (!headOk) ord++;

        // 官方推荐前缀的前四段必须按 quality -> safety 落位
        var prefIn = new[] { "safe", "score_7", "masterpiece", "best quality", "year 2025", "1girl" };
        var prefOut = VocabDb.Arrange(prefIn, noPrior);
        Console.WriteLine("    prefix in : " + string.Join(", ", prefIn));
        Console.WriteLine("    prefix out: " + string.Join(", ", prefOut));
        var prefOk = prefOut.IndexOf("masterpiece") < prefOut.IndexOf("score_7")
                  && prefOut.IndexOf("score_7") < prefOut.IndexOf("year 2025")
                  && prefOut.IndexOf("year 2025") < prefOut.IndexOf("safe")
                  && prefOut.IndexOf("safe") < prefOut.IndexOf("1girl");
        Console.WriteLine($"    quality -> score -> year -> safety -> count: {(prefOk ? "ok" : "BAD")}");
        if (!prefOk) ord++;

        // ---------- 手改归类：词库里没有的标签也能指定类型 ----------
        // 用户报的问题：自定义触发词（`you chan` 这类）在词库里查不到，
        // Classify 只能兜底成 general，想把它归进"角色/画师"没有任何途径。
        Console.WriteLine();
        Console.WriteLine("=== 手改归类（覆盖词库推断）===");
        var ov = 0;

        void OvCheck(string label, bool ok, string got)
        {
            Console.WriteLine($"  {(ok ? "ok  " : "BAD ")} {label,-42} {got}");
            if (!ok) ov++;
        }

        var custom = "you chan";   // 词库里没有的自定义标签
        var beforeSec = TagSections.Classify(custom, v);
        OvCheck("词库里没有时兜底成 general", beforeSec == TagSections.General, beforeSec);

        // 指定成"角色"：立刻生效，且**优先于词库推断**
        var changed = TagSections.SetOverride(custom, TagSections.Character);
        OvCheck("SetOverride 报告改变", changed, changed.ToString());
        OvCheck("Classify 采纳手改", TagSections.Classify(custom, v) == TagSections.Character,
                TagSections.Classify(custom, v));
        OvCheck("OverrideOf 查得到", TagSections.OverrideOf(custom) == TagSections.Character,
                TagSections.OverrideOf(custom) ?? "(null)");

        // 大小写与空白不敏感：同一个标签
        OvCheck("忽略大小写与空白", TagSections.Classify("  YOU CHAN ", v) == TagSections.Character,
                TagSections.Classify("  YOU CHAN ", v));

        // **手改压过官方控制词**：用户明确指定的答案优先于硬编码清单
        TagSections.SetOverride("safe", TagSections.General);
        OvCheck("手改压过官方控制词（safe -> general）",
                TagSections.Classify("safe", v) == TagSections.General,
                TagSections.Classify("safe", v));
        TagSections.SetOverride("safe", null);   // 清掉，别影响后面的用例

        // 排序：手改之后规整应当把它挪进角色段
        var unsorted = new[] { "1girl", "smile", custom, "hatsune miku" };
        var reSorted = TagSections.Canonicalize(unsorted, v);
        OvCheck("规整后排到 count 之后、general 之前",
                reSorted.IndexOf(custom) > reSorted.IndexOf("1girl") &&
                reSorted.IndexOf(custom) < reSorted.IndexOf("smile"),
                string.Join(" | ", reSorted));

        // 清除手改：回到词库推断
        var cleared = TagSections.SetOverride(custom, null);
        OvCheck("清除报告改变", cleared, cleared.ToString());
        OvCheck("清除后回到 general", TagSections.Classify(custom, v) == TagSections.General,
                TagSections.Classify(custom, v));
        OvCheck("OverrideOf 变回 null", TagSections.OverrideOf(custom) is null,
                TagSections.OverrideOf(custom) ?? "(null)");
        OvCheck("重复清除不报告改变", !TagSections.SetOverride(custom, null), "false");

        // 认不出的段名一律拒收（文件被手改坏时不能让垃圾进到排序键里）
        OvCheck("拒绝不存在的段名", !TagSections.SetOverride(custom, "nonsense"), "false");
        OvCheck("拒绝散文（它不是大类）",
                !TagSections.SetOverride(custom, TagSections.Prose), "false");
        OvCheck("拒收后没有留下记录", TagSections.OverrideOf(custom) is null,
                TagSections.OverrideOf(custom) ?? "(null)");

        // 空标签不参与
        OvCheck("空标签拒收", !TagSections.SetOverride("   ", TagSections.Artist), "false");

        // 批量设定 + 快照（界面一次改一批，落盘用快照）
        TagSections.SetOverride("you chan", TagSections.Artist);
        TagSections.SetOverride("@syrstyle", TagSections.Artist);
        var snap = TagSections.OverridesSnapshot();
        OvCheck("快照带上两条", snap.Count == 2, snap.Count.ToString());
        OvCheck("快照内容正确",
                snap.TryGetValue("you chan", out var s1) && s1 == TagSections.Artist &&
                snap.TryGetValue("@syrstyle", out var s2) && s2 == TagSections.Artist,
                string.Join(", ", snap.Select(kv => kv.Key + "=" + kv.Value)));

        // 整体替换：读盘那条路（SetOverrides 是 Load 用的入口）
        TagSections.SetOverrides(new Dictionary<string, string> { ["x tag"] = TagSections.Series });
        OvCheck("整体替换后只剩一条", TagSections.OverrideCount == 1,
                TagSections.OverrideCount.ToString());
        OvCheck("替换后的映射生效",
                TagSections.Classify("x tag", v) == TagSections.Series,
                TagSections.Classify("x tag", v));
        // 传 null = 清空
        TagSections.SetOverrides(null);
        OvCheck("传 null 清空", TagSections.OverrideCount == 0, TagSections.OverrideCount.ToString());
        OvCheck("清空后回到推断",
                TagSections.Classify(custom, v) == TagSections.General,
                TagSections.Classify(custom, v));

        return notIdempotent.Count == 0 && bad == 0 && badColor == 0 && ord == 0 && ov == 0 ? 0 : 1;
    }
}
