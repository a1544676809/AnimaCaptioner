using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AnimaCaptioner.Core;

/// <summary>
/// 左栏图片列表的一行。
///
/// 缩略图存在行对象上（由 <see cref="ThumbnailLoader"/> 填充并做容量上限），
/// 而不是在模板里实时解码——后者会在滚动时不停读盘。
///
/// 标注状态用文字而不是只靠颜色表达（Badge 直接写「已标注/未标注」），
/// 色盲用户也能分辨。
/// </summary>
public sealed class ImageItem : INotifyPropertyChanged
{
    public required DatasetItem Source { get; init; }

    /// <summary>在整个数据集里的序号，供预览窗口导航与状态栏显示。</summary>
    public int Index { get; set; }

    public string Name => Path.GetFileName(Source.ImagePath);

    public bool HasCaption { get; private set; }
    public int TagCount { get; private set; }
    public int IssueCount { get; private set; }

    /// <summary>列表第二行：标签数，或未标注提示。</summary>
    public string SubText
    {
        get
        {
            if (!HasCaption) return "未标注 · 保存时新建 .txt";
            var s = $"{TagCount} tags";
            if (IssueCount > 0) s += $" · ⚠ {IssueCount} 待确认";
            return s;
        }
    }

    public string Badge => HasCaption ? "已标注" : "未标注";

    /// <summary>标注状态徽标底色。用命名色而不是 FromArgb：后者在投影层里未必可见。</summary>
    public Brush BadgeBrush => HasCaption
        ? new SolidColorBrush(Microsoft.UI.Colors.SeaGreen)
        : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    /// <summary>徽标文字用白色（两种底色都足够深）。</summary>
    public Brush BadgeTextBrush => new SolidColorBrush(Microsoft.UI.Colors.White);

    /// <summary>文件扩展名，缩略图未就绪时显示，避免一片空白。</summary>
    public string Ext =>
        Path.GetExtension(Source.ImagePath).TrimStart('.').ToUpperInvariant();

    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            Raise();
            // 缩略图就绪后要藏掉扩展名占位：训练集是透明 PNG，
            // 文字留在 Image 底下会从透明区域透出来。
            Raise(nameof(ExtVisibility));
        }
    }

    /// <summary>缩略图没有时才显示扩展名占位，避免大片空白。</summary>
    public Visibility ExtVisibility =>
        _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>解码失败（文件损坏/格式不支持）。置位后不再重试。</summary>
    public bool ThumbFailed { get; set; }

    /// <summary>正在解码，防止同一行被并发触发多次。</summary>
    public bool ThumbLoading { get; set; }

    /// <summary>用最新的标注状态刷新这一行（载入 / 保存后调用）。</summary>
    public void RefreshStatus(bool hasCaption, int tagCount, int issueCount)
    {
        HasCaption = hasCaption;
        TagCount = tagCount;
        IssueCount = issueCount;
        Raise(nameof(HasCaption));
        Raise(nameof(TagCount));
        Raise(nameof(IssueCount));
        Raise(nameof(SubText));
        Raise(nameof(Badge));
        Raise(nameof(BadgeBrush));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
