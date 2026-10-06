using System;
using System.IO;
using AnimaCaptioner.Core;

internal static class DefaultsProbe
{
    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        int bad = 0;
        void Show(string label, string value)
        {
            Console.WriteLine($"  {label,-16} {value}");
        }

        Console.WriteLine("=== resolved defaults (what a NEW user gets) ===");
        var fresh = new AppSettings();
        Show("DatasetDir", fresh.DatasetDir);
        Show("VocabDbPath", fresh.VocabDbPath);
        Show("SeedPath", fresh.SeedPath);

        Console.WriteLine("\n=== checks ===");
        // No drive-specific hardcoding left
        foreach (var (name, val) in new (string, string)[]
                 { ("DatasetDir", fresh.DatasetDir),
                   ("VocabDbPath", fresh.VocabDbPath),
                   ("SeedPath", fresh.SeedPath) })
        {
            bool rooted = Path.IsPathRooted(val);
            bool ours = val.Contains("AnimaCaptioner", StringComparison.OrdinalIgnoreCase);
            bool noForeignDrive = !val.StartsWith(@"E:\ComfyUI", StringComparison.OrdinalIgnoreCase)
                               && !val.StartsWith(@"E:\train", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  {name,-12} rooted={rooted} underOurDir={ours} notThisDevBox={noForeignDrive}");
            if (!rooted || !ours || !noForeignDrive) bad++;
        }

        Console.WriteLine("\n=== the EXISTING user config must be untouched ===");
        var saved = AppSettings.Load();
        Show("DatasetDir", saved.DatasetDir);
        Show("VocabDbPath", saved.VocabDbPath);
        Show("SeedPath", saved.SeedPath);
        bool kept = saved.VocabDbPath.Contains("anima-vocab", StringComparison.OrdinalIgnoreCase)
                 && saved.SeedPath.EndsWith("seed.json", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"  existing settings preserved: {kept}");
        if (!kept) bad++;

        Console.WriteLine($"\n=== defaults: {(bad == 0 ? "OK" : bad + " FAILED")} ===");
        return bad == 0 ? 0 : 1;
    }
}
