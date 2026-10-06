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

        // ---- 3. the control tags MUST land in meta/count, not general ----
        Console.WriteLine("\n[3] control tags that must not fall into general:");
        string[] must = { "masterpiece", "best quality", "score_7", "safe", "explicit",
                          "nsfw", "sensitive", "year 2025", "newest", "highres",
                          "1girl", "solo", "absurdres", "jpeg artifacts" };
        var bad = 0;
        foreach (var t in must)
        {
            var s = TagSections.Classify(t, v);
            var ok = s is TagSections.Meta or TagSections.Count;
            if (!ok) bad++;
            Console.WriteLine($"    {(ok ? "ok  " : "BAD ")} {t,-18} -> {s}");
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

        return notIdempotent.Count == 0 && bad == 0 && badColor == 0 && ord == 0 ? 0 : 1;
    }
}
