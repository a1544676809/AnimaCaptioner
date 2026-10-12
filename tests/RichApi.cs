using System;
using System.IO;
using System.Reflection;

namespace CoreCheck;

/// <summary>
/// Probes the WinUI assembly for the exact API surface the prompt-colouring
/// feature needs. Reflection over the real projected assembly is the reliable
/// check (ASCII string search over the DLL is a weak proxy; a missing member
/// only shows up at compile time otherwise).
/// </summary>
public static class RichApi
{
    public static int Run(string winuiDll)
    {
        // Microsoft.UI.* 这批类型在 Windows App SDK 1.8 里是**类型转发**：
        // Microsoft.WinUI.dll 只留转发项，真正的定义在同代的
        // Microsoft.Windows.SDK.NET.dll（由 Windows SDK 投影包提供）。
        //
        // NuGet 缓存里这两个文件在**不同目录**，所以直接 LoadFrom 时被转发的类型
        // 解析不了，GetType 会把它们全报成 MISSING——看着像"API 不存在"，
        // 实际只是没找到转发目标。构建输出里两者相邻，所以只有从输出目录加载
        // 才碰不到这个问题：这个测试此前一直是"恰好跑得通"。
        // 这里挂一个解析钩子，按需把转发目标从磁盘上找出来，在哪台机器上都成立。
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var simple = new AssemblyName(e.Name).Name;
            if (string.IsNullOrEmpty(simple)) return null;
            try
            {
                var dir = Path.GetDirectoryName(winuiDll);
                if (string.IsNullOrEmpty(dir)) return null;
                var hit = WalkUpAndDown(dir, simple + ".dll");
                return hit is null ? null : Assembly.LoadFrom(hit);
            }
            catch { return null; }
        };

        Assembly asm;
        try { asm = Assembly.LoadFrom(winuiDll); }
        catch (Exception ex)
        {
            Console.WriteLine("load failed: " + ex.Message);
            return 1;
        }

        string[] wanted =
        {
            "Microsoft.UI.Xaml.Controls.RichEditBox",
            "Microsoft.UI.Text.RichEditTextDocument",
            "Microsoft.UI.Text.ITextDocument",
            "Microsoft.UI.Text.ITextRange",
            "Microsoft.UI.Text.ITextCharacterFormat",
            "Microsoft.UI.Text.TextGetOptions",
            "Microsoft.UI.Text.TextSetOptions",
        };

        var missing = 0;
        foreach (var n in wanted)
        {
            var t = asm.GetType(n);
            if (t is null) missing++;
            Console.WriteLine($"{(t is null ? "MISSING" : "ok     ")}  {n}");
        }
        Console.WriteLine(missing == 0
            ? "全部命中；被转发的类型也能解析"
            : $"有 {missing} 项没找到（若全是转发类型，可能是缺 Microsoft.Windows.SDK.NET.dll）");

        var doc = asm.GetType("Microsoft.UI.Text.RichEditTextDocument");
        if (doc is not null)
        {
            foreach (var m in doc.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name is "GetText" or "SetText" or "GetRange" or "BatchDisplayUpdates"
                    or "ApplyDisplayUpdates")
                {
                    var ps = string.Join(",", Array.ConvertAll(m.GetParameters(),
                        p => p.ParameterType.Name));
                    Console.WriteLine($"  doc.{m.Name}({ps}) -> {m.ReturnType.Name}");
                }
            }
        }

        var range = asm.GetType("Microsoft.UI.Text.ITextRange");
        if (range is not null)
        {
            foreach (var p in range.GetProperties())
                Console.WriteLine($"  range.{p.Name} : {p.PropertyType.Name}");
        }

        var cf = asm.GetType("Microsoft.UI.Text.ITextCharacterFormat");
        if (cf is not null)
        {
            foreach (var p in cf.GetProperties())
                Console.WriteLine($"  fmt.{p.Name} : {p.PropertyType.Name}");
        }

        var reb = asm.GetType("Microsoft.UI.Xaml.Controls.RichEditBox");
        if (reb is not null)
        {
            foreach (var p in reb.GetProperties())
                if (p.Name is "Document" or "Text" or "TextWrapping" or "FontFamily" or "IsSpellCheckEnabled")
                    Console.WriteLine($"  box.{p.Name} : {p.PropertyType.Name}");
        }

        return missing == 0 ? 0 : 1;
    }

    /// <summary>
    /// 从 <paramref name="start"/> 往上走，每层向下有界地找 <paramref name="fileName"/>，
    /// 命中即返回。只为定位 NuGet 缓存里的依赖而写，所以刻意不写死任何根目录：
    /// 缓存根在别的机器上可能完全不同（NUGET_PACKAGES 或自定义位置）。
    /// </summary>
    private static string? WalkUpAndDown(string start, string fileName)
    {
        var dir = new DirectoryInfo(start);
        for (var up = 0; dir is not null && up < 8; up++, dir = dir.Parent)
        {
            var hit = FindDown(dir.FullName, fileName, budget: 4000);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>在目录树里向下找文件，最多访问 <paramref name="budget"/> 个目录。</summary>
    private static string? FindDown(string root, string fileName, int budget)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        for (var visited = 0; pending.Count > 0 && visited < budget; visited++)
        {
            var dir = pending.Pop();
            string[] subdirs, files;
            try { subdirs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir, fileName); }
            catch { continue; }

            if (files.Length > 0) return files[0];
            foreach (var d in subdirs)
            {
                var n = Path.GetFileName(d);
                // 跳过明显无关的深树，避免在缓存根上把整棵树翻一遍
                if (n is ".git" or "obj" or "node_modules") continue;
                pending.Push(d);
            }
        }
        return null;
    }
}
