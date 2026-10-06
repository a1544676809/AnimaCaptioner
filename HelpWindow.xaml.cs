using AnimaCaptioner.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;      // KeyboardAccelerator
using Microsoft.UI.Xaml.Media;

namespace AnimaCaptioner;

/// <summary>
/// 帮助窗口。经典的「左侧目录 + 右侧内容」两栏。
///
/// 正文来自磁盘上的 Markdown（<see cref="HelpDocs"/>），所以改文档不必重新编译，
/// 也不必重启程序——工具栏的「重新载入」会重新读盘。渲染走
/// <see cref="MarkdownRenderer"/>（WinUI 原生控件，不用 WebView）。
///
/// 独立窗口而不是模态对话框：可以一边看帮助一边操作主窗口——帮助里讲的多半
/// 正是"下一步该点哪里"。
///
/// 控件实例来自 XAML 里的 x:Name（InitializeComponent 会接上），不要另外再
/// 声明同名字段，否则 _nav 和 NavList 会是两个不同的对象，点了没反应。
/// </summary>
public sealed partial class HelpWindow : Window
{
    private readonly List<HelpModule> _modules = new();
    private readonly ScrollHost _host = new();
    private readonly StackPanel _contentHost = new();

    private AppSettings? _cfg;
    private string _currentId = "";
    private double _lastLoggedW = -1;

    /// <summary>打开时通知主窗口清引用（否则再点菜单不会开新窗口）。</summary>
    public event Action? Closed2;

