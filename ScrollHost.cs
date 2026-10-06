using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AnimaCaptioner;

/// <summary>
/// 带常驻滚动条的滚动宿主。
///
/// 为什么自己画滚动条：WinUI 3 的 ScrollViewer 自带那根是**浮层、会自动淡出**，
/// 实测静止状态下截帧里一个像素都不画——“下面还有内容”这件事在界面上根本看不出来。
/// 独立的 ScrollBar 控件两条路也都试过：MouseIndicator 只在鼠标交互时才画滑块，
/// TouchIndicator 什么都不画。所以用 Thumb 自己摆，位置和长度按 ScrollableHeight 算。
///
/// （ScrollViewer.ScrollingIndicatorMode 在 WinUI 3 里**不存在**，那是 UWP 的 API，
/// 编译期就会报错。这条弯路走过一次，记在 README 里。）
/// </summary>
internal sealed class ScrollHost : Grid
{
    private readonly ScrollViewer _scroll;
    private readonly Border _track;
    private readonly Thumb _thumb;
    private readonly Border _barCol;
    private bool _syncing;

    /// <summary>滚动条常驻显示。设 false 就退回"需要时才出现"。</summary>
    public bool AlwaysShowBar { get; init; } = true;

    public ScrollViewer Scroller => _scroll;

    /// <summary>滚动内容被替换后（例如切换模块）重新同步一次几何。</summary>
    public event Action? ContentChanged;

    public ScrollHost()
    {
        _scroll = new ScrollViewer
        {
            // 自带那根藏掉，用旁边自己画的那根
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled
        };

        _track = new Border
        {
            Width = 12,
            CornerRadius = new CornerRadius(6),
            Background = Res("ControlFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _thumb = new Thumb
        {
            Width = 12,
            MinHeight = 28,
            CornerRadius = new CornerRadius(6),
            Background = Res("TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _track.Child = _thumb;

        _barCol = new Border
        {
            Padding = new Thickness(6, 0, 0, 0),
            Child = _track,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(_scroll, 0);
        SetColumn(_barCol, 1);
        Children.Add(_scroll);
        Children.Add(_barCol);

        _thumb.DragDelta += (_, e) =>
        {
            if (_syncing) return;
            _syncing = true;
            var viewport = _scroll.ViewportHeight;
            var trackH = _track.ActualHeight;
            var total = viewport + _scroll.ScrollableHeight;
            var thumbH = ThumbHeight(trackH, viewport, total);
            var travel = Math.Max(0, trackH - thumbH);
            if (travel > 0)
            {
                var ratio = Math.Clamp(_thumb.Margin.Top + e.VerticalChange, 0, travel) / travel;
                _scroll.ChangeView(null, _scroll.ScrollableHeight * ratio, null, true);
            }
            _syncing = false;
        };

        _scroll.ViewChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            SyncBar();
            _syncing = false;
        };
        _scroll.SizeChanged += (_, _) => SyncBar();

        // 打开时也要同步一次：ViewChanged 在首帧不一定会触发，
        // 只靠它的话初始滑块的位置和长度都是错的（实测滑块按 Maximum=0
        // 算出 31px，正确值是 117px，看着就像滚动条坏了）。
        Loaded += (_, _) => SyncBar();
    }

    /// <summary>换掉滚动内容：重新同步几何，并回到顶部。</summary>
    public void SetContent(UIElement content)
    {
        _scroll.Content = content;

        // 新内容的 Viewport/ScrollableHeight 要等一次布局才是真值。
        // 不先跑一次布局就同步，滑块长度是按旧内容算的。
        try { _scroll.UpdateLayout(); } catch { }
        SyncBar();
        ScrollToTop();

        ContentChanged?.Invoke();

        // 首次挂载时 UpdateLayout 可能仍拿不到真实视口（元素还没进可视树），
        // 所以再补一次。
        DispatcherQueue.TryEnqueue(() =>
        {
            try { _scroll.UpdateLayout(); } catch { }
            SyncBar();
        });
    }

    /// <summary>
    /// 滚回顶部。
    ///
    /// ChangeView 在内容刚换、还没测量完时会被**静默忽略**（本项目在预览窗口上
    /// 已经踩过一次）。所以这里先直接请求一次，再在布局之后补一次——
    /// 否则切换模块时新模块会停在上一模块的滚动位置，正文从中间开始显示。
    /// </summary>
    public void ScrollToTop()
    {
        _scroll.ChangeView(null, 0, null, true);

        DispatcherQueue.TryEnqueue(() =>
        {
            try { _scroll.UpdateLayout(); } catch { }
            _scroll.ChangeView(null, 0, null, true);
            SyncBar();
        });
    }

    /// <summary>把视口高度上限设成可用高度减去留白；avail &lt;= 0 表示不限制。</summary>
    public void LimitHeight(double avail, double reserve)
    {
        if (avail > 0) _scroll.MaxHeight = Math.Max(140, avail - reserve);
    }

    /// <summary>供诊断：当前滚动几何。</summary>
    public string Describe()
        => $"offset={_scroll.VerticalOffset:F0} max={_scroll.ScrollableHeight:F0} " +
           $"viewport={_scroll.ViewportHeight:F0} barH={_barCol.ActualHeight:F0} " +
           $"thumbH={_thumb.Height:F0} track={_track.Visibility}";

    /// <summary>供诊断：横向几何。正文没铺满窗口时，这一行能指出是哪一层窄了。</summary>
    public string DescribeWidth()
        => $"self={ActualWidth:F0} viewportW={_scroll.ViewportWidth:F0} " +
           $"extentW={_scroll.ExtentWidth:F0} bar={_barCol.ActualWidth:F0}";

    private static double ThumbHeight(double trackH, double viewport, double total)
        => Math.Max(28, total > 0 ? trackH * (viewport / total) : trackH);

    private void SyncBar()
    {
        var viewport = _scroll.ViewportHeight;
        var scrollable = _scroll.ScrollableHeight;

        // 量 _barCol（容器）而不是 _track：容器始终参与布局、高度恒为行高；
        // 而轨道本身会在不需要滚动时被折叠，折叠后 ActualHeight 恒为 0——
        // 若拿它当判据，就会"因为量到 0 所以折叠，因为折叠所以量到 0"自锁，
        // 滚动条永久不再出现。
        var trackH = _barCol.ActualHeight;

        if (scrollable <= 0.5 || trackH <= 0)
        {
            // 内容不够长就把轨道藏起来，但**保留容器占位**：
            // 连容器一起折叠会让文字宽度在"出现/消失"时跳一下（整段重排）。
            _track.Visibility = Visibility.Collapsed;
            return;
        }
        _track.Visibility = Visibility.Visible;

        var total = viewport + scrollable;
        var thumbH = ThumbHeight(trackH, viewport, total);
        _thumb.Height = thumbH;

        // 位置：滚动比例映射到"轨道高 - 滑块高"这段可移动范围
        var travel = Math.Max(0, trackH - thumbH);
        var ratio = scrollable > 0 ? _scroll.VerticalOffset / scrollable : 0;
        _thumb.Margin = new Thickness(0, travel * ratio, 0, 0);
    }

    private static Brush Res(string key)
    {
        try { return Application.Current?.Resources?[key] as Brush ?? new SolidColorBrush(Microsoft.UI.Colors.Gray); }
        catch { return new SolidColorBrush(Microsoft.UI.Colors.Gray); }
    }
}
