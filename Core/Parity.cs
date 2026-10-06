using System.Text;
using System.Text.Json;

namespace AnimaCaptioner.Core;

/// <summary>
/// 与已验证的 Python 实现（animatag.py）对拍。
///
/// C# 端口重写了几条容易被"顺手优化"改坏的规则（最优切分、单向模糊匹配、
/// Anima 索引优先于 Danbooru 废弃标记），所以必须拿黄金输出逐条比对，
/// 而不是靠肉眼看几个例子觉得"差不多"。
///
/// 用法：AnimaCaptioner.exe --parity &lt;golden.json&gt; [--out result.txt]
/// 退出码 0 = 全部一致，1 = 有差异。
/// </summary>
public static class Parity
{
    private static int _pass, _fail;
    private static readonly StringBuilder Report = new();

    private static void Check(string name, string expected, string actual)
    {
        if (expected == actual) { _pass++; return; }
        _fail++;
        Report.AppendLine($"FAIL {name}");
        Report.AppendLine($"  expected: {expected}");
        Report.AppendLine($"  actual  : {actual}");
    }

    private static void CheckEq(string name, object? expected, object? actual) =>
        Check(name, expected?.ToString() ?? "<null>", actual?.ToString() ?? "<null>");

    public static int Run(string goldenPath, string? outPath)
    {
        var sb = new StringBuilder();
        void Emit(string s) { sb.AppendLine(s); Console.WriteLine(s); }

        try
        {
            var json = File.ReadAllText(goldenPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var cfg = AppSettings.Load();
            var v = new VocabDb();
            v.Load(cfg.VocabDbPath, cfg.SeedPath);

            // ---- 规模 ----
            var counts = root.GetProperty("counts");
            CheckEq("counts.tags", counts.GetProperty("tags").GetInt32(), v.TagCount);
            CheckEq("counts.anima", counts.GetProperty("anima").GetInt32(), v.AnimaCount);
            CheckEq("counts.alias", counts.GetProperty("alias").GetInt32(), v.AliasCount);
            CheckEq("counts.seed", counts.GetProperty("seed").GetInt32(), v.SeedCount);
            CheckEq("counts.cn_keys", counts.GetProperty("cn_keys").GetInt32(), v.CnKeyCount);

            // ---- 扫描（最优切分）----
            foreach (var p in root.GetProperty("scan").EnumerateObject())
            {
                var want = p.Value.EnumerateArray()
                    .Select(h => $"{h[0].GetString()}|{h[1].GetString()}|{h[2].GetString()}")
                    .ToList();
                var got = v.Scan(p.Name).Select(h => $"{h.Tag}|{h.Status}|{h.Key}").ToList();
                Check($"scan[{p.Name}]",
                      string.Join(" ; ", want),
                      string.Join(" ; ", got));
            }

            // ---- 中文解析 ----
            foreach (var p in root.GetProperty("chinese").EnumerateObject())
            {
                var want = p.Value;
                var (tag, st) = v.ResolveChinese(p.Name);
                Check($"chinese[{p.Name}]",
                      $"{want[0].GetString() ?? "<null>"}|{want[1].GetString()}",
                      $"{tag ?? "<null>"}|{st}");
            }

            // ---- 英文解析 ----
            foreach (var p in root.GetProperty("english").EnumerateObject())
            {
                var want = p.Value;
                var (tag, st) = v.ResolveEnglish(p.Name);
                Check($"english[{p.Name}]",
                      $"{want[0].GetString() ?? "<null>"}|{want[1].GetString()}",
                      $"{tag ?? "<null>"}|{st}");
            }

            // ---- 排序 ----
            var prior = VocabDb.OrderFromDir(cfg.DatasetDir);
            var priorGolden = root.GetProperty("prior");
            CheckEq("prior.size", priorGolden.GetProperty("size").GetInt32(), prior.Count);
            var priorSample = priorGolden.GetProperty("sample");
            var priorBad = 0;
            foreach (var p in priorSample.EnumerateObject())
            {
                var want = p.Value.GetDouble();
                if (!prior.TryGetValue(p.Name, out var got) || Math.Abs(got - want) > 1e-6)
                {
                    priorBad++;
                    if (priorBad <= 5)
                    {
                        _fail++;
                        Report.AppendLine($"FAIL prior[{p.Name}] expected={want} actual={(prior.TryGetValue(p.Name, out var g2) ? g2 : double.NaN)}");
                    }
                }
            }
            if (priorBad == 0) _pass++;

            // arrange 是幂等的：把 Python 的输出再排一次必须得到自身。
            // 这条同时校验了 prior 取值与排序规则两件事。
            var plainWant = root.GetProperty("arrange_plain").EnumerateArray().Select(x => x.GetString()!).ToList();
            var plainGot = VocabDb.Arrange(plainWant, prior);
            Check("arrange_plain.idempotent", string.Join(", ", plainWant), string.Join(", ", plainGot));

            // 带 rating/artist 的那组必须把同样的参数再传一次才谈得上幂等
            var ratedWant = root.GetProperty("arrange_rated").EnumerateArray().Select(x => x.GetString()!).ToList();
            var ratedGot = VocabDb.Arrange(ratedWant, prior, rating: "explicit", artist: "@sayori style");
            Check("arrange_rated.idempotent", string.Join(", ", ratedWant), string.Join(", ", ratedGot));

            // ---- build 一致性 ----
            var b0 = root.GetProperty("build_case0");
            var b0text = b0.GetProperty("text").GetString()!;
            var b0want = b0.GetProperty("tags").EnumerateArray().Select(x => x.GetString()!).ToList();
            var b0got = VocabDb.Arrange(v.Scan(b0text).Select(h => h.Tag), prior);
            Check("build_case0", string.Join(", ", b0want), string.Join(", ", b0got));

            // ---- 审计 ----
            var audit = root.GetProperty("audit");
            var files = audit.GetProperty("files").GetInt32();
            var wantTags = audit.GetProperty("tags").GetInt32();
            var wantClean = audit.GetProperty("clean").GetInt32();

            var items = Dataset.Scan(cfg.DatasetDir).Where(i => i.HasCaption).ToList();
            var totalTags = 0; var clean = 0;
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

            CheckEq("audit.files", files, items.Count);
            CheckEq("audit.tags", wantTags, totalTags);
            CheckEq("audit.clean", wantClean, clean);

            var wantAgg = audit.GetProperty("agg");
            foreach (var p in wantAgg.EnumerateObject())
            {
                var want = p.Value.GetInt32();
                var got = agg.TryGetValue(p.Name, out var c) ? c : 0;
                CheckEq($"audit.agg[{p.Name}]", want, got);
            }
            foreach (var kv in agg)
                if (!wantAgg.TryGetProperty(kv.Key, out _))
                {
                    _fail++;
                    Report.AppendLine($"FAIL audit.agg 多出条目: {kv.Key} = {kv.Value}");
                }

            Emit($"parity: {_pass} passed, {_fail} failed");
            if (_fail > 0) Emit(Report.ToString());
        }
        catch (Exception ex)
        {
            _fail++;
            Emit("parity CRASHED: " + ex);
        }

        if (outPath is not null)
        {
            try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { Console.WriteLine("write result failed: " + ex.Message); }
        }
        return _fail == 0 ? 0 : 1;
    }
}
