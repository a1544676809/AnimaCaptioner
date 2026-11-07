using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnimaCaptioner.Core;

namespace AnimaCaptioner.Tests;

/// <summary>
/// 重新生成 tests/golden.json。
///
/// 背景：原 golden 是"与 Python 参考实现逐条对拍"的快照；那个 Python 生成器
/// （analyse/gen_golden.py）没有随仓库分发。后来 seed/别名/词库继续演进，
/// golden 就过期了（seed 273 → 285 等 8 条差异）。
///
/// 这个生成器**保留原 golden 里的"输入用例"**（要扫描的文本、要解析的中英文词、
/// arrange 的两个标签列表、build_case0 的文本、prior 的抽样键），用**当前实现**
/// 重新算出全部期望值写回去。所以 golden 的语义从"与 Python 对拍"变成
/// "对当前已验证实现的回归基线"——这是能在仓库内自洽、且能长期跑的唯一做法。
///
/// 用法：corecheck golden &lt;outPath&gt; &lt;datasetDir&gt;   （词库/seed 走 AC_VOCABDB/AC_SEED）
/// </summary>
public static class GoldenGen
{
    public static int Run(string? outPath, string? datasetDir)
    {
        outPath ??= "golden.json";
        var vocabPath = Environment.GetEnvironmentVariable("AC_VOCABDB")
                        ?? AppSettings.Load().VocabDbPath;
        var seedPath = Environment.GetEnvironmentVariable("AC_SEED")
                       ?? AppSettings.Load().SeedPath;
        datasetDir ??= Environment.GetEnvironmentVariable("AC_DATASET")
                       ?? AppSettings.Load().DatasetDir;

        var v = new VocabDb();
        v.Load(vocabPath, seedPath);
        Console.WriteLine($"vocab: tags={v.TagCount} anima={v.AnimaCount} alias={v.AliasCount} " +
                          $"seed={v.SeedCount} cn={v.CnKeyCount}");
        Console.WriteLine($"dataset: {datasetDir}");

        // ---- 旧 golden 里的"输入用例" ----
        var oldPath = Path.Combine(RepoRoot(), "tests", "golden.json");
        var scanInputs = new List<string>();
        var chineseInputs = new List<string>();
        var englishInputs = new List<string>();
        var arrangePlainIn = new List<string>();
        var arrangeRatedIn = new List<string>();
        string buildText = "1girl, solo, long hair, blue eyes, smile, white dress, standing";
        var priorSampleKeys = new List<string>();

        if (File.Exists(oldPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(oldPath, Encoding.UTF8));
            var root = doc.RootElement;
            if (root.TryGetProperty("scan", out var sc))
                foreach (var p in sc.EnumerateObject()) scanInputs.Add(p.Name);
            if (root.TryGetProperty("chinese", out var cn))
                foreach (var p in cn.EnumerateObject()) chineseInputs.Add(p.Name);
            if (root.TryGetProperty("english", out var en))
                foreach (var p in en.EnumerateObject()) englishInputs.Add(p.Name);
            if (root.TryGetProperty("arrange_plain", out var ap))
                arrangePlainIn = ap.EnumerateArray().Select(x => x.GetString()!).ToList();
            if (root.TryGetProperty("arrange_rated", out var ar))
                arrangeRatedIn = ar.EnumerateArray().Select(x => x.GetString()!).ToList();
            if (root.TryGetProperty("build_case0", out var bc) &&
                bc.TryGetProperty("text", out var bt))
                buildText = bt.GetString() ?? buildText;
            if (root.TryGetProperty("prior", out var pr) &&
                pr.TryGetProperty("sample", out var ps))
                foreach (var p in ps.EnumerateObject()) priorSampleKeys.Add(p.Name);
            Console.WriteLine($"old golden inputs: scan={scanInputs.Count} cn={chineseInputs.Count} " +
                              $"en={englishInputs.Count} priorSample={priorSampleKeys.Count}");
        }
        else
        {
            Console.WriteLine("（没有旧 golden，使用内置最小输入集）");
            scanInputs.AddRange(new[] { "masterpiece, 1girl, solo, blonde hair, blue eyes",
                                        "回头看向镜头", "银发少女在樱花树下侧身站立，穿水手服" });
            chineseInputs.AddRange(new[] { "微笑", "长发", "水手服" });
            englishInputs.AddRange(new[] { "long_hair", "smile", "not_a_real_tag_zzz" });
            arrangePlainIn = new List<string> { "smile", "1girl", "masterpiece", "long hair", "solo" };
            arrangeRatedIn = new List<string> { "safe", "1girl", "smile", "masterpiece" };
        }

        var prior = VocabDb.OrderFromDir(datasetDir);

        // ---- 计算 counts ----
        var counts = new JsonObject
        {
            ["tags"] = v.TagCount,
            ["anima"] = v.AnimaCount,
            ["alias"] = v.AliasCount,
            ["seed"] = v.SeedCount,
            ["cn_keys"] = v.CnKeyCount,
        };

        // ---- scan ----
        var scanObj = new JsonObject();
        foreach (var t in scanInputs)
        {
            var arr = new JsonArray();
            foreach (var h in v.Scan(t))
                arr.Add(new JsonArray { h.Tag, h.Status, h.Key });
            scanObj[t] = arr;
        }

        // ---- chinese / english ----
        var cnObj = new JsonObject();
        foreach (var kw in chineseInputs)
        {
            var (tag, st) = v.ResolveChinese(kw);
            cnObj[kw] = new JsonArray { tag, st };
        }
        var enObj = new JsonObject();
        foreach (var w in englishInputs)
        {
            var (tag, st) = v.ResolveEnglish(w);
            enObj[w] = new JsonArray { tag, st };
        }

        // ---- prior ----
        if (priorSampleKeys.Count == 0)
            priorSampleKeys = prior.OrderByDescending(kv => kv.Value).Take(12).Select(kv => kv.Key).ToList();
        var sampleObj = new JsonObject();
        foreach (var k in priorSampleKeys)
            sampleObj[k] = prior.TryGetValue(k, out var w) ? w : 0.0;
        var priorObj = new JsonObject { ["size"] = prior.Count, ["sample"] = sampleObj };

        // ---- arrange：期望值 = 当前 Arrange 的输出；同时保证幂等 ----
        var plainOut = VocabDb.Arrange(arrangePlainIn, prior);
        var ratedOut = VocabDb.Arrange(arrangeRatedIn, prior, rating: "explicit", artist: "@sayori style");
        var plainArr = new JsonArray(); foreach (var t in plainOut) plainArr.Add(t);
        var ratedArr = new JsonArray(); foreach (var t in ratedOut) ratedArr.Add(t);

        // ---- build_case0 ----
        var buildTags = VocabDb.Arrange(v.Scan(buildText).Select(h => h.Tag), prior);
        var b0tags = new JsonArray(); foreach (var t in buildTags) b0tags.Add(t);
        var buildObj = new JsonObject { ["text"] = buildText, ["tags"] = b0tags };

        // ---- audit（对同一数据集）----
        var items = Dataset.Scan(datasetDir).Where(i => i.HasCaption).ToList();
        int totalTags = 0, clean = 0;
        var agg = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var it in items)
        {
            var tags = CaptionFile.Load(it.CaptionPath).Tags;
            totalTags += tags.Count;
            var issues = VocabDb.AuditTags(tags, v);
            var dups = tags.GroupBy(t => t.ToLowerInvariant()).Any(g => g.Count() > 1);
            var underscore = tags.Any(t => t.Contains('_') && !t.StartsWith("score_", StringComparison.Ordinal));
            if (issues.Count == 0 && !dups && !underscore) clean++;
            foreach (var i in issues)
            {
                var k = $"{i.Tag}|{i.Status}|{i.Use ?? "None"}";
                agg[k] = agg.TryGetValue(k, out var c) ? c + 1 : 1;
            }
        }
        var aggObj = new JsonObject();
        foreach (var kv in agg.OrderBy(k => k.Key, StringComparer.Ordinal)) aggObj[kv.Key] = kv.Value;
        var auditObj = new JsonObject
        {
            ["files"] = items.Count,
            ["tags"] = totalTags,
            ["clean"] = clean,
            ["agg"] = aggObj,
        };

        var rootOut = new JsonObject
        {
            ["counts"] = counts,
            ["scan"] = scanObj,
            ["chinese"] = cnObj,
            ["english"] = enObj,
            ["prior"] = priorObj,
            ["arrange_plain"] = plainArr,
            ["arrange_rated"] = ratedArr,
            ["build_case0"] = buildObj,
            ["audit"] = auditObj,
        };

        var json = rootOut.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var full = Path.IsPathRooted(outPath) ? outPath : Path.Combine(RepoRoot(), outPath);
        File.WriteAllText(full, json, new UTF8Encoding(false));
        Console.WriteLine($"golden written: {full}");
        Console.WriteLine($"  files={items.Count} tags={totalTags} clean={clean} agg={agg.Count}");
        return 0;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tests", "golden.json"))
                               && !File.Exists(Path.Combine(dir.FullName, "AnimaCaptioner.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
