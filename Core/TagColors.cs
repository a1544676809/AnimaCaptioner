using System.Globalization;
using Microsoft.UI.Xaml.Media;

namespace AnimaCaptioner.Core;

/// <summary>
/// 大类颜色的解析与画刷构造。和 <see cref="TagSections"/> 分开放：
/// 那边是纯逻辑（要对拍、要能在 net8 控制台里编译），这边要用 WinUI 类型。
/// </summary>
public static class TagColors
{
    /// <summary>把 "#RRGGBB" / "#AARRGGBB" 解析成颜色。解析失败返回 null，由调用方回落默认。</summary>
    public static Windows.UI.Color? ParseHex(string? hex)
    {
        var s = TagSections.NormalizeHex(hex);
        if (s is null) return null;
        var v = uint.Parse(s.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Windows.UI.Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string ToHex(Windows.UI.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>取某个大类的颜色：配置里有就用配置的，损坏或缺失回落默认。
    /// 用 ColorOf 而不是 Get：自然语言不是大类，但它也要能配色。</summary>
    public static Windows.UI.Color ColorOf(string key, AppSettings? cfg)
    {
        var info = TagSections.ColorOf(key);
        return ParseHex(cfg?.GetSectionColor(key)) ?? ParseHex(info.DefaultHex)!.Value;
    }

    public static Brush BrushOf(string key, AppSettings? cfg, byte alpha = 255)
    {
        var c = ColorOf(key, cfg);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, c.R, c.G, c.B));
    }

    /// <summary>行底色：同一个大类所有行共享一层很淡的同色底，视觉上连成一个框。</summary>
    public static Brush RowTintOf(string key, AppSettings? cfg) => BrushOf(key, cfg, 26);

    /// <summary>分组头条带：比行底色明显，作为大类之间的分界。</summary>
    public static Brush HeaderOf(string key, AppSettings? cfg) => BrushOf(key, cfg, 54);

    /// <summary>
    /// 提示词原文里某个区间的底色。
    ///
    /// **必须是全不透明的实色**，不能用带 alpha 的淡色。踩过的坑：先拿
    /// alpha=0 的"透明色"去清空底色，再给标签段上 alpha=34 的淡色，结果
    /// RichEdit 把 alpha=0 当成**不透明黑**，整段正文变成白字黑底、完全读不了
    /// （截图 pt-main.png 就是那个样子）。所以这里改成把分类色按
    /// <paramref name="mix"/> 的比例混进**实际背景色**，得到一个不透明的实色；
    /// 文字色则显式设回主题前景色。
    /// </summary>
    public static Windows.UI.Color TintOf(string key, AppSettings? cfg,
                                          Windows.UI.Color background, double mix = 0.22)
    {
        var c = ColorOf(key, cfg);
        if (mix < 0) mix = 0;
        if (mix > 1) mix = 1;
        return Windows.UI.Color.FromArgb(255,
            (byte)Math.Round(background.R + (c.R - background.R) * mix),
            (byte)Math.Round(background.G + (c.G - background.G) * mix),
            (byte)Math.Round(background.B + (c.B - background.B) * mix));
    }
}
