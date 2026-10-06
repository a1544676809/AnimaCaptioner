using System.Text;
using AnimaCaptioner.Core;

namespace CoreCheck;

/// <summary>
/// PromptText（标签区 + 自然语言尾巴）的回归。
///
/// 最重要的一条：**现有 53 个 caption 必须被 100% 判成标签**。它们的解析结果
/// 一旦变了，工具就会把用户已经排好的标签当成散文、从中间列表里藏起来——
/// 那是静默的破坏。实测 679 个真实标签里含句末标点的 0 个、首字母大写的 0 个，
/// 所以这条应该恒等；测试就是把这个事实钉住。
///
/// 其余各项用官方两段真实提示词（工作流模板的纯散文、example.png 的
/// 「质量前缀 + 四句散文」）验证标签/散文的切分位置。
/// </summary>
public static class Prose
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

        // ---------- 1. 现有 caption 必须全是标签 ----------
        Console.WriteLine("=== 1. existing captions: tags only ===");
        var files = Directory.EnumerateFiles(dir, "*.txt").OrderBy(x => x, StringComparer.Ordinal).ToList();
        var totalSegs = 0;
        var leaked = 0;
        var leakSamples = new List<string>();
        foreach (var f in files)
        {
            string text;
            try { text = File.ReadAllText(f, Encoding.UTF8).Trim(); }
            catch { continue; }
            var segs = PromptText.SplitTags(text).Count;
            var parts = PromptText.Parse(text, vocab);
            totalSegs += segs;
            if (parts.HasProse || parts.Tags.Count != segs)
            {
                leaked++;
                if (leakSamples.Count < 5)
                    leakSamples.Add($"{Path.GetFileName(f)} -> prose=\"{Trunc(parts.ProseTail)}\" " +
                                    $"tags={parts.Tags.Count} (expected {segs})");
            }
        }
        Console.WriteLine($"  captions        : {files.Count}");
        Console.WriteLine($"  comma segments  : {totalSegs}");
        Console.WriteLine($"  with prose leak : {leaked}");
        foreach (var s in leakSamples) Console.WriteLine("      " + s);
        if (leaked == 0) Console.WriteLine("  PASS (identical to the current build)");
        else { Console.WriteLine("  FAIL"); fail++; }

        // ---------- 2. 逐字节还原 ----------
        Console.WriteLine();
        Console.WriteLine("=== 2. tags + prose round-trips byte-exact ===");
        var rt = 0;
        var rtBad = 0;
        foreach (var f in files)
        {
            string text;
            try { text = File.ReadAllText(f, Encoding.UTF8).Trim(); }
            catch { continue; }
            var parts = PromptText.Parse(text, vocab);
            var rebuilt = PromptText.Rebuild(parts.Tags, parts.ProseTail);
            rt++;
            if (rebuilt != text)
            {
                rtBad++;
                if (rtBad <= 3)
                    Console.WriteLine($"      {Path.GetFileName(f)}\n        got: {Trunc(rebuilt)}\n" +
                                      $"        exp: {Trunc(text)}");
            }
        }
        Console.WriteLine($"  checked {rt}, mismatches {rtBad}");
        if (rtBad == 0) Console.WriteLine("  PASS");
        else { Console.WriteLine("  FAIL"); fail++; }

        // ---------- 3. 官方工作流模板：纯散文 ----------
        Console.WriteLine();
        Console.WriteLine("=== 3. official workflow prompt (pure prose) ===");
        const string wf =
            "Anime monochrome cyberpunk front portrait, male figure, sleek skin with " +
            "delicate mechanical lines, piercing glowing eyes, partial exposed metallic " +
            "mecha components and light cables, sharp domineering cool style, textured " +
            "anime brushwork, faint circuit background, high contrast chiaroscuro " +
            "lighting, immersive cinematic shadows, ultra fine details, 8K high-def " +
            "render, futuristic dystopian mood";
        var wfp = PromptText.Parse(wf, vocab);
        Console.WriteLine($"  tags  ({wfp.Tags.Count}): [{string.Join(" | ", wfp.Tags)}]");
        Console.WriteLine($"  prose     : {Trunc(wfp.ProseTail)}");
        if (wfp.Tags.Count == 0 && wfp.ProseTail.Length > 0) Console.WriteLine("  PASS");
        else { Console.WriteLine("  FAIL (expected 0 tags, whole prompt as prose)"); fail++; }

        // ---------- 4. 官方 example.png：质量前缀 + 散文 ----------
        Console.WriteLine();
        Console.WriteLine("=== 4. official example.png (quality prefix + prose) ===");
        const string exPrompt =
            "masterpiece, best quality, score_7, safe. An anime girl wearing a black " +
            "tank-top and denim shorts is standing outdoors. She's holding a rectangular " +
            "sign out in front of her that reads \"ANIMA\". She's looking at the viewer " +
            "with a smile. The background features some trees and blue sky with clouds.";
        var exp = PromptText.Parse(exPrompt, vocab);
        Console.WriteLine($"  tags  ({exp.Tags.Count}): [{string.Join(" | ", exp.Tags)}]");
        Console.WriteLine($"  prose     : {Trunc(exp.ProseTail)}");
        var exOk = exp.Tags.Count == 4
                   && exp.Tags[0] == "masterpiece" && exp.Tags[3] == "safe"
                   && exp.ProseTail.StartsWith(". An anime girl", StringComparison.Ordinal);
        Console.WriteLine($"  round-trip byte-exact: {PromptText.Rebuild(exp.Tags, exp.ProseTail) == exPrompt}");
        if (exOk && PromptText.Rebuild(exp.Tags, exp.ProseTail) == exPrompt) Console.WriteLine("  PASS");
        else { Console.WriteLine("  FAIL"); fail++; }

        // ---------- 5. 追加散文 ----------
        Console.WriteLine();
        Console.WriteLine("=== 5. AppendProse ===");
        const string tags = "sensitive, 1girl, solo, @sayori style, smile";
        const string prose =
            "A cheerful girl with brown twintails stands in front of a plain white " +
            "background, wearing a red dress with frills, smiling at the viewer.";
        var appended = PromptText.AppendProse(tags, prose);
        var ap = PromptText.Parse(appended, vocab);
        Console.WriteLine($"  appended : {Trunc(appended)}");
        Console.WriteLine($"  reparsed : {ap.Tags.Count} tags, prose {ap.ProseTail.Length} chars");
        // ProseTail 按设计保留前导分隔符 ". "，正是它让 tags+tail 能逐字节还原原文
        var apOk = ap.Tags.Count == 5 && ap.ProseTail == ". " + prose;
        Console.WriteLine($"  tail keeps its \". \" delimiter: {ap.ProseTail.StartsWith(". ", StringComparison.Ordinal)}");
        // 再来一次：已在散文状态时应再补一句，而不是把标签区吃掉
        var twice = PromptText.AppendProse(appended, "She is looking at the viewer.");
        var tp = PromptText.Parse(twice, vocab);
        Console.WriteLine($"  append again: {tp.Tags.Count} tags (must stay 5), prose {tp.ProseTail.Length} chars");
        apOk = apOk && tp.Tags.Count == 5 && tp.ProseTail.Contains("She is looking at the viewer.", StringComparison.Ordinal);
        if (apOk) Console.WriteLine("  PASS");
        else { Console.WriteLine("  FAIL"); fail++; }

        // ---------- 6. 着色区间 ----------
        Console.WriteLine();
        Console.WriteLine("=== 6. colour spans ===");
        foreach (var (label, text) in new[] { ("example.png", exPrompt), ("workflow", wf), ("ours", appended) })
        {
            var spans = PromptText.ColorSpans(text, vocab);
            var covered = spans.Sum(s => s.Length);
            var secs = string.Join(",", spans.Select(s => s.Section).Distinct());
            Console.WriteLine($"  {label,-12} spans={spans.Count,2} covered={covered,3}/{text.Length,-4} " +
                              $"sections=[{secs}]");
            // 区间必须落在原文内且不重叠
            var ordered = spans.OrderBy(s => s.Start).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].Start < 0 || ordered[i].Start + ordered[i].Length > text.Length)
                { Console.WriteLine("      FAIL span out of range"); fail++; }
                if (i > 0 && ordered[i].Start < ordered[i - 1].Start + ordered[i - 1].Length)
                { Console.WriteLine("      FAIL spans overlap"); fail++; }
            }
        }
        // 散文不能被染色：尾巴区间内不该有任何 span
        foreach (var (label, text) in new[] { ("example.png", exPrompt), ("workflow", wf), ("ours", appended) })
        {
            var p = PromptText.Parse(text, vocab);
            var tailStart = text.Length - p.ProseTail.Length;
            var bad = PromptText.ColorSpans(text, vocab).Where(s => s.Start >= tailStart).ToList();
            if (bad.Count > 0)
            {
                Console.WriteLine($"  FAIL {label}: {bad.Count} span(s) inside the prose tail");
                fail++;
            }
        }
        Console.WriteLine("  prose tails are never coloured: ok");

        // ---------- 7. 标签操作不丢散文 ----------
        Console.WriteLine();
        Console.WriteLine("=== 7. tag edits preserve the prose tail ===");
        var baseParts = PromptText.Parse(appended, vocab);
        var reordered = baseParts.Tags.OrderBy(t => t, StringComparer.Ordinal).ToList();
        var rebuiltText = PromptText.Rebuild(reordered, baseParts.ProseTail);
        var rb = PromptText.Parse(rebuiltText, vocab);
        Console.WriteLine($"  reordered tags: [{string.Join(", ", rb.Tags)}]");
        Console.WriteLine($"  prose survived: {rb.ProseTail == baseParts.ProseTail}");
        var deleted = baseParts.Tags.Where(t => t != "smile").ToList();
        var delText = PromptText.Rebuild(deleted, baseParts.ProseTail);
        var dp = PromptText.Parse(delText, vocab);
        Console.WriteLine($"  after delete  : {dp.Tags.Count} tags, prose survived: {dp.ProseTail == baseParts.ProseTail}");
        if (rb.ProseTail == baseParts.ProseTail && dp.ProseTail == baseParts.ProseTail
            && dp.Tags.Count == 4 && !dp.Tags.Contains("smile"))
            Console.WriteLine("  PASS");
        else { Console.WriteLine("  FAIL"); fail++; }

        // ---------- 8. 保存路径必须保住散文 ----------
        // 这是本轮抓到的真缺陷：SaveCurrent 过去调 item.Caption.Save(tags)，
        // 只写标签，于是带散文的提示词一保存就把整段英文**静默删掉**。
        // 这里直接测写盘结果，而不是只看内存里的字符串。
        Console.WriteLine();
        Console.WriteLine("=== 8. saving preserves the prose tail ===");
        var tmpDir = Path.Combine(Path.GetTempPath(), "anima-prose-save-test");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        Directory.CreateDirectory(tmpDir);
        try
        {
            var full = PromptText.AppendProse(tags, prose);
            var p0 = PromptText.Parse(full, vocab);
            var target = Path.Combine(tmpDir, "case.txt");

            // 先落一个基线文件，再用"标签 + 尾巴"写回，模拟真实的保存
            File.WriteAllText(target, full, new UTF8Encoding(false));
            var cf = CaptionFile.Load(target);
            var body = PromptText.Rebuild(p0.Tags, p0.ProseTail, cf.Separator);
            var changed = cf.SaveBody(body);

            var onDisk = File.ReadAllText(target, Encoding.UTF8).TrimEnd('\r', '\n');
            var reloaded = PromptText.Parse(onDisk, vocab);
            Console.WriteLine($"  wrote            : {Trunc(onDisk)}");
            Console.WriteLine($"  changed          : {changed} (expected False: identical)");
            Console.WriteLine($"  tags kept        : {reloaded.Tags.Count}");
            Console.WriteLine($"  prose kept       : {reloaded.ProseTail == p0.ProseTail}");
            Console.WriteLine($"  byte-identical   : {onDisk == full}");

            if (reloaded.ProseTail == p0.ProseTail && onDisk == full && !changed)
                Console.WriteLine("  PASS");
            else { Console.WriteLine("  FAIL"); fail++; }

            // 再验一次"只写标签"确实会丢散文——把那个缺陷钉成反例
            var cf2 = CaptionFile.Load(target);
            cf2.Save(p0.Tags);              // 旧写法
            var lostDisk = File.ReadAllText(target, Encoding.UTF8);
            var lost = PromptText.Parse(lostDisk, vocab);
            Console.WriteLine();
            Console.WriteLine($"  old Save(tags) path -> prose kept: {lost.HasProse} " +
                              "(False proves the bug this test guards)");
            if (lost.HasProse)
            {
                Console.WriteLine("  NOTE: the old path unexpectedly kept prose; " +
                                  "the guard may be testing the wrong thing");
                fail++;
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("=== 9. edge cases ===");
        var cases = new (string Input, int Tags, bool Prose)[]
        {
            ("", 0, false),
            ("   ", 0, false),
            ("1girl", 1, false),
            ("1girl, solo", 2, false),
            ("1girl, solo, ", 2, false),
            ("@big chungus.", 1, false),          // model card's own example
            ("1girl. She is smiling.", 1, true),
        };
        foreach (var (input, nTags, hasProse) in cases)
        {
            var p = PromptText.Parse(input, vocab);
            var ok = p.Tags.Count == nTags && p.HasProse == hasProse;
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} tags={p.Tags.Count} prose={p.HasProse,-5} " +
                              $"<- \"{Trunc(input)}\"  [{string.Join("|", p.Tags)}] \"{Trunc(p.ProseTail)}\"");
            if (!ok) fail++;
        }

        Console.WriteLine();
        Console.WriteLine("=== 10. token budget (trainer truncates at 512) ===");
        // 训练器 strategy_anima.py 用 truncation=True，超出静默丢弃。
        // 现有 53 个 caption 都必须落在预算内，否则尾巴（散文）会被砍掉。
        var over = new List<(string File, int Tok)>();
        foreach (var f in files)
        {
            var t = File.ReadAllText(f, Encoding.UTF8).Trim();
            var n = PromptText.EstimateTokens(t);
            if (PromptText.OverBudget(t)) over.Add((Path.GetFileName(f), n));
        }
        Console.WriteLine($"  soft budget = {PromptText.SoftCharBudget} chars  (~{PromptText.MaxTokens} tokens @ " +
                          $"{PromptText.CharsPerTokenTagged} chars/token)");
        Console.WriteLine($"  existing captions over budget: {over.Count} / {files.Count}");
        foreach (var (file, tok) in over) Console.WriteLine($"    OVER {file}: ~{tok} tokens");
        if (over.Count > 0)
        {
            Console.WriteLine("  FAIL: an existing caption would be silently truncated");
            fail++;
        }
        // 边界：明显超长的必须被判出来，短的不能误报
        var longText = new string('a', PromptText.SoftCharBudget * 2);
        if (!PromptText.OverBudget(longText)) { Console.WriteLine("  FAIL: long text not flagged"); fail++; }
        if (PromptText.OverBudget("1girl, solo, smile")) { Console.WriteLine("  FAIL: short text flagged"); fail++; }
        if (PromptText.EstimateTokens("") != 0) { Console.WriteLine("  FAIL: empty should be 0"); fail++; }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "ALL PROSE CHECKS PASSED" : $"{fail} PROSE CHECK(S) FAILED");
        return fail == 0 ? 0 : 1;
    }

    private static string Trunc(string s, int n = 68) =>
        s.Length <= n ? s : s[..n] + "\u2026";
}