    public HelpWindow()
    {
        InitializeComponent();
        Title = "帮助";

        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(ico)) AppWindow.SetIcon(ico);
        }
        catch { }

        // 正文区装进自绘滚动条宿主。ScrollViewer 自带那根是浮层会淡出，
        // 静止时一个像素都不画（见 ScrollHost 的注释）。
        ScrollSlot.Children.Add(_host);

        NavList.ItemsSource = null;

        Closed += (_, _) => Closed2?.Invoke();

        // F5 挂在这个按钮上，而**不是** RootGrid 上。这是实测出来的：改前改后用同一个
        // 脚本、同一个坐标悬停正文，挂在 RootGrid（它铺满整个窗口）上时页面上会凭空
        // 冒出一个小「F5」提示框，挂到按钮上就不冒，而快捷键照样整个窗口有效。
        //
        // 机制没往下挖到底：把 F5 **同时**挂到 RootGrid 和这个按钮上做消融，反而不冒了。
        // 所以"就近归属"比"从悬停处往上找到持有者"更贴切，但这条没验实——写在这里是
        // 免得以后有人图省事把它挪回 RootGrid，又把这个提示放出来。
        AddAccel(ReloadBtn, Windows.System.VirtualKey.F5,
                 Windows.System.VirtualKeyModifiers.None, () => Reload());

        // Esc 不用加速键：它有同样的提示问题，而且语义上不属于任何控件。
        // 用 KeyDown 一样能关窗，还不会被输入法的窗口吃掉。
        RootGrid.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler((_, e) =>
            {
                if (e.Key != Windows.System.VirtualKey.Escape) return;
                e.Handled = true;
                Close();
            }), handledEventsToo: true);

        RootGrid.Loaded += (_, _) => ApplyWindowBounds();

        // 拖大窗口时各层宽度怎么变，逐次记一行（默认关，AC_TRACE=1 打开）。
        // 只在宽度变化超过 4 DIP 时记，免得拖动过程刷屏。
        if (Environment.GetEnvironmentVariable("AC_TRACE") == "1")
        {
            RootGrid.SizeChanged += (_, _) =>
            {
                if (Math.Abs(RootGrid.ActualWidth - _lastLoggedW) < 4) return;
                _lastLoggedW = RootGrid.ActualWidth;
                LogLayout();
            };
        }
    }

    private void AddAccel(UIElement owner, Windows.System.VirtualKey key,
                          Windows.System.VirtualKeyModifiers mods, Action act)
    {
        var a = new KeyboardAccelerator { Key = key, Modifiers = mods };
        a.Invoked += (_, e) => { e.Handled = true; act(); };
        owner.KeyboardAccelerators.Add(a);
    }

    // ================= 打开 =================

    /// <summary>载入文档并定位。wantId 为空则回到上次看的模块。</summary>
    public bool Open(AppSettings cfg, string? wantId = null)
    {
        _cfg = cfg;
        var target = wantId;
        if (string.IsNullOrEmpty(target)) target = cfg.HelpLastModuleId;
        Reload(target);
        return _modules.Count > 0;
    }

    /// <summary>
    /// 重新读盘。保留当前模块（除非指定了别的）。
    ///
    /// 全程 try/catch：帮助页本身挂掉是最糟的结果，因为用户正是来看怎么排错的。
    /// </summary>
    public void Reload(string? wantId = null)
    {
        var keep = wantId ?? _currentId;
        try
        {
            var mods = HelpDocs.Load(out var warnings);

            _modules.Clear();
            _modules.AddRange(mods);

            Log.Write($"help docs: dir={HelpDocs.Dir} modules={_modules.Count} " +
                      $"warnings={warnings.Count}");

            if (_modules.Count == 0)
            {
                ShowEmpty(warnings);
                return;
            }

            NavList.ItemsSource = _modules.Select(m => m.Title).ToList();

            var idx = _modules.FindIndex(m => string.Equals(m.Id, keep, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = 0;
            NavList.SelectedIndex = idx;
            Show(idx);

            // 状态栏：几个模块、有没有问题、怎么改
            var parts = new List<string> { $"{_modules.Count} 个模块" };
            if (warnings.Count > 0) parts.Add($"⚠ {warnings.Count} 处问题");
            parts.Add("改完 docs 里的 .md 点「重新载入」即可生效");
            StatusText.Text = string.Join(" · ", parts);
        }
        catch (Exception ex)
        {
            Log.Write("help reload FAILED: " + ex);
            _modules.Clear();
            NavList.ItemsSource = null;
            NavList.SelectedIndex = -1;
            ShowMessage("帮助载入失败", ex.Message);
        }
    }

    private void ShowEmpty(List<string> warnings)
    {
        NavList.ItemsSource = null;
        NavList.SelectedIndex = -1;

        TitleText.Text = "没有找到帮助文档";
        SummaryText.Visibility = Visibility.Collapsed;

        var body = new StackPanel();
        body.Children.Add(Para("程序在这个目录里没找到 .md 文件："));
        body.Children.Add(Code(HelpDocs.Dir));
        body.Children.Add(Para("docs 目录是随程序分发的。如果它是空的，多半是构建时没被复制到"
                               + "输出目录——检查 AnimaCaptioner.csproj 里那条 "
                               + "docs\\**\\*.md 的 Content 项。"));

        if (warnings.Count > 0)
        {
            body.Children.Add(Para("载入过程中的问题："));
            body.Children.Add(Code(string.Join("\n", warnings)));
        }

        var open = new Button { Content = "打开这个目录" };
        open.Click += (_, _) => OpenDocsFolder();
        body.Children.Add(open);

        _contentHost.Children.Clear();
        _contentHost.Children.Add(body);
        _host.SetContent(_contentHost);
        StatusText.Text = "未找到文档";
    }

    private void ShowMessage(string title, string detail)
    {
        TitleText.Text = title;
        SummaryText.Visibility = Visibility.Collapsed;
        _contentHost.Children.Clear();
        var sp = new StackPanel();
        sp.Children.Add(Para(detail));
        _contentHost.Children.Add(sp);
        _host.SetContent(_contentHost);
    }

    // ================= 左侧目录 =================

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var i = NavList.SelectedIndex;
        if (i >= 0 && i < _modules.Count) Show(i);
    }

    // ================= 右侧内容 =================

    private void Show(int index)
    {
        if (index < 0 || index >= _modules.Count) return;
        var m = _modules[index];
        _currentId = m.Id;

        TitleText.Text = m.Title;
        SummaryText.Text = m.Summary;
        SummaryText.Visibility = string.IsNullOrWhiteSpace(m.Summary)
            ? Visibility.Collapsed : Visibility.Visible;

        var renderer = new MarkdownRenderer(
            m.Dir,
            onNavigate: NavigateToDoc,        // 站内 .md 链接
            onExternal: OpenExternal);        // 外链走系统浏览器

        var body = renderer.Render(m.Body);
        _contentHost.Children.Clear();
        _contentHost.Children.Add(body);

        // 渲染时攒下的问题（图片找不到、未支持的语法…）显示在正文末尾，
        // 而不是静默吞掉——"我写的那段哪去了"是最难查的问题。
        var warns = renderer.Warnings;
        if (warns.Count > 0)
        {
            var box = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            box.Children.Add(Para($"⚠ 这篇文档有 {warns.Count} 处问题（不影响其余内容）：",
                                  muted: true));
            box.Children.Add(Code(string.Join("\n", warns.Distinct())));
            _contentHost.Children.Add(box);
            Log.Write($"help module '{m.Id}' render warnings: {string.Join(" | ", warns.Distinct())}");
        }

        _host.SetContent(_contentHost);   // 内部会同步几何并滚回顶部

        // 记住看到哪一页，下次打开回到这里
        if (_cfg is not null && !string.Equals(_cfg.HelpLastModuleId, m.Id, StringComparison.Ordinal))
        {
            _cfg.HelpLastModuleId = m.Id;
            try { _cfg.Save(); }
            catch (Exception ex) { Log.Write("help save last module failed: " + ex.Message); }
        }

        Title = "帮助 — " + m.Title;

        // 滚动几何落一行日志：切换模块后是否回到顶部、滑块长度对不对，
        // 这类问题看数字比看截图可靠（截图会拿到别的窗口，也会拍到中途状态）。
        // 排队两次：SetContent 内部那次布局之后轨道才有真实高度。
        DispatcherQueue.TryEnqueue(() => DispatcherQueue.TryEnqueue(() =>
        {
            Log.Write($"help module '{m.Id}': {_host.Describe()}");
            LogLayout();
        }));
    }

    /// <summary>
    /// 横向几何落一行日志：正文没铺满窗口时，从这一行就能看出是哪一层窄了——
    /// 是窗口、卡片、滚动宿主，还是右栏容器。
    /// </summary>
    private void LogLayout()
    {
        try
        {
            Log.Write($"help layout: root={RootGrid.ActualWidth:F0} " +
                      $"nav={NavCard.ActualWidth:F0} card={ContentCard.ActualWidth:F0} " +
                      $"area={ContentArea.ActualWidth:F0} slot={ScrollSlot.ActualWidth:F0} " +
                      $"host[{_host.DescribeWidth()}] " +
                      $"body={_contentHost.ActualWidth:F0} bodyWanted={_contentHost.DesiredSize.Width:F0}");
        }
        catch (Exception ex) { Log.Write("help layout log failed: " + ex.Message); }
    }

    /// <summary>站内 .md 链接：切成模块 id 并跳过去。</summary>
    private void NavigateToDoc(string url)
    {
        try
        {
            var file = url.Replace('\\', '/');
            var slash = file.LastIndexOf('/');
            if (slash >= 0) file = file[(slash + 1)..];
            var id = HelpDocs.IdOf(file);

            var i = _modules.FindIndex(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            if (i < 0)
            {
                Log.Write($"help navigate: no module '{id}' (from {url})");
                StatusText.Text = $"没有名为 {id} 的模块 · 共 {_modules.Count} 个";
                return;
            }
            NavList.SelectedIndex = i;
            NavList.ScrollIntoView(_modules[i].Title);
        }
        catch (Exception ex) { Log.Write("help navigate failed: " + ex.Message); }
    }

    private void OpenExternal(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Log.Write("help open link failed: " + ex.Message); }
    }

    // ================= 工具栏 =================

    private void Reload_Click(object sender, RoutedEventArgs e) => Reload();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenDocsFolder();

    private void OpenDocsFolder()
    {
        try
        {
            var dir = HelpDocs.Dir;
            if (!Directory.Exists(dir))
            {
                StatusText.Text = "目录不存在：" + dir;
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = "打开目录失败：" + ex.Message;
            Log.Write("help open folder failed: " + ex.Message);
        }
    }

    // ================= 小工具 =================

    private static TextBlock Para(string t, bool muted = false) => new()
    {
        Text = t,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 8),
        Foreground = muted ? Res("TextFillColorSecondaryBrush") : null
    };

    private static Border Code(string t) => new()
    {
        Margin = new Thickness(0, 2, 0, 10),
        Padding = new Thickness(10, 8, 10, 8),
        CornerRadius = new CornerRadius(4),
        Background = Res("ControlFillColorSecondaryBrush"),
        BorderBrush = Res("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        Child = new TextBlock
        {
            Text = t,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            IsTextSelectionEnabled = true
        }
    };

    private static Brush? Res(string key)
    {
        try { return Application.Current?.Resources?[key] as Brush; }
        catch { return null; }
    }

    // ================= 窗口位置与大小 =================

    private void ApplyWindowBounds()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var wa = area.WorkArea;

            var w = _cfg?.HelpWidth ?? 0;
            var h = _cfg?.HelpHeight ?? 0;

            if (w < 480 || h < 360)
            {
                w = (int)Math.Min(1080, wa.Width * 0.72);
                h = (int)Math.Min(820, wa.Height * 0.82);
            }
            if (w > wa.Width) w = wa.Width;
            if (h > wa.Height) h = wa.Height;

            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
            AppWindow.Move(new Windows.Graphics.PointInt32(
                wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2));

            Log.Write($"help window sized {w}x{h} (workarea {wa.Width}x{wa.Height})");
        }
        catch (Exception ex)
        {
            Log.Write("help bounds failed: " + ex.Message);
        }
    }

    /// <summary>把当前尺寸存回配置，下次打开同一个大小。</summary>
    public void SaveBounds()
    {
        if (_cfg is null) return;
        try
        {
            var s = AppWindow.Size;
            if (s.Width >= 480 && s.Height >= 360)
            {
                _cfg.HelpWidth = s.Width;
                _cfg.HelpHeight = s.Height;
                _cfg.Save();
                Log.Write($"help bounds saved {s.Width}x{s.Height}");
            }
        }
        catch (Exception ex) { Log.Write("help save bounds failed: " + ex.Message); }
    }
}
