using AnimaCaptioner.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System.Text;
using Windows.Foundation;
using Windows.Storage.Pickers;

namespace AnimaCaptioner;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly AppSettings _cfg;
    private readonly LlmClient _llm;
    private readonly SearchClient _search = new();

    /// <summary>每个大类当前选中的颜色，键是 TagSections 的 key。保存时写回配置。</summary>
    private readonly Dictionary<string, Windows.UI.Color> _colors = new(StringComparer.Ordinal);

    /// <summary>色块按钮，用来在取色后刷新预览。</summary>
    private readonly Dictionary<string, Button> _swatches = new(StringComparer.Ordinal);

    /// <summary>每行右侧的"大类名 + 色号"标签。</summary>
    private readonly Dictionary<string, TextBlock> _labels = new(StringComparer.Ordinal);

    /// <summary>弹出层里那行"当前 #RRGGBB（已保存/未保存）"。</summary>
    private readonly Dictionary<string, TextBlock> _hexLabels = new(StringComparer.Ordinal);

    /// <summary>每个大类的取色器控件，恢复默认时要把它的值也对齐。</summary>
    private readonly Dictionary<string, ColorPicker> _pickers = new(StringComparer.Ordinal);

    public SettingsDialog(AppSettings cfg, LlmClient llm)
    {
        InitializeComponent();
        _cfg = cfg;
        _llm = llm;

        BaseUrlBox.Text = cfg.ApiBaseUrl;
        ModelBox.Text = cfg.Model;
        ApiKeyBox.Password = cfg.ApiKey;
        DisableThinkingBox.IsChecked = cfg.DisableThinking;
        MaxTokensBox.Value = cfg.MaxTokens;
        TempBox.Value = cfg.Temperature;
        TimeoutBox.Value = cfg.TimeoutSeconds;

        DatasetBox.Text = cfg.DatasetDir;
        VocabBox.Text = cfg.VocabDbPath;
        SeedBox.Text = cfg.SeedPath;

        // 顺序有讲究：只把配置**读进**控件，绝不在构造期写回。
        // （早先的写法把 ApplyTo 那段误放进构造函数，于是构造时用复选框的默认值
        //   把 cfg.SearchEnabled 覆盖成了 false —— 保存后搜索就莫名关掉了。）
        SearchEnabledBox.IsChecked = cfg.SearchEnabled;
        SearchUrlBox.Text = cfg.SearchBaseUrl;
        SearchSniBox.Text = cfg.SearchSniHost;
        SearchKeyBox.Password = cfg.SearchApiKey;
        SearchMaxBox.Value = cfg.SearchMaxResults;
        SelectProvider(cfg.SearchProvider);
        UpdateSearchHint();

        SettingsPathText.Text = "配置文件：" + AppSettings.FilePath;

        BuildColorRows();

        if (cfg.ApiKeyUnreadable)
        {
            KeyHintText.Text = "⚠ 已保存的密钥无法解密（多半是换了机器或 Windows 账户），请重新填写。";
            KeyHintText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
        }
    }

    /// <summary>
    /// 按 TagSections.Colorable 生成配色控件：色块按钮 + 名称 + 当前色号。
    /// Colorable 是六个大类 +「自然语言」——散文也要能配色，才能和标签区分开。
    /// 行由代码生成而不是写死在 XAML 里，这样配色项增删时两边不会脱节。
    ///
    /// 取色器放在 Flyout 里，页面上只留一个色块：六个 ColorPicker 平铺会把
    /// 对话框撑到屏幕外（实测底部「保存」按钮都够不到），而这个对话框本身
    /// 已经很长了。
    /// </summary>
    private void BuildColorRows()
    {
        var stroke = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        var tertiary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

        var row = 0;
        // 必须先按配色项数量建好 RowDefinition：Grid 没有行定义时只有隐含的第 0 行，
        // 对子元素 SetRow(1..5) 全部无效，各行会叠在同一格上（截图里就是类名
        // 糊成一团）。这是只有真跑起来看才会发现的坑。
        // 用 Colorable 而不是 All：配色表比大类多一项「自然语言」。
        foreach (var _ in TagSections.Colorable)
            ColorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        foreach (var info in TagSections.Colorable)
        {
            var key = info.Key;
            _colors[key] = TagColors.ColorOf(key, _cfg);

            // ---- 第 0 列：可点的色块，点开才是取色器 ----
            // 用 Button 而不是 Border：Border 不能接收焦点，弹出层关闭时焦点会被
            // 对话框的第一个输入框抢走，ScrollViewer 随即把视口滚回顶部（实测
            // offset 551 -> 28）。Button 可聚焦，收起后焦点能留在配色区。
            // 外观靠 Padding=0 和自定背景还原成色块。
            var swatch = new Button
            {
                Width = 48,
                Height = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = stroke,
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(_colors[key]),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = key
            };
            _swatches[key] = swatch;

            var picker = new ColorPicker
            {
                Color = _colors[key],
                IsAlphaEnabled = false,          // 只存 #RRGGBB，不要透明度
                IsAlphaSliderVisible = false,
                IsAlphaTextInputVisible = false,
                IsHexInputVisible = true,
                IsMoreButtonVisible = false,
                ColorSpectrumShape = ColorSpectrumShape.Box,
                Tag = key
            };
            picker.ColorChanged += Picker_ColorChanged;
            _pickers[key] = picker;

            var hexText = new TextBlock { FontSize = 11, Foreground = tertiary };
            _hexLabels[key] = hexText;

            var resetOne = new Button { Content = "此项恢复默认", Tag = key, Margin = new Thickness(0, 4, 0, 0) };
            resetOne.Click += ResetOneColor_Click;

            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(picker);
            panel.Children.Add(hexText);
            panel.Children.Add(resetOne);

            var flyout = new Flyout { Content = panel };
            FlyoutBase.SetAttachedFlyout(swatch, flyout);
            swatch.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(swatch);

            // 取色器收起后把焦点收回色块本身。
            // 曾经的 bug：点开取色器再点别处，设置页会跳回顶部。根因是弹出层关闭
            // 时焦点无处可去，落回对话框的第一个可聚焦元素（接口地址输入框），而
            // ScrollViewer 默认 BringIntoViewOnFocusChange 会把它滚进视野。
            // 色块过去是 Border（不能接收焦点），所以焦点只能被抢走；换成可聚焦的
            // Button 并显式收回后，视口留在原地。消融实测：旧实现 offset 551 -> 28，
            // 修复后停在 551。
            flyout.Opened += (_, _) =>
            {
                _offsetAtFlyoutOpen = ContentScroll.VerticalOffset;
                Log.Write($"colour flyout opened: {key}  offset={_offsetAtFlyoutOpen:F0}");
            };
            flyout.Closed += (_, _) =>
            {
                var before = ContentScroll.VerticalOffset;
                swatch.Focus(FocusState.Programmatic);
                // 自检：焦点收回不该把视口挪走。真被挪了（比如以后又有人把色块换回
                // 不可聚焦的元素）就立刻告警，不必等到用户抱怨"怎么跳回顶部了"。
                var moved = Math.Abs(ContentScroll.VerticalOffset - _offsetAtFlyoutOpen);
                Log.Write($"colour flyout closed: {key}  offset={before:F0} -> {ContentScroll.VerticalOffset:F0}");
                if (moved > 8)
                    Log.Write($"!! settings viewport jumped {moved:F0} DIP after closing the colour " +
                              "flyout - focus is being stolen again");
            };

            Grid.SetRow(swatch, row);
            Grid.SetColumn(swatch, 0);
            ColorGrid.Children.Add(swatch);

            // ---- 第 1 列：大类名 + 当前色号 ----
            var label = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            _labels[key] = label;
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 1);
            ColorGrid.Children.Add(label);

            UpdateColorUi(key);
            row++;
        }

        // 布局完成后记录六个色块的真实 Y 坐标。六个色块必须有各自不同的 Y，
        // 全部相同就说明 SetRow 没生效（Grid 没有行定义时六行会叠在一起）。
        ColorGrid.Loaded += (_, _) =>
        {
            try
            {
                var rows = _swatches.Select(kv =>
                {
                    var p = kv.Value.TransformToVisual(ColorGrid)
                                   .TransformPoint(new Windows.Foundation.Point(0, 0));
                    return $"{kv.Key}@y={p.Y:F0}";
                });
                Log.Write("colour rows laid out: " + string.Join(" ", rows));
            }
            catch (Exception ex) { Log.Write("colour row probe failed: " + ex.Message); }

            // 完整可视树转储有 400 行，平时太吵，用 AC_DUMP_TREE=1 按需开启；
            // 几何转储和溢出告警很便宜，常驻。
            if (Environment.GetEnvironmentVariable("AC_DUMP_TREE") == "1") DumpVisualTree();
            ProbeScreenRects();
        };
        // 滚动位置与焦点变化：正常使用时每格滚轮、每次点击都会触发，太吵，
        // 用 AC_TRACE=1 按需开启。弹出层的开关日志（下面）常驻，它带着
        // 关键证据——视口在弹出层收起前后有没有被挪走。
        if (Environment.GetEnvironmentVariable("AC_TRACE") == "1")
        {
            ContentScroll.ViewChanged += (_, _) =>
            {
                if (Math.Abs(ContentScroll.VerticalOffset - _lastOffset) < 0.5) return;
                var from = _lastOffset;
                _lastOffset = ContentScroll.VerticalOffset;
                Log.Write($"scroll offset {from:F0} -> {ContentScroll.VerticalOffset:F0}" +
                          "  focus=" + DescribeFocus());
            };

            // 焦点是"跳回顶部"的头号嫌疑：ScrollViewer 默认
            // BringIntoViewOnFocusChange=true，谁拿到焦点就会被滚进视野。
            AddHandler(UIElement.GettingFocusEvent,
                       new TypedEventHandler<UIElement, GettingFocusEventArgs>((_, e) =>
                       {
                           var src = e.NewFocusedElement as FrameworkElement;
                           Log.Write("getting focus -> " + Describe(src) +
                                     "  offset=" + ContentScroll.VerticalOffset.ToString("F0"));
                       }), true);
        }
    }

    private double _lastOffset;

    /// <summary>弹出层打开时的滚动位置，用来检测收起后视口有没有被挪走。</summary>
    private double _offsetAtFlyoutOpen;

    /// <summary>
    /// 把几个关键控件的屏幕矩形写进日志，并在内容比视口宽时告警。
    /// 可视树转储用的是 ScrollViewer 内坐标，"控件被裁掉"在那种坐标下看不出来：
    /// ContentDialog 的内容区只有 496 DIP 宽，RootPanel 曾经写死 560，于是右侧
    /// 64 DIP 被切掉，「测试连接」按钮只剩十几像素——树里一切正常，只有视口
    /// 宽高比能暴露它。所以这条检查常驻，溢出就直接告警。
    /// </summary>
    private void ProbeScreenRects()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("---- key controls, screen rect ----");

            var named = new (string Label, FrameworkElement? El)[]
            {
                ("ContentScroll", ContentScroll),
                ("RootPanel", RootPanel),
                ("TestBtn", TestBtn),
                ("ResetColorsBtn", ResetColorsBtn),
                ("ColorGrid", ColorGrid),
            };

            foreach (var (label, el) in named)
            {
                if (el is null) { sb.AppendLine($"{label}: (null)"); continue; }
                var p = el.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                sb.AppendLine($"{label}: screen=({p.X:F0},{p.Y:F0}) size={el.ActualWidth:F0}x{el.ActualHeight:F0}");
            }

            sb.AppendLine($"viewport={ContentScroll.ViewportWidth:F0}x{ContentScroll.ViewportHeight:F0}" +
                          $" extent={ContentScroll.ExtentWidth:F0}x{ContentScroll.ExtentHeight:F0}");
            Log.Write(sb.ToString().TrimEnd());

            // 横向溢出一定意味着右边的控件被裁了，没有例外
            if (ContentScroll.ExtentWidth > ContentScroll.ViewportWidth + 0.5)
            {
                Log.Write($"!! settings content is {ContentScroll.ExtentWidth - ContentScroll.ViewportWidth:F0} DIP " +
                          "wider than the viewport - the right edge is being clipped");
            }
        }
        catch (Exception ex) { Log.Write("screen rect probe failed: " + ex.Message); }
    }

    /// <summary>当前焦点元素，用于排查滚动跳动。</summary>
    private string DescribeFocus()
    {
        try
        {
            var f = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot);
            return Describe(f as FrameworkElement);
        }
        catch (Exception ex) { return "(focus probe failed: " + ex.Message + ")"; }
    }

    private static string Describe(FrameworkElement? fe) => fe is null
        ? "(none)"
        : fe.GetType().Name +
          (string.IsNullOrEmpty(fe.Name) ? "" : "#" + fe.Name) +
          (fe is ContentControl cc && cc.Content is string s ? " \"" + s + "\"" : "");

    /// <summary>
    /// 把对话框里的可视树按几何位置转储到日志：类型、名字、文本、相对 ContentScroll
    /// 的矩形。用来回答"这个位置到底是什么控件"，而不是靠截图猜。
    /// </summary>
    private void DumpVisualTree()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("---- settings visual tree (rect relative to ContentScroll) ----");
            var count = 0;
            Walk(Content as DependencyObject, sb, ref count, 0);
            sb.AppendLine($"---- {count} elements ----");
            Log.Write(sb.ToString().TrimEnd());
        }
        catch (Exception ex) { Log.Write("visual tree dump failed: " + ex.Message); }
    }

    private void Walk(DependencyObject? node, StringBuilder sb, ref int count, int depth)
    {
        if (node is null || depth > 12 || count > 400) return;

        if (node is FrameworkElement fe)
        {
            count++;
            string rect;
            try
            {
                var p = fe.TransformToVisual(ContentScroll)
                          .TransformPoint(new Windows.Foundation.Point(0, 0));
                rect = $"{p.X:F0},{p.Y:F0} {fe.ActualWidth:F0}x{fe.ActualHeight:F0}" +
                       (fe.Visibility != Visibility.Visible ? " HIDDEN" : "");
            }
            catch { rect = "(no transform)"; }

            var txt = fe switch
            {
                TextBlock tb => tb.Text,
                Button b => b.Content as string,
                CheckBox cb => cb.Content as string,
                TextBox tx => tx.Text,
                NumberBox nb => "value=" + nb.Value,
                ColorPicker cp => cp.Color.ToString(),
                _ => null
            };
            if (!string.IsNullOrEmpty(txt) && txt.Length > 70) txt = txt[..70] + "…";

            var name = string.IsNullOrEmpty(fe.Name) ? "" : "#" + fe.Name;
            sb.AppendLine($"{new string(' ', depth * 2)}{fe.GetType().Name}{name} [{rect}]" +
                          (txt is null ? "" : " \"" + txt + "\""));
        }

        var n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < n; i++)
            Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i), sb, ref count, depth + 1);
    }

    /// <summary>把某个大类的当前颜色刷新到色块、色号和弹出层里的提示上。</summary>
    private void UpdateColorUi(string key)
    {
        if (!_colors.TryGetValue(key, out var c)) return;
        var hex = TagColors.ToHex(c);

        if (_swatches.TryGetValue(key, out var sw))
            sw.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(c);

        if (_labels.TryGetValue(key, out var lb))
            lb.Text = TagSections.Get(key).Title + "    " + hex;

        if (_hexLabels.TryGetValue(key, out var hx))
            hx.Text = "当前 " + hex +
                      (string.Equals(_cfg.GetSectionColor(key), hex, StringComparison.OrdinalIgnoreCase)
                          ? "（已保存）" : "（未保存，点「保存」生效）");
    }

    private void Picker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (sender.Tag is not string key) return;
        _colors[key] = args.NewColor;
        UpdateColorUi(key);
        // 记下颜色变化。取色器是弹出层，截图看不全也点不准，日志才是可靠的取证。
        Log.Write($"colour picked: {key} -> {TagColors.ToHex(args.NewColor)}");
    }

    private void ResetOneColor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        _cfg.ResetSectionColor(key);
        _colors[key] = TagColors.ColorOf(key, _cfg);
        SyncPicker(key);
        UpdateColorUi(key);
        Log.Write("section colour reset (one): " + key);
    }

    /// <summary>把取色器控件的值对齐到当前颜色（恢复默认之后要跟上）。</summary>
    private void SyncPicker(string key)
    {
        if (_pickers.TryGetValue(key, out var p) && _colors.TryGetValue(key, out var c))
            p.Color = c;
    }

    private void ResetColors_Click(object sender, RoutedEventArgs e)
    {
        foreach (var info in TagSections.Colorable)
        {
            // 直接把默认色写回配置，而不是只改界面：这样"恢复默认"立刻生效，
            // 不必等用户再点一次保存。
            _cfg.ResetSectionColor(info.Key);
            _colors[info.Key] = TagColors.ColorOf(info.Key, _cfg);
            SyncPicker(info.Key);
            UpdateColorUi(info.Key);
        }
        try { _cfg.Save(); } catch (Exception ex) { Log.Write("reset colours save failed: " + ex.Message); }
        Log.Write("section colours reset to defaults");
    }

    /// <summary>把对话框里的值写回配置对象。保存动作由调用方负责。</summary>
    public void ApplyTo(AppSettings cfg)
    {
        cfg.ApiBaseUrl = BaseUrlBox.Text.Trim();
        cfg.Model = ModelBox.Text.Trim();
        cfg.ApiKey = ApiKeyBox.Password;
        cfg.DisableThinking = DisableThinkingBox.IsChecked == true;
        cfg.MaxTokens = (int)(double.IsNaN(MaxTokensBox.Value) ? 400 : MaxTokensBox.Value);
        cfg.Temperature = double.IsNaN(TempBox.Value) ? 0.3 : TempBox.Value;
        cfg.TimeoutSeconds = (int)(double.IsNaN(TimeoutBox.Value) ? 300 : TimeoutBox.Value);

        cfg.DatasetDir = DatasetBox.Text.Trim();
        cfg.VocabDbPath = VocabBox.Text.Trim();
        cfg.SeedPath = SeedBox.Text.Trim();

        // 搜索配置。地址留空时按后端填回默认值，避免出现"看着配好了、
        // 实际调不通"的空地址状态。
        cfg.SearchEnabled = SearchEnabledBox.IsChecked == true;
        cfg.SearchProvider = CurrentProvider;
        cfg.SearchBaseUrl = string.IsNullOrWhiteSpace(SearchUrlBox.Text)
            ? (CurrentProvider == "shim" ? "https://192.168.1.4:49977"
               : CurrentProvider == "searxng" ? "http://127.0.0.1:8888" : "")
            : SearchUrlBox.Text.Trim();
        cfg.SearchSniHost = SearchSniBox.Text.Trim();
        cfg.SearchApiKey = SearchKeyBox.Password;
        cfg.SearchMaxResults = (int)(double.IsNaN(SearchMaxBox.Value) ? 5 : SearchMaxBox.Value);

        foreach (var kv in _colors) cfg.SetSectionColor(kv.Key, TagColors.ToHex(kv.Value));
    }

    // ---- 联网搜索 ----

    private string CurrentProvider =>
        (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "shim";

    private void SelectProvider(string key)
    {
        foreach (var o in ProviderBox.Items.OfType<ComboBoxItem>())
            if (string.Equals(o.Tag as string, key, StringComparison.OrdinalIgnoreCase))
            { ProviderBox.SelectedItem = o; return; }

        // 配置里是个不认识的键（手改过 settings.json）时落到第一项，
        // 而不是留空让用户以为搜索坏了。
        ProviderBox.SelectedIndex = 0;
    }

    private void Provider_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSearchHint();

    /// <summary>
    /// 每家后端的地址和 Key 要求都不一样，写一段会随选择变化的说明。
    /// 这些差异是实测出来的（尤其是 SNI/Host 那两条），写在这里比写进文档更容易被看到。
    /// </summary>
    private void UpdateSearchHint()
    {
        if (SearchHintText is null) return;

        var (url, hint) = CurrentProvider switch
        {
            "searxng" => ("http://127.0.0.1:8888",
                "自建 SearXNG 的根地址。要求实例在 settings.yml 的 search.formats 里包含 json，" +
                "否则 JSON 接口返回 403。不需要 API Key。"),
            "tavily" => ("", "留空用官方地址。需要 API Key（走 Authorization 头）。免费额度约 1000 次/月。"),
            "brave" => ("", "留空用官方地址。需要 API Key（X-Subscription-Token）。"),
            "serper" => ("", "留空用官方地址。需要 API Key（X-API-KEY），返回的是 Google 结果。"),
            _ => ("https://192.168.1.4:49977",
                "指向 192.168.1.4 上已部署的搜索网关（Anthropic 兼容 + 自建 SearXNG）。" +
                "SNI 必须填证书域名：直连 IP 时若 SNI 是 IP，Caddy 会回 TLS 错误；" +
                "若 Host 用 IP，则会得到 200 + 空体的静默响应。"),
        };

        SearchUrlBox.PlaceholderText = url;
        SearchHintText.Text = hint;

        // SNI 只对 TLS 网关有意义，其它后端禁用掉以免误导
        var needSni = CurrentProvider == "shim";
        SearchSniBox.IsEnabled = needSni;
        if (!needSni) SearchSniBox.Text = "";
    }

    private async void TestSearch_Click(object sender, RoutedEventArgs e)
    {
        // 用界面上的当前值测，而不是已保存的值——否则改了地址点测试会测到旧地址
        var probe = new AppSettings
        {
            SearchProvider = CurrentProvider,
            SearchBaseUrl = SearchUrlBox.Text.Trim(),
            SearchSniHost = SearchSniBox.Text.Trim(),
            SearchMaxResults = (int)(double.IsNaN(SearchMaxBox.Value) ? 5 : SearchMaxBox.Value),
        };
        probe.SearchApiKey = SearchKeyBox.Password;

        SearchTestBtn.IsEnabled = false;
        SearchTestText.Text = "正在搜索…";
        try
        {
            var (ok, msg) = await _search.TestAsync(probe);
            SearchTestText.Text = (ok ? "✓ " : "✗ ") + msg;
            SearchTestText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                ok ? Microsoft.UI.Colors.MediumSeaGreen : Microsoft.UI.Colors.OrangeRed);
            Log.Write("search test: " + (ok ? "OK " : "FAIL ") + msg);
        }
        catch (Exception ex)
        {
            SearchTestText.Text = "✗ " + ex.Message;
        }
        finally { SearchTestBtn.IsEnabled = true; }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        // 用界面上的当前值测，而不是已保存的值——否则用户改了地址点测试会测到旧地址
        var probe = new AppSettings
        {
            ApiBaseUrl = BaseUrlBox.Text.Trim(),
            Model = ModelBox.Text.Trim(),
            TimeoutSeconds = (int)(double.IsNaN(TimeoutBox.Value) ? 300 : TimeoutBox.Value)
        };
        probe.ApiKey = ApiKeyBox.Password;

        TestBtn.IsEnabled = false;
        TestResultText.Text = "正在连接…";
        try
        {
            var (ok, msg) = await _llm.TestAsync(probe);
            TestResultText.Text = (ok ? "✓ " : "✗ ") + msg;
            TestResultText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                ok ? Microsoft.UI.Colors.MediumSeaGreen : Microsoft.UI.Colors.OrangeRed);
            Log.Write("settings test: " + (ok ? "OK " : "FAIL ") + msg);
        }
        catch (Exception ex)
        {
            TestResultText.Text = "✗ " + ex.Message;
        }
        finally { TestBtn.IsEnabled = true; }
    }

    private async void BrowseDataset_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolder();
        if (path is not null) DatasetBox.Text = path;
    }

    private async void BrowseSeed_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is not null) SeedBox.Text = file.Path;
    }

    private async Task<string?> PickFolder()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
