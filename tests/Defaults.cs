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

        Console.WriteLine("\n=== model profiles: old flat fields -> site list ===");
        // 这一步最危险的地方是**密钥**：迁移写错既不报错也不崩，只是密钥变空，
        // 而用户直到某次请求 401 才知道。所以逐字段断言，包括密文原样搬运。
        int profBad = 0;
        void Prof(string label, string json, Action<AppSettings> check, string why)
        {
            var s = AppSettings.FromJson(json);
            bool ok;
            string got;
            try
            {
                check(s!);
                ok = true; got = "OK";
            }
            catch (Exception ex) { ok = false; got = ex.Message; }
            Console.WriteLine($"  {label,-30} {got,-46} {(ok ? "OK" : "WRONG")}  {why}");
            if (!ok) { bad++; profBad++; }
        }

        // 旧的四个顶层字段要原样搬进第一个站点
        Prof("旧配置：地址+模型+档位", """
            {"ApiBaseUrl":"https://api.deepseek.com","Model":"deepseek-flash",
             "ReasoningEffort":"max","MaxTokens":321}
            """,
            s =>
            {
                if (s.Profiles.Count != 1) throw new Exception("站点数=" + s.Profiles.Count);
                var p = s.ActiveProfile!;
                if (p.BaseUrl != "https://api.deepseek.com") throw new Exception("地址=" + p.BaseUrl);
                if (p.Model != "deepseek-flash") throw new Exception("模型=" + p.Model);
                if (p.ReasoningEffort != "max") throw new Exception("档位=" + p.ReasoningEffort);
                if (s.MaxTokens != 321) throw new Exception("MaxTokens 被弄丢");
            }, "顶层字段搬进站点，别的不动");

        // 密钥密文必须原样搬（不能解密再加密，那会多一次经手机会）
        Prof("旧配置：密钥密文原样搬走", """
            {"ApiBaseUrl":"https://x/v1","Model":"m","ApiKeyProtected":"QUJDREVGRw=="}
            """,
            s =>
            {
                if (s.ApiKeyProtected != "QUJDREVGRw==") throw new Exception("密文=" + s.ApiKeyProtected);
            }, "DPAPI 密文逐字节相同");

        // 迁移后旧顶层键必须消失，否则新旧两套长期并存、说法不一。
        // **判据必须落在根对象上**：这三个名字在站点条目内部是**正确存在**的
        // （每个站点都自带 BaseUrl / ApiKeyProtected / ReasoningEffort），
        // 所以"字符串里有没有出现"这种检查一定会误报。
        var migratedProf = AppSettings.FromJson("""
            {"ApiBaseUrl":"https://x/v1","Model":"m","ApiKeyProtected":"QUJD","ReasoningEffort":"low"}
            """)!;
        var pj = System.Text.Json.JsonSerializer.Serialize(migratedProf);
        using (var root = System.Text.Json.JsonDocument.Parse(pj))
        {
            var r = root.RootElement;
            var flat = new List<string>();
            foreach (var k in new[] { "ApiBaseUrl", "Model", "ApiKeyProtected", "ReasoningEffort" })
                if (r.TryGetProperty(k, out _)) flat.Add(k);
            var hasTable = r.TryGetProperty("Profiles", out var pl)
                        && pl.ValueKind == System.Text.Json.JsonValueKind.Array
                        && pl.GetArrayLength() == 1;
            Console.WriteLine($"  顶层残留的旧键=[{string.Join(",", flat)}]（应为空）" +
                              $" 站点表已写出={hasTable}");
            if (flat.Count > 0 || !hasTable) { bad++; profBad++; }
        }

        // 已经是新配置（有表、没有旧键）时**不能**被重置：这条守的是
        // "升级一次之后每次启动都把自己的站点表清掉"。
        Prof("新配置原样保留", """
            {"ProfileSchema":1,"ActiveProfileId":"b","Profiles":[
              {"Id":"a","Name":"本地","BaseUrl":"http://127.0.0.1:8080/v1","Model":"8b",
               "ReasoningEffort":"none"},
              {"Id":"b","Name":"云端","BaseUrl":"https://api.deepseek.com","Model":"flash",
               "ReasoningEffort":"max"}]}
            """,
            s =>
            {
                if (s.Profiles.Count != 2) throw new Exception("站点数=" + s.Profiles.Count);
                if (s.Model != "flash") throw new Exception("活动模型=" + s.Model);
                if (s.ReasoningEffort != "max") throw new Exception("活动档位=" + s.ReasoningEffort);
            }, "两个站点、活动指针生效");

        // 站点表为空 / 指针指向不存在的站点，都不该让视图变空
        Prof("空站点表补默认站点", """{"ProfileSchema":1,"Profiles":[]}""",
            s => { if (string.IsNullOrWhiteSpace(s.ApiBaseUrl)) throw new Exception("地址为空"); },
            "视图永远有落脚点");
        Prof("指针指向已删除的站点", """
            {"ProfileSchema":1,"ActiveProfileId":"gone","Profiles":[
              {"Id":"a","BaseUrl":"https://a/v1","Model":"ma"}]}
            """,
            s => { if (s.Model != "ma") throw new Exception("模型=" + s.Model); },
            "回落到第一个而不是变空");

        // 视图属性写回要落在活动站点上，而不是造出一个游离字段
        Prof("视图写回落到活动站点", """
            {"ProfileSchema":1,"ActiveProfileId":"b","Profiles":[
              {"Id":"a","BaseUrl":"https://a/v1","Model":"ma"},
              {"Id":"b","BaseUrl":"https://b/v1","Model":"mb"}]}
            """,
            s =>
            {
                s.Model = "changed";
                s.ApiBaseUrl = "https://b2/v1";
                if (s.Profiles.Count != 2) throw new Exception("站点数变了=" + s.Profiles.Count);
                if (s.Profiles[1].Model != "changed") throw new Exception("没落在活动站点");
                if (s.Profiles[0].Model != "ma") throw new Exception("改动了别的站点");
            }, "只有活动站点变");

        // 最后一个站点不许删（删光之后所有视图都是空串，看起来像配置丢了）
        var keepLast = AppSettings.FromJson(
            "{\"ProfileSchema\":1,\"Profiles\":[{\"Id\":\"only\"," +
            "\"BaseUrl\":\"https://a/v1\",\"Model\":\"ma\"}]}")!;
        var removedLast = keepLast.RemoveProfile("only");
        Console.WriteLine($"  最后一个站点可删={removedLast}（应为 False） 站点数={keepLast.Profiles.Count}");
        if (removedLast || keepLast.Profiles.Count != 1) { bad++; profBad++; }

        Console.WriteLine($"  站点迁移子项：{(profBad == 0 ? "全部 OK" : profBad + " WRONG")}");

        Console.WriteLine($"\n=== defaults: {(bad == 0 ? "OK" : bad + " FAILED")} ===");
        return bad == 0 ? 0 : 1;
    }
}
