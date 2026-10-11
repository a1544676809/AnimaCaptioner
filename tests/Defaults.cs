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

        Console.WriteLine("\n=== settings JSON migration: old bool -> reasoning effort ===");
        // 升级时那段迁移必须逐条对上：写错既不报错、也不崩溃，只会让用户的选择
        // 悄悄变掉（昨晚关掉的思考今早自己开了），所以这里按行为钉住。
        int migBad = 0;
        void Mig(string label, string json, string wantEffort, string why)
        {
            var s = AppSettings.FromJson(json);
            var ok = s is not null && s.ReasoningEffort == wantEffort && s.LegacyDisableThinking is null;
            Console.WriteLine($"  {label,-34} -> '{s?.ReasoningEffort}'  期望 '{wantEffort}'  {(ok ? "OK" : "WRONG")}   {why}");
            if (!ok) { bad++; migBad++; }
        }

        Mig("旧 DisableThinking=true", "{\"DisableThinking\":true}", "none", "真的关掉");
        Mig("旧 DisableThinking=false", "{\"DisableThinking\":false}", "", "自动，不替用户选档");
        Mig("两个键都没有（更老的配置）", "{\"MaxTokens\":500}", "none", "回落默认，与旧默认一致");
        Mig("已经是新键", "{\"ReasoningEffort\":\"max\"}", "max", "原样保留");
        Mig("新键是空串（自动）", "{\"ReasoningEffort\":\"\"}", "", "空串不能被当成缺失");
        Mig("两个键同时在场", "{\"DisableThinking\":true,\"ReasoningEffort\":\"max\"}", "none",
            "旧键优先（这种情况只可能来自手改）");

        // 迁移过就自我清理：旧键不能再写回去，否则它会和档位长期并存、说法不一。
        var migrated = AppSettings.FromJson("{\"DisableThinking\":false}")!;
        var json = System.Text.Json.JsonSerializer.Serialize(migrated);
        var noLegacy = !json.Contains("DisableThinking", StringComparison.Ordinal);
        var hasNew = json.Contains("ReasoningEffort", StringComparison.Ordinal);
        Console.WriteLine($"  序列化后旧键已消失={noLegacy} 新键已写出={hasNew}");
        if (!noLegacy || !hasNew) { bad++; migBad++; }
        // 迁移不能顺手把**别的**字段弄丢：读进来 -> 写出去 -> 再读进来必须一致。
        // 这条守的是"下次保存时用户的配置被悄悄改小"——那不会报错，只会让人纳闷。
        var rich = """
        {"ApiBaseUrl":"https://example.invalid/v1","Model":"m1","ReasoningEffort":"medium",
         "MaxTokens":123,"Temperature":0.7,"TimeoutSeconds":99,"DatasetDir":"D:\\d1",
         "SearchEnabled":true,"SearchProvider":"tavily","UndoMaxSteps":42,
         "SectionColors":{"general":"#112233","quality":"#445566"},"ColorSchema":2,
         "AgentEnabled":true,"AgentAttachImage":false}
        """;
        var r1 = AppSettings.FromJson(rich)!;
        var r2 = AppSettings.FromJson(System.Text.Json.JsonSerializer.Serialize(r1))!;
        foreach (var (name, a, b) in new (string, object?, object?)[]
                 {
                     ("ApiBaseUrl", r1.ApiBaseUrl, r2.ApiBaseUrl),
                     ("Model", r1.Model, r2.Model),
                     ("ReasoningEffort", r1.ReasoningEffort, r2.ReasoningEffort),
                     ("MaxTokens", r1.MaxTokens, r2.MaxTokens),
                     ("Temperature", r1.Temperature, r2.Temperature),
                     ("TimeoutSeconds", r1.TimeoutSeconds, r2.TimeoutSeconds),
                     ("DatasetDir", r1.DatasetDir, r2.DatasetDir),
                     ("SearchEnabled", r1.SearchEnabled, r2.SearchEnabled),
                     ("SearchProvider", r1.SearchProvider, r2.SearchProvider),
                     ("UndoMaxSteps", r1.UndoMaxSteps, r2.UndoMaxSteps),
                     ("SectionColors[general]", r1.GetSectionColor("general"), r2.GetSectionColor("general")),
                     ("SectionColors[quality]", r1.GetSectionColor("quality"), r2.GetSectionColor("quality")),
                     ("ColorSchema", r1.ColorSchema, r2.ColorSchema),
                     ("AgentEnabled", r1.AgentEnabled, r2.AgentEnabled),
                     ("AgentAttachImage", r1.AgentAttachImage, r2.AgentAttachImage),
                 })
        {
            var ok = Equals(a, b);
            if (!ok) { Console.WriteLine($"  {name,-24} 往返后变了：'{a}' -> '{b}'"); bad++; migBad++; }
        }
        Console.WriteLine($"  字段往返（15 个）：{(migBad == 0 ? "全部一致" : "有差异")}");
        Console.WriteLine($"  迁移子项：{(migBad == 0 ? "全部 OK" : migBad + " WRONG")}");

        Console.WriteLine($"\n=== defaults: {(bad == 0 ? "OK" : bad + " FAILED")} ===");
        return bad == 0 ? 0 : 1;
    }
}
