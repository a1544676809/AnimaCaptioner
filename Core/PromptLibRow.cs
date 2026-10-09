using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AnimaCaptioner.Core;

/// <summary>
/// 写标签模式左栏（提示词库）的一行。
///
/// 与 <see cref="ImageItem"/> 同构：状态用**文字 + 颜色**双通道表达
/// （Badge 直接写「预设 / 历史」），不单靠颜色，色盲也能分辨。
///
/// 行上带 token 估算与安全档位：这两项在"组装一段要拿去出图的提示词"时
/// 比在标注时更有用——超 512 会被训练器静默截断，而安全词缺失/矛盾会让
/// 出图结果和预期差一档。
/// </summary>
public sealed class PromptLibRow : INotifyPropertyChanged
{
    public required PromptLibraryStore.Entry Source { get; init; }

    /// <summary>
    /// 词库，交给 <see cref="PromptText.Summarize"/> 判散文边界用。
    /// 可以为 null（词库没加载时），那时摘要里的标签数会按控制词表来判——
    /// 但中栏此时用的是同一个 null，所以两处仍然一致。
    /// </summary>
    public VocabDb? Vocab { get; init; }

    /// <summary>供列表按 id 反查，避免依赖对象引用（列表会整体重建）。</summary>
    public string Id => Source.Id;

    public string Title => Source.Title.Length > 0 ? Source.Title : "(未命名)";

    public bool Pinned => Source.Pinned;

    public string Badge => Source.Pinned ? "预设" : "历史";

    /// <summary>预设用金色、历史用灰色：两者保留策略不同，必须一眼可分。</summary>
    public Brush BadgeBrush => Source.Pinned
        ? new SolidColorBrush(Microsoft.UI.Colors.Goldenrod)
        : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    public Brush BadgeTextBrush => new SolidColorBrush(Microsoft.UI.Colors.White);

    /// <summary>第二行：标签数 + token 估算 + 使用次数。</summary>
    public string SubText
    {
        get
        {
            var parts = PromptText.Summarize(Source.Body, Vocab);
            if (Source.Uses > 1) parts += $" · 用过 {Source.Uses} 次";
            return parts;
        }
    }

    /// <summary>整行正文的预览，鼠标悬停时看得到，避免标题不够时无从判断。</summary>
    public string Preview => Source.Body;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
