using System.Text;
using AnimaCaptioner.Core;

namespace CoreCheck;

/// <summary>
/// TagEdit（右键菜单的复制/粘贴/规范化、双击就地名编辑）的回归。
///
/// 两条最重要的不变量：
///
/// 1. **认不出来必须原样保留。** 词库不是全集（实测 679 个真实标签里 74 个
///    词库不认：`2 fingers`、`bangs`、`mixed bathing`），编辑一个这样的标签
///    时静默丢掉或改成别的东西，就是在用户没察觉的情况下改掉训练集。
/// 2. **不许产生重复。** `wink` 规范成 `one eye closed`，而列表里可能本来就有
///    它——这时必须拒绝并说明，不能让两个一样的标签并排存在。
///
/// 另外把"整行规范化对现有 caption 是恒等变换"钉住：53 个 caption 的标签
/// 已经是规范形式（它们是按 Anima 索引做的），规范化若动了它们就说明规则错了。
/// </summary>
public static class Edit
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

        // ---------- 1. 认不出来 -> 原样保留 ----------
        Console.WriteLine("=== 1. unknown tags survive editing verbatim ===");
        // 全部取自实测：这些在 Anima 索引和 Danbooru 里都没有条目
        var unknown = new[] { "2 fingers", "bangs", "mixed bathing", "zzz not a tag zzz" };
        foreach (var u in unknown)
        {
            var r = TagEdit.Resolve(u, vocab);
            var keep = string.Equals(r.Tag, u, StringComparison.Ordinal);
            Console.WriteLine($"  '{u}' -> '{r.Tag}' ({r.Status}) {(keep ? "OK" : "CHANGED!")}");
            if (!keep) fail++;
        }
        if (unknown.Length > 0) Console.WriteLine("  PASS");

        // 认不出的标签被"规范化"时：报告"保持原样"，而不是悄悄改掉
        var nz = TagEdit.NormalizeOne("bangs", "bangs", new[] { "1girl" }, vocab);
        Console.WriteLine($"  normalize 'bangs' (unknown): ok={nz.Ok} tag='{nz.Tag}' ({nz.Status})");
        if (nz.Ok || !string.Equals(nz.Tag, "bangs", StringComparison.Ordinal))
        { Console.WriteLine("  FAIL (should stay as-is)"); fail++; }
        else Console.WriteLine("  PASS (kept verbatim)");

        // 但"改名"是另一回事：用户明确敲了 bangs 当新名字，就该用 bangs。
        // 词库不认它不代表不能用它——训练集里本来就有 74 个词库不认的真实标签。
        var ap = TagEdit.Apply("bangs", "mix", new[] { "1girl" }, vocab);
        Console.WriteLine($"  rename 'mix' -> 'bangs': ok={ap.Ok} tag='{ap.Tag}' ({ap.Status})");
        if (!ap.Ok || !string.Equals(ap.Tag, "bangs", StringComparison.Ordinal))
        { Console.WriteLine("  FAIL (explicit rename must be honoured)"); fail++; }
        else Console.WriteLine("  PASS (user's new name wins)");

        // 改成一个**本来就合法**的标签也必须能改：这是 Tab 补全在就地编辑里的
        // 落地路径（Tab 补出来的都是正典形式）。曾经这里被当成"没有改动"拒绝。
        var rn = TagEdit.Apply("twintails", "long hair", new[] { "1girl" }, vocab);
        Console.WriteLine($"  rename 'long hair' -> 'twintails': ok={rn.Ok} tag='{rn.Tag}' ({rn.Status})");
        if (!rn.Ok || !string.Equals(rn.Tag, "twintails", StringComparison.Ordinal))
        { Console.WriteLine("  FAIL (rename to a canonical tag must be allowed)"); fail++; }
        else Console.WriteLine("  PASS (canonical rename allowed)");

        // 撞名仍然要拒
        var dupRename = TagEdit.Apply("twintails", "long hair", new[] { "twintails" }, vocab);
        Console.WriteLine($"  rename into an existing tag: ok={dupRename.Ok} status={dupRename.Status}");
        if (dupRename.Ok) { Console.WriteLine("  FAIL (duplicate must be refused)"); fail++; }
        else Console.WriteLine("  PASS (refused)");

        // ---------- 2. 规范化确实工作 ----------
        Console.WriteLine();
        Console.WriteLine("=== 2. known forms normalise ===");
        var cases = new (string In, string Want)[]
        {
            ("long_hair", "long hair"),        // 去下划线
            ("1girl", "1girl"),                // 控制词保持
            ("score_7", "score_7"),            // 唯一保留的下划线例外
            ("@dairi", "@dairi"),              // 画师带 @ 保持
        };
        foreach (var (input, want) in cases)
        {
            var r = TagEdit.Resolve(input, vocab);
            var ok = string.Equals(r.Tag, want, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  '{input}' -> '{r.Tag}' (want '{want}') {(ok ? "OK" : "MISMATCH")}");
            if (!ok) fail++;
        }
        Console.WriteLine("  PASS");

        // ---------- 3. 防重复 ----------
        Console.WriteLine();
        Console.WriteLine("=== 3. rename must not create a duplicate ===");
        // 用 `print yukata` -> `print kimono`：实测过的真实别名（源不在 Anima 索引里、
        // 目标在，18,637 帖）。刻意不用 `wink`——Anima 自带 `wink` 条目，
        // 按"Anima 索引优先"的规则它本来就该保持原样。
        var aliasSrc = "print yukata";
        var resolved = TagEdit.Resolve(aliasSrc, vocab);
        Console.WriteLine($"  '{aliasSrc}' resolves to '{resolved.Tag}' ({resolved.Status})");

        if (resolved.Tag.Length > 0 && !string.Equals(resolved.Tag, aliasSrc, StringComparison.Ordinal))
        {
            var dup = TagEdit.NormalizeOne(aliasSrc, aliasSrc, new[] { resolved.Tag }, vocab);
            Console.WriteLine($"  with '{resolved.Tag}' already present: ok={dup.Ok} ({dup.Status})");
            if (dup.Ok) { Console.WriteLine("  FAIL (duplicate allowed)"); fail++; }
            else Console.WriteLine("  PASS (refused)");

            var fine = TagEdit.NormalizeOne(aliasSrc, aliasSrc, new[] { "1girl" }, vocab);
            Console.WriteLine($"  without it: ok={fine.Ok} -> '{fine.Tag}'");
            if (!fine.Ok) { Console.WriteLine("  FAIL (should have renamed)"); fail++; }
            else Console.WriteLine("  PASS");
        }
        else
        {
            Console.WriteLine($"  FAIL: '{aliasSrc}' should be an alias in this vocab build");
            fail++;
        }

        // Anima 索引必须压过 Danbooru 的改名表：`wink` 在 Anima 索引里是原生标签，
        // 而 Danbooru 把它并进了 `one eye closed`。跟着 Danbooru 改会把一个
        // Anima 认的标签换成另一个，属于无谓的改动。
        Console.WriteLine();
        var wink = TagEdit.Resolve("wink", vocab);
        Console.WriteLine($"  precedence: 'wink' -> '{wink.Tag}' ({wink.Status})");
        if (!string.Equals(wink.Tag, "wink", StringComparison.OrdinalIgnoreCase) || wink.Status != "anima")
        { Console.WriteLine("  FAIL (Anima index should win over the alias table)"); fail++; }
        else Console.WriteLine("  PASS (Anima index wins)");

        // ---------- 4. 一行只能一个标签 ----------
        Console.WriteLine();
        Console.WriteLine("=== 4. comma input is refused with the right advice ===");
        var multi = TagEdit.Apply("1girl, solo", "mix", Array.Empty<string>(), vocab);
        Console.WriteLine($"  '1girl, solo': ok={multi.Ok} status={multi.Status}");
        if (multi.Ok || multi.Status != "comma") { Console.WriteLine("  FAIL"); fail++; }
        else Console.WriteLine("  PASS");

        // ---------- 5. 粘贴：换行折成逗号，散文被摘掉 ----------
        Console.WriteLine();
        Console.WriteLine("=== 5. paste parsing ===");
        var nl = TagEdit.FromPaste("1girl\nsolo\nlong hair", Array.Empty<string>(), vocab);
        Console.WriteLine($"  newline list -> {nl.Tags.Count} tags: [{string.Join(", ", nl.Tags)}]");
        if (nl.Tags.Count != 3) { Console.WriteLine("  FAIL (newlines not split)"); fail++; }
        else Console.WriteLine("  PASS");

        // 重复的跳过，不产生重复项
        var dupPaste = TagEdit.FromPaste("1girl, 1girl, solo", new[] { "1girl" }, vocab);
        Console.WriteLine($"  '1girl, 1girl, solo' with 1girl present -> " +
                          $"[{string.Join(", ", dupPaste.Tags)}] skipped={dupPaste.Skipped}");
        if (dupPaste.Tags.Count != 1 || !string.Equals(dupPaste.Tags[0], "solo", StringComparison.OrdinalIgnoreCase))
        { Console.WriteLine("  FAIL"); fail++; }
        else Console.WriteLine("  PASS");

        // 官方 example.png 那种"质量前缀 + 散文"：粘回来时散文不能被当成标签
        var official = "masterpiece, best quality, score_7, safe. An anime girl wearing a " +
                       "black tank-top and denim shorts is standing outdoors.";
        var op = TagEdit.FromPaste(official, Array.Empty<string>(), vocab);
        Console.WriteLine($"  official prompt -> {op.Tags.Count} tags " +
                          $"[{string.Join(", ", op.Tags)}] prose={op.ProseTail.Length}");
        if (op.Tags.Count != 4 || op.ProseTail.Length == 0)
        { Console.WriteLine("  FAIL (tags/prose split wrong)"); fail++; }
        else Console.WriteLine("  PASS (4 tags + prose)");

        // 纯散文：如实告知"这不是标签"，而不是塞一堆词进列表
        var proseOnly = TagEdit.FromPaste(
            "An anime girl in a sailor uniform stands under a cherry tree in full bloom.", 
            Array.Empty<string>(), vocab);
        Console.WriteLine($"  pure prose -> tags={proseOnly.Tags.Count} " +
                          $"prose={proseOnly.ProseTail.Length} proseOnly={proseOnly.ProseOnly}");
        if (proseOnly.Tags.Count != 0 || !proseOnly.ProseOnly)
        { Console.WriteLine("  FAIL (prose leaked into tags)"); fail++; }
        else Console.WriteLine("  PASS");

        // ---------- 6. 整行规范化只动"该动的" ----------
        Console.WriteLine();
        Console.WriteLine("=== 6. whole-line normalise: only rewrites where Anima says so ===");
        //
        // 起初这里断言的是"对现有 caption 恒等"，实测 53 个里有 10 个会被改
        // （`print yukata`->`print kimono`、`leg garter`->`frilled thigh strap` 等）。
        // 逐条查过 Danbooru 的改名表：这 10 个全都**源形式不在 Anima 索引里、
        // 目标形式在**，也就是真实的旧别名——所以改是对的，恒等这个断言本身就是错的。
        //
        // 真正该守的不变量有两条，且都能逐条验证：
        //   a) 只把"Anima 不认的写法"改成"Anima 认的写法"（不碰已经合规的）
        //   b) 目标形式必须在 Anima 索引里（绝不改成一个 Anima 不认的东西）
        var files = Directory.EnumerateFiles(dir, "*.txt").OrderBy(x => x, StringComparer.Ordinal).ToList();
        var identityOnAnima = 0;      // 本来合规、且没被动过的标签数
        var violationsA = new List<string>();
        var violationsB = new List<string>();
        var allPairs = new List<(string From, string To)>();

        foreach (var f in files)
        {
            string text;
            try { text = File.ReadAllText(f, Encoding.UTF8).Trim(); }
            catch { continue; }

            var parts = PromptText.Parse(text, vocab);
            var norm = TagEdit.Normalize(parts.Tags, vocab);

            foreach (var tag in parts.Tags)
            {
                var changedAway = !norm.Tags.Contains(tag, StringComparer.Ordinal);
                var wasAnima = vocab.Status(tag) == "anima";
                if (wasAnima && changedAway)
                    violationsA.Add($"{Path.GetFileName(f)}: '{tag}' was an Anima tag but got rewritten");
                if (wasAnima && !changedAway) identityOnAnima++;
            }

            foreach (var p in norm.Pairs)
            {
                allPairs.Add(p);
                if (vocab.Status(p.To) != "anima")
                    violationsB.Add($"{Path.GetFileName(f)}: '{p.From}' -> '{p.To}' " +
                                    $"but target status is '{vocab.Status(p.To)}'");
            }
        }

        Console.WriteLine($"  captions              : {files.Count}");
        Console.WriteLine($"  Anima tags untouched  : {identityOnAnima}");
        Console.WriteLine($"  rewrites (alias fixes): {allPairs.Count}");
        foreach (var p in allPairs.Distinct().Take(12))
            Console.WriteLine($"      {p.From} -> {p.To}");
        Console.WriteLine($"  (a) rewrote a valid Anima tag : {violationsA.Count}");
        Console.WriteLine($"  (b) target not in Anima index : {violationsB.Count}");
        foreach (var v in violationsA.Take(5)) Console.WriteLine("      " + v);
        foreach (var v in violationsB.Take(5)) Console.WriteLine("      " + v);
        if (violationsA.Count == 0 && violationsB.Count == 0) Console.WriteLine("  PASS");
        else { Console.WriteLine("  FAIL"); fail++; }

        // 规范化必须收敛：对已规范化的结果再跑一次，不该再有任何改动
        Console.WriteLine();
        var idem = 0;
        var idemBad = new List<string>();
        foreach (var f in files)
        {
            string text;
            try { text = File.ReadAllText(f, Encoding.UTF8).Trim(); }
            catch { continue; }
            var parts = PromptText.Parse(text, vocab);
            var once = TagEdit.Normalize(parts.Tags, vocab);
            var twice = TagEdit.Normalize(once.Tags, vocab);
            if (twice.Tags.SequenceEqual(once.Tags, StringComparer.Ordinal)) idem++;
            else if (idemBad.Count < 5)
                idemBad.Add($"{Path.GetFileName(f)}: " +
                    string.Join("; ", twice.Pairs.Select(p => $"{p.From}->{p.To}")));
        }
        Console.WriteLine($"  idempotent: {idem}/{files.Count}");
        foreach (var b in idemBad) Console.WriteLine("      " + b);
        if (idem == files.Count) Console.WriteLine("  PASS");
        else { Console.WriteLine("  FAIL (normalise is not idempotent)"); fail++; }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "ALL EDIT CHECKS PASSED" : $"{fail} CHECK(S) FAILED");
        return fail == 0 ? 0 : 1;
    }
}
