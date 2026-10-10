using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AnimaCaptioner.Core;

namespace CoreCheck;

/// <summary>
/// 撤销 / 重做栈的回归。
///
/// 纯逻辑，不需要数据集也不需要词库。这里测的是**栈本身的语义**——
/// "哪个命令该在什么位置记撤销点"属于 MainWindow 的接线，corecheck 碰不到
/// WinUI，那一半靠 GUI 冒烟验证。
/// </summary>
internal static class History
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

        static List<string> Tags(params string[] t) => new(t);

        // ================= 1. 空栈 =================
        Console.WriteLine("=== 1. 空栈 ===");
        {
            var h = new PromptHistory();
            Check("初始不可撤销、不可重做", !h.CanUndo && !h.CanRedo);
            Check("空栈 Undo 返回 null", h.Undo(Tags("a"), "") is null);
            Check("空栈 Redo 返回 null", h.Redo(Tags("a"), "") is null);
            Check("默认步数", h.MaxSteps == PromptHistory.DefaultMaxSteps, $"max={h.MaxSteps}");
            Check("标签为空串", h.UndoLabel.Length == 0 && h.RedoLabel.Length == 0);
        }

        // ================= 2. 基本往返 =================
        Console.WriteLine("\n=== 2. Push / Undo / Redo 往返 ===");
        {
            var h = new PromptHistory();
            var before = Tags("1girl", "solo");
            h.Push(before, " prose.", "添加标签");
            Check("记下后可以撤销", h.CanUndo && h.UndoCount == 1);
            Check("撤销标签名", h.UndoLabel == "添加标签", $"'{h.UndoLabel}'");

            var after = Tags("1girl", "solo", "smile");
            var s = h.Undo(after, " prose.");
            Check("撤销拿到改动前的状态", s is not null && s.Tags.SequenceEqual(before));
            Check("撤销保留散文尾巴", s!.ProseTail == " prose.", $"'{s.ProseTail}'");
            Check("撤销后不可再撤销", !h.CanUndo && h.UndoCount == 0);
            Check("撤销后可以重做", h.CanRedo && h.RedoCount == 1);
            Check("重做标签沿用同一步", h.RedoLabel == "添加标签", $"'{h.RedoLabel}'");

            var s2 = h.Redo(before, " prose.");
            Check("重做拿回改动后的状态", s2 is not null && s2.Tags.SequenceEqual(after));
            Check("重做后回到可撤销", h.CanUndo && !h.CanRedo);
            Check("重做后栈深一致", h.UndoCount == 1 && h.RedoCount == 0,
                  $"undo={h.UndoCount} redo={h.RedoCount}");
        }

        // ================= 3. 快照是副本 =================
        Console.WriteLine("\n=== 3. 撤销点不受后续原地改写影响 ===");
        {
            var h = new PromptHistory();
            var live = Tags("1girl", "solo");
            h.Push(live, "x", "改名");

            // 模拟 MainWindow 的原地改写：_pendingTags[i] = newTag / Add / AddRange
            live[0] = "1boy";
            live.Add("sword");

            var s = h.Undo(Tags("1boy", "solo", "sword"), "x");
            Check("原地改写没有污染撤销点",
                  s is not null && s.Tags.SequenceEqual(Tags("1girl", "solo")),
                  s is null ? "null" : string.Join(",", s.Tags));
        }

        // ================= 4. 新操作作废重做 =================
        Console.WriteLine("\n=== 4. 新操作清空重做栈 ===");
        {
            var h = new PromptHistory();
            h.Push(Tags("a"), "", "第一步");
            h.Undo(Tags("a", "b"), "");
            Check("先有可重做", h.CanRedo);

            h.Push(Tags("a", "c"), "", "另一步");
            Check("新操作后重做被清空", !h.CanRedo && h.RedoCount == 0);
            Check("撤销仍可用", h.CanUndo);
        }

        // ================= 5. 步数上限 =================
        Console.WriteLine("\n=== 5. 步数上限裁剪 ===");
        {
            var h = new PromptHistory { MaxSteps = 3 };
            var cur = Tags("t0");
            for (var i = 1; i <= 4; i++)
            {
                h.Push(cur, "", $"op{i}");
                cur = Tags(Enumerable.Range(0, i + 1).Select(k => "t" + k).ToArray());
            }
            Check("超出上限只留 3 步", h.UndoCount == 3, $"count={h.UndoCount}");

            // 连续撤销：拿回 op4 / op3 / op2 之前的状态；op1 之前的已被裁掉
            var got = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var s = h.Undo(cur, "");
                if (s is null) break;
                got.Add(s.Label);
                cur = new List<string>(s.Tags);
            }
            Check("撤销顺序是最新的在前",
                  got.SequenceEqual(new[] { "op4", "op3", "op2" }), string.Join(",", got));
            Check("裁掉的旧撤销点拿不回来", !h.CanUndo);
            Check("重做栈收了 3 步", h.RedoCount == 3, $"redo={h.RedoCount}");
        }

        // ================= 6. 步数为 0 = 关闭 =================
        Console.WriteLine("\n=== 6. 步数为 0 时关闭撤销 ===");
        {
            var h = new PromptHistory { MaxSteps = 0 };
            Check("Push 被拒绝", !h.Push(Tags("a"), "", "添加标签"));
            Check("栈仍为空", !h.CanUndo && h.UndoCount == 0);
            Check("Undo 返回 null", h.Undo(Tags("a", "b"), "") is null);

            h.MaxSteps = 5;
            Check("调大后恢复可用", h.Push(Tags("a"), "", "添加标签") && h.CanUndo);
        }

        // ================= 7. 调小步数立刻裁剪 =================
        Console.WriteLine("\n=== 7. 运行中调小步数 ===");
        {
            var h = new PromptHistory { MaxSteps = 10 };
            for (var i = 0; i < 6; i++) h.Push(Tags("t" + i), "", "op" + i);
            Check("先攒够 6 步", h.UndoCount == 6, $"count={h.UndoCount}");

            h.MaxSteps = 2;
            Check("调小后立刻裁到 2", h.UndoCount == 2, $"count={h.UndoCount}");
            Check("保留的是最新的两步", h.UndoLabel == "op5", $"'{h.UndoLabel}'");
        }

        // ================= 8. 越界值收敛 =================
        Console.WriteLine("\n=== 8. 越界步数收敛 ===");
        {
            var h = new PromptHistory { MaxSteps = -5 };
            Check("负数当 0（关闭）", h.MaxSteps == 0, $"max={h.MaxSteps}");

            h.MaxSteps = 99999;
            Check("超大值收到上限", h.MaxSteps == PromptHistory.MaxStepsLimit, $"max={h.MaxSteps}");
        }

        // ================= 9. 没改动不记撤销点 =================
        Console.WriteLine("\n=== 9. 状态未变时不记 ===");
        {
            var h = new PromptHistory();
            h.Push(Tags("a", "b"), "tail", "添加标签");
            var again = h.Push(Tags("a", "b"), "tail", "又点了一下");
            Check("相同状态被拒绝", !again);
            Check("栈深没变", h.UndoCount == 1, $"count={h.UndoCount}");

            var proseOnly = h.Push(Tags("a", "b"), "tail!", "只改了散文");
            Check("散文不同则照记", proseOnly && h.UndoCount == 2, $"count={h.UndoCount}");

            var tagsOnly = h.Push(Tags("a", "b", "c"), "tail!", "只改了标签");
            Check("标签不同则照记", tagsOnly && h.UndoCount == 3, $"count={h.UndoCount}");
        }

        // ================= 10. Clear =================
        Console.WriteLine("\n=== 10. 换文档时清空 ===");
        {
            var h = new PromptHistory();
            h.Push(Tags("a"), "", "第一步");
            h.Push(Tags("a", "b"), "", "第二步");
            // 撤掉第二步：撤销栈里还剩第一步，重做栈里有第二步
            h.Undo(Tags("a", "b", "c"), "");
            Check("清空前两个栈都有东西", h.CanUndo && h.CanRedo,
                  $"undo={h.UndoCount} redo={h.RedoCount}");

            h.Clear();
            Check("清空后不可撤销也不可重做", !h.CanUndo && !h.CanRedo);
            Check("清空后栈深为 0", h.UndoCount == 0 && h.RedoCount == 0);
        }

        // ================= 11. 多步往返后状态一致 =================
        Console.WriteLine("\n=== 11. 多步撤销到底再重做回来 ===");
        {
            var h = new PromptHistory();
            var states = new List<List<string>>
            {
                Tags("1girl"),
                Tags("1girl", "solo"),
                Tags("1girl", "solo", "smile"),
                Tags("1girl", "solo", "smile", "safe"),
            };
            for (var i = 0; i < states.Count - 1; i++)
                h.Push(states[i], " p" + i, "op" + i);

            var cur = states[^1];
            var prose = " p" + (states.Count - 2);
            for (var i = states.Count - 2; i >= 0; i--)
            {
                var s = h.Undo(cur, prose);
                if (s is null) { Check($"撤销第 {i} 步", false, "null"); break; }
                cur = new List<string>(s.Tags);
                prose = s.ProseTail;
            }
            Check("一路撤销回到最初状态", cur.SequenceEqual(states[0]), string.Join(",", cur));
            Check("撤销到底后栈空", !h.CanUndo);

            for (var i = 1; i < states.Count; i++)
            {
                var s = h.Redo(cur, prose);
                if (s is null) { Check($"重做第 {i} 步", false, "null"); break; }
                cur = new List<string>(s.Tags);
                prose = s.ProseTail;
            }
            Check("一路重做回到最终状态", cur.SequenceEqual(states[^1]), string.Join(",", cur));
            Check("重做到底后栈空", !h.CanRedo);
        }

        // ================= 12. 分桶：两份文档互不干扰 =================
        Console.WriteLine("\n=== 12. 按文档身份分桶 ===");
        {
            var s = new PromptHistoryStore();
            var a = s.Attach("img:a", Tags("1girl"), "");
            var b = s.Attach("img:b", Tags("1boy"), "");

            Check("两份文档各有一个桶", s.DocumentCount == 2, $"count={s.DocumentCount}");
            Check("初始都不可撤销", !a.CanUndo && !b.CanUndo);
            Check("不同身份拿到不同的栈", !ReferenceEquals(a, b));

            a.Push(Tags("1girl"), "", "添加标签");
            Check("a 记了撤销点", a.CanUndo);
            Check("b 完全不受影响", !b.CanUndo, $"undo={b.UndoCount}");
            Check("同一身份复用同一个桶",
                  ReferenceEquals(s.Attach("img:a", Tags("1girl"), ""), a));
        }

        // ================= 13. 离开再回来，历史还在 =================
        Console.WriteLine("\n=== 13. 回到同一份文档，撤销能力还在 ===");
        {
            var s = new PromptHistoryStore();

            // 编辑 a，然后离开（离开时记下内容，供下次校验）
            var liveA = s.Attach("img:a", Tags("1girl"), "");
            liveA.Push(Tags("1girl"), "", "添加标签");
            var afterA = Tags("1girl", "solo");
            s.Remember("img:a", afterA, "");

            // 翻到 b 编辑，再回到 a —— 盘上内容与离开时一致
            var liveB = s.Attach("img:b", Tags("x"), "");
            liveB.Push(Tags("x"), "", "添加标签");
            s.Remember("img:b", Tags("x", "y"), "");

            var backA = s.Attach("img:a", afterA, "");
            Check("回到 a 撤销栈还在", backA.CanUndo && backA.UndoCount == 1, $"undo={backA.UndoCount}");
            Check("回到 a 拿到的是原来那个栈", ReferenceEquals(backA, liveA));

            var undone = backA.Undo(afterA, "");
            Check("撤销真的退回改动前",
                  undone is not null && undone.Tags.SequenceEqual(Tags("1girl")),
                  undone is null ? "null" : string.Join(",", undone.Tags));

            var backB = s.Attach("img:b", Tags("x", "y"), "");
            Check("b 的历史各自保留", backB.CanUndo && ReferenceEquals(backB, liveB));
        }

        // ================= 14. 内容被外部改过 → 丢掉历史 =================
        Console.WriteLine("\n=== 14. 文档被外部改过时丢掉历史 ===");
        {
            var s = new PromptHistoryStore();
            var live = s.Attach("img:a", Tags("1girl"), "");
            live.Push(Tags("1girl"), "", "添加标签");
            s.Remember("img:a", Tags("1girl", "solo"), "");

            // 盘上多了一个词：别的编辑器（或脚本）改过这个 caption
            var back = s.Attach("img:a", Tags("1girl", "solo", "external"), "");
            Check("历史被丢弃", !back.CanUndo && back.UndoCount == 0, $"undo={back.UndoCount}");
            Check("保留的是同一个桶", ReferenceEquals(back, live));
            Check("文档数没变", s.DocumentCount == 1, $"count={s.DocumentCount}");

            back.Push(Tags("1girl", "solo", "external"), "", "添加标签");
            Check("丢弃之后重新编辑照样能撤销", back.CanUndo);
        }

        // ================= 15. 只有尾随空白不同不算外部改动 =================
        Console.WriteLine("\n=== 15. 尾随空白不算外部改动 ===");
        {
            var s = new PromptHistoryStore();
            var live = s.Attach("draft:x", Tags("1girl"), "");
            live.Push(Tags("1girl"), "", "添加标签");
            // 草稿入库时正文会被 Trim，重新解析回来可能少一个尾随空白
            s.Remember("draft:x", Tags("1girl", "solo"), " A girl. ");

            var back = s.Attach("draft:x", Tags("1girl", "solo"), "A girl.");
            Check("只差尾随空白仍保留历史", back.CanUndo, $"undo={back.UndoCount}");
        }

        // ================= 16. 只浏览不编辑的文档不占桶 =================
        Console.WriteLine("\n=== 16. 只浏览不编辑的文档不占桶 ===");
        {
            var s = new PromptHistoryStore();
            s.Remember("img:never", Tags("a"), "");
            Check("没有桶时 Remember 不创建桶", s.DocumentCount == 0, $"count={s.DocumentCount}");

            var h = s.Attach("img:seen", Tags("a"), "");
            Check("Attach 会建桶", s.DocumentCount == 1, $"count={s.DocumentCount}");
            Check("但没编辑过就不可撤销", !h.CanUndo);
        }

        // ================= 17. 新草稿的共用身份必须迁移 =================
        Console.WriteLine("\n=== 17. 首次入库时把历史搬离共用键 ===");
        {
            var s = new PromptHistoryStore();
            var live = s.Attach(DraftDoc.NewIdentity, Tags("seed"), "");
            live.Push(Tags("seed"), "", "添加标签");
            s.Remember(DraftDoc.NewIdentity, Tags("seed", "chibi"), "");

            s.Migrate(DraftDoc.NewIdentity, DraftDoc.IdentityOf("abc123"));

            var back = s.Attach(DraftDoc.IdentityOf("abc123"), Tags("seed", "chibi"), "");
            Check("历史搬到了新身份上", back.CanUndo && ReferenceEquals(back, live),
                  $"undo={back.UndoCount}");

            // 关键：下一份全新草稿拿到的是**空**历史，不会继承上一份的撤销栈
            var fresh = s.Attach(DraftDoc.NewIdentity, Tags("seed"), "");
            Check("下一份新草稿的历史是空的", !fresh.CanUndo, $"undo={fresh.UndoCount}");
            Check("空身份常量与空 id 一致", DraftDoc.IdentityOf("") == DraftDoc.NewIdentity);
        }

        // ================= 18. 迁移不覆盖已存在的目标桶 =================
        Console.WriteLine("\n=== 18. 迁移不覆盖已存在的桶 ===");
        {
            var s = new PromptHistoryStore();
            var from = s.Attach("from", Tags("a"), "");
            from.Push(Tags("a"), "", "op-from");

            var to = s.Attach("to", Tags("b"), "");
            to.Push(Tags("b"), "", "op-to");

            s.Migrate("from", "to");
            Check("目标桶保持原样",
                  ReferenceEquals(s.Attach("to", Tags("b"), ""), to) && to.UndoLabel == "op-to",
                  $"'{to.UndoLabel}'");
            Check("来源桶被丢掉", !s.Attach("from", Tags("a"), "").CanUndo);

            var keep = s.Attach("keep", Tags("k"), "");
            keep.Push(Tags("k"), "", "op-keep");
            s.Migrate("keep", "keep");
            Check("同键迁移不丢历史",
                  ReferenceEquals(s.Attach("keep", Tags("k"), ""), keep) && keep.CanUndo);

            var before = s.DocumentCount;
            s.Migrate("", "x");
            s.Migrate("x", "");
            Check("空身份迁移是空操作", s.DocumentCount == before, $"count={s.DocumentCount}");
        }

        // ================= 19. LRU 淘汰：优先丢只浏览过的 =================
        Console.WriteLine("\n=== 19. 超过上限时优先淘汰只浏览过的 ===");
        {
            var s = new PromptHistoryStore { MaxDocuments = 2 };
            var edited = s.Attach("edited", Tags("a"), "");
            edited.Push(Tags("a"), "", "添加标签");
            s.Remember("edited", Tags("a", "b"), "");

            s.Attach("browse1", Tags("x"), "");   // 只点开过
            s.Attach("browse2", Tags("x"), "");   // 只点开过 → 超上限

            Check("文档数受上限约束", s.DocumentCount == 2, $"count={s.DocumentCount}");
            Check("真正编辑过的那份没被挤掉",
                  s.Attach("edited", Tags("a", "b"), "") is { CanUndo: true });

            s.Attach("browse3", Tags("x"), "");
            Check("继续淘汰的仍是只浏览过的", s.DocumentCount == 2, $"count={s.DocumentCount}");
            Check("编辑过的历史依然在",
                  s.Attach("edited", Tags("a", "b"), "") is { CanUndo: true });
        }

        // ================= 20. 步数上限设在仓库上 =================
        Console.WriteLine("\n=== 20. 仓库上的步数上限作用于每一桶 ===");
        {
            var s = new PromptHistoryStore { MaxSteps = 2 };
            var a = s.Attach("a", Tags("t0"), "");
            var b = s.Attach("b", Tags("t0"), "");
            for (var i = 1; i <= 3; i++)
            {
                a.Push(Tags("t" + (i - 1)), "", "op" + i);
                b.Push(Tags("t" + (i - 1)), "", "op" + i);
            }
            Check("a 被裁到上限", a.UndoCount == 2, $"undo={a.UndoCount}");
            Check("b 也被裁到上限", b.UndoCount == 2, $"undo={b.UndoCount}");

            s.MaxSteps = 5;
            Check("调大不会补出历史", a.UndoCount == 2, $"undo={a.UndoCount}");

            var c = s.Attach("c", Tags("z"), "");
            Check("新建的桶沿用当前上限", c.MaxSteps == 5, $"max={c.MaxSteps}");
            Check("无文档时的空栈也沿用上限", s.NewDetached().MaxSteps == 5);
            Check("空身份每次都拿到新栈",
                  !ReferenceEquals(s.Attach(null, Tags("z"), ""), s.Attach(null, Tags("z"), "")));
            Check("空身份不入桶", s.DocumentCount == 3, $"count={s.DocumentCount}");
        }

        // ================= 21. Drop / Clear =================
        Console.WriteLine("\n=== 21. Drop 与 Clear ===");
        {
            var s = new PromptHistoryStore();
            var a = s.Attach("a", Tags("x"), "");
            a.Push(Tags("x"), "", "op-a");
            var b = s.Attach("b", Tags("y"), "");
            b.Push(Tags("y"), "", "op-b");
            Check("两个桶", s.DocumentCount == 2, $"count={s.DocumentCount}");

            s.Drop("a");
            Check("Drop 后只剩一个", s.DocumentCount == 1, $"count={s.DocumentCount}");
            Check("Drop 掉的历史真的没了", !s.Attach("a", Tags("x"), "").CanUndo);
            Check("另一个桶不受影响", b.CanUndo && b.UndoLabel == "op-b", $"'{b.UndoLabel}'");

            s.Clear();
            Check("Clear 后清空全部桶", s.DocumentCount == 0, $"count={s.DocumentCount}");
            // 调用方手上还握着 _history 这个引用，所以 Clear 必须连它一起清，
            // 否则菜单会停在一份没人管的历史上。
            Check("Clear 也清空已发出的栈引用", !b.CanUndo, $"undo={b.UndoCount}");
        }

        Console.WriteLine(fail == 0 ? "\nhistory: 全部通过" : $"\nhistory: {fail} 项失败");
        return fail == 0 ? 0 : 1;
    }
}
