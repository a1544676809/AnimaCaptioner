using System;
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

        foreach (var n in wanted)
        {
            var t = asm.GetType(n);
            Console.WriteLine($"{(t is null ? "MISSING" : "ok     ")}  {n}");
        }

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

        return 0;
    }
}
