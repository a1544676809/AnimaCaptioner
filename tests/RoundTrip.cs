using System.Text;
using AnimaCaptioner.Core;

/// <summary>
/// 写盘无损性验证：caption 文件必须原样往返。
/// 这是最容易静默损坏训练集的地方（BOM、CRLF、结尾换行、分隔符变一个都会
/// 让整个文件在 diff 里报红，且部分训练脚本对 BOM 敏感），所以逐字节比对。
/// </summary>
internal static class RoundTrip
{
    public static int Run(string dir)
    {
        var files = Directory.EnumerateFiles(dir, "*.txt").OrderBy(x => x, StringComparer.Ordinal).ToList();
        var bad = 0;

        foreach (var f in files)
        {
            var before = File.ReadAllBytes(f);
            var cap = CaptionFile.Load(f);

            // 用「保存原样」走一遍完整写盘路径（会真的落盘，随后校验并还原）
            var changed = cap.Save(cap.Tags);
            var after = File.ReadAllBytes(f);

            if (!before.AsSpan().SequenceEqual(after))
            {
                bad++;
                Console.WriteLine($"MISMATCH {Path.GetFileName(f)}");
                Console.WriteLine($"  before({before.Length}): {Tail(before)}");
                Console.WriteLine($"  after ({after.Length}): {Tail(after)}");
                // 还原，绝不留下被改动的训练集
                File.WriteAllBytes(f, before);
                Console.WriteLine("  -> restored original");
            }
            else if (changed)
            {
                Console.WriteLine($"NOTE {Path.GetFileName(f)}: Save reported changed but bytes identical");
            }
        }

        Console.WriteLine($"round-trip: {files.Count} files, {bad} mismatched");
        return bad == 0 ? 0 : 1;
    }

    private static string Tail(byte[] b)
    {
        var n = Math.Min(24, b.Length);
        var sb = new StringBuilder();
        for (var i = b.Length - n; i < b.Length; i++)
            sb.Append(b[i] >= 32 && b[i] < 127 ? (char)b[i] : $"[{b[i]:X2}]");
        return sb.ToString();
    }

    /// <summary>建立一个新的 caption 时，字节风格必须与目录里既有的完全一致。</summary>
    public static int CheckNewFileStyle(string dir)
    {
        var files = Directory.EnumerateFiles(dir, "*.txt").ToList();
        if (files.Count == 0) { Console.WriteLine("no captions to compare"); return 1; }

        var refBytes = File.ReadAllBytes(files[0]);
        var refHasBom = refBytes.Length >= 3 && refBytes[0] == 0xEF;
        var refCrlf = Encoding.UTF8.GetString(refBytes).Contains("\r\n");
        var refTrailing = refBytes[^1] == 0x0A;

        var cap = new CaptionFile
        {
            Path = Path.Combine(Path.GetTempPath(), "style-probe.txt"),
            HasBom = false, NewLine = "\r\n", TrailingNewLine = true, Separator = ", "
        };
        cap.Tags.AddRange(new[] { "1girl", "solo", "smile" });
        cap.Save();

        var made = File.ReadAllBytes(cap.Path);
        var madeHasBom = made.Length >= 3 && made[0] == 0xEF;
        var madeCrlf = Encoding.UTF8.GetString(made).Contains("\r\n");
        var madeTrailing = made[^1] == 0x0A;

        var ok = refHasBom == madeHasBom && refCrlf == madeCrlf && refTrailing == madeTrailing;
        Console.WriteLine($"new-file style: ref(bom={refHasBom},crlf={refCrlf},trail={refTrailing}) " +
                          $"made(bom={madeHasBom},crlf={madeCrlf},trail={madeTrailing}) -> {(ok ? "OK" : "MISMATCH")}");
        Console.WriteLine($"  sample bytes: {Tail(made)}");
        File.Delete(cap.Path);
        return ok ? 0 : 1;
    }
}
