using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AnimaCaptioner.Core;

/// <summary>
/// 搜索窗口里的一行结果。
///
/// 单独做成视图模型（而不是直接绑 <see cref="TagSearch.Hit"/>）有两个原因：
/// 1. 行上要显示"是否已经在当前图片里"，那是窗口侧的状态，不属于查询结果；
/// 2. 配色要用 <see cref="TagColors"/>，而那是 WinUI 类型，不能进 TagSearch
///    （那个文件要被 net8 回归程序直接编译）。
///
/// 用 x:Bind 绑定，所以属性必须是公开的、类型必须能在 XAML 里解析到
/// （嵌套类型在 XAML 里写不出来，这也是它不能塞进 TagSearch 的原因之一）。
///
/// <see cref="Already"/> 是唯一可变的字段：加完标签就地把它标成"已在图中"，
/// 而不是重跑一次搜索——重搜会把列表滚回顶部、清掉选中，连加几个标签时很难受。
/// 所以它带变更通知，XAML 侧用 <c>Mode=OneWay</c> 绑。
/// </summary>
public sealed class TagHitRow : INotifyPropertyChanged
{
    public required string Tag { get; init; }
    public string Cn { get; init; } = "";
    public string Meta { get; init; } = "";
    public Brush Tint { get; init; } = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    public Brush Accent { get; init; } = new SolidColorBrush(Microsoft.UI.Colors.Gray);

    private bool _already;

    /// <summary>已经在当前图片的标签里。免得用户反复加同一个。</summary>
    public bool Already
    {
        get => _already;
        set
        {
            if (_already == value) return;
            _already = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Already)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlreadyVisibility)));
        }
    }

    public Visibility CnVisibility => Cn.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AlreadyVisibility => Already ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}
