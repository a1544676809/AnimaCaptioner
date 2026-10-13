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

    /// <summary>最近一次从服务端取到的模型，键是 id（忽略大小写）。给当前填写项补说明用。</summary>
    private readonly Dictionary<string, LlmClient.ModelInfo> _modelInfos = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>还没取过列表时提示行显示的内容。</summary>
    private const string ModelHintIdle =
        "模型名可以从服务端拉取：点「获取列表」或「测试连接」都会请求 GET /models。" +
        "列表里没有的名字（别名、自定义名）也可以直接键入——有些网关根本不实现这个接口。";

    public SettingsDialog(AppSettings cfg, LlmClient llm)
    {
        InitializeComponent();
        _cfg = cfg;
        _llm = llm;

        // 站点表在**副本**上编辑，点「保存」时由 ApplyTo 整表写回。
        // 这样「取消」是真的取消：直接在真配置上改的话，用户删掉一个站点又点取消，
        // 那个站点的密钥就已经从内存配置里没了——而屏幕上什么都没保存过。
        _draftProfiles = cfg.Profiles.Select(p => p.Clone()).ToList();
        _draftActiveId = cfg.ActiveProfile?.Id ?? "";

        BuildProfileRows();
        SelectProfileRow(_draftActiveId);

        // 上面那句只落在依赖属性上，**界面画不出来**——可编辑 ComboBox 只把「选中项」
        // 可靠地画进模板里的 TextBox，而构造期模板还没应用、Items 也还空着。
        // 于是打开设置看到的是一个空框（或灰着的占位符），像模型名根本没读进来。
        // 模板就绪后再走一次统一同步（见 SyncModelText）。
        Loaded += (_, _) =>
        {
            SyncModelText(ActiveDraft?.Model ?? "");
            // 尺寸与几何自检都要等对话框真正布局完才有意义（构造期控件还没量过尺寸，
            // 读出来全是 0）。推后一拍，量到的才是屏幕上那副样子。
            DispatcherQueue.TryEnqueue(() =>
            {
                // 定尺寸要跑两轮：第一轮之后祖先链上的每一环才量出最终高度，而
                // FitToDialog 的高度判据正是读它们（读 0 会被跳过，第一轮可能一个都读不到）。
                ContentScroll.UpdateLayout();
                FitToDialog();
                ContentScroll.UpdateLayout();
                FitToDialog();
                // 定完尺寸要再跑一次布局：下面那几条探针读的是控件位置，
                // 不重排就会读到改尺寸之前的样子。
                ContentScroll.UpdateLayout();
                ProbeColourRows();
                ProbeScreenRects();
            });

            // 窗口尺寸变化要重算。**只挂 SizeChanged 不够**：根面板一旦被钉成固定尺寸，
            // 窗口变大时对话框跟着不再变尺寸，SizeChanged 就不触发，于是尺寸被闩在缩小
            // 时算出的那个小值上（实测窗口 585→852 之后仍报 534x243）。XamlRoot.Changed
            // 是窗口级别的通知，不依赖对话框自身尺寸是否变化，这才是可靠的那个信号。
            if (XamlRoot is not null) XamlRoot.Changed += (_, _) => FitToDialog();
        };
        // 对话框自身尺寸变化也要跟一次（弹出层夹小我们的 Min 时就靠这条收敛）。
        SizeChanged += (_, _) => FitToDialog();
        SyncKeyHint();
        BuildEffortItems();
        SelectEffort(ActiveDraft?.ReasoningEffort ?? "");
        UpdateEffortHint();
        AgentEnabledBox.IsChecked = cfg.AgentEnabled;
        AgentAttachImageBox.IsChecked = cfg.AgentAttachImage;
        AgentFullAccessBox.IsChecked = cfg.AgentFullAccess;
        SyncAgentBoxes();
        MaxTokensBox.Value = cfg.MaxTokens;
        TempBox.Value = cfg.Temperature;
        TimeoutBox.Value = cfg.TimeoutSeconds;
        ModelListText.Text = ModelHintIdle;

        DatasetBox.Text = cfg.DatasetDir;
        VocabBox.Text = cfg.VocabDbPath;
        SeedBox.Text = cfg.SeedPath;
        VocabProxyBox.Text = cfg.VocabProxy;

        UndoStepsBox.Value = cfg.UndoMaxSteps;

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

        // 词库构建页：输入框沿用当前配置，素材目录默认取词库同级的 sources\。
        VocabOutBox.Text = cfg.VocabDbPath;
        VocabSourceDirBox.Text = cfg.VocabSourceDir is { Length: > 0 } savedSrc
            ? savedSrc
            : DefaultVocabSourceDir(cfg.VocabDbPath);
        BuildVocabSourceRows();
        UpdateVocabMissingText();

        // 先把类别和页面挂上，再建配色行——否则第一页露出来时竞态探针找不到页面。
        MapPages();
        // AC_SETTINGS_PAGE 用来直接落到某一页，供自动化验证（菜单在自动化下不好点）。
        // 取不到就回第一页；SelectPage 自己也兜底。
        SelectPage(Environment.GetEnvironmentVariable("AC_SETTINGS_PAGE") is { Length: > 0 } pg
            ? pg : "model");

        BuildColorRows();
    }

    // ---- 模型站点 ----

    /// <summary>正在编辑的站点表**副本**。点保存时才由 ApplyTo 整表写回真配置。</summary>
    private readonly List<ModelProfile> _draftProfiles = new();

    /// <summary>副本里当前选中的站点 id。</summary>
    private string _draftActiveId = "";

    /// <summary>正在编辑的站点；表永远非空，所以不会返回 null。</summary>
    private ModelProfile ActiveDraft =>
        _draftProfiles.FirstOrDefault(p => p.Id == _draftActiveId) ?? _draftProfiles[0];

    /// <summary>
    /// 把站点表灌进列表。行由代码生成：站点数量随配置变，写死在 XAML 里就得维护两处。
    ///
    /// 用 <see cref="ModelProfile.DisplayName"/> / <see cref="ModelProfile.Subtitle"/>
    /// 两行显示——只写地址的话，同一台网关上的两个模型（比如一个能读图一个不能）
    /// 在列表里长得一模一样，选错一次就是一次 400。
    /// </summary>
    private void BuildProfileRows()
    {
        ProfileList.Items.Clear();
        foreach (var p in _draftProfiles)
        {
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new TextBlock { Text = p.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock
            {
                Text = p.Subtitle,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            });
            var item = new ListViewItem
            {
                Tag = p.Id,
                Content = stack,
            };
            // 自动化名称要显式给：默认会用 Content 的 ToString（那个 StackPanel），
            // 冒烟脚本与读屏都按名字找行。
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, p.DisplayName);
            ProfileList.Items.Add(item);
        }

        // 最后一个站点不许删：删光之后四个视图属性全是空串，界面看起来像配置丢了。
        DeleteProfileBtn.IsEnabled = _draftProfiles.Count > 1;
    }

    private void SelectProfileRow(string id)
    {
        foreach (var item in ProfileList.Items.OfType<ListViewItem>())
            if (string.Equals(item.Tag as string, id, StringComparison.Ordinal))
            { ProfileList.SelectedItem = item; return; }

        if (ProfileList.Items.Count > 0) ProfileList.SelectedIndex = 0;
    }

    /// <summary>
    /// 换站点。**先把编辑器里的值收回副本**：用户改完地址不点别处直接点另一行是很自然的
    /// 操作，此时控件里的值还没写回副本，不收回就丢了。
    /// </summary>
    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((ProfileList.SelectedItem as ListViewItem)?.Tag is not string id) return;
        if (_suspendProfileCapture) return;

        CaptureEditor();
        _draftActiveId = id;
        LoadEditor();
    }

    /// <summary>
    /// 重建列表 / 构造期抑制"把编辑器收回副本"：那时控件里还是空的或上一行的内容。
    ///
    /// 这条不是可选的优化，而是一个**已经踩过的数据丢失点**：构造函数里
    /// `SelectProfileRow` 会同步触发 SelectionChanged，而那一刻 BaseUrlBox / ModelBox
    /// 都还是空串（LoadEditor 还没跑），于是回收动作把地址和模型名**清空**了——
    /// 界面上看不出任何异常，点保存就写进配置。
    /// </summary>
    private bool _suspendProfileCapture;

    /// <summary>
    /// 编辑器是否已经装载过。作为上面那道闸的第二重保险：**没装载过就不许回收**。
    /// 只靠调用方记得设 _suspendProfileCapture 是不够的——漏掉一处就是一次静默清空，
    /// 而调用点在构造、重建列表、增删站点三处。
    /// </summary>
    private bool _editorLoaded;

    /// <summary>装载编辑器期间抑制密钥"被改动"的误判（见 ApiKeyBox_PasswordChanged）。</summary>
    private bool _suppressKeyTouch;

    /// <summary>把编辑器里的值收回当前站点副本。</summary>
    private void CaptureEditor()
    {
        if (!_editorLoaded) return;

        var p = ActiveDraft;
        p.Name = ProfileNameBox.Text.Trim();
        p.BaseUrl = BaseUrlBox.Text.Trim();
        p.Model = ModelBox.Text.Trim();
        p.ReasoningEffort = SelectedEffort;
        p.CustomHeaders = CustomHeadersBox.Text;
        // 密钥只在**用户真的改过**时才写回：PasswordBox 拿到的是明文，
        // 而空框既可能是"没有密钥"也可能是"我们没把已有的填进去"。
        // 这里靠 _keyTouched 区分，避免把已有密钥无声抹掉。
        if (_keyTouched) p.ApiKey = ApiKeyBox.Password;
    }

    /// <summary>用户是否在这份对话框里动过密钥框。没动过就不回写。</summary>
    private bool _keyTouched;

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // 程序自己灌值（LoadEditor 清空）不算用户改动。不挡这一下的话，每次换站点
        // 都会把 _keyTouched 置真，于是空框被当成"用户清空了密钥"，已保存的密钥丢。
        if (_suppressKeyTouch) return;
        _keyTouched = true;
    }

    /// <summary>把当前站点副本灌进编辑器。</summary>
    private void LoadEditor()
    {
        var p = ActiveDraft;

        // 装载期间两道闸都要关：ProfileNameBox.Text = ... 之类会同步触发事件
        // （可编辑 ComboBox 尤其会），而密钥框被程序写空时不该算"用户改过"。
        _editorLoaded = false;
        _suppressKeyTouch = true;
        try
        {
            ProfileNameBox.Text = p.Name;
            BaseUrlBox.Text = p.BaseUrl;
            SyncModelText(p.Model);
            SelectEffort(p.ReasoningEffort);
            CustomHeadersBox.Text = p.CustomHeaders;
            // 密钥**不回填**：PasswordBox 显示明文会让人担心"我的密钥就这么露着"，
            // 而且用户多半只是来改地址的。已在配置里的密钥照常生效（不回写就不动它），
            // 这一点的说明由 SyncKeyHint 写出来。
            ApiKeyBox.Password = "";
        }
        finally
        {
            _suppressKeyTouch = false;
            _keyTouched = false;
            _editorLoaded = true;
        }

        EditorTitleText.Text = "编辑站点：" + p.DisplayName;
        DeleteProfileBtn.IsEnabled = _draftProfiles.Count > 1;

        SyncKeyHint();
        UpdateModelHint(p.Model);
        UpdateEffortHint();
        UpdateCustomHeadersHint();
        TestResultText.Text = "";
    }

    /// <summary>
    /// 密钥框下面那行说明。**必须说清"空框不等于没有密钥"**——因为密钥从不回填，
    /// 用户看到的永远是空框，不说的话会以为密钥丢了、重新粘一遍（无害），
    /// 或者以为它没存上（去别处找问题）。
    /// </summary>
    /// <summary>
    /// 额外请求头下面那行说明。
    ///
    /// 这里**必须把被拒的行报出来**，不能静默丢弃：实测
    /// <c>TryAddWithoutValidation</c> 遇到非法头名会返回 false 而不抛异常，
    /// 头就这么消失了、请求照发、服务端回 401——用户看到的是"我明明填了却没用"，
    /// 没有任何线索。所以校验放在 <see cref="CustomHeaders.Parse"/> 里，由这里显示。
    /// </summary>
    private void UpdateCustomHeadersHint()
    {
        var text = CustomHeadersBox.Text ?? "";
        var r = CustomHeaders.Parse(text);

        if (r.HasRejected)
        {
            CustomHeadersHintText.Text = "✗ 这些行没生效：" + string.Join("；", r.Rejected);
            CustomHeadersHintText.Foreground = BadBrush;
            return;
        }

        if (r.Entries.Count == 0)
        {
            CustomHeadersHintText.Text =
                "留空即可。格式每行一个，如 x-opencode-session: ses_xxxx（冒号或等号都认）。" +
                "Authorization 由上面的 API Key 负责，别填在这里。";
            CustomHeadersHintText.Foreground = HintBrush;
            return;
        }

        CustomHeadersHintText.Text =
            $"✓ 将发送 {r.Entries.Count} 个头：" +
            string.Join("、", r.Entries.Select(e => e.Name));
        CustomHeadersHintText.Foreground = GoodBrush;
    }

    private void CustomHeadersBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 只在用户编辑、且不在装载期间才回收——否则换站点时会把上一站点的头
        // 写进刚选中的那个（和密钥框同一个陷阱）。
        if (_editorLoaded) CaptureEditor();
        UpdateCustomHeadersHint();
    }

    private void SyncKeyHint()
    {
        if (KeyHintText is null) return;
        var p = ActiveDraft;

        if (p.ApiKeyUnreadable)
        {
            KeyHintText.Text = "⚠ 已保存的密钥无法解密（多半是换了机器或 Windows 账户），请重新填写。";
            KeyHintText.Foreground = BadBrush;
            return;
        }

        KeyHintText.Foreground = HintBrush;
        var has = !string.IsNullOrEmpty(p.ApiKeyProtected);
        KeyHintText.Text =
            (has ? "已保存密钥（出于安全不回填，留空即保持不变）。" : "尚未保存密钥。")
            + "留空表示不发送 Authorization 头（本地 llama-server 不需要）。"
            + "密钥用 Windows DPAPI 加密后存放，仅限本机本账户可解密。";
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        CaptureEditor();

        // 新站点默认继承当前这条的地址：同一台网关上加第二个模型是最常见的用法，
        // 让用户少改一处。模型名留空——那正是要区分的东西。
        var cur = ActiveDraft;
        var p = new ModelProfile
        {
            BaseUrl = cur.BaseUrl,
            ReasoningEffort = cur.ReasoningEffort,
        };
        _draftProfiles.Add(p);

        _suspendProfileCapture = true;
        BuildProfileRows();
        _draftActiveId = p.Id;
        SelectProfileRow(p.Id);
        _suspendProfileCapture = false;
        LoadEditor();
        Log.Write("settings: profile added, total=" + _draftProfiles.Count);
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_draftProfiles.Count <= 1) return;

        var p = ActiveDraft;
        _draftProfiles.Remove(p);
        _draftActiveId = _draftProfiles[0].Id;

        _suspendProfileCapture = true;
        BuildProfileRows();
        SelectProfileRow(_draftActiveId);
        _suspendProfileCapture = false;
        LoadEditor();
        Log.Write($"settings: profile '{p.DisplayName}' removed, total={_draftProfiles.Count}");
    }

    /// <summary>供设置页的自检与自动化查询当前站点数。</summary>
    internal int DraftProfileCount => _draftProfiles.Count;

    // ---- 尺寸 ----

    /// <summary>量尺寸期间的重入闸。写 Width/Height 会触发 SizeChanged，而它又调回 FitToDialog。</summary>
    private bool _fitting;

    /// <summary>
    /// 定根面板的尺寸：**两个方向都钉成固定值**，与当前显示的是哪一页无关。
    ///
    /// 高度早先取的是"祖先链里最矮的那一环"，那是错的。内容比宿主矮时，祖先链每一环
    /// 都**贴着内容长**（实测模型页 443、数据路径页 291、配色页 371），于是根面板高度
    /// 跟着页面走——切一次类别整个对话框纵向跳一下。而这一跳没有任何好处：宿主给得起
    /// 多少只由窗口决定，与内容无关。
    ///
    /// **尺寸用 Min=Max 钉，不用 Width/Height。** 设置显式 Width/Height 在这个宿主里
    /// 留不住：实测设完 800x443、下一次探针读回来是 `explicit=NaNxNaN`（而同一处设的
    /// MinWidth 完好），于是尺寸继续由内容决定、页面照旧跳。Min/Max 会保留，把两者设成
    /// 同一个值等价于固定尺寸，而且顺带省掉"内容期望宽小于 Min 时要不要 Stretch"这类问题。
    ///
    /// 边界：Min 不能超过宿主给得起的量，否则右边/下边被裁（滚动器不会报这个）。
    /// 所以先把上限量准（MeasureHostCap），再往下夹。
    /// </summary>
    private void FitToDialog()
    {
        if (_fitting) return;
        if (XamlRoot is null) return;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (SettingsRoot.ActualWidth <= 0) return;

        _fitting = true;
        try
        {
            var cap = MeasureHostCap();

            // 宽度上限 800 是排版选择（再宽一行文字太长），不是宿主限制。
            var w = Math.Clamp(cap.Width, 380, 800);
            var h = Math.Max(cap.Height, 240);

            if (Math.Abs(SettingsRoot.MinWidth - w) > 1 || Math.Abs(SettingsRoot.MaxWidth - w) > 1)
            { SettingsRoot.MinWidth = w; SettingsRoot.MaxWidth = w; }

            if (Math.Abs(SettingsRoot.MinHeight - h) > 1 || Math.Abs(SettingsRoot.MaxHeight - h) > 1)
            { SettingsRoot.MinHeight = h; SettingsRoot.MaxHeight = h; }

            Log.Write($"settings size pinned to {w:F0}x{h:F0} DIP " +
                      $"(host cap {cap.Width:F0}x{cap.Height:F0}, " +
                      $"window {XamlRoot.Size.Width:F0}x{XamlRoot.Size.Height:F0})");
        }
        finally { _fitting = false; }
    }

    /// <summary>
    /// 量出宿主真正给得起的内容尺寸。做法是**故意要一个给不起的尺寸**：把根面板的
    /// Min/Max 全部放开、显式尺寸设到 4000，弹出层与对话框就会把整条祖先链夹在各自
    /// 的上限上，此时链上最小的一环就是答案（正常状态下它们贴着内容长，读不到上界）。
    ///
    /// **必须连 MaxWidth 一起放开**：MaxWidth 是"排版上限 800"，留着它连祖先链都会被
    /// 内容带着只长到 800 出头，量不出窗口真正给得起的宽度。
    ///
    /// 两道兜底：结果再按窗口尺寸夹一次（万一某层没同步夹住，读到 4000 会把内容裁掉，
    /// 这个错误的代价太大，不能只靠"应该会夹"）；拿不到任何一环时退回保守常数。
    /// </summary>
    private (double Width, double Height) MeasureHostCap()
    {
        var sMinW = SettingsRoot.MinWidth; var sMaxW = SettingsRoot.MaxWidth;
        var sMinH = SettingsRoot.MinHeight; var sMaxH = SettingsRoot.MaxHeight;
        var sW = SettingsRoot.Width; var sH = SettingsRoot.Height;

        SettingsRoot.MinWidth = 0; SettingsRoot.MaxWidth = double.PositiveInfinity;
        SettingsRoot.MinHeight = 0; SettingsRoot.MaxHeight = double.PositiveInfinity;
        SettingsRoot.Width = 4000; SettingsRoot.Height = 4000;
        ContentScroll.UpdateLayout();
        // 布局在本帧内可能还没落到底（第一轮祖先链上有环节仍是 0），多推一次。
        ContentScroll.UpdateLayout();

        var w = double.PositiveInfinity;
        var h = double.PositiveInfinity;
        for (var p = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(SettingsRoot) as FrameworkElement;
             p is not null;
             p = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(p) as FrameworkElement)
        {
            // 尚未布局的环节 ActualWidth/Height 是 0，读到 0 会把上限算成 0。
            if (p.ActualWidth <= 0 || p.ActualHeight <= 0) continue;
            if (p.ActualWidth < w) w = p.ActualWidth;
            if (p.ActualHeight < h) h = p.ActualHeight;
        }

        SettingsRoot.MinWidth = sMinW; SettingsRoot.MaxWidth = sMaxW;
        SettingsRoot.MinHeight = sMinH; SettingsRoot.MaxHeight = sMaxH;
        SettingsRoot.Width = sW; SettingsRoot.Height = sH;
        ContentScroll.UpdateLayout();

        if (double.IsInfinity(w)) w = 800;
        if (double.IsInfinity(h)) h = 420;

        w = Math.Min(w, Math.Max(XamlRoot!.Size.Width - 20, 380));
        h = Math.Min(h, Math.Max(XamlRoot.Size.Height - 40, 240));
        return (w, h);
    }

    // ---- 左栏类别导航 ----
    //
    // 七个页面全都常驻在 PageHost 里，切换只翻 Visibility，**不重建**。这一点是
    // 刻意的：`ApplyTo` 读的是一批具名控件，只要控件还在树上，切来切去就不会丢值，
    // 也不需要为"当前在该页"补任何保存/恢复逻辑。代价是七页叠在同一个 Grid 里。

    /// <summary>类别键 → 右栏那一页。键同时写在 XAML 里 ListViewItem 的 Tag 上。</summary>
    private readonly Dictionary<string, FrameworkElement> _pages = new(StringComparer.Ordinal);

    /// <summary>离开每一页时的滚动位置，回来时恢复。切类别不该把我滚回页首。</summary>
    private readonly Dictionary<string, double> _pageOffsets = new(StringComparer.Ordinal);

    private string? _currentPage;

    /// <summary>
    /// 把 XAML 里的类别项和右栏页面配起来，并自检两边一致。
    ///
    /// 自检不是装饰：漏挂一页的后果是那一整页设置在界面上**永远打不开**——控件还在、
    /// 值照样会被保存，但用户看不见也改不了。这属于不会崩、只会静默少东西的那类错，
    /// 所以两边任何一个对不上都写进日志。
    /// </summary>
    private void MapPages()
    {
        _pages["model"] = PageModel;
        _pages["effort"] = PageEffort;
        _pages["agent"] = PageAgent;
        _pages["search"] = PageSearch;
        _pages["paths"] = PagePaths;
        _pages["vocab"] = PageVocab;
        _pages["edit"] = PageEdit;
        _pages["colors"] = PageColors;

        foreach (var item in PageList.Items.OfType<ListViewItem>())
        {
            var key = item.Tag as string;
            if (key is null || !_pages.ContainsKey(key))
                Log.Write("!! settings category has no page: " + (key ?? "(no tag)"));
        }

        foreach (var child in PageHost.Children.OfType<FrameworkElement>())
            if (!_pages.ContainsValue(child))
                Log.Write("!! settings page is not attached to any category: " + child.Name);
    }

    private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((PageList.SelectedItem as ListViewItem)?.Tag is string key) ShowPage(key);
    }

    /// <summary>选左栏的某一项（会连带触发 PageList_SelectionChanged）。</summary>
    private void SelectPage(string key)
    {
        foreach (var item in PageList.Items.OfType<ListViewItem>())
            if (string.Equals(item.Tag as string, key, StringComparison.Ordinal))
            { PageList.SelectedItem = item; return; }
        PageList.SelectedIndex = 0;
    }

    private void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var panel)) return;

        if (_currentPage is not null) _pageOffsets[_currentPage] = ContentScroll.VerticalOffset;

        foreach (var kv in _pages) kv.Value.Visibility = kv.Key == key ? Visibility.Visible : Visibility.Collapsed;
        _currentPage = key;

        // 自检：任何时刻有且只有一页可见。两页同时可见在 StackPanel 里会**堆叠**
        // （不像 Grid 那样重叠），眼睛看得见；一页都不显示则是整片空白。
        var visible = _pages.Count(kv => kv.Value.Visibility == Visibility.Visible);
        if (visible != 1)
            Log.Write($"!! settings is showing {visible} pages at once (expected exactly 1)");

        Log.Write($"settings page -> {key}");

        // 还原滚动位置必须等这一页重新布局完：折叠的面板宽度是 0，紧接着调
        // ChangeView 会被夹到 0（extent 还没长出来）。推后一拍才是最终状态。
        var want = _pageOffsets.TryGetValue(key, out var off) ? off : 0;
        DispatcherQueue.TryEnqueue(() =>
        {
            // 显式跑一次布局再读几何。只靠"推后一拍"是不够的：实测回调与换页日志
            // 只差 1 毫秒，那时新页还没量过尺寸——探针会读到**上一页**的 extent，
            // 于是"这一页到底有没有被裁"这个问题的答案完全不可信。
            ContentScroll.UpdateLayout();
            ContentScroll.ChangeView(null, want, null, true);

            // 几何自检也跟着换页走：被折叠的页宽度是 0，"右边被裁掉"只有在该页
            // 真正露出来时才测得出来。AC_DUMP_TREE=1 时才转储整棵树（400 行）。
            if (Environment.GetEnvironmentVariable("AC_DUMP_TREE") == "1") DumpVisualTree();
            if (key == "colors") ProbeColourRows();
            ProbeScreenRects();
        });
    }

    /// <summary>
    /// 按 TagSections.Colorable 生成配色控件：色块按钮 + 名称 + 当前色号。
    /// Colorable 是九个大类 +「自然语言」——散文也要能配色，才能和标签区分开。
    /// 行由代码生成而不是写死在 XAML 里，这样配色项增删时两边不会脱节。
    ///
    /// 取色器放在 Flyout 里，页面上只留一个色块：十个 ColorPicker 平铺会把
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

        // 配色行的几何自检。**不能只挂在 Loaded 上**：这一页默认是折叠的，而折叠时
        // 宽高都是 0——探针会把每一行都读成 y=0，看起来正好像"SetRow 又失效了"。
        // 所以 ShowPage 切到配色页之后会再跑一次（那时布局已经落定）。
        ColorGrid.Loaded += (_, _) => ProbeColourRows();
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
    /// 内容区宽度由 ContentDialogMaxWidth 减去内边距定死，SettingsRoot 写宽了就会
    /// 把右边缘切掉——历史事故是 RootPanel 写死 560 DIP 而内容区只有 496，
    /// 「测试连接」按钮只剩十几像素，树里却一切正常。只有视口宽高比能暴露它，
    /// 所以这条检查常驻，**每次换页都跑**：被折起来的那几页宽高是 0，只有该页
    /// 真正露出来时才测得准。
    /// </summary>
    private void ProbeScreenRects()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"---- key controls, screen rect (page={_currentPage ?? "(none)"}) ----");

            var named = new List<(string Label, FrameworkElement? El)>
            {
                ("ContentScroll", ContentScroll),
                ("SettingsRoot", SettingsRoot),
                ("PageHost", PageHost),
                ("PageList", PageList),
            };
            if (_currentPage is not null && _pages.TryGetValue(_currentPage, out var cur))
                named.Add(("page:" + _currentPage, cur));
            // 两处历史事故的现场：被裁掉的按钮、需要各自不同 Y 的配色表
            if (_currentPage == "model") named.Add(("TestBtn", TestBtn));
            if (_currentPage == "colors")
            {
                named.Add(("ColorGrid", ColorGrid));
                named.Add(("ResetColorsBtn", ResetColorsBtn));
            }
            // 词库构建页最长（素材清单 11 行 + 两个输入框 + 进度 + 日志），是这一页
            // 最可能把「开始构建」顶出视野的地方，所以把操作控件也量一遍。
            if (_currentPage == "vocab")
            {
                named.Add(("VocabSourceList", VocabSourceList));
                named.Add(("VocabSourceDirBox", VocabSourceDirBox));
                named.Add(("VocabOutBox", VocabOutBox));
                named.Add(("BuildVocabBtn", BuildVocabBtn));
                named.Add(("VocabStatusText", VocabStatusText));
            }

            foreach (var (label, el) in named)
            {
                if (el is null) { sb.AppendLine($"{label}: (null)"); continue; }
                var p = el.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                sb.AppendLine($"{label}: screen=({p.X:F0},{p.Y:F0}) size={el.ActualWidth:F0}x{el.ActualHeight:F0}");
            }

            sb.AppendLine($"viewport={ContentScroll.ViewportWidth:F0}x{ContentScroll.ViewportHeight:F0}" +
                          $" extent={ContentScroll.ExtentWidth:F0}x{ContentScroll.ExtentHeight:F0}" +
                          $" root={SettingsRoot.ActualWidth:F0}x{SettingsRoot.ActualHeight:F0}" +
                          $" explicit={SettingsRoot.Width:F0}x{SettingsRoot.Height:F0}" +
                          $" min={SettingsRoot.MinWidth:F0}x{SettingsRoot.MinHeight:F0}" +
                          $" max={SettingsRoot.MaxWidth:F0}x{SettingsRoot.MaxHeight:F0}");

            // 祖先链：回答"内容区到底有多少空间、是谁给的"。这条链上任意一环比根面板矮，
            // 根面板就在溢出（超出的部分既不滚动也不报错，直接看不见），所以它既是诊断
            // 也是哨兵的判据来源。ElementName 里的括号是 ContentPresenter 之类的内部类。
            for (var p = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(SettingsRoot) as FrameworkElement;
                 p is not null;
                 p = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(p) as FrameworkElement)
                sb.AppendLine($"  host {p.GetType().Name}{Describe(p)} = {p.ActualWidth:F0}x{p.ActualHeight:F0}");

            Log.Write(sb.ToString().TrimEnd());

            // 横向溢出一定意味着右边的控件被裁了，没有例外
            if (ContentScroll.ExtentWidth > ContentScroll.ViewportWidth + 0.5)
            {
                Log.Write($"!! settings content is {ContentScroll.ExtentWidth - ContentScroll.ViewportWidth:F0} DIP " +
                          "wider than the viewport - the right edge is being clipped");
            }

            // "被裁到脚/被切掉右边"的哨兵。这条比上面的横向检查更要紧，因为**滚动器
            // 不会报它**：根面板写死 520 DIP 而对话框只给得起约 415 时，ScrollViewer 的
            // extent/viewport 都读 520（它以为正好装得下，于是连滚动条都不出现），
            // 超出的部分被对话框静默裁掉——实测「回灌条数」下半截和「测试搜索」按钮
            // 就是这样点不到的。判据只能是"根面板比任何一个祖先给的还大"。
            for (var p = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(SettingsRoot) as FrameworkElement;
                 p is not null;
                 p = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(p) as FrameworkElement)
            {
                if (p.ActualWidth <= 0 || p.ActualHeight <= 0) continue;

                if (p.ActualHeight + 0.5 < SettingsRoot.ActualHeight)
                {
                    Log.Write($"!! settings root ({SettingsRoot.ActualHeight:F0} DIP) is taller than " +
                              $"{p.GetType().Name} gives it ({p.ActualHeight:F0} DIP) - the bottom is being clipped");
                    break;
                }
                if (p.ActualWidth + 0.5 < SettingsRoot.ActualWidth)
                {
                    Log.Write($"!! settings root ({SettingsRoot.ActualWidth:F0} DIP) is wider than " +
                              $"{p.GetType().Name} gives it ({p.ActualWidth:F0} DIP) - the right edge is being clipped");
                    break;
                }
            }
        }
        catch (Exception ex) { Log.Write("screen rect probe failed: " + ex.Message); }
    }

    /// <summary>
    /// 配色表的几何自检：十个色块必须有各自不同的 Y，全部相同就说明 SetRow 没生效
    /// （Grid 没有行定义时十行会叠在同一格上，截图里就是类名糊成一团）。
    /// 只在配色页**真正可见**时才跑——面板折叠时宽高为 0，转储出来每行都是 y=0，
    /// 那看起来正好像那个 bug 又回来了。
    /// </summary>
    private void ProbeColourRows()
    {
        if (ColorGrid.ActualWidth <= 0) return;
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
            // 用 ColorOf 而不是 Get：Get 只认九个真大类（散文不算大类，传进去会落到
            // general），于是第十行会顶着「一般标签」的名字显示自然语言的颜色——
            // 实测就是这样，一行里名字和色号说的不是同一件事。
            lb.Text = TagSections.ColorOf(key).Title + "    " + hex;

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

    // ---- AI 助手 ----

    /// <summary>面板关着时"是否发图"没有意义，灰掉而不是隐藏，免得用户以为丢了一项。</summary>
    private void AgentEnabledBox_Click(object sender, RoutedEventArgs e) => SyncAgentBoxes();

    private void SyncAgentBoxes()
    {
        if (AgentAttachImageBox is null || AgentEnabledBox is null) return;
        AgentAttachImageBox.IsEnabled = AgentEnabledBox.IsChecked == true;
        // 档位提示里那句"带工具那几轮不受影响"只在助手开着时才该出现
        UpdateEffortHint();
    }

    /// <summary>把对话框里的值写回配置对象。保存动作由调用方负责。</summary>
    public void ApplyTo(AppSettings cfg)
    {
        // 站点：先把编辑器里最后的值收回副本，再整表替换。
        // **用 setter 整体替换，不要 Clear() + AddRange()**：Profiles 的 getter 有个
        // "表空就补一个默认站点"的兜底（保证四个视图属性永远有落脚点），于是
        // Clear() 之后它已经塞回一个默认站点，接着 AddRange 就会多出一条
        // 用户从没建过的站点——还带着默认地址，看起来像配置被改乱了。
        CaptureEditor();
        cfg.Profiles = _draftProfiles.Select(p => p.Clone()).ToList();
        cfg.ActiveProfileId = _draftActiveId;

        cfg.AgentEnabled = AgentEnabledBox.IsChecked == true;
        cfg.AgentAttachImage = AgentAttachImageBox.IsChecked == true;
        cfg.AgentFullAccess = AgentFullAccessBox.IsChecked == true;
        cfg.MaxTokens = (int)(double.IsNaN(MaxTokensBox.Value) ? 400 : MaxTokensBox.Value);
        cfg.Temperature = double.IsNaN(TempBox.Value) ? 0.3 : TempBox.Value;
        cfg.TimeoutSeconds = (int)(double.IsNaN(TimeoutBox.Value) ? 300 : TimeoutBox.Value);

        cfg.DatasetDir = DatasetBox.Text.Trim();
        cfg.VocabDbPath = VocabBox.Text.Trim();
        cfg.SeedPath = SeedBox.Text.Trim();

        // 词库构建页那几项一并存：素材目录和代理下次打开要还在，否则用户
        // 每次都得重填这两项才能继续构建（素材目录还是构建的前提）。
        cfg.VocabSourceDir = VocabSourceDirBox.Text.Trim();
        cfg.VocabProxy = VocabProxyBox.Text.Trim();

        // 步数越界由 PromptHistory.MaxSteps 收敛，这里只管把 NaN 兜住
        cfg.UndoMaxSteps = (int)(double.IsNaN(UndoStepsBox.Value)
            ? PromptHistory.DefaultMaxSteps : UndoStepsBox.Value);

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

    // ---- 推理档位 ----

    /// <summary>
    /// 档位下拉框的条目：值 + 显示名。**值是
    /// <see cref="LlmClient.EffortVocabulary"/> 那八个加一个"自动"**（那是服务端 422
    /// 时自己列出的全集，硬约束就是它）；显示名给中文，因为 minimal / xhigh / ultra
    /// 这几个词光看英文很难排出强弱。
    ///
    /// 表本身复用 <see cref="MainWindow.EffortChoices"/>：助手面板里那个模型选择器
    /// 也要列同一批档位，两处各写一份的话改一处漏一处，同一个档位会在两个界面上
    /// 叫不同名字。
    /// </summary>
    private static (string Value, string Label)[] EffortItems => MainWindow.EffortChoices;

    /// <summary>当前选中的档位值（空串 = 自动）。控件还没建好时回落到当前站点。</summary>
    private string SelectedEffort =>
        (ReasoningBox?.SelectedItem as ComboBoxItem)?.Tag as string
        ?? (_draftProfiles.Count > 0 ? ActiveDraft.ReasoningEffort : "");

    private void BuildEffortItems()
    {
        ReasoningBox.Items.Clear();
        foreach (var (value, label) in EffortItems)
            ReasoningBox.Items.Add(new ComboBoxItem { Content = label, Tag = value });
    }

    /// <summary>
    /// 把配置里的档位落到下拉框。配置里是不认识的值时（手改过 settings.json、
    /// 或服务端换了词表）落到「自动」——**不能落到 none**：那会静默把思考关掉，
    /// 而用户原本想要的可能是"多想想"。
    /// </summary>
    private void SelectEffort(string value)
    {
        var want = value ?? "";
        foreach (var o in ReasoningBox.Items.OfType<ComboBoxItem>())
            if (string.Equals(o.Tag as string, want, StringComparison.OrdinalIgnoreCase))
            { ReasoningBox.SelectedItem = o; return; }
        ReasoningBox.SelectedIndex = 0;
    }

    private void Reasoning_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateEffortHint();

    /// <summary>
    /// 说明当前档位到底会不会下发。**这不是装饰**：档位能不能生效取决于服务端，
    /// 而"选了一个不会生效的档位"是看不出来的——提示行就是不让它静默。
    ///
    /// 判据只有一条：服务端在 `/models` 里报过 `effort` 才下发（见
    /// LlmClient.EffortToSend，那里写了为什么没报就不发）。
    /// </summary>
    private void UpdateEffortHint()
    {
        if (EffortHintText is null) return;

        var chosen = SelectedEffort;
        var model = ModelBox?.Text.Trim() ?? "";
        LlmClient.ModelInfo? info = null;
        var known = model.Length > 0 && _modelInfos.TryGetValue(model, out info);

        // 先假定提示是信息性的；下面遇到"选了但不生效"再换成警示色。
        EffortHintText.Foreground = HintBrush;
        var sb = new StringBuilder();

        if (!known)
        {
            sb.Append("还没取到服务端能力：点「获取列表」或「测试连接」之后，这里会说明这一档会不会下发。");
            if (LlmClient.IsThinkingOff(chosen))
                sb.Append("「关闭思考」不受影响——本地 llama-server 靠 enable_thinking=false，始终有效。");
        }
        else if (!info!.SupportsEffort)
        {
            sb.Append("该服务端没有声明 reasoning_effort（llama-server 就是这种），所以档位不会下发。");
            if (LlmClient.IsThinkingOff(chosen))
            {
                sb.Append("「关闭思考」仍然有效——它会下发 enable_thinking=false。");
            }
            else if (chosen.Length == 0)
            {
                sb.Append("「自动」本来就是不下发，行为与从前一致。");
            }
            else
            {
                EffortHintText.Foreground = BadBrush;
                sb.Append("在这个服务端上，这一档等于「自动」——只有「关闭思考」才能真正改变行为。");
            }
        }
        else
        {
            var declared = info.EffortLevels ?? new List<string>();
            if (chosen.Length == 0)
                sb.Append($"不下发档位，由服务端自己决定（该模型默认 {info.DefaultEffort}）。");
            else if (LlmClient.IsThinkingOff(chosen))
                sb.Append("下发 reasoning_effort=none。实测云端真能关掉思考——同一条提问，"
                          + "enable_thinking=false 会给回 69 字符思考，加上 none 才是 0。");
            else if (declared.Contains(chosen, StringComparer.OrdinalIgnoreCase))
                sb.Append($"下发 reasoning_effort={chosen}。");
            else
                sb.Append($"下发 reasoning_effort={chosen}——但该模型在 /models 里只声明了 "
                          + $"{string.Join("/", declared)}。没声明的档位实测服务端照样接受（HTTP 200），"
                          + "只是未必比声明过的那几档更有区分度。");
        }

        // 非 none 档位一律会思考，此时程序会替思考留出 token 预算——说一句，
        // 免得用户以为 max_tokens 设小了是自己算错。
        if (!LlmClient.IsThinkingOff(chosen) && chosen.Length > 0)
            sb.Append(" 会思考，程序因此会另外留一份 token 预算给思考过程。");

        // 工具那一路刻意不跟档位走（关了思考模型就不发 tool_calls），这条差异
        // 在 user 把档位设成 none 时最容易误解。
        if (LlmClient.IsThinkingOff(chosen) && AgentEnabledBox?.IsChecked == true)
            sb.Append(" 例外：AI 助手里带工具的那几轮始终保留思考，不受这里影响。");

        EffortHintText.Text = sb.ToString();
    }

    // ---- 模型列表 ----

    /// <summary>提示行的两种配色。都只在需要时构造，没有缓存价值。</summary>
    private static Microsoft.UI.Xaml.Media.Brush HintBrush =>
        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

    private static Microsoft.UI.Xaml.Media.Brush BadBrush =>
        new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed);

    private static Microsoft.UI.Xaml.Media.Brush GoodBrush =>
        new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen);

    /// <summary>进行中/中性提示。不用绿或橙——那两种在这个对话框里已经表示"成功/失败"。</summary>
    private static Microsoft.UI.Xaml.Media.Brush InfoBrush =>
        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        var (ok, models, err) = await FetchModelsAsync();
        Log.Write(ok
            ? $"settings fetch models: {models.Count} 个"
            : "settings fetch models FAILED: " + err);
    }

    private void Model_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModelHint(ModelBox.Text.Trim());
        // 档位能不能下发取决于**当前模型**所属服务端报了什么，所以换模型就要重算
        UpdateEffortHint();
    }

    /// <summary>
    /// 拉一次 /models，结果同时喂给下拉框和提示行。
    /// 「获取列表」和「测试连接」共用它——同一个请求没必要发两次。
    /// </summary>
    private async Task<(bool Ok, List<LlmClient.ModelInfo> Models, string Error)> FetchModelsAsync()
    {
        // 用编辑器上的当前值去取，而不是已保存的值：改了地址还没保存就点获取，
        // 想看的当然是新地址有什么。
        // 密钥要用**编辑器里的值**：用户可能刚粘了一把新 key 还没保存就想先试试。
        // 没动过密钥框时回落到副本里已保存的那把（否则这次请求会不带密钥）。
        var probe = new AppSettings
        {
            ApiBaseUrl = BaseUrlBox.Text.Trim(),
            TimeoutSeconds = (int)(double.IsNaN(TimeoutBox.Value) ? 300 : TimeoutBox.Value),
        };
        probe.ApiKey = _keyTouched ? ApiKeyBox.Password : ActiveDraft.ApiKey;
        // 自定义头**必须一起带上**：它来自编辑器而不是那个临时实例，忘了搬的话
        // 「测试连接」会在没带头的状态下打过去。对需要专属头的网关（如 OpenCode 的
        // Go 档缺 x-opencode-session）结果是 400/401，看着像"地址或密钥填错了"，
        // 而保存之后又能用——这种"按钮说不行、实际可以"的错最难查。
        probe.ActiveProfile!.CustomHeaders = CustomHeadersBox.Text;

        FetchModelsBtn.IsEnabled = false;
        TestBtn.IsEnabled = false;
        ModelListText.Foreground = HintBrush;
        ModelListText.Text = "正在获取模型列表…";
        try
        {
            var (ok, models, err) = await _llm.ListModelsAsync(probe);
            if (ok)
            {
                PopulateModels(models);
            }
            else
            {
                ModelListText.Foreground = BadBrush;
                ModelListText.Text = "✗ " + err;
            }
            return (ok, models, err);
        }
        catch (Exception ex)
        {
            ModelListText.Foreground = BadBrush;
            ModelListText.Text = "✗ " + ex.Message;
            return (false, new(), ex.Message);
        }
        finally
        {
            FetchModelsBtn.IsEnabled = true;
            TestBtn.IsEnabled = true;
        }
    }

    /// <summary>把取到的模型灌进下拉框。</summary>
    private void PopulateModels(List<LlmClient.ModelInfo> models)
    {
        _modelInfos.Clear();
        foreach (var m in models) _modelInfos[m.Id] = m;

        // 踩过的坑：清空 Items 会把可编辑 ComboBox 的 Text 一并清掉，
        // 于是用户刚填好的模型名凭空消失。先存后恢复。
        var keep = ModelBox.Text;
        ModelBox.Items.Clear();
        foreach (var m in models) ModelBox.Items.Add(m.Id);

        // 重建 Items 会把模板里的 TextBox 重置掉，所以这里走同一套同步（见 SyncModelText）。
        SyncModelText(keep);

        // 再晚一拍补一次：重建 Items 之后 ComboBox 会**异步**把选中项落到列表第一项上，
        // 那一刻界面与下面那行提示都会短暂变成"列表里的第一个模型"（实测保存的值不受影响，
        // 但看起来像你的模型被换掉了）。晚一拍的这次同步才是最终状态。
        DispatcherQueue.TryEnqueue(() =>
        {
            SyncModelText(keep);
            UpdateModelHint(ModelBox.Text.Trim());
            UpdateEffortHint();
        });

        // 只有"服务端恰好报了一个模型"且"用户还没填名字"时才代填。
        // 多个模型时代选第一个纯属乱猜。
        if (string.IsNullOrWhiteSpace(keep) && models.Count == 1)
        {
            SyncModelText(models[0].Id);
            Log.Write("model list: 服务端只报了 " + models[0].Id + "，已自动填入模型名");
        }

        UpdateModelHint(ModelBox.Text.Trim());
        UpdateEffortHint();
    }

    /// <summary>
    /// 把模型名落到「模型名」那个可编辑 ComboBox 上——**属性与界面都要落到**。
    ///
    /// 属性那一半是给保存用的（`ApplyTo` 读 `ModelBox.Text`），它一直是好的；
    /// 难的是**界面那一半**：`ComboBox.Text` 只是依赖属性，界面上真正画的是模板里的
    /// `PART_EditableTextBox`，而**只有「选中项」会被可靠地画出来**。
    /// 试过两条不行的路，别再走：构造后（`Loaded`）再设一次 `Text`；以及走可视树
    /// 直接写那个模板 TextBox（`TryEnqueue` 推后一拍写也一样）——后者会被 ComboBox
    /// 按"当前没有选中项"再刷回空。
    ///
    /// 所以这里的做法是：值在列表里就选中它；不在列表里（别名 / 自定义名 / 网关不实现
    /// `/models`）就**把它自己补进列表**再选中。补进去只影响这个下拉框的候选，
    /// 下面那行提示仍按服务端真正报的列表说话，所以"不在列表里"那句警告不会失效。
    ///
    /// 时机也重要：`PopulateModels` 重建 Items 之后 ComboBox 会**异步**把选中项落到
    /// 列表第一项上，所以那边晚一拍还要再调一次，否则界面和提示会显示成第一个模型名
    /// （实测保存的值不受影响，纯粹是画错了）。
    /// </summary>
    private void SyncModelText(string value)
    {
        ModelBox.Text = value;
        if (string.IsNullOrWhiteSpace(value)) return;

        var idx = ModelBox.Items.IndexOf(value);
        if (idx < 0)
        {
            ModelBox.Items.Insert(0, value);
            idx = 0;
        }
        ModelBox.SelectedIndex = idx;
    }

    /// <summary>把"服务端给了什么"和"当前填的是什么"写进提示行。</summary>
    private void UpdateModelHint(string current)
    {
        if (ModelListText is null) return;

        if (_modelInfos.Count == 0)
        {
            ModelListText.Foreground = HintBrush;
            ModelListText.Text = ModelHintIdle;
            return;
        }

        var sb = new StringBuilder();
        sb.Append($"服务端列出 {_modelInfos.Count} 个模型。");

        if (!string.IsNullOrWhiteSpace(current) && _modelInfos.TryGetValue(current, out var info))
        {
            ModelListText.Foreground = HintBrush;
            sb.Append($"当前「{info.Id}」");
            var detail = info.Describe();
            // 这行现在还会报服务端声明的能力（能否读图、上下文与输出上限、
            // 思考强度档位）——那正是"该不该勾带图"和"要不要发 reasoning_effort"
            // 的判据，不是装饰。
            sb.Append(detail.Length > 0 ? '：' + detail : "（服务端没有提供模型说明）");

            // 服务端允许的输出上限常常远大于界面这个框的上限（实测 DeepSeek：
            // 393216 vs 界面的 8192）。不说明的话，用户会以为"模型就只能写这么长"。
            if (info.MaxOutputTokens > 0 && MaxTokensBox.Maximum > 0 &&
                info.MaxOutputTokens > MaxTokensBox.Maximum)
                sb.Append($"（该模型允许输出到 {info.MaxOutputTokens}，" +
                          $"界面 max_tokens 上限是 {MaxTokensBox.Maximum:0}）");

            var alias = (info.Aliases ?? new List<string>())
                .Where(a => !string.Equals(a, info.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (alias.Count > 0) sb.Append(" · 别名 ").Append(string.Join(", ", alias));
        }
        else if (!string.IsNullOrWhiteSpace(current))
        {
            // 不当成错误：很多网关就是不在 /models 里报别名或自定义名，
            // 报成错误会把"能用"的配置吓退。
            ModelListText.Foreground = BadBrush;
            sb.Append($"⚠ 填写的「{current}」不在列表里。若这个名字确实可用（别名、自定义名），照常保存即可。");
        }

        ModelListText.Text = sb.ToString();
    }

    private static string SummarizeModels(List<LlmClient.ModelInfo> models)
    {
        if (models.Count == 0) return "连接成功，但服务端没有列出模型（可以直接手填模型名）";
        var head = string.Join(", ", models.Take(8).Select(m => m.Id));
        return models.Count > 8
            ? $"连接成功，可用模型：{head} …共 {models.Count} 个"
            : "连接成功，可用模型：" + head;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestResultText.Text = "正在连接…";
        // 与「获取列表」共用同一个请求：一次调用既验连通性，也顺手刷新模型列表
        var (ok, models, err) = await FetchModelsAsync();
        TestResultText.Text = ok ? "✓ " + SummarizeModels(models) : "✗ " + err;
        TestResultText.Foreground = ok ? GoodBrush : BadBrush;
        Log.Write("settings test: " + (ok ? "OK " : "FAIL ") +
                  (ok ? SummarizeModels(models) : err));
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

    // ================= 词库构建页 =================
    //
    // 这一页是整个设置里唯一会联网下载几百 MB、并且跑几分钟的地方，所以：
    // 素材清单必须显示出来（用户得知道要下什么、许可是什么），进度必须有反馈
    // （否则看着像卡死），而且真正的构建放在后台线程——它要几百秒，占着 UI 线程
    // 会让整个窗口失去响应，用户会以为程序挂了。

    /// <summary>停在内存里的取消源；构建期间点「取消」用它。</summary>
    private CancellationTokenSource? _vocabCts;

    /// <summary>
    /// 素材目录的默认值：词库文件旁边的 <c>sources</c>。
    /// 刻意不放在程序目录——那里是 <c>bin\</c>，重新构建就被清掉，
    /// 而这几百 MB 的素材用户不会想再下一遍。
    ///
    /// 若 <c>sources</c> 不存在但旁边有 <c>raw</c>，就用 <c>raw</c>：早期那套 Python
    /// 脚本（build_vocab.py）把上游文件放在 <c>raw\</c> 里，已经按那个布局下载过的人
    /// 不该被要求再挪一次或重下一遍。
    /// </summary>
    private static string DefaultVocabSourceDir(string vocabDbPath)
    {
        var dir = Path.GetDirectoryName(vocabDbPath);
        if (string.IsNullOrEmpty(dir)) dir = AppSettings.Dir;
        var preferred = Path.Combine(dir, "sources");
        if (Directory.Exists(preferred)) return preferred;
        var legacy = Path.Combine(dir, "raw");
        return Directory.Exists(legacy) ? legacy : preferred;
    }

    /// <summary>生成素材清单行：文件名 + 用途 + 来源 + 许可 + 是否已在本地。</summary>
    private void BuildVocabSourceRows()
    {
        VocabSourceList.Children.Clear();
        var dir = VocabSourceDirBox.Text.Trim();

        var all = VocabSources.All
            .Concat(VocabSources.PackSourceFiles())
            .ToList();

        foreach (var f in all)
        {
            var present = dir.Length > 0 && File.Exists(Path.Combine(dir, f.FileName));

            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(6, 4, 6, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 有无标记。没下到的用醒目色，用户一眼看出还缺什么。
            var mark = new TextBlock
            {
                Text = present ? "\uE73E" : "\uE711",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = present ? GoodBrush : BadBrush
            };
            Grid.SetColumn(mark, 0);
            row.Children.Add(mark);

            var name = new TextBlock
            {
                Text = f.FileName,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var what = new TextBlock
            {
                Text = f.What,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
            };
            Grid.SetColumn(what, 2);
            row.Children.Add(what);

            var lic = new TextBlock
            {
                Text = f.License,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
            };
            Grid.SetColumn(lic, 3);
            row.Children.Add(lic);

            ToolTipService.SetToolTip(row, $"{f.Upstream}\n{f.Url}");
            VocabSourceList.Children.Add(row);
        }

        // GPL 那条单独提示：它是唯一带 copyleft 的来源，容易被忽略。
        var note = new TextBlock
        {
            Text = "⚠ " + VocabSources.CopyleftNote,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(6, 6, 6, 2),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
        };
        VocabSourceList.Children.Add(note);
    }

    /// <summary>把"还缺哪些必需素材"显示在按钮下方。缺了就禁用构建按钮。</summary>
    private void UpdateVocabMissingText()
    {
        var dir = VocabSourceDirBox.Text.Trim();
        if (dir.Length == 0)
        {
            VocabMissingText.Text = "";
            BuildVocabBtn.IsEnabled = false;
            return;
        }

        var missing = VocabBuilder.MissingRequired(dir);
        var optional = VocabSources.OptionalFiles()
            .Where(f => !File.Exists(Path.Combine(dir, f)))
            .ToList();

        if (missing.Count == 0)
        {
            VocabMissingText.Text = optional.Count == 0
                ? "✓ 素材齐全，可以构建。"
                : $"✓ 必需素材齐全，可以构建（可选缺失：{string.Join("、", optional)}）";
            VocabMissingText.Foreground = GoodBrush;
        }
        else
        {
            VocabMissingText.Text = "✗ 还缺必需素材：" + string.Join("、", missing) +
                                    "。点「下载素材」，或手工下载后放进素材目录。";
            VocabMissingText.Foreground = BadBrush;
        }
        BuildVocabBtn.IsEnabled = missing.Count == 0;
    }

    private async void BrowseVocabSources_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolder();
        if (path is not null)
        {
            VocabSourceDirBox.Text = path;
            BuildVocabSourceRows();
            UpdateVocabMissingText();
        }
    }

    private void VocabSourceDir_Changed(object sender, TextChangedEventArgs e)
    {
        BuildVocabSourceRows();
        UpdateVocabMissingText();
    }

    /// <summary>
    /// 下载全部素材。
    ///
    /// 刻意用 HttpClient 直接下到目标路径，而不是让用户自己去浏览器里点：
    /// 这里有 12 个文件、总共约 200 MB，手工下极易漏掉某个（漏了必需文件构建会直接失败）。
    /// 已存在且大小非零的文件跳过，所以中断之后重跑是安全的、也是补下。
    /// </summary>
    private async void DownloadVocab_Click(object sender, RoutedEventArgs e)
    {
        var dir = VocabSourceDirBox.Text.Trim();
        if (dir.Length == 0)
        {
            VocabStatusText.Text = "先指定素材目录。";
            VocabStatusText.Foreground = BadBrush;
            return;
        }

        try { Directory.CreateDirectory(dir); }
        catch (Exception ex)
        {
            VocabStatusText.Text = "无法创建素材目录：" + ex.Message;
            VocabStatusText.Foreground = BadBrush;
            return;
        }

        DownloadVocabBtn.IsEnabled = false;
        VocabProgress.Value = 0;
        var all = VocabSources.All.Concat(VocabSources.PackSourceFiles()).ToList();
        var done = 0;
        var failed = new List<string>();

        // 代理。留空就是直连——但上游在国内直连是不通的，所以下面失败时会明确提这一点，
        // 而不是笼统报"下载失败"让人以为是自己的网络坏了。
        var proxy = VocabProxyBox.Text.Trim();
        HttpClient http;
        try
        {
            http = VocabFetch.CreateClient(proxy);
        }
        catch (ArgumentException ex)
        {
            VocabStatusText.Text = ex.Message + "（形如 http://127.0.0.1:10808）";
            VocabStatusText.Foreground = BadBrush;
            DownloadVocabBtn.IsEnabled = true;
            return;
        }

        using (http)

        foreach (var f in all)
        {
            var dest = Path.Combine(dir, f.FileName);
            if (File.Exists(dest) && new FileInfo(dest).Length > 0)
            {
                done++;
                VocabProgress.Value = done * 100.0 / all.Count;
                continue;
            }

            VocabStatusText.Text = $"正在下载 {f.FileName}…（{done + 1}/{all.Count}）";
            VocabStatusText.Foreground = InfoBrush;
            try
            {
                // 先下到 .part 再改名：下到一半断了不会留下一个"看起来已存在"的假文件，
                // 否则下次重跑会跳过它、构建时才发现是坏的。
                var part = dest + ".part";
                await VocabFetch.DownloadSourceAsync(http, f, part);

                File.Move(part, dest, overwrite: true);
                done++;
            }
            catch (Exception ex)
            {
                failed.Add($"{f.FileName}（{ex.Message}）");
                Log.Write($"vocab download failed: {f.FileName} {ex.Message}");
            }
            VocabProgress.Value = done * 100.0 / all.Count;
            BuildVocabSourceRows();
        }

        DownloadVocabBtn.IsEnabled = true;
        BuildVocabSourceRows();
        UpdateVocabMissingText();

        if (failed.Count == 0)
        {
            VocabStatusText.Text = $"✓ 素材已就绪（{done}/{all.Count}）。";
            VocabStatusText.Foreground = GoodBrush;
        }
        else
        {
            // 多半是网络/代理问题，所以明确说出来而不是笼统报"失败"。
            VocabStatusText.Text =
                $"部分素材下载失败（{failed.Count} 个）：{string.Join("；", failed)}。\n" +
                (proxy.Length == 0
                    ? "上游在国内网络下直连会超时——在「下载代理」里填一个可用代理（如 http://127.0.0.1:10808）再试。"
                    : "已用代理 " + proxy + "，但仍失败：确认代理在运行、且允许访问 github/danbooru。") +
                "也可以手工下载后放进素材目录。";
            VocabStatusText.Foreground = BadBrush;
        }
        Log.Write($"vocab download: {done}/{all.Count} ok, {failed.Count} failed");
    }

    /// <summary>
    /// 构建词库。
    ///
    /// 全程在后台线程上跑（<see cref="Task.Run(Func{Task})"/> 而不是直接 await）：
    /// 构建实测约 2–3 分钟，其中"生成去重表"一步是几十秒的纯 CPU 工作，
    /// 放在 UI 线程上会让窗口整个失去响应——那不是"慢"，用户会当成崩溃。
    /// </summary>
    private async void BuildVocab_Click(object sender, RoutedEventArgs e)
    {
        var srcDir = VocabSourceDirBox.Text.Trim();
        var outDb = VocabOutBox.Text.Trim();
        if (srcDir.Length == 0 || outDb.Length == 0)
        {
            VocabStatusText.Text = "素材目录和输出位置都要填。";
            VocabStatusText.Foreground = BadBrush;
            return;
        }

        var missing = VocabBuilder.MissingRequired(srcDir);
        if (missing.Count > 0)
        {
            VocabStatusText.Text = "还缺必需素材：" + string.Join("、", missing);
            VocabStatusText.Foreground = BadBrush;
            return;
        }

        if (File.Exists(outDb))
        {
            var confirm = new ContentDialog
            {
                Title = "覆盖现有词库？",
                Content = "已存在：\n" + outDb + "\n\n构建会替换它（先写临时文件，成功了才替换；" +
                          "失败或取消都不会动到原文件）。",
                PrimaryButtonText = "覆盖",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        // 种子母本随程序分发（vocab\seed.json）。构建时复制到库旁边，
        // 这样"建完就能用"，不必再让用户去别处找这个文件。
        var seedMaster = Path.Combine(AppContext.BaseDirectory, "vocab", "seed.json");

        _vocabCts = new CancellationTokenSource();
        var ct = _vocabCts.Token;
        BuildVocabBtn.IsEnabled = false;
        DownloadVocabBtn.IsEnabled = false;
        VocabProgress.IsIndeterminate = false;
        VocabProgress.Value = 0;
        VocabStatusText.Foreground = InfoBrush;

        // 进度：构建器的 log 回调在后台线程上，改控件必须回 UI 线程。
        var lines = new List<string>();
        void Report(string m)
        {
            Log.Write("vocab build: " + m);
            DispatcherQueue.TryEnqueue(() =>
            {
                lines.Add(m);
                // 只留最近几行：整页塞满会把按钮顶出视野（这个对话框最老的毛病）。
                if (lines.Count > 6) lines.RemoveAt(0);
                VocabStatusText.Text = string.Join("\n", lines);
            });
        }

        try
        {
            var result = await Task.Run(() => VocabBuilder.Build(
                srcDir, outDb, seedMaster, exportCsvDir: null, Report, ct), ct);

            VocabProgress.Value = 100;
            VocabStatusText.Text =
                $"✓ 构建完成：{result.DistinctTags:N0} 条标签，中文覆盖 {result.ChinesePercent:F1}%，" +
                $"Anima 索引 {result.AnimaIndex:N0} 条，" +
                $"退役名 {result.AliasCount:N0} 条，用时 {result.Elapsed.TotalSeconds:F1} 秒。\n" +
                $"输出：{result.DbPath}（{result.DbBytes / 1024.0 / 1024.0:F1} MB）\n" +
                "已把「数据路径」指向它；点下面的「保存」后生效（取消则不改动配置）。";
            VocabStatusText.Foreground = GoodBrush;

            // 只改界面上的值，**不直接写 _cfg**。这个对话框的约定是"取消是真的取消"
            // （见构造函数里对站点表副本的说明）：直接改 _cfg 的话，用户建完库又点取消，
            // 配置已经在内存里被改掉了——而屏幕上什么都没保存过。
            // 保存走 ApplyTo（它读的正是这两个框），随后 ShowSettingsAsync 会重载词库，
            // 所以"建好了却在读旧库"的困惑仍然不会出现。
            VocabBox.Text = outDb;
            var seedBeside = Path.Combine(Path.GetDirectoryName(outDb)!, "seed.json");
            if (File.Exists(seedBeside)) SeedBox.Text = seedBeside;

            foreach (var w in result.Warnings)
                Log.Write("vocab build warning: " + w);
        }
        catch (OperationCanceledException)
        {
            VocabStatusText.Text = "已取消。原有词库未被改动。";
            VocabStatusText.Foreground = InfoBrush;
        }
        catch (Exception ex)
        {
            VocabStatusText.Text = "构建失败：" + ex.Message + "\n（原有词库未被改动）";
            VocabStatusText.Foreground = BadBrush;
            Log.Write("vocab build failed: " + ex);
        }
        finally
        {
            _vocabCts?.Dispose();
            _vocabCts = null;
            BuildVocabBtn.IsEnabled = true;
            DownloadVocabBtn.IsEnabled = true;
            UpdateVocabMissingText();
        }
    }
}
