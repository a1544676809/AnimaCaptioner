using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AnimaCaptioner.Core;

namespace CoreCheck;

/// <summary>
/// 写标签模式的纯逻辑回归：提示词库（历史 / 预设 / 淘汰 / 去重 / 标题派生）
/// 与模式相关的默认值。
///
/// 刻意**只用临时文件**（Load(path) 那个重载），绝不碰用户真实的
/// %LOCALAPPDATA%\AnimaCaptioner\prompts.json —— 一个测试去改用户的真实库，
/// 就算断言全绿也是有害的。
/// </summary>
internal static class Prompts
{
    public static int Run()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var fail = 0;

        void Check(string label, bool ok, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!ok) fail++;
        }

        var dir = Path.Combine(Path.GetTempPath(), "anima-prompts-test");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "prompts.json");

        try
        {
            Console.WriteLine("=== 1. 增删改查与落盘往返 ===");
            var lib = PromptLibraryStore.Load(path);
            Check("新库为空", lib.Count == 0, $"count={lib.Count}");

            var a = lib.Add("1girl, solo, smile");
            var b = lib.Add("1boy, solo, sword");
            Check("加入两条", lib.Count == 2, $"count={lib.Count}");
            Check("id 非空且互不相同", a.Id.Length > 0 && b.Id.Length > 0 && a.Id != b.Id);

            lib.Save();
            var reloaded = PromptLibraryStore.Load(path);
            Check("落盘后重新载入条数一致", reloaded.Count == 2, $"count={reloaded.Count}");
            Check("正文按原样还原", reloaded.Get(a.Id)?.Body == "1girl, solo, smile");
            Check("标题被派生（不是空）", reloaded.Get(a.Id)?.Title.Length > 0,
                  $"title='{reloaded.Get(a.Id)?.Title}'");

            Console.WriteLine();
            Console.WriteLine("=== 2. 按正文去重，而不是每次复制都加一条 ===");
            var dup1 = lib.Add("1girl, solo, smile");
            Check("同正文不新增", lib.Count == 2, $"count={lib.Count}");
            Check("返回的是已有那条", dup1.Id == a.Id, $"{dup1.Id} vs {a.Id}");
            Check("使用次数累加", dup1.Uses == 2, $"uses={dup1.Uses}");

            Console.WriteLine();
            Console.WriteLine("=== 3. 预设不被历史上限淘汰 ===");
            var pin = lib.Add("masterpiece, best quality, score_7, safe, 1girl", pinned: true);
            Check("钉住的进库", lib.Get(pin.Id)?.Pinned == true);

            // 灌到超过上限，逼出淘汰逻辑
            for (var i = 0; i < PromptLibraryStore.MaxHistory + 40; i++)
                lib.Add($"{i}girl, unique_tag_number_{i}");

            Check("预设仍在（未被淘汰）", lib.Get(pin.Id) is not null,
                  $"count={lib.Count} pinned={lib.PinnedCount}");
            Check("条数被压到上限内", lib.Count <= PromptLibraryStore.MaxHistory,
                  $"count={lib.Count} max={PromptLibraryStore.MaxHistory}");
            Check("最早的那条历史已被淘汰", lib.Get(b.Id) is null);

            Console.WriteLine();
            Console.WriteLine("=== 4. 清空历史保留预设 ===");
            var removed = lib.ClearHistory();
            Check("清掉了历史", removed > 0, $"removed={removed}");
            Check("只剩预设", lib.Count == lib.PinnedCount, $"{lib.Count} vs {lib.PinnedCount}");
            Check("预设内容还完整", lib.Get(pin.Id)?.Body == "masterpiece, best quality, score_7, safe, 1girl");

            Console.WriteLine();
            Console.WriteLine("=== 5. 钉住 / 取消钉住 ===");
            var hist = lib.Add("sword, armor, red cape");
            Check("默认是历史", hist.Pinned == false);
            lib.SetPinned(hist.Id, true);
            Check("钉住生效", lib.Get(hist.Id)?.Pinned == true);
            lib.SetPinned(hist.Id, false);
            Check("取消钉住生效", lib.Get(hist.Id)?.Pinned == false);

            Console.WriteLine();
            Console.WriteLine("=== 6. 过滤 ===");
            Check("空关键词返回全部", lib.Search("").Count == lib.Count, $"{lib.Search("").Count} vs {lib.Count}");
            Check("按正文命中", lib.Search("cape").Count >= 1);
            Check("按标题命中", lib.Search("sword").Count >= 1);
            Check("无关词返回空", lib.Search("zzz_no_such_prompt").Count == 0);
            Check("大小写不敏感", lib.Search("SWORD").Count == lib.Search("sword").Count);

            Console.WriteLine();
            Console.WriteLine("=== 7. 标题派生跳过通用控制词 ===");
            // 提示词常以 masterpiece/best quality/score_7/safe 开头，按字符截会让
            // 一堆条目标题长得一样。派生必须跳过这些头部控制词。
            var t1 = PromptLibraryStore.DeriveTitle(
                "masterpiece, best quality, score_7, safe, 1girl, silver hair, kimono");
            Check("跳过控制词，从有信息量的标签起", t1.StartsWith("1girl", StringComparison.Ordinal),
                  $"title='{t1}'");
            Check("标题不过长", t1.Length <= 48, $"len={t1.Length}");

            var t2 = PromptLibraryStore.DeriveTitle(
                "masterpiece, best quality, score_7, safe. An anime girl stands under a tree.");
            Check("散文尾巴不混进标题", !t2.Contains("stands", StringComparison.Ordinal),
                  $"title='{t2}'");

            var t3 = PromptLibraryStore.DeriveTitle("year 2025, newest, highres, safe, 1girl");
            Check("年代/元信息也当控制词跳过", t3.StartsWith("1girl", StringComparison.Ordinal),
                  $"title='{t3}'");

            var t4 = PromptLibraryStore.DeriveTitle("");
            Check("空正文有占位标题", t4.Length > 0, $"title='{t4}'");

            var t5 = PromptLibraryStore.DeriveTitle(
                "An anime girl with silver hair stands beneath a blossoming tree.");
            Check("纯散文也能派生", t5.Length > 0 && t5.Length <= 48, $"title='{t5}'");

            Console.WriteLine();
            Console.WriteLine("=== 8. 行摘要口径（列表与状态栏必须同源）===");
            var sum = PromptText.Summarize("masterpiece, best quality, score_7, safe, 1girl");
            Check("含 token 估算", sum.Contains("tokens", StringComparison.Ordinal), $"'{sum}'");

            // 多词标签必须算成标签。曾经用"含空格的段不算标签"来排除散文，
            // 结果 best quality / silver hair 被漏数，状态栏左边 3 tags、右边 4 tags。
            Check("多词标签不被当成散文漏数", sum.StartsWith("5 tags", StringComparison.Ordinal), $"'{sum}'");
            Check("摘要标签数与解析器一致",
                  sum.StartsWith($"{PromptText.Parse("masterpiece, best quality, score_7, safe, 1girl", null).Tags.Count} tags",
                                 StringComparison.Ordinal),
                  $"'{sum}'");
            // 散文尾巴必须被排除，而不是靠空格猜
            var sumProse = PromptText.Summarize("masterpiece, safe. An anime girl stands under a tree.");
            Check("散文尾巴不混进标签数",
                  sumProse.StartsWith($"{PromptText.Parse("masterpiece, safe. An anime girl stands under a tree.", null).Tags.Count} tags",
                                      StringComparison.Ordinal),
                  $"'{sumProse}'");

            // 超预算必须报出来——训练侧 truncation=True 会静默截断
            var huge = string.Join(", ", Enumerable.Range(0, 400).Select(i => $"some_long_tag_name_{i}"));
            var sumHuge = PromptText.Summarize(huge);
            Check("超 512 时给出警告", sumHuge.Contains("512", StringComparison.Ordinal), $"'{sumHuge}'");
            Check("OverBudget 与摘要一致", PromptText.OverBudget(huge) == sumHuge.Contains("超 512"));
            Check("空正文摘要为「空」", PromptText.Summarize("") == "空");

            Console.WriteLine();
            Console.WriteLine("=== 9. 坏文件不致命（坏了就当空库重建）===");
            var badPath = Path.Combine(dir, "bad.json");
            File.WriteAllText(badPath, "{ this is not json ");
            var bad = PromptLibraryStore.Load(badPath);
            Check("坏 JSON 不抛异常且为空库", bad.Count == 0, $"count={bad.Count}");
            bad.Add("1girl");
            bad.Save();
            Check("坏文件可被覆盖重建", PromptLibraryStore.Load(badPath).Count == 1);

            Console.WriteLine();
            Console.WriteLine("=== 10. 文档身份（模式门禁的判据）===");
            var draft = DraftDoc.New();
            Check("新草稿没有 id", draft.Id.Length == 0);
            Check("新草稿是写标签模式", draft.Mode == PromptMode.Draft);
            Check("新草稿的主操作是复制", draft.PrimaryActionText == "复制提示词");
            Check("未命名草稿有显示名", draft.DisplayName.Length > 0, $"'{draft.DisplayName}'");
            Check("新草稿标题是占位名，且未标记为自定义", !draft.HasCustomTitle);
            Check("入库前后 Identity 不同（去重靠它）",
                  draft.Identity != (new DraftDoc { Id = "abc" }).Identity,
                  $"{draft.Identity} vs {(new DraftDoc { Id = "abc" }).Identity}");

            Console.WriteLine();
            Console.WriteLine("=== 10b. 占位标题不得覆盖库里派生的标题（实测踩到的缺陷）===");
            // 场景：新建草稿 -> 写入一段具体的提示词 -> 入库。
            // 若把占位名「未命名草稿」当标题传下去，Update 会覆盖派生标题，
            // 那条提示词就永远叫「未命名草稿」了。
            var lib3 = PromptLibraryStore.Load(Path.Combine(dir, "titles.json"));
            var body3 = "masterpiece, best quality, score_7, safe, 1girl, silver hair, kimono";
            var e3 = lib3.Add(body3, pinned: false, title: null);   // null = 让库派生
            Check("标题是派生的、不是占位串",
                  e3.Title != DraftDoc.DefaultTitle && e3.Title.Contains("1girl", StringComparison.Ordinal),
                  $"title='{e3.Title}'");

            // 模拟"已有条目再次 Flush，但用户没命名过"：标题传 null，不能被改坏
            var again = lib3.Update(e3.Id, body3, null);
            Check("再次入库不破坏已有标题", again?.Title == e3.Title, $"'{again?.Title}'");

            // 用户显式命名后，钉住/取消钉住都不该把名字改掉
            lib3.Update(e3.Id, body3, "银色和服少女");
            lib3.SetPinned(e3.Id, true);
            Check("钉住不覆盖用户起的名字", lib3.Get(e3.Id)?.Title == "银色和服少女",
                  $"'{lib3.Get(e3.Id)?.Title}'");
            lib3.SetPinned(e3.Id, false);
            Check("取消钉住也不覆盖", lib3.Get(e3.Id)?.Title == "银色和服少女",
                  $"'{lib3.Get(e3.Id)?.Title}'");

            Console.WriteLine();
            Console.WriteLine("=== 11. 模式配置的默认与往返 ===");
            var cfg = new AppSettings();
            Check("默认是标注模式", cfg.LastMode == "label", $"'{cfg.LastMode}'");
            Check("写标签上次编辑的条目默认为空", cfg.DraftLastPromptId == "", $"'{cfg.DraftLastPromptId}'");
            Check("写标签分栏比例默认未存", cfg.DraftLeftStar == 0 && cfg.DraftMidStar == 0 && cfg.DraftRightStar == 0);

            // 「按热度自动排」是菜单上的一次性动作，刻意**不做成配置项**：
            // 做成开关会让人误以为新标签会自动按热度插入，而实际插入位置是列表末尾。
            // 这里钉住"它不参与配置往返"，避免以后被当开关重新加回来。
            var cfgJson = System.Text.Json.JsonSerializer.Serialize(cfg);
            Check("自动排不是配置项（避免误当成开关）",
                  !cfgJson.Contains("AutoArrange", StringComparison.OrdinalIgnoreCase));

            Console.WriteLine();
            Console.WriteLine("=== 12. 空提示词不该凭空建出 caption 文件（既有缺陷）===");
            // SaveCurrent 会在"切到别的图"时被静默调用，所以以前在一张未标注的图上
            // 停留过再点下一张，就会生成一个只有换行的 .txt：左栏把它标成「已标注」、
            // 「跳到下一个未标注」从此跳过它、训练侧读到空白 caption。
            var emptyDir = Path.Combine(dir, "emptysave");
            Directory.CreateDirectory(emptyDir);
            var emptyPath = Path.Combine(emptyDir, "unlabeled.txt");
            var emptyCf = new CaptionFile
            {
                Path = emptyPath, HasBom = false, NewLine = "\r\n",
                TrailingNewLine = true, Separator = ", "
            };
            var emptyBody = PromptText.Rebuild(new List<string>(), "");
            Check("空正文拼出来就是空串", emptyBody.Length == 0, $"'{emptyBody}'");

            // SaveBody 本身不负责挡这个（它是底层写盘原语），所以守卫在
            // SaveCurrent 里；这里钉住"空正文的判定"这一半契约。
            var wouldCreate = emptyBody.Trim().Length == 0;
            Check("空正文可被识别出来（守卫的判据）", wouldCreate);
            Check("守卫生效前该文件确实不存在", !File.Exists(emptyPath));

            // 而非空的正文必须照常能建文件——不能把功能一起挡掉
            var okPath = Path.Combine(emptyDir, "labeled.txt");
            var okCf = new CaptionFile
            {
                Path = okPath, HasBom = false, NewLine = "\r\n",
                TrailingNewLine = true, Separator = ", "
            };
            var okBody = PromptText.Rebuild(new[] { "1girl", "solo" }, "");
            Check("非空正文写出成功", okCf.SaveBody(okBody));
            Check("非空文件内容正确",
                  File.ReadAllText(okPath).Trim() == "1girl, solo",
                  $"'{File.ReadAllText(okPath).Trim()}'");

            Console.WriteLine();
            Console.WriteLine(fail == 0
                ? "=== prompts: 全部通过 ==="
                : $"=== prompts: {fail} 项失败 ===");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        return fail == 0 ? 0 : 1;
    }
}
