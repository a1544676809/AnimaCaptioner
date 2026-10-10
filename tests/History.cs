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

        Console.WriteLine(fail == 0 ? "\nhistory: 全部通过" : $"\nhistory: {fail} 项失败");
        return fail == 0 ? 0 : 1;
    }
}
