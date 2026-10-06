using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AnimaCaptioner.Core;

namespace CoreCheck;

/// <summary>
/// 安全标签判据的回归测试。
///
/// 关键约束：**不许对既有 53 个 caption 做自动改写**。这个测试只报告，
/// 把分歧列出来交给人判断——因为"更严一档"是一种标准选择，不是错。
/// </summary>
internal static class Safety
{
    public static int Run(string dir)
    {
        Console.OutputEncoding = Encoding.UTF8;
        int fails = 0, checks = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            checks++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}" + (detail is null ? "" : "  " + detail));
            if (!ok) fails++;
        }

        Console.WriteLine("=== [1] 梯子与别名 ===");
        Check("Rank(safe)=0", SafetyRating.Rank("safe") == 0);
        Check("Rank(explicit)=3", SafetyRating.Rank("explicit") == 3);
        Check("Danbooru 别名 general→safe", SafetyRating.Rank("general") == 0);
        Check("Danbooru 别名 questionable→nsfw", SafetyRating.Rank("questionable") == 2);
        Check("非安全词 → -1", SafetyRating.Rank("smile") == -1);
        Check("大小写不敏感", SafetyRating.Rank("NSFW") == 2);
        Check("IsSafetyWord(safe)", SafetyRating.IsSafetyWord("safe"));
        Check("IsSafetyWord(smile)=false", !SafetyRating.IsSafetyWord("smile"));

        Console.WriteLine("\n=== [2] 官方判据逐条 ===");
        // explicit：打码不降档
        var (r1, _) = SafetyRating.Judge(new[] { "1girl", "pussy", "censored", "mosaic censoring" });
        Check("露性器+打码 => explicit（打码不降档）", r1 == "explicit", r1);
        // nsfw：非性器裸露
        var (r2, _) = SafetyRating.Judge(new[] { "1girl", "nipples", "nude" });
        Check("裸乳 => nsfw（非器裸露不是 explicit）", r2 == "nsfw", r2);
        // sensitive：官方点名的镂空
        var (r3, e3) = SafetyRating.Judge(new[] { "1girl", "clothing cutout", "cleavage cutout" });
        Check("clothing cutout => sensitive（官方点名）", r3 == "sensitive", r3);
        // sensitive：泳装
        var (r4, _) = SafetyRating.Judge(new[] { "1girl", "bikini" });
        Check("泳装 => sensitive", r4 == "sensitive", r4);
        // safe：无性化标签
        var (r5, _) = SafetyRating.Judge(new[] { "1girl", "solo", "smile", "long hair" });
        Check("无性化标签 => safe", r5 == "safe", r5);
        // 优先级：explicit 压过 sensitive
        var (r6, _) = SafetyRating.Judge(new[] { "bikini", "pussy" });
        Check("explicit 优先于 sensitive", r6 == "explicit", r6);
        // 优先级：nsfw 压过 sensitive
        var (r7, _) = SafetyRating.Judge(new[] { "bikini", "nipples" });
        Check("nsfw 优先于 sensitive", r7 == "nsfw", r7);

        Console.WriteLine("\n=== [3] Check() 的三种情形 ===");
        var v1 = SafetyRating.Check(new[] { "sensitive", "1girl", "bikini" });
        Check("写了 1 个且相符 => 无 note", v1.Notes.Count == 0, string.Join(" | ", v1.Notes));
        var v2 = SafetyRating.Check(new[] { "1girl", "bikini" });
        Check("缺安全标签 => 有 note", v2.Notes.Count == 1 && v2.Declared is null);
        var v3 = SafetyRating.Check(new[] { "safe", "nsfw", "1girl" });
        Check("写了 2 个 => 提示互斥", v3.DeclaredCount == 2 && v3.Notes.Any(n => n.Contains("互斥")));

        Console.WriteLine("\n=== [4] 散文不得触发定档 ===");
        // 散文里的 "sex" 是英文单词，不该把整条 caption 判成 explicit。
        // 调用方必须只传标签部分——这里验证 judge 对纯标签集合的行为边界。
        var v4 = SafetyRating.Check(new[] { "nsfw", "1girl", "clothed", "smile" });
        Check("clothed 场景 => safe，与标注 nsfw 分歧被报出",
              v4.Expected == "safe" && v4.Notes.Any(n => n.Contains("偏严")),
              v4.Expected + " | " + string.Join(" | ", v4.Notes));

        Console.WriteLine("\n=== [5] 对真实 53 个 caption 的统计（只报告，不改写）===");
        if (!Directory.Exists(dir))
        {
            Check("训练集目录存在", false, dir);
        }
        else
        {
            var dist = new Dictionary<string, int>();
            var diverge = new List<string>();
            var missing = new List<string>();
            int total = 0, agree = 0;

            foreach (var f in Directory.EnumerateFiles(dir, "*.txt").OrderBy(x => x))
            {
                var raw = File.ReadAllText(f, Encoding.UTF8);
                var tags = raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                var v = SafetyRating.Check(tags);
                var name = Path.GetFileNameWithoutExtension(f);
                total++;
                var key = v.Declared ?? "(缺失)";
                dist[key] = dist.GetValueOrDefault(key) + 1;

                if (v.Declared is null) missing.Add(name);
                else if (v.Notes.Count == 0) agree++;
                else diverge.Add($"{name}: {v.Declared} → 判据 {v.Expected}  [{string.Join(", ", v.Evidence.Take(3))}]");
            }

            Console.WriteLine($"  分布: {string.Join("  ", dist.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value))}");
            Console.WriteLine($"  与官方判据一致: {agree}/{total}");
            Console.WriteLine($"  分歧 {diverge.Count} 处（**需人工确认，不是自动判错**）:");
            foreach (var d in diverge) Console.WriteLine("     " + d);
            if (missing.Count > 0)
                Console.WriteLine($"  缺安全标签 {missing.Count} 处: {string.Join(", ", missing)}");

            // 不变量：文件数必须是 53；分布必须与实测基线一致（改动词表会打破它）
            //
            // 2026-10-07：基线从「9 处分歧 + safe 一档为空」改成「8 处 + safe=1」——
            // 训练集本身变了（022_01_Chocola_20_white 标成了 safe），不是判据变了。
            // 这一条正好是当初建议的"给 safe 补一个样例"，所以它是对的方向；
            // 断言跟着数据走，不把旧快照当成不可变的事实。
            Check("扫描到 53 个 caption", total == 53, "实际 " + total);
            Check("有分歧时会被报出（当前基线 8 处）", diverge.Count == 8, "实际 " + diverge.Count);
            Check("只应有 1 处缺安全标签", missing.Count == 1, "实际 " + missing.Count);
            Check("safe 一档已有样例（1 张）", dist.GetValueOrDefault("safe") == 1,
                  "实际 " + dist.GetValueOrDefault("safe"));
        }

        Console.WriteLine($"\n=== safety: {checks - fails}/{checks} 通过 ===");
        return fails == 0 ? 0 : 1;
    }
}
