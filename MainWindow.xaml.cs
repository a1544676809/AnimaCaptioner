using System.Collections.ObjectModel;
using System.Text;
using AnimaCaptioner.Core;
using Microsoft.UI.Input;                    // InputKeyboardSource（问 Shift 有没有按着）
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;   // Thumb / DragDeltaEventArgs
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;              // VisualTreeHelper
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI.Core;                       // CoreVirtualKeyStates

namespace AnimaCaptioner;

public sealed partial class MainWindow : Window
{
    private AppSettings _cfg = null!;
    private VocabDb? _vocab;
    private readonly LlmClient _llm = new();
    private readonly SearchClient _search = new();
    private TagTranslator? _tagTranslator;

    /// <summary>
    /// 模型补译的中文。与词库的官方释义分开存放，UI 上分别标记——
    /// 前者是推断（可能错），后者来自 Danbooru 对照表。
    /// </summary>
    private ModelGlossStore _gloss = new();

    private List<DatasetItem> _items = new();
    private int _index = -1;
    private bool _loading;                      // 载入/程序性改写期间抑制 TextChanged 回灌
    private List<string> _pendingTags = new();

    /// <summary>
    /// 提示词里那段英文自然语言（含它前面的 ". " 分隔符），没有则为空串。
    /// 单独保存是因为它不是标签：不参与段内排序、不进中间列表、不参与标签校验，
    /// 但改写标签时必须原样接回去——否则保存一次就把用户写的散文弄丢了。
    /// </summary>
    private string _proseTail = "";

    /// <summary>
    /// 上一次解析的正文原文。用来挡掉重复事件：
    /// 给 RichEditBox 改字符格式也会触发 TextChanged，而那个事件里的正文
    /// 和上一次完全相同，所以比一下就短路了（否则会自激成死循环）。
    /// </summary>
    private string _lastParsedRaw = "\u0000";
    private Dictionary<string, double> _prior = new();

    private readonly ObservableCollection<TagItem> _tagRows = new();
    private readonly List<TagItem> _dragged = new();

    /// <summary>
    /// 中栏标签列表内部的 ScrollViewer，用来在列表重建后把滚动位置放回去。
    /// 首次布局之前可视树里还找不到它，所以**找不到时不缓存**，下次再找；
    /// 找到就留着（主窗口的列表模板建好之后不会再重建）。
    /// </summary>
    private ScrollViewer? _tagScroll;

    /// <summary>左栏图片列表。与 _items 一一对应（同序），列表项带缩略图与标注状态。</summary>
    private readonly ObservableCollection<ImageItem> _imageItems = new();

    private readonly ThumbnailLoader _thumbs = new();

    /// <summary>
    /// 两个输入框各自的 Tab 补全状态。**必须一份一个**：共用一个实例时，
    /// 在"添加标签"里按过 Tab 之后，就地编辑框的第一次 Tab 会从那一格继续，
    /// 于是补出来的候选和框里写的字毫无关系。
    /// </summary>
    private readonly TagComplete.Cycler _addCycle = new();
    private readonly TagComplete.Cycler _editCycle = new();

    /// <summary>
    /// Tab 补全是程序性改写输入框文字，而那次改动同样会触发 TextChanged。
    /// 这个标志让处理函数分清"用户打的字"和"我们自己填的候选"——后者不能
    /// 清掉候选列表（清了列表会闪一下，用户就看不出还有几个候选可切）。
    /// </summary>
    private bool _completing;

    /// <summary>独立预览窗口。null 表示当前没开；关闭后置回 null。</summary>
    private PreviewWindow? _preview;

    /// <summary>帮助窗口。同样是独立的顶层窗口，关闭后置回 null。</summary>
    private HelpWindow? _help;

    /// <summary>标签搜索窗口。同样是独立的顶层窗口，关闭后置回 null。</summary>
    private TagSearchWindow? _searchWin;

    /// <summary>程序性设置 SelectedIndex 时抑制 SelectionChanged，避免重复载入。</summary>
    private bool _selectGuard;

    /// <summary>翻译前的整段提示词，供「撤销翻译」还原。</summary>
    private string? _undoPrompt;

    /// <summary>
    /// --settings 启动开关请求的延迟动作。
    /// 不能在构造期直接开设置对话框：_cfg 要等 Initialize() 才赋值，
    /// 而 Initialize() 挂在 RootGrid.Loaded 上。构造期就开的话 _cfg 还是 null，
    /// SettingsDialog 构造函数里第一句取 cfg.ApiBaseUrl 就 NullReferenceException。
    /// </summary>
    private bool _settingsAfterInit;
    private bool _helpAfterInit;
    private bool _searchAfterInit;

    public MainWindow()
    {
        InitializeComponent();
        TagList.ItemsSource = _tagRows;
        ImageList.ItemsSource = _imageItems;

        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(ico)) AppWindow.SetIcon(ico);
        }
        catch { }

        Title = "AnimaCaptioner";

        AddAccel(Windows.System.VirtualKey.S, Windows.System.VirtualKeyModifiers.Control,
                 () => SaveCurrent(showDialog: true));
        AddAccel(Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.Menu,
                 () => Navigate(-1));
        AddAccel(Windows.System.VirtualKey.Right, Windows.System.VirtualKeyModifiers.Menu,
                 () => Navigate(1));

        // 记住上次看到的文件，下次启动直接定位
        Closed += (_, _) =>
        {
            try
            {
                if (_index >= 0 && _index < _items.Count)
                {
                    _cfg.LastFile = _items[_index].ImagePath;
                    _cfg.Save();
                }
            }
            catch (Exception ex) { Log.Write("save last file failed: " + ex.Message); }

            // 预览是独立的顶层窗口，不随主窗口自动关闭。不显式关掉的话，
            // 关掉主窗口后预览还留在屏幕上，进程也不会退出。帮助窗口同理。
            try { _preview?.Close(); } catch (Exception ex) { Log.Write("close preview failed: " + ex.Message); }
            try { _help?.Close(); } catch (Exception ex) { Log.Write("close help failed: " + ex.Message); }
            try { _searchWin?.Close(); } catch (Exception ex) { Log.Write("close tag search failed: " + ex.Message); }
        };

        RootGrid.Loaded += (_, _) => Initialize();
    }

    private void AddAccel(Windows.System.VirtualKey key,
                          Windows.System.VirtualKeyModifiers mods, Action act)
    {
        var a = new KeyboardAccelerator { Key = key, Modifiers = mods };
        a.Invoked += (_, e) => { e.Handled = true; act(); };
        RootGrid.KeyboardAccelerators.Add(a);
    }

    /// <summary>
    /// 按屏幕可用区域定初始大小。
    /// 构造函数里调 Resize 是无效的——窗口还没有显示器，AppWindow 拿不到真实
    /// 工作区，尺寸会被静默丢弃（实测窗口停在 1000×600 的默认值）。
    /// 所以放到 Loaded 之后再设，并且用工作区换算，避免超出屏幕。
    /// </summary>
    private void SizeToScreen()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var wa = area.WorkArea;
            var scale = 1.0;
            try { scale = Content.XamlRoot?.RasterizationScale ?? 1.0; } catch { }

            // WorkArea 是物理像素，AppWindow.Resize 也用物理像素
            var w = (int)Math.Min(1500, wa.Width * 0.95);
            var h = (int)Math.Min(940, wa.Height * 0.95);
            if (w < 800) w = Math.Min(800, wa.Width);
            if (h < 600) h = Math.Min(600, wa.Height);

            AppWindow.Resize(new SizeInt32(w, h));

            // 居中
            var x = wa.X + (wa.Width - w) / 2;
            var y = wa.Y + (wa.Height - h) / 2;
            AppWindow.Move(new PointInt32(x, y));

            Log.Write($"window sized {w}x{h} (workarea {wa.Width}x{wa.Height}, scale {scale:F2})");
        }
        catch (Exception ex)
        {
            Log.Write("SizeToScreen failed: " + ex.Message);
        }
    }

    // ================= 启动 =================

    private async void Initialize()
    {
        SizeToScreen();

        _cfg = AppSettings.Load();
        Log.Write("initialize: dataset=" + _cfg.DatasetDir + " vocab=" + _cfg.VocabDbPath);

        // 模型补译的缓存：它是数据不是配置，所以独立 sidecar 文件。
        // 词库加载前读出来，列表首次构建时就能带上。
        _gloss = ModelGlossStore.Load();

        // 分栏比例要等配置读出来才能设。放在 XAML 里只能写死，
        // 而它本来就是用户拖出来的、需要跨次启动记住的东西。
        ApplyColumnRatios();

        if (_cfg.ApiKeyUnreadable)
        {
            Log.Write("API key 存在但 DPAPI 解不开（换机器/换账户），需重填");
            await Info("API Key 无法解密",
                "保存的 API Key 由当前 Windows 账户加密，现在解不开了（通常是换了机器或账户）。\n" +
                "请到「设置」里重新填写。");
        }

        LoadVocab();

        // 若配置里还没有最近列表，把当前目录作为第一条，避免菜单空着
        if (_cfg.RecentFolders.Count == 0 && !string.IsNullOrWhiteSpace(_cfg.DatasetDir))
            _cfg.PushRecent(_cfg.DatasetDir);
        RebuildRecentMenu();

        var wantName = string.IsNullOrWhiteSpace(_cfg.LastFile)
            ? null : Path.GetFileNameWithoutExtension(_cfg.LastFile);
        LoadFolder(_cfg.DatasetDir, wantName);

        // 窗口与配置都就绪了，这时候才轮到 --settings / --help
        if (_settingsAfterInit)
        {
            _settingsAfterInit = false;
            await ShowSettingsAsync();
        }

        if (_helpAfterInit)
        {
            _helpAfterInit = false;
            ShowHelp();
        }

        if (_searchAfterInit)
        {
            _searchAfterInit = false;
            ShowTagSearch();
        }
    }

    private void LoadVocab()
    {
        try
        {
            var v = new VocabDb();
            v.Load(_cfg.VocabDbPath, _cfg.SeedPath);
            _vocab = v;
            VocabStatusText.Text =
                $"词库 {v.TagCount:N0} tags · Anima {v.AnimaCount:N0} · 中文键 {v.CnKeyCount:N0}";
            Log.Write("vocab ok: " + VocabStatusText.Text);
        }
        catch (Exception ex)
        {
            _vocab = null;
            VocabStatusText.Text = "词库未加载（查看设置）";
            Log.Write("vocab FAILED: " + ex);
        }
    }

    private void LoadFolder(string dir, string? selectName = null)
    {
        _items = Dataset.Scan(dir);
        _prior = VocabDb.OrderFromDir(dir);

        // 重建图片列表。缩略图缓存随之作废（行对象都换了）。
        _thumbs.Clear();
        _imageItems.Clear();
        for (var i = 0; i < _items.Count; i++)
            _imageItems.Add(new ImageItem { Source = _items[i], Index = i });
        RefreshListStatus();

        RebuildRecentMenu();
        UpdateStats();
        Log.Write($"folder loaded: {dir} items={_items.Count} prior={_prior.Count}");

        var idx = 0;
        if (selectName is not null)
        {
            var found = _items.FindIndex(i =>
                string.Equals(i.Name, selectName, StringComparison.OrdinalIgnoreCase));
            if (found >= 0) idx = found;
        }
        Select(idx);
    }

    /// <summary>
    /// 刷新每一行的标注状态与缩略图。
    /// 读 caption 只为取标签数和校验问题数——列表要显示这些，
    /// 而它们只在载入目录和保存后变化，所以不放在滚动路径上。
    /// </summary>
    private void RefreshListStatus()
    {
        foreach (var row in _imageItems)
        {
            var has = row.Source.HasCaption;
            var count = 0;
            var issues = 0;
            if (has)
            {
                try
                {
                    var tags = CaptionFile.Load(row.Source.CaptionPath).Tags;
                    count = tags.Count;
                    if (_vocab is not null) issues = VocabDb.AuditTags(tags, _vocab).Count;
                }
                catch (Exception ex)
                {
                    Log.Write($"list status failed {row.Name}: {ex.Message}");
                }
            }
            row.RefreshStatus(has, count, issues);
        }

        ListCountText.Text = _imageItems.Count == 0
            ? ""
            : $"{_imageItems.Count} 张";
    }

    /// <summary>只是刷新某一行的标注状态（保存后调用，避免全量重扫）。</summary>
    private void RefreshRowStatus(int index)
    {
        if (index < 0 || index >= _imageItems.Count) return;
        var row = _imageItems[index];
        var has = row.Source.HasCaption;
        var count = 0;
        var issues = 0;
        if (has)
        {
            try
            {
                var tags = CaptionFile.Load(row.Source.CaptionPath).Tags;
                count = tags.Count;
                if (_vocab is not null) issues = VocabDb.AuditTags(tags, _vocab).Count;
            }
            catch (Exception ex) { Log.Write($"row status failed: {ex.Message}"); }
        }
        row.RefreshStatus(has, count, issues);
    }

    /// <summary>重建「最近打开」子菜单。空列表时给一条禁用的占位项，
    /// 否则 MenuFlyoutSubItem 展开后是空白，看起来像坏了。</summary>
    private void RebuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        if (_cfg.RecentFolders.Count == 0)
        {
            RecentMenu.Items.Add(new MenuFlyoutItem { Text = "（暂无）", IsEnabled = false });
            return;
        }

        foreach (var dir in _cfg.RecentFolders)
        {
            var item = new MenuFlyoutItem
            {
                Text = dir,
                Tag = dir,
                Icon = new FontIcon { Glyph = "\uE8B7" }
            };
            item.Click += RecentFolder_Click;
            RecentMenu.Items.Add(item);
        }

        RecentMenu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "清除列表" };
        clear.Click += (_, _) =>
        {
            _cfg.RecentFolders.Clear();
            _cfg.Save();
            RebuildRecentMenu();
        };
        RecentMenu.Items.Add(clear);
    }

    private void RecentFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuFlyoutItem)?.Tag is not string dir) return;
        if (!Directory.Exists(dir))
        {
            _ = Info("目录不存在", dir + "\n\n可能已被移动或删除，将把它从列表里移除。");
            _cfg.RecentFolders.RemoveAll(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase));
            _cfg.Save();
            RebuildRecentMenu();
            return;
        }
        _cfg.DatasetDir = dir;
        _cfg.PushRecent(dir);
        _cfg.Save();
        LoadFolder(dir);
    }

    /// <summary>
    /// 底部统计。直接汇总左栏列表每行已经算好的数字，而不是重新读一遍所有
    /// caption：一次目录载入里 RefreshListStatus 已经读过并校验过全部文件，
    /// 再走一遍 Dataset.ComputeStats 会把 53 个文件多读一次，而且两处若口径
    /// 不同还会显示互相矛盾的数字。
    /// 必须先调用 RefreshListStatus。
    /// </summary>
    private void UpdateStats()
    {
        var total = _imageItems.Count;
        var captioned = 0;
        var tags = 0;
        var issues = 0;

        foreach (var row in _imageItems)
        {
            if (!row.HasCaption) continue;
            captioned++;
            tags += row.TagCount;
            if (row.IssueCount > 0) issues++;
        }

        DatasetStatsText.Text =
            $"{total} 张图 · 已标注 {captioned} · 未标注 {total - captioned} · " +
            $"标签 {tags:N0} · 有校验问题 {issues}";
    }

    // ================= 选择与载入 =================

    private void Select(int i)
    {
        if (_items.Count == 0)
        {
            _index = -1;
            // 不要设 ItemsSource = null：那会断开绑定，之后再也刷不出来。
            _tagRows.Clear();
            SetCaptionText("");
            TagCountText.Text = "";
            _pendingTags = new List<string>();
            _proseTail = "";
            PrevBtn.IsEnabled = NextBtn.IsEnabled = false;
            PreviewBtn.IsEnabled = false;
            SelectionText.Text = "0 / 0";
            SyncListSelection();
            return;
        }

        _index = Math.Clamp(i, 0, _items.Count - 1);
        LoadCurrent();
    }

    private void LoadCurrent()
    {
        if (_index < 0 || _index >= _items.Count) return;
        var item = _items[_index];

        _loading = true;
        try
        {
            // ---- caption ----
            // 每次都从盘上重读：本程序或别的工具可能刚改过它，
            // 缓存会在切图时把旧内容写回去。
            item.Caption = item.HasCaption ? CaptionFile.Load(item.CaptionPath) : null;
            var raw = item.Caption?.Text ?? "";

            // 标签和散文尾巴分开存：散文不是标签，不能进 _pendingTags。
            // 注意不能用 CaptionFile.Tags——那是纯逗号切分，会把散文尾巴
            // 也当成一堆标签。
            var parsed = PromptText.Parse(raw, _vocab);
            _pendingTags = new List<string>(parsed.Tags);
            _proseTail = parsed.ProseTail;

            SetCaptionText(raw);
            // 换图片：这是**另一张图**的内容，回到顶部才对（其余重建都保留滚动位置）。
            RefreshTagRows(resetScroll: true);
        }
        finally { _loading = false; }

        PrevBtn.IsEnabled = _index > 0;
        NextBtn.IsEnabled = _index < _items.Count - 1;
        PreviewBtn.IsEnabled = true;
        SelectionText.Text = $"{_index + 1} / {_items.Count}";

        SyncListSelection();

        // 预览窗口开着就跟着走，避免两个窗口显示不同的图
        _preview?.GoTo(_index);
    }

    /// <summary>把左栏列表的选中项对齐到 _index，并滚进视野。</summary>
    private void SyncListSelection()
    {
        _selectGuard = true;
        try
        {
            if (_index < 0 || _index >= _imageItems.Count)
            {
                ImageList.SelectedIndex = -1;
                return;
            }
            if (ImageList.SelectedIndex == _index) return;
            ImageList.SelectedIndex = _index;
            ImageList.ScrollIntoView(_imageItems[_index]);
        }
        catch (Exception ex) { Log.Write("sync list selection failed: " + ex.Message); }
        finally { _selectGuard = false; }
    }

    // ================= 图片列表 =================

    /// <summary>单击：读取这张图的 tag。</summary>
    private void ImageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectGuard) return;
        var i = ImageList.SelectedIndex;
        if (i < 0 || i >= _items.Count || i == _index) return;
        SaveCurrent(showDialog: false);   // 静默保存，避免切图丢改动
        Select(i);
    }

    /// <summary>双击：开独立预览窗口。</summary>
    private void ImageList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // 双击落在空白处也会冒泡到这里，此时不该开窗口
        var i = ImageList.SelectedIndex;
        if (i < 0 || i >= _items.Count) return;
        if (i != _index) { SaveCurrent(showDialog: false); Select(i); }
        OpenPreview();
    }

    /// <summary>
    /// ListView 的容器复用钩子。缩略图在这里按需加载：只有在真的需要显示
    /// 这一行时才解码，滚出视野的行不会白白解码。
    /// </summary>
    private void ImageList_ContainerContentChanging(
        ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.Item is ImageItem row) _thumbs.Request(row);
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_index < 0 || _index >= _items.Count)
        {
            _ = Info("没有选中图片", "先在左栏点一张图，再预览。");
            return;
        }
        OpenPreview();
    }

    /// <summary>
    /// 列表上的空格键。挂在 ListView 而不是全局：全局加速键会连右侧文本框里
    /// 打空格一起吃掉。KeyboardAccelerator 没接 Invoked 就是死的（只会让空格
    /// 失效），所以这里必须显式处理并把事件标记为已处理。
    /// </summary>
    private void PreviewAccel_Invoked(KeyboardAccelerator sender,
                                      KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_index >= 0 && _index < _items.Count) OpenPreview();
    }

    /// <summary>
    /// 打开（或激活）独立预览窗口。真正的动作排到本次输入处理完之后再做。
    ///
    /// 不能在双击事件里就把窗口建出来并激活：Win32 的「点击激活」是系统处理这次输入
    /// 时才落到主窗口上的，晚于事件里的 Activate()，于是刚打开的预览立刻被压下去。
    /// 实测这个竞态与双击速度相关——快 → 主窗口留下焦点，慢 → 预览拿到焦点。
    /// 排到输入处理完之后，新窗口的激活就不会再被覆盖。
    /// </summary>
    private void OpenPreview()
    {
        if (_index < 0 || _index >= _items.Count) return;

        var index = _index;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => OpenPreviewNow(index));
    }

    private void OpenPreviewNow(int index)
    {
        if (index < 0 || index >= _items.Count) return;

        try
        {
            if (_preview is null)
            {
                var w = new PreviewWindow();
                w.Closed2 += () =>
                {
                    w.SaveBounds();
                    _preview = null;
                    Log.Write("preview window closed");
                };
                _preview = w;
            }

            if (!_preview.Open(_items, index, _cfg))
            {
                _preview = null;
                _ = Info("没有可预览的图片", "这个目录里没有图片。");
                return;
            }

            _preview.BringToFront();
        }
        catch (Exception ex)
        {
            // 预览窗口构造失败不能把主窗口一起带走（实测 MinZoomFactor 越界会让
            // LoadComponent 抛异常；那条路径以前是未捕获的，整个进程都会退出）。
            Log.Write("preview open FAILED: " + ex);
            _preview = null;
            _ = Info("预览打开失败", ex.Message);
        }
    }

    /// <summary>
    /// 把当前标签序列 + 散文尾巴写回提示词并刷新列表。
    ///
    /// 所有标签操作（上移/下移/删除/规整/添加/翻译）都必须走这里。以前各处
    /// 直接写 <c>string.Join(", ", _pendingTags)</c>，在提示词只有标签时没问题，
    /// 但现在提示词可能带一段自然语言尾巴——那样写会把散文整个丢掉。
    /// </summary>
    /// <param name="keepVisibleIndex">重建后必须留在视野里的那一行（_pendingTags 下标）。
    /// 只有上移/下移需要传：其余操作改动的是行的内容而不是行的位置。</param>
    private void CommitPrompt(int keepVisibleIndex = -1)
    {
        SetCaptionText(PromptText.Rebuild(_pendingTags, _proseTail));
        RefreshTagRows(keepVisibleIndex: keepVisibleIndex);
    }

    /// <summary>改写正文但不触发 TextChanged 的数据回灌。</summary>
    private void SetCaptionText(string text)
    {
        var was = _loading;
        _loading = true;
        try
        {
            // RichEditBox 的 Document.SetText 不会触发 TextChanged，但显式置
            // _loading 更稳（不同版本行为有差异，而误触发一次会把 _pendingTags
            // 从半写完的文本里重算，把刚设的内容改掉）。
            CaptionBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, text ?? "");

            // 同步"上次解析的正文"。不同步的话，程序性改完正文后再由用户改动时，
            // 首次差异比较会误判成"没变化"而漏掉一次刷新。
            _lastParsedRaw = text ?? "";
            ApplyPromptColors();
        }
        finally { _loading = was; }
    }

    /// <summary>
    /// 读出提示词原文。
    ///
    /// RichEditBox 内部把段落结尾规范成 '\r'（实测：SetText 里写 "\r\n"，
    /// GetText 读回来是 "\r"），而 caption 是单行格式——所以统一折成空格再
    /// 交给解析器，避免把行尾当成内容写进 .txt。
    /// </summary>
    private string GetCaptionText()
    {
        try
        {
            CaptionBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.None, out var text);
            // 文档末尾总带一个段落标记，去掉；其余换行折成空格
            return (text ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
        catch (Exception ex)
        {
            Log.Write("read caption box failed: " + ex.Message);
            return "";
        }
    }

    /// <summary>
    /// 给提示词原文按大类上底色：标签段用该大类的颜色，自然语言那段用「自然语言」
    /// 那一档颜色，两者一眼可分。
    ///
    /// **必须带 _loading 保护**。踩过的坑（实测日志 #215→#254，4 秒内自激约 40 次、
    /// 每 ~85ms 一次）：RichEditBox 的字符格式改动**本身也会触发 TextChanged**，
    /// 而 TextChanged 里又调本方法改色，于是形成
    /// 「改色 → TextChanged → 再改色」的无限回环，界面卡到没法操作。
    /// 所以着色期间把 _loading 立起来，让回灌的那次事件直接返回。
    ///
    /// 另外两个必须守住的点：
    /// 1. **底色只能用不透明的实色**。用 alpha=0 去"清空"底色时 RichEdit 会当成
    ///    不透明黑，正文变成白字黑底、完全读不了。做法是把分类色按比例混进实际背景色。
    /// 2. **文字色要显式设回主题前景色**，否则换底色时可能继承到不搭的前景色。
    ///
    /// TextBox 做不到区间着色（没有区间字符格式 API），所以这一栏换成了 RichEditBox。
    /// </summary>
    private void ApplyPromptColors()
    {
        var wasLoading = _loading;
        _loading = true;          // 关键：挡掉本方法自己触发的那次 TextChanged
        try
        {
            var text = GetCaptionText();
            var doc = CaptionBox.Document;

            var bg = ThemeColor("CardBackgroundFillColorDefaultBrush",
                                Windows.UI.Color.FromArgb(255, 255, 255, 255));
            var fg = ThemeColor("TextFillColorPrimaryBrush",
                                Windows.UI.Color.FromArgb(255, 0, 0, 0));

            // 逐段改色会闪，也会把光标挤走，所以批量提交 + 存还选区
            var selStart = doc.Selection.StartPosition;
            var selEnd = doc.Selection.EndPosition;
            doc.BatchDisplayUpdates();

            try
            {
                // 整段先复位成"背景色 + 前景色"，不留任何上一次解析的残留
                var full = doc.GetRange(0, Math.Max(text.Length, 1));
                full.CharacterFormat.BackgroundColor = bg;
                full.CharacterFormat.ForegroundColor = fg;

                if (text.Length > 0)
                {
                    // 标签段：按大类上色
                    foreach (var span in PromptText.ColorSpans(text, _vocab))
                    {
                        var r = doc.GetRange(span.Start, span.Start + span.Length);
                        r.CharacterFormat.BackgroundColor = TagColors.TintOf(span.Section, _cfg, bg);
                        r.CharacterFormat.ForegroundColor = fg;
                    }

                    // 散文尾巴：整段一色，明确告诉用户"这块不是标签"
                    var parts = PromptText.Parse(text, _vocab);
                    if (parts.HasProse)
                    {
                        var start = text.Length - parts.ProseTail.Length;
                        var r = doc.GetRange(start, text.Length);
                        r.CharacterFormat.BackgroundColor =
                            TagColors.TintOf(TagSections.Prose, _cfg, bg);
                        r.CharacterFormat.ForegroundColor = fg;
                    }
                }
            }
            finally
            {
                doc.ApplyDisplayUpdates();
                doc.Selection.SetRange(selStart, selEnd);
            }
        }
        catch (Exception ex)
        {
            Log.Write("apply prompt colours failed: " + ex.Message);
        }
        finally { _loading = wasLoading; }
    }

    /// <summary>取主题画刷的颜色；拿不到就回落给定值。</summary>
    private static Windows.UI.Color ThemeColor(string key, Windows.UI.Color fallback)
    {
        try
        {
            if (Application.Current.Resources[key] is Microsoft.UI.Xaml.Media.SolidColorBrush b)
                return b.Color;
        }
        catch (Exception ex) { Log.Write($"theme colour {key} failed: {ex.Message}"); }
        return fallback;
    }

    /// <summary>
    /// 按 _pendingTags 重建中间的标签列表，按 Anima 官方大类分段。
    ///
    /// 每行单独取中文释义走直接查表；用 Search 会每次扫 33 万行，
    /// 而本方法在每次编辑时都会跑，那样会直接卡死界面。
    ///
    /// 列表**按大类重排后**显示（段内保持 _pendingTags 的相对次序），所以
    /// 显示下标与待保存下标不同：每行的 PendingIndex 记着它在 _pendingTags
    /// 里的位置，上下移动靠它换算。
    ///
    /// 分段边界画在每段第一行的行模板里，而不是用 ListView 的分组头——
    /// 后者在 WinUI 3 上实测不渲染。
    ///
    /// 关于滚动位置：<c>_tagRows</c> 一 Clear，ListView 的 extent 就归零，
    /// ScrollViewer 会**立刻弹回顶端**——上移/下移一个标签也会连带把视线甩到
    /// 列表开头。所以默认做法是重建前记下位置、重建后放回去，让视线跟着标签走。
    /// 只有换图片时例外（不同的图 = 不同的内容，回到顶部才对）。
    /// </summary>
    /// <param name="resetScroll">换图片时置 true：新图的标签从顶部看起。</param>
    /// <param name="keepVisibleIndex">重建后必须仍留在视野里的那一行（_pendingTags 下标）。
    /// 上移/下移传被移动的那个标签：它被移到视口上/下边缘之外时，列表跟着翻，
    /// 让它停在最上/最下一行，而不是滚出屏幕。</param>
    private void RefreshTagRows(bool resetScroll = false, int keepVisibleIndex = -1)
    {
        // 列表即将被整体重建：正在编辑的那一行对象会被丢掉（新 TagItem 是另一批实例），
        // 而且此时 _pendingTags 很可能已经被别的操作换过内容——再拿编辑框里的文字
        // 去改，就是往一个已经变了的列表上套旧名字。所以这里**放弃**编辑而不是提交。
        //
        // 正常路径不受影响：Enter 提交和失焦提交都会先把 _editingRow 置空，
        // 走到这里时已经是 no-op。真正会撞上的是"编辑中切了图片"这类重建。
        AbandonEdit("列表被重建");

        // 重建前的滚动位置。resetScroll 时不要它（切图回到顶部）。
        var sv = resetScroll ? null : TagScrollViewer();
        var keepOffset = sv?.VerticalOffset ?? 0;
        var keepSelBefore = TagList.SelectedItems.Cast<TagItem>().Select(r => r.Tag).ToList();

        var issues = new Dictionary<string, (string Status, string? Use)>(StringComparer.OrdinalIgnoreCase);
        if (_vocab is not null)
            foreach (var i in VocabDb.AuditTags(_pendingTags, _vocab))
                issues[i.Tag] = (i.Status, i.Use);

        _tagRows.Clear();

        // 先把所有标签行建好并着色，同时记下它在 _pendingTags 里的下标
        var rows = new List<TagItem>(_pendingTags.Count);
        for (var i = 0; i < _pendingTags.Count; i++)
        {
            var t = _pendingTags[i];
            var row = new TagItem { Tag = t, Cn = _vocab?.LookupCn(t) ?? "", PendingIndex = i };

            // 词库没有中文时，才轮到模型补的译文顶上来。
            // **绝不覆盖词库值**：官方释义优先，模型译只是填空。
            if (row.Cn.Length == 0 && _gloss.TryGet(t, out var g))
            {
                row.Cn = g.Cn;
                row.CnIsModel = true;
                row.CnNote = g.Searched ? "模型译 · 已联网核对" : "模型译 · 非官方";
            }

            if (issues.TryGetValue(t, out var iss))
            {
                row.Status = iss.Status;
                row.Note = iss.Status switch
                {
                    "alias" => "旧名 → " + (iss.Use ?? "?"),
                    "underscore" => "含下划线 → " + (iss.Use ?? "?"),
                    "retired" => "已废弃（Anima 索引里没有）",
                    "no-usage" => "无引用",
                    "not-a-tag" => "不在任何索引中",
                    "wrong-category" => "匹配到了角色/画师名",
                    _ => iss.Status
                };
            }
            else if (VocabDb.IsStyleTrigger(t)) { row.Status = "control"; row.Note = "风格触发器"; }
            else if (VocabDb.IsControlToken(t)) { row.Status = "control"; row.Note = "控制词"; }
            else if (_vocab is not null && _vocab.Status(t) == "anima") { row.Status = "ok"; row.Note = ""; }

            row.ApplySection(_vocab, _cfg);
            rows.Add(row);
        }

        // 按大类稳定排序：段内相对次序原样保留（这正是"段内可调"的含义）
        var ordered = rows
            .OrderBy(r => r.SectionIndex)
            .ThenBy(r => r.PendingIndex)
            .ToList();

        // 每段第一行挂上大类条带，并把该段标签数写进去
        for (var i = 0; i < ordered.Count; i++)
        {
            var isStart = i == 0 || ordered[i].SectionIndex != ordered[i - 1].SectionIndex;
            if (!isStart) continue;

            var sec = ordered[i].SectionIndex;
            var count = ordered.Count(r => r.SectionIndex == sec);
            ordered[i].IsSectionStart = true;
            // 用 TitleOf 而不是 All[sec]：散文的 SectionIndex 是哨兵值 99，
            // 直接索引会越界。
            ordered[i].SectionTitle = TagSections.TitleOf(sec);
            ordered[i].SectionSubtitle = sec >= TagSections.ProseIndex
                ? "不参与标签排序"
                : count + " tags";
        }

        foreach (var r in ordered) _tagRows.Add(r);

        // 散文尾巴单独一行接在**最后**，用「自然语言」那一档颜色加一条说明。
        //
        // 必须在标签行加完之后再加：早先的写法是把它先塞进 _tagRows 再拼标签行，
        // 于是它被顶到了列表最上面（截图里那句话就压在「质量/元信息」段之前），
        // 和注释里写的"挂在末尾"正好相反。散文排在所有标签之后，也正对应
        // 官方 example.png 的写法（标签区 + 句号 + 散文）。
        //
        // 它不在 _pendingTags 里，所以不参与上移/下移/删除和校验；之所以要显示，
        // 是为了让用户看到"这段被当成散文了，不是标签"——否则右边明明写了一段
        // 英文，中间列表却空着，看起来像内容丢了。
        if (_proseTail.Length > 0)
        {
            var txt = _proseTail.TrimStart('.', ' ', '\u3002');
            if (txt.Length > 0)
            {
                var prow = new TagItem
                {
                    Tag = txt,
                    Cn = "自然语言（不是标签）",
                    Status = "prose",
                    Note = "散文段 · 不参与校验与排序 · 保存时原样写回",
                    PendingIndex = -1,      // 不是标签，没有待保存下标
                };
                prow.ApplySection(_vocab, _cfg);
                prow.ForceSection(TagSections.Prose, _cfg);
                prow.IsSectionStart = true;
                prow.SectionTitle = TagSections.TitleOf(TagSections.ProseIndex);
                prow.SectionSubtitle = "不参与标签排序";
                _tagRows.Add(prow);
            }
        }

        // 计数只说标签：_pendingTags 本来就不含散文，而列表里那一行散文要排除掉，
        // 否则「N tags」会比实际标签数多 1。
        var proseRows = _tagRows.Count(r => r.Section == TagSections.Prose);
        TagCountText.Text = _pendingTags.Count + " tags" +
                            (issues.Count > 0 ? $" · ⚠ {issues.Count}" : "") +
                            (proseRows > 0 ? " · +1 散文" : "");
        CaptionStatusText.Text = _tagRows.Count == 0
            ? "空"
            : _pendingTags.Count + " tags" + (issues.Count > 0 ? $" · ⚠ {issues.Count} 待确认" : " · 校验通过")
              + (proseRows > 0 ? " · 含自然语言" : "");

        // 训练侧的 token 预算提示。超 512 会被训练器**静默截断**（truncation=True），
        // 而散文排在最后，所以被丢的正好是散文——界面上必须说出来，否则无从察觉。
        var raw = GetCaptionText();
        if (PromptText.OverBudget(raw))
            CaptionStatusText.Text += $" · ⚠ 约 {PromptText.EstimateTokens(raw)} tokens，超过训练上限 512，末尾会被静默截断";

        // 列表是按大类分段显示的，但文件里的实际顺序可能不是。
        // 两者不一致时必须说出来：否则用户看到的是分好段的界面，存下去的却是
        // 另一个顺序，而界面上没有任何迹象。给一条提示和一个一键修好的入口。
        var orderedTags = ordered.Select(r => r.Tag).ToList();
        if (!_pendingTags.SequenceEqual(TagSections.Canonicalize(_pendingTags, _vocab),
                                        StringComparer.Ordinal))
        {
            var n = CountOutOfSectionOrder(_pendingTags);
            SectionOrderHint.Visibility = Visibility.Visible;
            SectionOrderHint.Text = $"⚠ 文件里的大类顺序不规范（{n} 处跨段），" +
                                    "可用「标签 → 规整大类顺序」(Ctrl+Shift+S) 修正。";
        }
        else
        {
            SectionOrderHint.Visibility = Visibility.Collapsed;
        }
        _ = orderedTags;

        if (sv is null) return;
        if (resetScroll) ResetTagScroll(sv);
        else RestoreTagScroll(sv, keepOffset, keepVisibleIndex);

        // 重建前的选中按**标签名**套回新一批行对象上。
        //
        // 必须重新设：_tagRows.Clear() 换掉的是一批**全新的 TagItem 实例**，
        // 原来的选中对象已被丢弃，于是 ListView 的选中变成空（实测
        // RDBG before: sel=[closed mouth] → 改完就什么都不选）。这里按名字
        // 找回等价的那一行，用户的"选中某个标签"才不会因为改个名就丢掉。
        // keepVisibleIndex 点名的那一行优先——它就是刚被操作的对象。
        if (!resetScroll && keepSelBefore.Count > 0)
        {
            var restore = keepVisibleIndex >= 0
                ? _tagRows.Where(r => r.PendingIndex == keepVisibleIndex).Select(r => r.Tag).ToList()
                : keepSelBefore;
            var restored = restore
                .Select(t => _tagRows.FirstOrDefault(r => string.Equals(r.Tag, t, StringComparison.OrdinalIgnoreCase)))
                .Where(r => r is not null).Cast<TagItem>().ToList();
            if (restored.Count > 0)
            {
                TagList.SelectedItems.Clear();
                foreach (var r in restored) TagList.SelectedItems.Add(r);
            }
        }
    }

    /// <summary>
    /// 换图片时把列表放回顶部。
    ///
    /// 显式设一次，**不依赖"集合 Clear 会自然弹回顶端"**——那是我们正在修的 bug 行为
    /// （实测每个重建周期里 `natural` 都是 0），拿它当特性用，等于把"回到顶部"这件事
    /// 押在一个将来可能被上游改掉、也可能被我们别处顺手修掉的副作用上。
    /// </summary>
    private void ResetTagScroll(ScrollViewer sv)
    {
        sv.UpdateLayout();
        sv.ChangeView(null, 0, null, true);
        DispatcherQueue.TryEnqueue(() =>
        {
            try { sv.UpdateLayout(); } catch { }
            sv.ChangeView(null, 0, null, true);
        });
    }

    /// <summary>中栏列表内部的 ScrollViewer。首次布局前还不存在，所以只缓存找到的结果。</summary>
    private ScrollViewer? TagScrollViewer()
    {
        if (_tagScroll is not null) return _tagScroll;
        _tagScroll = FindDescendant<ScrollViewer>(TagList);
        return _tagScroll;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var deeper = FindDescendant<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    /// <summary>
    /// 把重建前的滚动位置放回去，并保证 <paramref name="keepVisibleIndex"/> 那一行还在视野里。
    ///
    /// 顺序是实测出来的，不能想当然地合并：
    /// 1. 位置先放回去——集合 Clear 之后 extent 归零，ListView 自己会退回顶端。
    /// 2. 隔一跳再设一次——<c>ChangeView</c> 在布局尚未更新、以及内容刚换时会被**静默
    ///    忽略**（预览窗口和帮助窗口都踩过），所以要 <c>UpdateLayout()</c> 之后再设。
    /// 3. 再隔一跳才去量目标行、必要时翻页。**这一步不能和第 1 步挤在同一个回调里**：
    ///    `ChangeView` 是异步的，后写的"放回位置"会覆盖先算好的翻页，而量几何又量到
    ///    还没滚的旧布局，结果翻页量算成 0、行停在视口外（实测外了 38 DIP）。
    ///
    /// 贴边量自己算，不用 <c>ScrollIntoView</c>：后者带滚动动画、位置不是立刻可读，
    /// 而且给的是"最少滚动"，一旦有行的行高变了（上移会让某个标签变成段首、多出一条
    /// 段头），落点就不好预期。这里直接把行坐标和视口比，越界多少就滚多少——于是行
    /// 正好停在视口的上/下边缘。行还没被实体化（`ContainerFromItem` 返回 null，实测
    /// 约四成）时算不出来，那时才退回 <c>ScrollIntoView</c> 兜底。
    ///
    /// 日志：`natural` 是不做补救时它自己停在哪儿（也就是那个 bug 的样子），`want` 是
    /// 重建前记下的位置，`row=` 是量到的行位置、必要时附上翻页量。
    /// </summary>
    private void RestoreTagScroll(ScrollViewer sv, double offset, int keepVisibleIndex)
    {
        sv.UpdateLayout();

        // 不做补救时 ListView 自己停在哪儿：集合 Clear 会把 extent 归零，
        // 于是这里读到的就是 0——也就是"每次移动都把视线甩回列表开头"那个现象。
        var natural = sv.VerticalOffset;

        if (offset > 0) sv.ChangeView(null, offset, null, true);

        // 第一跳：内容刚换时首次 ChangeView 可能被静默忽略，布局之后再设一次。
        DispatcherQueue.TryEnqueue(() =>
        {
            try { sv.UpdateLayout(); } catch { }
            if (offset > 0) sv.ChangeView(null, offset, null, true);

            // 第二跳：等位置真正落地，再量目标行、必要时翻页。
            DispatcherQueue.TryEnqueue(() =>
            {
                try { sv.UpdateLayout(); } catch { }
                var geo = MeasureRow(sv, keepVisibleIndex);
                var over = geo.Over;

                string action;
                if (geo.Known)
                {
                    if (Math.Abs(over) > 0.5)
                    {
                        sv.ChangeView(null, sv.VerticalOffset + over, null, true);
                        action = $" -> 翻页 {over:F0}";
                    }
                    else action = "（已在视野内）";
                }
                else if (keepVisibleIndex >= 0 &&
                         _tagRows.FirstOrDefault(r => r.PendingIndex == keepVisibleIndex) is { } row)
                {
                    // 量不到容器：ListView 还没把这一行实体化（虚拟化是增量做的，
                    // 实测约四成的次数在两跳之后仍拿不到容器）。这时退回平台自己的
                    // ScrollIntoView——它内部会先实体化再滚动，能保证这行进视野。
                    // 代价是带一点滚动动画、位置不是立刻可读，所以只在量不到时用。
                    TagList.ScrollIntoView(row);
                    action = " -> 量不到容器，交给 ScrollIntoView";
                }
                else action = "";

                // 只在"本来就有滚动位置"或"要求保持某行可见"时记一行：
                // 在提示词框里打字也会走到这里，那时 offset=0，不必刷屏。
                if (offset <= 0 && keepVisibleIndex < 0) return;
                Log.Write($"tag scroll: want={offset:F0} natural={natural:F0} " +
                          $"keep={keepVisibleIndex} {geo.Text}{action}");
            });
        });
    }

    private readonly record struct RowBox(double Top, double Bottom, double Viewport,
                                          string Text, bool Known)
    {
        /// <summary>要把它带回视口需要滚多少（0 = 已经在视野里）。</summary>
        public double Over => !Known || Viewport <= 0 ? 0
            : Top < 0 ? Top
            : Bottom > Viewport ? Bottom - Viewport
            : 0;
    }

    /// <summary>
    /// 量目标行在视口里的位置。相对 ScrollViewer 的坐标**已包含滚动位移**，
    /// 所以 `Top` 就是它在视口里的纵向位置（单位是 DIP，不是物理像素）。
    /// </summary>
    private RowBox MeasureRow(ScrollViewer sv, int index)
    {
        var vp = sv.ViewportHeight;
        if (index < 0) return new RowBox(0, 0, vp, "row=[n/a]", false);
        var row = _tagRows.FirstOrDefault(r => r.PendingIndex == index);
        if (row is null) return new RowBox(0, 0, vp, "row=[no-row]", false);
        if (TagList.ContainerFromItem(row) is not FrameworkElement c)
            return new RowBox(0, 0, vp, "row=[no-container]", false);   // 还没实体化
        try
        {
            var y = c.TransformToVisual(sv).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            var bottom = y + c.ActualHeight;
            return new RowBox(y, bottom, vp,
                $"row=[top={y:F0} bottom={bottom:F0} vp={vp:F0}]", true);
        }
        catch { return new RowBox(0, 0, vp, "row=[transform-failed]", false); }
    }

    /// <summary>数一数有多少个标签排在了比它所属大类更靠后的大类之后（即跨段乱序）。</summary>
    private int CountOutOfSectionOrder(IReadOnlyList<string> tags)
    {
        var bad = 0;
        var maxSeen = -1;
        foreach (var t in tags)
        {
            var sec = TagSections.IndexOf(TagSections.Classify(t, _vocab));
            if (sec < maxSeen) bad++;
            else maxSeen = sec;
        }
        return bad;
    }

    // ================= 输入框的候选与 Tab 补全 =================

    private void AddTagMenu_Click(object sender, RoutedEventArgs e) => AddTagBox.Focus(FocusState.Programmatic);

    /// <summary>
    /// 把候选铺进下拉列表。列表内容与 Tab 循环走的是同一份
    /// <see cref="TagComplete.Suggest"/> 结果——否则按 Tab 会跳到列表里
    /// 根本看不见的东西上，"看得见的才切得到"这条就不成立了。
    /// </summary>
    private static void ShowCandidates(AutoSuggestBox box, IReadOnlyList<TagComplete.Candidate> list)
    {
        box.ItemsSource = list.Select(TagComplete.Display).ToList();
        box.IsSuggestionListOpen = list.Count > 0;
    }

    /// <summary>
    /// Shift 有没有按着。Shift+Tab 要往回切，而 KeyRoutedEventArgs 里只有
    /// 一个 VirtualKey，修饰键状态得另外问。
    /// </summary>
    private static bool ShiftDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                           .HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// Tab / Shift+Tab 补全。返回 true 表示这次按键被吃掉了（要阻止它继续
    /// 去做焦点切换）。
    ///
    /// 没有候选时返回 false：空输入框里按 Tab 是"跳到下一个控件"，
    /// 拦住它又什么都不做会让 Tab 在这个窗口里失灵。
    /// </summary>
    private bool CompleteFrom(AutoSuggestBox box, TagComplete.Cycler cycle,
                              bool backwards, string? hint = null)
    {
        var step = cycle.Next(box.Text, q => TagComplete.Suggest(q, _vocab), backwards);
        if (step is null)
        {
            TranslateInfoText.Text = box.Text.Trim().Length == 0
                ? "先在输入框里打几个字，再按 Tab 补全。"
                : $"词库里没有和「{box.Text.Trim()}」匹配的标签。";
            return false;
        }

        var s = step.Value;

        // 填字和铺列表都要包在标志里：给候选列表设 ItemsSource 会再触发一次
        // TextChanged，那一下如果被当成"用户改了字"，循环就被清了，
        // 于是连按两次 Tab 卡在同一格。
        _completing = true;
        try
        {
            box.Text = s.Item.Tag;
            ShowCandidates(box, cycle.Items);
        }
        finally { _completing = false; }

        TranslateInfoText.Text = TagComplete.Status(s) + (hint ?? "");
        Log.Write($"complete: '{s.Item.Tag}' {s.Index + 1}/{s.Count} fresh={s.Fresh} back={backwards}");
        return true;
    }

    /// <summary>输入时用词库给候选。中英混输都支持。</summary>
    private void AddTagBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // 循环作废按**内容**判，不按事件判：Tab 填完字之后再给列表设 ItemsSource
        // 还会触发一次 TextChanged，那一下看着像"用户改了字"，真去清循环就会让
        // 连按两次 Tab 卡在同一格上（实测日志里两条 fresh=True 同格）。
        if (!_addCycle.At(sender.Text)) _addCycle.Reset();

        // 只在用户真的打字时给建议；程序性设 Text 会触发一次
        // UserInput 之外的原因（Tab 补全也走这条路），不过滤掉会在
        // 回车加入时又弹出候选框。
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            if (_completing) return;          // 是我们自己填的候选，列表留着别动
            sender.ItemsSource = null;
            sender.IsSuggestionListOpen = false;
            return;
        }

        ShowCandidates(sender, TagComplete.Suggest(sender.Text, _vocab));
    }

    /// <summary>
    /// Tab 补全。Enter 不在这里接——AutoSuggestBox 自己会把它转成
    /// QuerySubmitted，两条路都接会重复触发（虽然 EndEdit / AddTag
    /// 都是幂等的，但没必要留两条入口）。
    /// </summary>
    private void AddTagBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Tab) return;
        if (sender is not AutoSuggestBox box) return;
        if (CompleteFrom(box, _addCycle, ShiftDown())) e.Handled = true;
    }

    /// <summary>从候选里选一个：把候选还原成纯标签填进输入框。</summary>
    private void AddTagBox_SuggestionChosen(AutoSuggestBox sender,
                                            AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        var tag = TagComplete.Bare(args.SelectedItem as string);
        if (tag.Length == 0) return;
        sender.Text = tag;
        _addCycle.SyncTo(tag);      // 接着按 Tab 从这一格往后走，而不是从头重来
    }

    private void AddTagBox_QuerySubmitted(AutoSuggestBox sender,
                                          AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // 用户选了候选就用候选，否则用输入框原文
        var tag = TagComplete.Bare(args.ChosenSuggestion as string);
        if (tag.Length == 0) tag = (args.QueryText ?? "").Trim();
        if (tag.Length > 0) AddTag(tag);
    }

    private void AddTag_Click(object sender, RoutedEventArgs e)
    {
        var text = AddTagBox.Text.Trim();
        if (text.Length == 0) { _ = Info("没有输入", "先在输入框里填一个标签，中文或英文都可以。"); return; }
        AddTag(text);
    }

    /// <summary>
    /// 把一个标签加进当前行。中文会先过词库翻成标签；已经是合法英文标签就原样收下。
    /// 加完按大类归位，所以新标签会出现在它该在的段里，而不是一律追加到末尾。
    /// </summary>
    private void AddTag(string text)
    {
        var (ok, msg) = AddTagCore(text, showDialog: true);
        if (ok) AddTagBox.Text = "";
        TranslateInfoText.Text = msg;
    }

    /// <summary>
    /// 供标签搜索窗口调用。返回的消息由调用方显示——搜索窗口有自己的状态栏，
    /// 写进 TranslateInfoText 的话用户正在看的那个窗口里什么都看不到。
    /// </summary>
    public (bool Ok, string Message) AddTagFromSearch(string tag) => AddTagCore(tag, showDialog: false);

    /// <summary>
    /// 添加标签的核心逻辑。
    ///
    /// <paramref name="showDialog"/> 只控制"没有选中图片"要不要弹对话框：
    /// 主窗口里弹是对的（用户可能没意识到没选图），搜索窗口里弹会把主窗口
    /// 盖住、且消息本身返回给调用方了，所以不弹。
    ///
    /// 返回值语义：<c>Ok</c> 表示"这个标签现在在图片里了"——新加进去和本来就有
    /// 都算 true，只有"没有选中图片"才是 false。
    /// </summary>
    private (bool Ok, string Message) AddTagCore(string text, bool showDialog)
    {
        if (_index < 0 || _index >= _items.Count)
        {
            const string m = "先选一张图再添加标签。";
            if (showDialog) _ = Info("没有选中图片", m);
            return (false, m);
        }

        var tag = text;
        var via = "";

        if (_vocab is not null)
        {
            // 先当英文标签解析，失败再当中文关键词——用户不必自己区分
            var (en, enSt) = _vocab.ResolveEnglish(text);
            if (en is not null && enSt is not ("unknown" or "no-usage"))
            {
                tag = en;
                via = enSt;
            }
            else
            {
                var (cn, cnSt) = _vocab.ResolveChinese(text);
                if (cn is not null) { tag = cn; via = cnSt; }
                else if (en is not null) { tag = en; via = enSt; }
            }
        }

        if (_pendingTags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
            return (true, $"「{tag}」已经在列表里了。");

        _pendingTags.Add(tag);
        // 加完立刻按大类归位：否则新加的标签会落在列表末尾，
        // 和它所属的框分离开，看起来像没生效。
        _pendingTags = TagSections.Canonicalize(_pendingTags, _vocab);

        CommitPrompt();
        SelectTagRow(tag);

        var kind = tag.StartsWith('@') ? "画师" : TagSections.Get(TagSections.Classify(tag, _vocab)).Title;
        Log.Write($"add tag: {tag} via={via} total={_pendingTags.Count}");
        return (true, $"已添加「{tag}」（{kind} 段）" + (via.Length > 0 ? $" · 词库 {via}" : ""));
    }

    /// <summary>
    /// 把某一行滚进视野并选中，方便确认刚加进去的标签在哪。
    ///
    /// <paramref name="alsoFollow"/> 表示"这一行刚从别处搬过来，视线要跟着它走"——
    /// 这时不再用 <c>ScrollIntoView</c>：那个是**同步**的，而列表重建后的位置恢复
    /// （<see cref="RestoreTagScroll"/>）是隔两跳的异步动作，两者在时间上错开，
    /// 结果就是 ScrollIntoView 先滚过去、稍后又被恢复逻辑覆盖（实测
    /// offAfter=1089 之后又被拉回 want=0 的相反情形）。这种行改由
    /// <paramref name="keepVisibleIndex"/> 在重建路径里统一处理。
    /// </summary>
    private void SelectTagRow(string tag, bool alsoFollow = false)
    {
        var row = _tagRows.FirstOrDefault(r => string.Equals(r.Tag, tag, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        TagList.SelectedItems.Clear();
        TagList.SelectedItems.Add(row);
        if (!alsoFollow) TagList.ScrollIntoView(row);
    }

    /// <summary>菜单/按钮：把所有标签按官方大类顺序规整一遍，段内顺序不动。</summary>
    private void SortSections_Click(object sender, RoutedEventArgs e)
    {
        var before = new List<string>(_pendingTags);
        var after = TagSections.Canonicalize(before, _vocab);
        if (before.SequenceEqual(after, StringComparer.Ordinal))
        {
            TranslateInfoText.Text = "大类顺序已经是规范的，无需调整。";
            return;
        }

        _undoPrompt = GetCaptionText();   // 复用「撤销翻译」这一条后悔路径
        _pendingTags = after;
        CommitPrompt();
        UndoTranslateBtn.IsEnabled = true;
        UndoMenuItem.IsEnabled = true;

        // 数一下有多少个标签换了位置，让用户知道动了多少
        var moved = 0;
        for (var i = 0; i < Math.Min(before.Count, after.Count); i++)
            if (!string.Equals(before[i], after[i], StringComparison.Ordinal)) moved++;

        TranslateInfoText.Text = $"已按官方大类顺序规整（{moved} 个标签换位，段内顺序未动）。";
        Log.Write($"canonicalize: {before.Count} tags, {moved} moved");
    }

    // ================= 导航 =================

    private void Navigate(int delta)
    {
        if (_items.Count == 0) return;
        var next = _index + delta;
        if (next < 0 || next >= _items.Count) return;
        SaveCurrent(showDialog: false);   // 静默保存，避免切图丢改动
        Select(next);
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Navigate(1);

    // ================= 保存 =================

    private bool SaveCurrent(bool showDialog)
    {
        if (_index < 0 || _index >= _items.Count) return false;
        var item = _items[_index];

        // 从输入框重新解析（用户可能直接改过正文）。必须用 PromptText 而不是
        // 只切逗号：提示词里可能带一段自然语言尾巴，那些段不是标签，
        // 不能写进 _pendingTags，也不能在保存时被当成标签重排。
        var parsed = PromptText.Parse(GetCaptionText(), _vocab);
        var tags = parsed.Tags;
        _proseTail = parsed.ProseTail;
        _pendingTags = new List<string>(tags);

        try
        {
            if (item.Caption is null)
            {
                // 原本没有 .txt：新建时沿用训练集既有的字节风格
                // （实测 53 个文件均为 无 BOM + CRLF + 结尾换行 + ", " 分隔）
                item.Caption = new CaptionFile
                {
                    Path = item.CaptionPath,
                    HasBom = false,
                    NewLine = "\r\n",
                    TrailingNewLine = true,
                    Separator = ", "
                };
            }

            // 关键：写回时要把散文尾巴带上。
            // 以前这里是 item.Caption.Save(tags)——只写标签。提示词只有标签时没问题，
            // 但一旦带自然语言（官方写法：标签 + 句号 + 散文），那样保存会**把散文
            // 整段删掉**，而且是静默的：列表看不出变化，.txt 里那段英文没了。
            // 用 PromptText.Rebuild 拼回"标签区 + 尾巴"，尾巴按原文切片，逐字节还原。
            var body = PromptText.Rebuild(tags, parsed.ProseTail, item.Caption.Separator);
            var changed = item.Caption.SaveBody(body);
            if (changed)
            {
                Log.Write($"saved {Path.GetFileName(item.CaptionPath)} " +
                          $"tags={tags.Count} prose={parsed.ProseTail.Length}");
                RefreshTagRows();
                RefreshRowStatus(_index);   // 左栏那一行的「已标注 / N tags」要立刻跟上
                UpdateStats();
            }

            if (showDialog)
                _ = Info("已保存", changed
                    ? $"已写入 {Path.GetFileName(item.CaptionPath)}（{tags.Count} tags" +
                      (parsed.HasProse ? " + 自然语言）" : "）")
                    : "内容没有变化，未写入文件。");
            return changed;
        }
        catch (Exception ex)
        {
            Log.Write("save FAILED: " + ex);
            _ = Info("保存失败", ex.Message);
            return false;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveCurrent(showDialog: true);

    // ================= 工具条 =================

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            _cfg.DatasetDir = folder.Path;
            _cfg.PushRecent(folder.Path);
            _cfg.Save();
            LoadFolder(folder.Path);
        }
        catch (Exception ex)
        {
            Log.Write("open folder failed: " + ex);
            await Info("打开失败", ex.Message);
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        LoadVocab();
        LoadFolder(_cfg.DatasetDir,
                   _index >= 0 && _index < _items.Count ? _items[_index].Name : null);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void JumpFirst_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count > 0) { SaveCurrent(showDialog: false); Select(0); }
    }

    /// <summary>跳到下一个还没有 .txt 的图片；已经在最后一张时给个明确反馈。</summary>
    private void JumpUnlabeled_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;
        for (var step = 1; step <= _items.Count; step++)
        {
            var i = (_index + step) % _items.Count;
            if (!_items[i].HasCaption)
            {
                SaveCurrent(showDialog: false);
                Select(i);
                return;
            }
        }
        _ = Info("都标完了", $"这 {_items.Count} 张图都已有 caption。");
    }

    // ================= 菜单：工具 =================

    private void AppendModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // 菜单项与翻译区的勾选框是同一个开关，保持两处一致
        AppendModeBox.IsChecked = AppendModeMenuItem.IsChecked;
    }

    private void AppendModeBox_Click(object sender, RoutedEventArgs e)
        => AppendModeMenuItem.IsChecked = AppendModeBox.IsChecked == true;

    private async void ShowStats_Click(object sender, RoutedEventArgs e)
    {
        var st = Dataset.ComputeStats(_items, _vocab);
        var sb = new StringBuilder();
        sb.AppendLine($"目录：{_cfg.DatasetDir}");
        sb.AppendLine();
        sb.AppendLine($"图片总数      {st.Total}");
        sb.AppendLine($"已有 caption  {st.Captioned}");
        sb.AppendLine($"尚未标注      {st.Total - st.Captioned}");
        sb.AppendLine($"标签实例      {st.TagInstances:N0}");
        sb.AppendLine($"有校验问题    {st.WithIssues}");
        sb.AppendLine();

        if (_vocab is not null && st.Captioned > 0)
        {
            // 汇总全目录的标签使用频次与校验问题，便于决定先修哪一类
            var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var issues = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var it in _items.Where(i => i.HasCaption))
            {
                var tags = CaptionFile.Load(it.CaptionPath).Tags;
                foreach (var t in tags) freq[t] = freq.TryGetValue(t, out var c) ? c + 1 : 1;
                foreach (var i in VocabDb.AuditTags(tags, _vocab))
                {
                    var k = i.Use is null ? $"{i.Tag}  ({i.Status})" : $"{i.Tag}  →  {i.Use}";
                    issues[k] = issues.TryGetValue(k, out var c) ? c + 1 : 1;
                }
            }

            sb.AppendLine($"不同标签      {freq.Count}");
            sb.AppendLine();
            sb.AppendLine("最常用的 15 个：");
            foreach (var kv in freq.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(15))
                sb.AppendLine($"  {kv.Value,4}×  {kv.Key}");

            if (issues.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"待确认标签（按出现次数）：");
                foreach (var kv in issues.OrderByDescending(k => k.Value).Take(20))
                    sb.AppendLine($"  {kv.Value,4}×  {kv.Key}");
                if (issues.Count > 20) sb.AppendLine($"  … 其余 {issues.Count - 20} 种省略");
            }

            // 安全标签分布：全目录一次算清，便于发现"整体偏严"这类系统性问题
            var dist = new Dictionary<string, int>(StringComparer.Ordinal);
            var diverge = new List<string>();
            foreach (var it in _items.Where(i => i.HasCaption))
            {
                var v = SafetyRating.Check(CaptionFile.Load(it.CaptionPath).Tags);
                var k = v.Declared ?? "(缺失)";
                dist[k] = dist.TryGetValue(k, out var c) ? c + 1 : 1;
                if (v.Notes.Count > 0)
                    diverge.Add($"{it.Name}: {v.Declared ?? "缺失"} → 判据 {v.Expected}");
            }
            sb.AppendLine();
            sb.AppendLine("安全标签分布：");
            foreach (var k in SafetyRating.Ladder.Concat(new[] { "(缺失)" }))
                if (dist.TryGetValue(k, out var c))
                    sb.AppendLine($"  {k,-10} {c,3} 张");
            if (dist.TryGetValue("safe", out var ns) && ns == 0)
                sb.AppendLine("  ⚠ `safe` 一档为空 —— 模型没有\"安全样貌\"的样例");
            if (diverge.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"与 Danbooru 判据不一致 {diverge.Count} 处（多为整体偏严，见帮助文档 08）：");
                foreach (var d in diverge.Take(12)) sb.AppendLine("  " + d);
                if (diverge.Count > 12) sb.AppendLine($"  … 其余 {diverge.Count - 12} 处省略");
            }
        }

        await Info("标签统计", sb.ToString());
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
        => OpenInShell(Log.FilePath);

    private void OpenAppFolder_Click(object sender, RoutedEventArgs e)
        => OpenInShell(AppContext.BaseDirectory);

    private async void OpenInShell(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                await Info("找不到", path);
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            await Info("打开失败", ex.Message);
        }
    }

    // ================= 菜单：帮助 =================

    /// <summary>
    /// 打开帮助窗口。正文来自磁盘上的 Markdown（见 <see cref="HelpDocs"/>），
    /// 改文档不必重新编译、也不必重启程序。
    ///
    /// 独立窗口而不是模态对话框：可以一边看帮助一边操作主窗口——帮助里讲的多半
    /// 正是"下一步该点哪里"，模态会把主窗口锁住，恰好挡住要看的东西。
    /// </summary>
    private void ShowHelp()
    {
        try
        {
            if (_help is null)
            {
                var w = new HelpWindow();
                w.Closed2 += () =>
                {
                    try { w.SaveBounds(); }
                    catch (Exception ex) { Log.Write("help save bounds failed: " + ex.Message); }
                    _help = null;
                };
                _help = w;
            }

            // 一个模块都没找到时窗口自己会显示说明页和"复制到用户目录"按钮，
            // 所以这里不当作失败。
            _help.Open(_cfg);
            _help.Activate();
            Log.Write("help window opened");
        }
        catch (Exception ex)
        {
            Log.Write("help window FAILED: " + ex);
            _ = Info("帮助打不开", ex.Message);
        }
    }

    private void ShowHelp_Click(object sender, RoutedEventArgs e) => ShowHelp();

    // ================= 菜单：标签搜索 =================

    /// <summary>
    /// 打开标签搜索窗口。
    ///
    /// 独立窗口而不是对话框：搜到标签之后要做的下一件事就是把它加进当前图片，
    /// 模态会把主窗口锁住，恰好挡住要看的东西。
    ///
    /// 两个回调都是**实时**的，不是打开时的快照：词库可能在窗口开着的时候
    /// 重新载入，标签列表也在随时变（用户可能一边搜一边在中间栏删标签）。
    /// </summary>
    private void ShowTagSearch()
    {
        try
        {
            if (_searchWin is null)
            {
                var w = new TagSearchWindow();
                w.Closed2 += () =>
                {
                    try { w.SaveBounds(); }
                    catch (Exception ex) { Log.Write("tag search save bounds failed: " + ex.Message); }
                    _searchWin = null;
                };
                _searchWin = w;
            }

            _searchWin.Open(_cfg, _vocab,
                            () => _pendingTags,
                            AddTagFromSearch);
            _searchWin.Activate();
            Log.Write("tag search window opened");
        }
        catch (Exception ex)
        {
            Log.Write("tag search window FAILED: " + ex);
            _ = Info("搜索窗口打不开", ex.Message);
        }
    }

    private void TagSearch_Click(object sender, RoutedEventArgs e) => ShowTagSearch();

    private async void ShowAbout_Click(object sender, RoutedEventArgs e)
    {
        var root = new StackPanel();

        void Line(string t, double top = 0) => root.Children.Add(new TextBlock
        {
            Text = t,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, top, 0, 0)
        });

        // 路径单独用只读 TextBox：这两条路径又长又要能复制（要拿去资源管理器打开），
        // TextBlock 既选不中也复制不了。
        void PathRow(string label, string path)
        {
            root.Children.Add(new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 10, 0, 2),
                Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"]
                             as Microsoft.UI.Xaml.Media.Brush
            });
            root.Children.Add(new TextBox
            {
                Text = path,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap
            });
        }

        Line("AnimaCaptioner");
        Line("Anima 训练集标注工具");
        Line("为 sayori-style 数据集做的本地标注器：图片、tag、中文描述三栏并排。", 10);
        Line("标签来源：Anima 官方索引 + Danbooru 中文对照表 + 人工整理的映射。", 10);
        PathRow("配置文件", AppSettings.FilePath);
        PathRow("日志", Log.FilePath);

        await Info("关于", root);
    }

    // ================= Tag 操作 =================

    private void TagUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void TagDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        // 与删除同理：Ctrl+↑/↓ 是窗口级加速键，打字时不该动标签。
        if (TextInputFocused())
        {
            TranslateInfoText.Text = "焦点在输入框里，Ctrl+↑/↓ 归输入框用。";
            return;
        }

        // 散文那一行不是标签（PendingIndex = -1），选中它时 TagOrder 会把它过滤掉；
        // 但如果选中的**只有**散文行，那就是"什么都没动"，要给明确的说法，
        // 否则用户会以为按钮坏了。
        var sel = TagList.SelectedItems.Cast<TagItem>().Where(r => r.PendingIndex >= 0).ToList();
        if (sel.Count == 0)
        {
            TranslateInfoText.Text = TagList.SelectedItems.Count > 0
                ? "自然语言那段不是标签，不参与排序（它固定在末尾）。"
                : "先选中要移动的标签。";
            return;
        }

        // 列表是"按大类重排后"显示的，所以要用每行的 PendingIndex 换算回
        // _pendingTags 的下标，不能用显示下标——两者在大类交错时并不相同。
        var res = TagOrder.Move(_pendingTags, sel.Select(s => s.PendingIndex), delta, _vocab);
        if (res.Moved == 0)
        {
            TranslateInfoText.Text = res.Blocked > 0
                ? "已经在本段边界上（上移/下移只在同一大类内部调整）。"
                : "没有可移动的标签。";
            Log.Write($"move delta={delta}: nothing moved (blocked={res.Blocked})");
            return;
        }

        _pendingTags = res.Tags;
        var keep = res.MovedTo.Select(i => _pendingTags[i]).ToList();

        // 列表会被整体重建，所以重建前记下的滚动位置会被放回去（见 RefreshTagRows），
        // 再点名"被移动的那一行"要留在视野里：它被带到视口上/下边缘之外时，列表
        // 跟着翻一页，让它停在最上/最下一行——而不是让视线被甩回列表开头。
        //
        // 取 MovedTo 首项而不是末项：delta<0 时它是升序、delta>0 时是降序，
        // 所以首项恰好就是最可能先出屏幕的那一个（上移取最上、下移取最下）。
        CommitPrompt(keepVisibleIndex: res.MovedTo.Count > 0 ? res.MovedTo[0] : -1);

        TagList.SelectedItems.Clear();
        foreach (var t in keep)
        {
            var row = _tagRows.FirstOrDefault(r => string.Equals(r.Tag, t, StringComparison.OrdinalIgnoreCase));
            if (row is not null) TagList.SelectedItems.Add(row);
        }

        TranslateInfoText.Text = res.Blocked > 0
            ? $"移动了 {res.Moved} 个；{res.Blocked} 个已在本段边界，未跨段。"
            : $"移动了 {res.Moved} 个。";
        Log.Write($"move delta={delta}: moved={res.Moved} blocked={res.Blocked} " +
                  $"-> [{string.Join(", ", _pendingTags.Take(8))}...]");
    }

    private void TagDelete_Click(object sender, RoutedEventArgs e) => DeleteSelectedTags();

    /// <summary>
    /// 删除选中的标签。按钮和右键菜单都走这里，避免两处各写一份。
    /// 打字时不动手：Del 是全局加速键（挂在菜单项上），没有这道闸的话
    /// 在提示词框或行内编辑框里按 Del 会连带删掉一个标签。
    /// </summary>
    private void DeleteSelectedTags()
    {
        if (TextInputFocused())
        {
            TranslateInfoText.Text = "焦点在输入框里，Del 归输入框用。";
            return;
        }

        // 散文行没有 PendingIndex，选中它时不能当成"删一个标签"——
        // 按 -1 去删会把列表最后一个标签删掉。
        var sel = TagList.SelectedItems.Cast<TagItem>().Where(r => r.PendingIndex >= 0).ToList();
        if (sel.Count == 0)
        {
            TranslateInfoText.Text = TagList.SelectedItems.Count > 0
                ? "自然语言那段不是标签，删不掉；要清空它请直接改右上的提示词原文。"
                : "先选中要删除的标签。";
            return;
        }

        // 按 PendingIndex 删：列表显示顺序已被大类重排，用显示下标会删错行。
        var before = _pendingTags.Count;
        _pendingTags = TagOrder.Delete(_pendingTags, sel.Select(s => s.PendingIndex));
        var removed = before - _pendingTags.Count;

        CommitPrompt();
        TranslateInfoText.Text = $"已删除 {removed} 个标签。";
        Log.Write($"delete: removed={removed} remaining={_pendingTags.Count}");
    }

    private async void TagAudit_Click(object sender, RoutedEventArgs e)
    {
        if (_vocab is null) { await Info("词库未加载", "无法校验。请到设置里确认词库路径后点「重新载入」。"); return; }
        var issues = VocabDb.AuditTags(_pendingTags, _vocab);

        // 安全标签单独一段：它不查词库，而是按 Danbooru 官方评级判据核对档位。
        // 详见 docs/08-safety-ratings.md。这里只报告，绝不自动改写。
        var verdict = SafetyRating.Check(_pendingTags);
        var sec = new StringBuilder();
        sec.Append("安全标签：");
        sec.Append(verdict.Declared is null ? "缺失" : verdict.Declared);
        if (verdict.Notes.Count > 0)
        {
            sec.AppendLine();
            foreach (var n in verdict.Notes) sec.Append("• ").AppendLine(n);
            if (verdict.Evidence.Count > 0)
                sec.Append("  判据命中：").AppendLine(string.Join(", ", verdict.Evidence.Take(8)));
        }
        else sec.AppendLine("（与判据一致）");

        if (issues.Count == 0 && verdict.Notes.Count == 0)
        {
            await Info("校验通过", $"{_pendingTags.Count} 个标签全部有效。\n\n" + sec);
            return;
        }

        var sb = new StringBuilder();
        if (issues.Count > 0)
        {
            sb.AppendLine($"{issues.Count} 个标签需确认：");
            sb.AppendLine();
            foreach (var i in issues.Take(60))
            {
                sb.Append("• ").Append(i.Tag);
                if (i.Use is not null) sb.Append("  →  ").Append(i.Use);
                else sb.Append("  (").Append(i.Status).Append(')');
                sb.AppendLine();
            }
            if (issues.Count > 60) sb.AppendLine($"… 其余 {issues.Count - 60} 个省略");
            sb.AppendLine();
        }
        sb.Append(sec);
        await Info("校验结果", sb.ToString());
    }

    // ================= 标签行的右键菜单 / 就地编辑 =================

    /// <summary>
    /// 焦点是否落在文本输入控件里。用来挡住那些挂在菜单项上的全局加速键
    /// （Del、Ctrl+↑/↓）——它们在打字时不该生效。
    ///
    /// 这不是吹毛求疵：菜单里的 Del 是**窗口级**加速键，而列表子树里的
    /// 行内编辑框、提示词框（RichEditBox）、中文描述框都可能是焦点所在。
    /// 没有这道闸，在提示词框里按 Del 会连带删掉一个标签。
    /// </summary>
    private bool TextInputFocused()
    {
        var f = FocusManager.GetFocusedElement(Content.XamlRoot);
        return f is TextBox or RichEditBox or AutoSuggestBox;
    }

    /// <summary>从事件源往上找承载它的那一行。虚拟化后行可能没有实体容器，所以拿不到就返回 null。</summary>
    private static TagItem? RowOf(object? source)
    {
        var fe = source as FrameworkElement;
        while (fe is not null)
        {
            if (fe.DataContext is TagItem t) return t;
            fe = VisualTreeHelper.GetParent(fe) as FrameworkElement;
        }
        return null;
    }

    /// <summary>
    /// 右键：定位目标行、把它选上（不打断已有的多选），再在指针位置弹菜单。
    ///
    /// 菜单项是按"当前选中"来做的，所以右键必须先建立选中——
    /// 否则用户在未选中的行上右键，菜单操作的会是别的标签。
    /// 点在空白处则保留原有选中，让菜单对整批选中项生效。
    /// </summary>
    private void TagList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var row = RowOf(e.OriginalSource);

        // 正在编辑时先收起编辑框：右键会让它失焦，而"失焦即提交"的路径
        // 和这里重复触发会读到一个已经被收起、内容不可靠的输入框。
        // 先显式收掉，后续的 LostFocus 因为 _editingRow 已置空而自然短路。
        if (_editingRow is not null) EndEdit(commit: true);

        if (row is not null && !TagList.SelectedItems.Contains(row))
        {
            TagList.SelectedItems.Clear();
            TagList.SelectedItems.Add(row);
        }

        if (TagList.SelectedItems.Count == 0)
        {
            e.Handled = true;
            return;
        }

        var menu = BuildTagMenu(row);
        var pt = e.GetPosition(TagList);
        menu.ShowAt(TagList, new FlyoutShowOptions { Position = pt });
        e.Handled = true;

        Log.Write($"tag context menu: row='{row?.Tag ?? "(blank)"}' " +
                  $"selected={TagList.SelectedItems.Count} at={pt.X:F0},{pt.Y:F0}");
    }

    private MenuFlyout BuildTagMenu(TagItem? row)
    {
        var menu = new MenuFlyout();

        var sel = TagList.SelectedItems.Cast<TagItem>().Where(r => r.PendingIndex >= 0).ToList();
        var isProse = row is not null && row.PendingIndex < 0;
        var n = sel.Count;

        // 组 1：剪贴板。多选时措辞要如实说明操作的是几个。
        var copy = new MenuFlyoutItem
        {
            Text = n > 1 ? $"复制（{n} 个标签）" : "复制",
            Icon = new SymbolIcon(Symbol.Copy),
            KeyboardAcceleratorTextOverride = "Ctrl+C",
        };
        copy.Click += (_, _) => CopySelectedTags();
        menu.Items.Add(copy);

        var paste = new MenuFlyoutItem
        {
            Text = "粘贴",
            Icon = new SymbolIcon(Symbol.Paste),
            KeyboardAcceleratorTextOverride = "Ctrl+V",
        };
        paste.Click += async (_, _) => await PasteTagsAsync();
        menu.Items.Add(paste);

        menu.Items.Add(new MenuFlyoutSeparator());

        // 组 2：编辑。散文那段不是标签，这些项对它没有意义，直接禁用并说明。
        var edit = new MenuFlyoutItem
        {
            Text = "编辑标签…",
            Icon = new SymbolIcon(Symbol.Edit),
            KeyboardAcceleratorTextOverride = "F2",
            IsEnabled = n == 1 && !isProse,
        };
        edit.Click += (_, _) => BeginEdit(sel[0]);
        menu.Items.Add(edit);

        var norm = new MenuFlyoutItem
        {
            Text = n > 1 ? $"翻译 / 规范化（{n} 个）" : "翻译 / 规范化",
            Icon = new SymbolIcon(Symbol.Refresh),
            IsEnabled = n > 0,
        };
        norm.Click += (_, _) => NormalizeSelectedTags();
        menu.Items.Add(norm);

        // 组 2b：模型翻译。只对"词库没有中文"的标签有意义——词库已经给了
        // 官方释义时，再让模型译一遍只会引入一个可能更差的版本。
        var noCn = sel.Where(r => _vocab?.LookupCn(r.Tag) is not { Length: > 0 }).ToList();
        var modelGloss = new MenuFlyoutItem
        {
            Text = noCn.Count == 1
                ? "模型翻译（补中文释义）…"
                : noCn.Count > 1
                    ? $"模型翻译（{noCn.Count} 个缺中文的标签）…"
                    : "模型翻译（这些标签词库已有中文）",
            Icon = new SymbolIcon(Symbol.Character),
            IsEnabled = noCn.Count > 0,
        };
        ToolTipService.SetToolTip(modelGloss, noCn.Count > 0
            ? "这些标签在词库里查不到中文，让模型补一个。结果是推断，会标为「模型译 · 非官方」。"
            : "选中的标签词库里都已有中文释义，无需模型翻译。");
        modelGloss.Click += async (_, _) => await ModelTranslateAsync(noCn.Select(r => r.Tag).ToList());
        menu.Items.Add(modelGloss);

        menu.Items.Add(new MenuFlyoutSeparator());

        // 组 3：结构编辑，复用按钮行那套已验证的逻辑
        var up = new MenuFlyoutItem
        {
            Text = "上移",
            KeyboardAcceleratorTextOverride = "Ctrl+↑",
            IsEnabled = n > 0,
        };
        up.Click += (_, _) => MoveSelected(-1);
        menu.Items.Add(up);

        var down = new MenuFlyoutItem
        {
            Text = "下移",
            KeyboardAcceleratorTextOverride = "Ctrl+↓",
            IsEnabled = n > 0,
        };
        down.Click += (_, _) => MoveSelected(1);
        menu.Items.Add(down);

        var del = new MenuFlyoutItem
        {
            Text = n > 1 ? $"删除（{n} 个）" : "删除",
            Icon = new SymbolIcon(Symbol.Delete),
            KeyboardAcceleratorTextOverride = "Del",
            IsEnabled = n > 0,
        };
        del.Click += (_, _) => DeleteSelectedTags();
        menu.Items.Add(del);

        menu.Items.Add(new MenuFlyoutSeparator());

        // 组 4：查看。对散文也有意义（它是自由文本，只能整段看/改）。
        var audit = new MenuFlyoutItem
        {
            Text = "校验整行",
            IsEnabled = _pendingTags.Count > 0,
        };
        audit.Click += (_, _) => TagAudit_Click(this, new RoutedEventArgs());
        menu.Items.Add(audit);

        if (isProse)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(new MenuFlyoutItem
            {
                Text = "这是自然语言段，不是标签（不可编辑/复制为标签）",
                IsEnabled = false,
            });
        }

        return menu;
    }

    // ---- 复制 / 粘贴 ----

    private void CopySelectedTags()
    {
        var sel = TagList.SelectedItems.Cast<TagItem>().Where(r => r.PendingIndex >= 0).ToList();
        if (sel.Count == 0) { TranslateInfoText.Text = "没有可复制的标签。"; return; }

        // 按列表里的可见顺序复制，不是选中顺序——用户看到的顺序就是他期望的
        var text = string.Join(", ", sel.OrderBy(r => _tagRows.IndexOf(r)).Select(r => r.Tag));

        try
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
            Clipboard.Flush();   // 不 Flush 的话程序退出后剪贴板内容会丢
            TranslateInfoText.Text = sel.Count > 1
                ? $"已复制 {sel.Count} 个标签。"
                : $"已复制「{text}」。";
            Log.Write($"tag copy: n={sel.Count} text='{text}'");
        }
        catch (Exception ex)
        {
            Log.Write("tag copy failed: " + ex.Message);
            TranslateInfoText.Text = "复制失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 粘贴：把剪贴板里的文本解析成标签追加到当前行。
    ///
    /// 多行文本先折成逗号——从表格/文件里复制出来的一列标签是换行分隔的，
    /// 而解析器只认逗号，不折的话整列会变成一个带换行的"标签"。
    /// 自然语言部分会被解析器摘掉：粘一整行提示词回来时，散文不该变成一堆标签。
    /// </summary>
    private async Task PasteTagsAsync()
    {
        if (_index < 0 || _index >= _items.Count)
        {
            TranslateInfoText.Text = "先选一张图再粘贴标签。";
            return;
        }

        string text;
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text))
            {
                TranslateInfoText.Text = "剪贴板里没有文本。";
                return;
            }
            text = await view.GetTextAsync();
        }
        catch (Exception ex)
        {
            Log.Write("tag paste read failed: " + ex.Message);
            TranslateInfoText.Text = "读剪贴板失败：" + ex.Message;
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            TranslateInfoText.Text = "剪贴板里没有文本。";
            return;
        }

        var res = TagEdit.FromPaste(text, _pendingTags, _vocab);

        if (res.Tags.Count == 0)
        {
            TranslateInfoText.Text = res.ProseOnly
                ? "剪贴板里是一段自然语言，不是标签。要加散文请粘到右上的提示词原文里。"
                : "剪贴板里没有可识别的标签。";
            return;
        }

        _pendingTags.AddRange(res.Tags);
        CommitPrompt();
        SelectTagRow(res.Tags[^1]);

        var skipped = res.Skipped > 0 ? $"，跳过 {res.Skipped} 个（重复或为空）" : "";
        TranslateInfoText.Text = $"已粘贴 {res.Tags.Count} 个标签{skipped}。";
        Log.Write($"tag paste: added={res.Tags.Count} skipped={res.Skipped} " +
                  $"prose={res.ProseTail.Length} -> {string.Join(", ", res.Tags)}");
    }

    // ---- 翻译 / 规范化 ----

    /// <summary>
    /// 把选中标签的写法规范成词库认可的形式：中文→英文标签、去下划线、
    /// 补画师 @、旧别名→正典名。
    ///
    /// 认不出来的一律**原样保留**——词库不是全集（实测 679 个真实标签里 74 个
    /// 词库不认），静默丢掉会悄悄改掉训练集。所以这里只报告"哪些没动"，
    /// 而不是替用户决定。
    /// </summary>
    private void NormalizeSelectedTags()
    {
        var sel = TagList.SelectedItems.Cast<TagItem>().Where(r => r.PendingIndex >= 0).ToList();
        if (sel.Count == 0) { TranslateInfoText.Text = "先选中要规范的标签。"; return; }

        var idx = sel.Select(r => r.PendingIndex).Where(i => i >= 0 && i < _pendingTags.Count)
                     .Distinct().OrderBy(i => i).ToList();
        if (idx.Count == 0) { TranslateInfoText.Text = "没有可规范的标签。"; return; }

        // 逐个按"其余标签"防重：一次改多个时，后面的要看到前面已改好的结果。
        var work = new List<string>(_pendingTags);
        var changed = new List<(string From, string To)>();
        var unchanged = new List<string>();
        var dup = new List<string>();

        // 还没轮到的那些下标：它们的当前写法即将被替换掉，不该拿旧写法来挡重复。
        // 轮到自己时从集合里摘掉，于是"已经改好的"会参与防重，"还没改的"不会。
        var pending = new HashSet<int>(idx);

        foreach (var i in idx)
        {
            pending.Remove(i);
            var old = work[i];

            var others = new List<string>(work.Count);
            for (var k = 0; k < work.Count; k++)
                if (k != i && !pending.Contains(k))
                    others.Add(work[k]);

            var r = TagEdit.NormalizeOne(old, old, others, _vocab);

            if (!r.Ok)
            {
                if (r.Status == "duplicate") dup.Add(r.Message);
                else unchanged.Add(old);
                continue;
            }

            work[i] = r.Tag;
            changed.Add((old, r.Tag));
        }

        if (changed.Count == 0)
        {
            TranslateInfoText.Text = dup.Count > 0
                ? dup[0]
                : $"选中的 {idx.Count} 个标签已经是规范形式。";
            return;
        }

        _pendingTags = work;
        CommitPrompt();

        var keep = changed.Select(c => c.To).ToList();
        TagList.SelectedItems.Clear();
        foreach (var t in keep)
        {
            var row = _tagRows.FirstOrDefault(r =>
                string.Equals(r.Tag, t, StringComparison.OrdinalIgnoreCase));
            if (row is not null) TagList.SelectedItems.Add(row);
        }

        var tail = new List<string>();
        if (unchanged.Count > 0) tail.Add($"{unchanged.Count} 个已是规范形式");
        if (dup.Count > 0) tail.Add($"{dup.Count} 个撞名未改");

        TranslateInfoText.Text = $"规范了 {changed.Count} 个标签" +
            (tail.Count > 0 ? $"（{string.Join("，", tail)}）" : "") + "。";
        Log.Write($"tag normalize: changed={changed.Count} unchanged={unchanged.Count} " +
                  $"dup={dup.Count} pairs=[{string.Join("; ", changed.Select(c => c.From + "->" + c.To))}]");
    }

    // ---- 模型翻译（补词库没有的中文释义）----

    /// <summary>
    /// 菜单入口：对选中的、词库查不到中文的标签做模型补译。
    /// 没选中任何标签时退化成"当前行里所有缺中文的标签"——那才是常见意图，
    /// 因为用户通常是看到右栏一片空白才想补的。
    /// </summary>
    private async void ModelGlossMenu_Click(object sender, RoutedEventArgs e)
    {
        var sel = TagList.SelectedItems.Cast<TagItem>().Where(r => r.PendingIndex >= 0).ToList();
        var pool = sel.Count > 0 ? sel.Select(r => r.Tag).ToList() : new List<string>(_pendingTags);

        var missing = pool
            .Where(t => (_vocab?.LookupCn(t) is not { Length: > 0 }))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0)
        {
            TranslateInfoText.Text = sel.Count > 0
                ? "选中的标签词库里都已有中文释义。"
                : "当前这一行没有缺中文的标签。";
            return;
        }

        await ModelTranslateAsync(missing);
    }

    /// <summary>
    /// 让模型给一批"词库查不到中文"的标签补释义。
    ///
    /// 三条刻意的约束：
    /// - **只处理缺中文的标签**。词库已有官方释义时再让模型译一遍，
    ///   只会得到一个可能更差的版本，而且会诱导用户去信它。
    /// - **逐个串行**。本地 8B 一次只跑一个请求，并发只会互相抢显存。
    /// - **弃权也如实报告**。模型答 UNKNOWN 时明说"无法确定"，
    ///   而不是留一条空记录让用户以为工具没跑。
    /// </summary>
    private async Task ModelTranslateAsync(List<string> tags)
    {
        if (tags.Count == 0) { TranslateInfoText.Text = "没有需要补译的标签。"; return; }
        if (string.IsNullOrWhiteSpace(_cfg.ApiBaseUrl))
        {
            TranslateInfoText.Text = "先在「设置」里配置模型接口。";
            return;
        }

        _tagTranslator ??= new TagTranslator(_llm, _search);

        BusyRing.IsActive = true;
        UndoTranslateBtn.IsEnabled = false;

        var done = 0;
        var unknown = new List<string>();
        var failed = new List<string>();
        var searched = 0;

        try
        {
            for (var i = 0; i < tags.Count; i++)
            {
                var tag = tags[i];
                TranslateInfoText.Text = $"模型翻译中… {i + 1}/{tags.Count}：{tag}";
                Log.Write($"model gloss begin: '{tag}'");

                var r = await _tagTranslator.TranslateAsync(_cfg, tag);
                if (!r.Ok)
                {
                    failed.Add($"{tag}（{r.Error}）");
                    Log.Write($"model gloss FAILED '{tag}': {r.Error}");
                    continue;
                }

                if (r.Unknown)
                {
                    unknown.Add(tag);
                    Log.Write($"model gloss UNKNOWN '{tag}' rounds={r.Rounds} searched={r.Searched}");
                    continue;
                }

                _gloss.Put(tag, r.Cn, _cfg.Model, r.Searched);
                if (r.Searched) searched++;
                done++;
                Log.Write($"model gloss ok: '{tag}' -> '{r.Cn}' rounds={r.Rounds} " +
                          $"searched={r.Searched} note={r.Note}");
            }
        }
        finally
        {
            BusyRing.IsActive = false;
            _gloss.Save();
            RefreshTagRows();
        }

        var parts = new List<string>();
        if (done > 0) parts.Add($"补译 {done} 个" + (searched > 0 ? $"（{searched} 个联网核对过）" : ""));
        if (unknown.Count > 0) parts.Add($"{unknown.Count} 个模型也无法确定");
        if (failed.Count > 0) parts.Add($"{failed.Count} 个失败");

        TranslateInfoText.Text = parts.Count > 0
            ? "模型翻译：" + string.Join("，", parts) + "。带「≈」的是模型译文，不是词库官方释义。"
            : "没有产生新译文。";

        if (failed.Count > 0)
            await Info("模型翻译部分失败",
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = string.Join("\n", failed) +
                           "\n\n常见原因：模型服务未启动、超时过短、或 max_tokens 太小" +
                           "（思考链会先吃掉预算）。",
                });
    }

    // ---- 就地编辑 ----

    /// <summary>当前正在编辑的那一行。退出编辑、切图、重建列表时都要清掉。</summary>
    private TagItem? _editingRow;

    /// <summary>
    /// 正在编辑的输入框。存引用而不是每次去可视树里找：
    /// 行被虚拟化回收后容器就找不到了，那时用户已经敲进去的内容会读不着，
    /// 提交时会退化成"没有改动"——把用户的输入静默丢掉。
    /// 持有引用则即使容器被回收，Text 仍然读得到。
    /// </summary>
    private AutoSuggestBox? _editingBox;

    /// <summary>Esc 取消时回到这个值，避免"改了一半又反悔"变成提交。</summary>
    private bool _editCancelled;

    private void TagList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var row = RowOf(e.OriginalSource);
        if (row is null) return;

        // 点在大类条带上时不要进编辑——条带不是标签行
        if (row.PendingIndex < 0)
        {
            TranslateInfoText.Text = "自然语言那段不是标签，不能就地编辑；请改右上的提示词原文。";
            return;
        }

        BeginEdit(row);
        e.Handled = true;
    }

    /// <summary>
    /// 进入就地编辑。
    ///
    /// 焦点不能在这里直接抓：此刻输入框刚从 Collapsed 变 Visible，还没参与布局，
    /// Focus 会被静默忽略（这个坑在预览窗口的 ChangeView 上踩过）。
    /// 所以先 UpdateLayout 再聚焦。
    /// </summary>
    private void BeginEdit(TagItem row)
    {
        if (!row.CanEdit) return;

        if (_editingRow is not null && !ReferenceEquals(_editingRow, row)) EndEdit(commit: true);

        _editingRow = row;
        _editCancelled = false;
        row.IsEditing = true;

        // 选中它，让"我在编辑哪一行"这件事在视觉上明确
        TagList.SelectedItems.Clear();
        TagList.SelectedItems.Add(row);
        TagList.ScrollIntoView(row);

        TagList.UpdateLayout();
        var box = FindEditBox(row);
        if (box is null)
        {
            // 行已滚出视口（虚拟化没实体化容器）时拿不到输入框。
            // 不留一个"看起来在编辑其实点不动"的状态，直接退出并说明。
            row.IsEditing = false;
            _editingRow = null;
            TranslateInfoText.Text = "这一行不在视野里，先滚动到它可见再双击。";
            Log.Write("edit: container not realized, aborted");
            return;
        }

        _editingBox = box;
        _editCycle.Reset();
        box.ItemsSource = null;
        box.IsSuggestionListOpen = false;
        box.Text = row.Tag;          // 每次进编辑都从当前值开始，不吃上次的半成品

        // 焦点和全选都落到模板里的内部 TextBox 上：AutoSuggestBox 自己没有
        // SelectAll（那是 TextBox 的方法，编译期就报 CS1061），而且全选与否
        // 决定了用户接着打的一个字是"替换整个标签"还是"插进去"。
        var inner = InnerTextBox(box);
        if (inner is not null) { inner.Focus(FocusState.Programmatic); inner.SelectAll(); }
        else box.Focus(FocusState.Programmatic);

        TranslateInfoText.Text = "编辑中：Enter 提交，Esc 取消，Tab 补全。";
        Log.Write($"edit begin: '{row.Tag}'");
    }

    /// <summary>找到某一行模板里的输入框。虚拟化后该行可能没有实体容器，返回 null。</summary>
    private AutoSuggestBox? FindEditBox(TagItem row)
    {
        var container = TagList.ContainerFromItem(row);
        return container is null ? null : FindEditBox(container);
    }

    private static AutoSuggestBox? FindEditBox(DependencyObject root)
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is AutoSuggestBox b && b.Name == "TagEditBox") return b;
            if (FindEditBox(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>AutoSuggestBox 模板里的那个 TextBox（聚焦与全选要落在它身上）。</summary>
    private static TextBox? InnerTextBox(DependencyObject root)
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox tb) return tb;
            if (InnerTextBox(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// 就地编辑时的候选。模板里每一行都有这个输入框、共用同一个处理器，
    /// 所以先确认事件来自"正在编辑的那个框"——否则重建列表时那些
    /// 刚被实体化出来的空输入框会各自去查一遍词库。
    /// </summary>
    private void TagEditBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _editingBox)) return;

        // 同"添加标签"：作废循环要看内容，不看事件——设 ItemsSource 也会
        // 再触发一次 TextChanged，那一下不能清循环。
        if (!_editCycle.At(sender.Text)) _editCycle.Reset();

        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            if (_completing) return;
            sender.ItemsSource = null;
            sender.IsSuggestionListOpen = false;
            return;
        }

        ShowCandidates(sender, TagComplete.Suggest(sender.Text, _vocab));
    }

    private void TagEditBox_SuggestionChosen(AutoSuggestBox sender,
                                             AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (!ReferenceEquals(sender, _editingBox)) return;
        var tag = TagComplete.Bare(args.SelectedItem as string);
        if (tag.Length == 0) return;
        sender.Text = tag;
        _editCycle.SyncTo(tag);
    }

    /// <summary>点了候选就直接提交——点它本身已经表达了"就要这个"。</summary>
    private void TagEditBox_QuerySubmitted(AutoSuggestBox sender,
                                           AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (!ReferenceEquals(sender, _editingBox)) return;
        var tag = TagComplete.Bare(args.ChosenSuggestion as string);
        if (tag.Length > 0) sender.Text = tag;
        EndEdit(commit: true);
    }

    private void TagEditBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not AutoSuggestBox box) return;

        if (e.Key == Windows.System.VirtualKey.Tab)
        {
            // 补全后要提醒怎么收尾：Tab 只改字，不提交
            if (CompleteFrom(box, _editCycle, ShiftDown(), "　（Enter 提交，Esc 取消）"))
                e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            EndEdit(commit: true);
        }
    }

    /// <summary>
    /// 列表子树里的 Esc。两段式：候选列表开着时第一次只关列表，再按一次才取消编辑
    /// ——一次 Esc 就把用户刚打的字全丢掉太狠了。
    ///
    /// 必须在**隧道**阶段（PreviewKeyDown）做，不能用冒泡的 KeyDown：行内输入框是
    /// AutoSuggestBox，它自己的类处理函数会先把 Esc 吃掉、顺手关掉候选列表，
    /// 等冒泡到我们这里 `IsSuggestionListOpen` 已经是 false 了——两段式永远进不去
    /// 第一段（实测踩到）。隧道阶段读到的才是"这一下之前"的状态；
    /// 而这里置 Handled 会连冒泡的 KeyDown 一起抑制，AutoSuggestBox 就收不到。
    /// </summary>
    private void TagList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        if (_editingRow is null) return;
        if (_editingBox is { IsSuggestionListOpen: true }) return;   // 这一下归候选列表

        e.Handled = true;
        _editCancelled = true;
        EndEdit(commit: false);
    }

    /// <summary>
    /// 失焦即提交。点别处等于"改完了"，这在文件管理器里是常规行为；
    /// 而丢弃改动会让用户以为改好了其实没有。
    /// Esc 取消的路径已经把 _editCancelled 置上，所以不会被这里覆盖。
    /// </summary>
    private void TagEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_editingRow is null) return;
        if (_editCancelled) { _editCancelled = false; return; }
        if (sender is not AutoSuggestBox box) return;

        // 候选列表还开着时失焦，多半是用户在点候选。那一刻的顺序是
        // 点击 → SuggestionChosen（把选中项写回输入框）→ 失焦；在这里立刻
        // 提交，提交到的是"点之前"那几个字——用户点的是 A，存下去的却是半个 B。
        // 所以推迟一轮，等选中项先落到输入框里。
        if (box.IsSuggestionListOpen)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(box, _editingBox)) return;   // 已经换了一行在编辑
                if (_editingRow is null || _editCancelled) return;
                if (box.IsSuggestionListOpen) return;             // 列表又开了，说明焦点回来了
                EndEdit(commit: true, lostFocus: true);
            });
            return;
        }

        EndEdit(commit: true, lostFocus: true);
    }

    /// <summary>
    /// 丢弃正在进行的编辑，不做任何数据改动。
    /// 用在"列表被重建/换了一张图"这类场景：那时编辑框里的文字已经不属于
    /// 当前列表了，提交它等于把上一个标签的名字套到新的列表上。
    /// </summary>
    private void AbandonEdit(string why)
    {
        if (_editingRow is null) return;
        Log.Write($"edit abandoned ({why}): '{_editingRow.Tag}'");

        _editingRow.IsEditing = false;
        _editingRow = null;
        _editingBox = null;
        _editCancelled = false;
    }

    /// <param name="lostFocus">
/// true 表示这次结束是**失焦**造成的 —— 也就是"用户点了别处"的那一下。
/// 那一下同时还在被 ListView 处理，所以列表重建必须推迟一轮，否则指针底下的
/// 行会被换掉、这次点击落空（详见调用处的说明）。
/// </param>
private void EndEdit(bool commit, bool lostFocus = false)
    {
        var row = _editingRow;
        if (row is null) return;
        _editingRow = null;

        // 用存下来的引用读内容，而不是重新去可视树里找：行被虚拟化回收后
        // 容器就找不到了，那时用户已经敲进去的内容会被读成 null 而丢掉。
        var typed = _editingBox?.Text ?? row.Tag;
        _editingBox = null;
        row.IsEditing = false;

        if (!commit)
        {
            TranslateInfoText.Text = "已取消编辑。";
            Log.Write($"edit cancel: '{row.Tag}'");
            return;
        }

        if (string.Equals(typed.Trim(), row.Tag, StringComparison.Ordinal))
        {
            TranslateInfoText.Text = "没有改动。";
            return;
        }

        var res = TagEdit.Apply(typed, row.Tag, OthersOf(row.Tag), _vocab);
        if (!res.Ok)
        {
            TranslateInfoText.Text = res.Message;
            Log.Write($"edit rejected: '{row.Tag}' -> '{typed.Trim()}' ({res.Status})");
            return;
        }

        TranslateInfoText.Text = $"已改名：{res.Message}";
        Log.Write($"edit commit: '{row.Tag}' -> '{res.Tag}' via={res.Status}");

        // 失焦触发的改名必须**低优先级推迟**再落地（lostFocus=true）。
        //
        // 实测根因：失焦就是"点了别处"的那一下，而 ApplyTagRename → CommitPrompt →
        // RefreshTagRows 会 _tagRows.Clear() 后**重建一批全新的 TagItem**。重建若落在
        // PointerPressed 与 PointerReleased 之间，指针底下那一行的对象当场被销毁 ——
        // 这次点击选中的是一个已经不存在的对象，界面表现就是"整行没反应"。挪一下
        // 鼠标再点，指针位置变了、也没有编辑态，所以又正常了。
        //
        // 为什么是 Low 优先级而不是默认：WinUI 里一次点击的顺序是
        // PointerPressed → LostFocus → PointerReleased → SelectionChanged。
        // 默认优先级排进队列的任务仍可能抢在 PointerReleased 之前执行，重建就还在
        // 这一帧里（实测推迟一轮只能把命中率从 3/3 提到 11/12）；Low 优先级会排到
        // 输入处理之后，那一刻点击已经落地，重建只影响"之后"看到什么。
        // 这个优先级在滚动跟随那轮验证过有效（见 RefreshTagRows 的 keepVisibleIndex）。
        //
        // 两种入口要分开：Enter / F2 / 右键菜单是**键盘/命令**触发的，没有"用户
        // 正在点的这一下"，同步落地才对（早了一帧会让用户看到旧名字）。
        // 所以只有失焦这一条走推迟。
        if (!lostFocus) ApplyTagRename(row.Tag, res.Tag);
        else DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => ApplyTagRename(row.Tag, res.Tag));
    }

    /// <summary>列表里除 <paramref name="tag"/> 之外的标签，用于防重。</summary>
    private List<string> OthersOf(string tag) =>
        _pendingTags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// 把一个标签改成新名字。
    ///
    /// 原地替换而不是"删掉再追加"：后者会把它挪到本段末尾，用户的段内排序
    /// 就白排了。换完按大类归位（改名可能改变归属，比如中文名→标签会从
    /// general 挪到它该在的段），段内次序仍然保持。
    ///
    /// 重建要**点名被改的那一行要留在视野里**，和上移/下移走同一条已验证的路径：
    /// 不点名的后果实测有三样——(1) 列表回到顶部（重建把 extent 归零，位置没人
    /// 放回去）；(2) 选中丢在别的行上（重建换了批新的 TagItem 对象，原选中对象
    /// 已被丢弃）；(3) 改动引起的跨段归位会把行挪出视口。所以走
    /// <c>keepVisibleIndex</c>：它既负责让该行停在最上/最下一行，也在行还没被
    /// 实体化时退回 <c>ScrollIntoView</c> 兜底（那条是重建路径内部的一跳，
    /// 不会和恢复逻辑抢）。
    /// </summary>
    private void ApplyTagRename(string oldTag, string newTag)
    {
        var i = _pendingTags.FindIndex(t => string.Equals(t, oldTag, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;

        _pendingTags[i] = newTag;
        _pendingTags = TagSections.Canonicalize(_pendingTags, _vocab);

        // Canonicalize 可能把它拨到别的段，所以它在新序列里的下标未必还是 i。
        var j = _pendingTags.FindIndex(t => string.Equals(t, newTag, StringComparison.OrdinalIgnoreCase));
        CommitPrompt(keepVisibleIndex: j >= 0 ? j : i);

        // alsoFollow=true：滚动交给 keepVisibleIndex，这里只负责选中。
        // 若改名被 Canonicalize 换掉了（同名重复等），就退回"选它原来的下标"。
        if (!_tagRows.Any(r => string.Equals(r.Tag, newTag, StringComparison.OrdinalIgnoreCase)))
            CommitPrompt();
        SelectTagRow(newTag, alsoFollow: true);
    }

    private void TagEditAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (TextInputFocused()) return;
        BeginEditSelectedTag();
    }

    /// <summary>菜单「标签 → 编辑标签…」与 F2 走同一条路。</summary>
    private void TagEditMenu_Click(object sender, RoutedEventArgs e) => BeginEditSelectedTag();

    private void TagCopyMenu_Click(object sender, RoutedEventArgs e) => CopySelectedTags();

    private async void TagPasteMenu_Click(object sender, RoutedEventArgs e) => await PasteTagsAsync();

    private void TagNormalizeMenu_Click(object sender, RoutedEventArgs e) => NormalizeSelectedTags();

    /// <summary>对当前选中的那一行进编辑。多选时只取第一个可编辑的。</summary>
    private void BeginEditSelectedTag()
    {
        var row = TagList.SelectedItems.Cast<TagItem>().FirstOrDefault(r => r.CanEdit);
        if (row is null) { TranslateInfoText.Text = "先选中一个标签再按 F2。"; return; }
        BeginEdit(row);
    }

    private void TagCopyAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TextInputFocused()) return;      // 输入框里的 Ctrl+C 归它自己
        args.Handled = true;
        CopySelectedTags();
    }

    /// <summary>
    /// Ctrl+↑ / Ctrl+↓（焦点在标签列表里时）。
    ///
    /// 为什么必须另外挂一份：菜单项上的同名加速键在列表有焦点时**收不到**——
    /// ListView 把 Ctrl+方向键当成"移动焦点"用掉了（实测：焦点在列表里按 Ctrl+↓，
    /// 列表滚了 3%、标签一个没动）。这和 Ctrl+C/V 是同一类问题，所以处理方式
    /// 也一样：菜单项只留快捷键提示，真正的加速键挂在列表上。
    /// </summary>
    private void TagUpAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TextInputFocused()) return;
        args.Handled = true;
        MoveSelected(-1);
    }

    private void TagDownAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TextInputFocused()) return;
        args.Handled = true;
        MoveSelected(1);
    }

    private async void TagPasteAccel_Invoked(KeyboardAccelerator sender,
                                             KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TextInputFocused()) return;
        args.Handled = true;
        await PasteTagsAsync();
    }

    // ================= 拖拽排序 =================

    private void TagList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        // 编辑中不许拖：在行内输入框里按住鼠标划选文字会命中 ListView 的行拖拽，
        // 于是选字变成拖行。编辑优先。
        if (_editingRow is not null)
        {
            e.Cancel = true;
            return;
        }

        _dragged.Clear();
        // 散文行不可拖：它不是标签，拖了也没有对应的落点语义
        _dragged.AddRange(e.Items.Cast<TagItem>().Where(r => r.PendingIndex >= 0));
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void TagList_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
    }

    private void TagList_Drop(object sender, DragEventArgs e)
    {
        if (_dragged.Count == 0) return;
        try
        {
            // 落点：指针下的那一行；落在空白处则挪到末尾
            var target = (e.OriginalSource as FrameworkElement)?.DataContext as TagItem;
            var moving = _dragged.Select(d => d.Tag).ToList();
            var movingSet = new HashSet<string>(moving, StringComparer.OrdinalIgnoreCase);

            var rest = _pendingTags.Where(t => !movingSet.Contains(t)).ToList();
            var insertAt = rest.Count;
            if (target is not null)
            {
                var at = rest.FindIndex(t => string.Equals(t, target.Tag, StringComparison.OrdinalIgnoreCase));
                if (at >= 0) insertAt = at;
            }

            rest.InsertRange(insertAt, moving);
            // 拖拽只在同一段内有可见效果：落点若在别的大类里，Canonicalize 会把
            // 它拨回自己那段（那才是符合官方顺序的位置）。段内的相对次序被保留，
            // 所以正常的段内拖拽是所见即所得的。
            _pendingTags = TagSections.Canonicalize(rest, _vocab);
            CommitPrompt();

            TagList.SelectedItems.Clear();
            foreach (var t in moving)
            {
                var row = _tagRows.FirstOrDefault(r =>
                    string.Equals(r.Tag, t, StringComparison.OrdinalIgnoreCase));
                if (row is not null) TagList.SelectedItems.Add(row);
            }
        }
        catch (Exception ex) { Log.Write("drop failed: " + ex.Message); }
        finally { _dragged.Clear(); }
    }

    // ================= 分栏拖拽 =================

    /// <summary>默认分栏比例 2:2:3。双击分隔条和配置损坏时都回落到它。</summary>
    private static readonly double[] DefaultStars = { 2, 2, 3 };

    /// <summary>
    /// 把配置里的分栏比例应用到三栏。
    /// 存 * 比例而不是像素：换窗口大小、换显示器后仍是同一个布局意图。
    /// 越界或损坏的值直接回落默认——否则某栏可能被压成 0 宽且拖不回来。
    /// </summary>
    private void ApplyColumnRatios()
    {
        var s = new[] { _cfg.LeftStar, _cfg.MidStar, _cfg.RightStar };
        var ok = true;
        foreach (var v in s)
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0.1 || v > 100) { ok = false; break; }

        if (!ok)
        {
            Log.Write($"column ratios invalid ({string.Join(", ", s)}), using defaults");
            s = (double[])DefaultStars.Clone();
        }

        ColLeft.Width = new GridLength(s[0], GridUnitType.Star);
        ColMid.Width = new GridLength(s[1], GridUnitType.Star);
        ColRight.Width = new GridLength(s[2], GridUnitType.Star);
    }

    /// <summary>
    /// 把当前分栏比例写回配置。只在拖动结束时调用，不在拖动过程中写盘——
    /// DragDelta 每次移动都会触发，那样会变成每帧一次文件写入。
    /// </summary>
    private void SaveColumnRatios()
    {
        try
        {
            // 先同步刷新布局再读 ActualWidth。
            // 不刷新会读到改动前的旧值：Grid 要等一次布局过程才更新 ActualWidth，
            // 而刚设完 Width 就立刻读，拿到的是上一次的宽度——表现为「拖完之后
            // 存下来的比例是拖动前的」。UpdateLayout() 是同步的，返回后即为最终值。
            BodyGrid.UpdateLayout();

            // 按「实际渲染宽度」反算比例，而不是直接存 Width.Value。
            // 两者会不一致：Grid 的 MinWidth 会静默把过窄的列撑回去，而
            // Width.Value 仍是我们设进去的名义 star 值。存实际值才是所见即所得。
            var l = ColLeft.ActualWidth;
            var m = ColMid.ActualWidth;
            var r = ColRight.ActualWidth;

            if (l > 0 && m > 0 && r > 0)
            {
                // 归一到和 = 7，保持默认布局同一量级，避免数值随窗口大小漂移
                var total = l + m + r;
                _cfg.LeftStar = 7.0 * l / total;
                _cfg.MidStar = 7.0 * m / total;
                _cfg.RightStar = 7.0 * r / total;
            }
            else
            {
                // 窗口还没布局完（ActualWidth 全为 0）时退回名义值
                _cfg.LeftStar = ColLeft.Width.Value;
                _cfg.MidStar = ColMid.Width.Value;
                _cfg.RightStar = ColRight.Width.Value;
            }

            _cfg.Save();
            Log.Write($"column ratios saved " +
                      $"{_cfg.LeftStar:F2}:{_cfg.MidStar:F2}:{_cfg.RightStar:F2}");
        }
        catch (Exception ex) { Log.Write("save column ratios failed: " + ex.Message); }
    }

    /// <summary>
    /// 直接保存给定的名义比例，不读实际宽度。
    /// 只用于「恢复默认布局」：这里要记的是用户的**意图**（2:2:3）。
    /// 当前窗口如果太窄，MinWidth 会把中栏撑住、实际比例不是 2:2:3，
    /// 但那是当前窗口尺寸的临时产物；存名义值才能在窗口重新变宽时
    /// 恢复成真正的默认布局，而不是把窄窗口下的畸形比例固化下来。
    /// </summary>
    private void SaveNominalRatios(double[] stars)
    {
        try
        {
            _cfg.LeftStar = stars[0];
            _cfg.MidStar = stars[1];
            _cfg.RightStar = stars[2];
            _cfg.Save();
            Log.Write($"column ratios saved (nominal) " +
                      $"{_cfg.LeftStar:F2}:{_cfg.MidStar:F2}:{_cfg.RightStar:F2}");
        }
        catch (Exception ex) { Log.Write("save nominal ratios failed: " + ex.Message); }
    }

    /// <summary>
    /// 应用一组分栏比例。save=true 时落盘：
    /// 传 nominal=true 存意图值（恢复默认布局），否则存实际渲染宽度（拖动结束）。
    /// </summary>
    private void ApplyColumns(double[] stars, bool save, bool nominal = false)
    {
        ColLeft.Width = new GridLength(stars[0], GridUnitType.Star);
        ColMid.Width = new GridLength(stars[1], GridUnitType.Star);
        ColRight.Width = new GridLength(stars[2], GridUnitType.Star);
        if (!save) return;

        if (nominal) SaveNominalRatios(stars);
        else SaveColumnRatios();
    }

    // 三栏用 * 比例而不是像素：窗口缩放时各栏按比例增减，
    // 拖拽时把像素增量折算成比例增量，MinWidth 仍由列定义兜底。
    private void DragSplitter(ColumnDefinition left, ColumnDefinition right, double deltaX)
    {
        if (double.IsNaN(deltaX) || double.IsInfinity(deltaX)) return;

        // 下限直接取列自己的 MinWidth，不再硬编码常量。
        // 硬编码的 160 和 XAML 的 MinWidth="200" 会脱节：比例能被存成小于
        // MinWidth 的宽度，随后被 Grid 静默截断，于是「存下来的比例」和
        // 「实际渲染的宽度」不一致——表现是拖到最后一段没反应、重启后布局
        // 又和刚拖完时不同。取列自身的下限可以让两边永远一致。
        var minLeft = left.MinWidth;
        var minRight = right.MinWidth;

        // 每 star 多少像素，用左栏自己的实测宽度换算——同一个 Grid 里所有
        // star 列共享这一个比值，所以对右栏同样成立。
        // 不能用 BodyGrid.ActualWidth / (left+right 的 star 和)：ActualWidth 是
        // 整个三栏宽度，而 star 和只含两栏，比值会偏大约 1.8 倍，拖起来明显不跟手。
        var leftPx = left.ActualWidth;
        var leftStar = left.Width.Value;
        if (leftPx <= 0 || leftStar <= 0) return;
        var pxPerStar = leftPx / leftStar;

        var deltaStar = deltaX / pxPerStar;

        var newLeft = leftStar + deltaStar;
        var newRight = right.Width.Value - deltaStar;
        if (newLeft <= 0 || newRight <= 0) return;
        if (newLeft * pxPerStar < minLeft || newRight * pxPerStar < minRight) return;

        left.Width = new GridLength(newLeft, GridUnitType.Star);
        right.Width = new GridLength(newRight, GridUnitType.Star);
    }

    private void LeftSplitter_DragDelta(object sender, Microsoft.UI.Xaml.Controls.Primitives.DragDeltaEventArgs e)
        => DragSplitter(ColLeft, ColMid, e.HorizontalChange);

    private void RightSplitter_DragDelta(object sender, Microsoft.UI.Xaml.Controls.Primitives.DragDeltaEventArgs e)
        => DragSplitter(ColMid, ColRight, e.HorizontalChange);

    /// <summary>拖动结束才落盘，避免拖动过程中反复写文件。</summary>
    private void Splitter_DragCompleted(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.DragCompletedEventArgs e) => SaveColumnRatios();

    private void LeftSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ResetColumns();
    private void RightSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ResetColumns();

    private void ResetColumns() => ApplyColumns(DefaultStars, save: true, nominal: true);

    /// <summary>菜单「视图 → 恢复默认布局」。和双击分隔条等效，只是入口更明显。</summary>
    private void ResetLayout_Click(object sender, RoutedEventArgs e) => ResetColumns();

    // ================= 翻译器 =================
    //
    // 这里只做一件事：**中文自然语言 → 英文自然语言**，结果接到提示词末尾。
    //
    // 刻意不再产出散装 tag。理由有两条：
    // 1. 架构上中文必须翻成英文才能进 Anima。它的实编码器是 Qwen3-0.6B，但信号
    //    要走 T5-XXL 的嵌入空间（DiT 内 6 层 adapter 把 Qwen 隐状态交叉注意力到
    //    T5 词表索引的嵌入上）。实测 T5 词表 32100 个 piece 里 CJK 数量为 0，
    //    51 个常用汉字里 49 个直接塌成 <unk>，整句中文只有 33–45% 的位置有效。
    //    作者原话："T5 wasn't trained on Chinese... the model only understands English."
    // 2. 职责上 tag 归中栏：那里有词库补全、大类分段、校验和段内排序。
    //    两个入口都产 tag 会互相打架，而且模型直接写英文 tag 实测是错的
    //    （会写成散文，用语法约束又会选错词义）。

    private async void Translate_Click(object sender, RoutedEventArgs e) => await TranslateAsync();

    private async Task TranslateAsync()
    {
        var text = NaturalBox.Text.Trim();
        if (text.Length == 0) { await Info("没有输入", "请先在下方填入中文描述。"); return; }

        BusyRing.IsActive = true;
        TranslateBtn.IsEnabled = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // 纯词库那条路走不通了：词库给的是标签，而这一栏要的是英文散文。
            // 所以「翻译并追加」也必须有模型；没有可用模型时如实说明，
            // 而不是偷偷塞一堆标签进去冒充散文。
            // 先按词库给一份"中文词 → 英文单词"的对照塞进系统提示。
            // 这不是装饰：人称词（少女/少年/女孩…）在词库里根本没有条目，
            // 不给译法模型就会把中文原样留下，实测留存率 2/4 → 给对照后 0/4。
            // 详见 VocabDb.PersonWords 的注释。
            var sysPrompt = LlmClient.ProseSystemPrompt;
            var glossary = _vocab?.ProseGlossary(text) ?? new List<(string Cn, string En)>();
            if (glossary.Count > 0)
            {
                sysPrompt += "\nGlossary (Chinese = English). Use these, and translate " +
                             "every remaining Chinese word too:\n" +
                             string.Join("\n", glossary.Select(g => $"  {g.Cn} = {g.En}"));
                Log.Write($"prose glossary: {glossary.Count} entries");
            }

            var res = await _llm.ChatAsync(_cfg, sysPrompt, text);
            if (!res.Ok)
            {
                Log.Write("prose translate failed: " + res.Error);
                TranslateInfoText.Text = "模型调用失败：" + res.Error;
                await Info("无法翻译",
                    "把中文描述写成英文自然语言需要模型：\n\n" + res.Error +
                    "\n\n请到「设置」里确认接口地址和模型名，并点「测试连接」。");
                return;
            }

            var prose = SanitizeProse(res.Content);

            // 模型偶尔会把中文词原样留在英文句子里（实测 `A silver-haired少女 stands…`）。
            // 这不能放任写进训练集：Anima 的 T5 词表 32100 个 piece 里 CJK 数量为 0，
            // 中文整段会塌成 <unk>，那段描述等于没写；而在编辑器里又看不出来。
            //
            // 修法经过实测挑选（同一段中文、同一个 8B，四组对照）：
            //   笼统地说"全部用英文"        -> 仍然留下 `少女`，无效
            //   降低 temperature            -> 无效
            //   **点名那几个字并给出译法**  -> 干净通过
            //   事后让模型就地改写整句      -> 也干净（作为兜底）
            // 所以先用点名法重试；再不行就整句改写一次；仍不行才如实告知用户。
            if (ContainsCjk(prose))
            {
                Log.Write("prose contains CJK, retrying: " + prose);
                var offenders = string.Concat(CjkChars(prose).Distinct());
                var retry = await _llm.ChatAsync(_cfg,
                    LlmClient.ProseSystemPrompt +
                    "\nIMPORTANT: your previous answer contained the Chinese " +
                    $"character(s) {offenders}. Output no Chinese characters at all. " +
                    "Write the English word instead.",
                    text);
                var again = retry.Ok ? SanitizeProse(retry.Content) : prose;
                if (!ContainsCjk(again))
                {
                    prose = again;
                }
                else
                {
                    // 兜底：把它当成一次"就地校对"——只换掉中文字符，其余保持不变。
                    // 实测这条路对同一段文字是有效的。
                    Log.Write("naming the chars did not help, trying copy-edit: " + again);
                    var fix = await _llm.ChatAsync(_cfg,
                        "You are a copy editor. Replace any Chinese characters in the " +
                        "user's English sentence with their correct English translation. " +
                        "Keep everything else identical. Output ONLY the corrected sentence.",
                        again);
                    var fixedUp = fix.Ok ? SanitizeProse(fix.Content) : again;
                    if (!ContainsCjk(fixedUp)) prose = fixedUp;
                    else
                    {
                        // 三种办法都试过了：如实告知，并把中文字符标出来，
                        // 让用户决定，而不是悄悄写进训练集。
                        Log.Write("prose still contains CJK: " + fixedUp);
                        await Info("译文里混了中文",
                            "模型反复把中文词留在英文句子里：\n\n" + fixedUp +
                            "\n\n含中文的部分：" + string.Concat(CjkChars(fixedUp).Distinct()) +
                            "\n\n这段若直接写进 caption，Anima 的 T5 分词器会把中文变成 " +
                            "<unk>，等于那句话没写。请手工改掉，或换个模型再试。");
                        prose = fixedUp;
                    }
                }
            }

            if (prose.Length == 0)
            {
                TranslateInfoText.Text = "模型返回了空内容。";
                await Info("翻译失败", "模型没有返回可用的英文内容。");
                return;
            }

            // 撤销点存的是整段提示词，不只是标签——翻译动的可能只是散文
            _undoPrompt = GetCaptionText();

            var replaced = AppendModeBox.IsChecked == true && _proseTail.Length > 0;
            if (replaced)
            {
                _proseTail = ". " + prose;
                CommitPrompt();
            }
            else
            {
                // 默认：接到提示词末尾。官方 example.png 就是这个形状
                // （标签区用句号收尾，后面接散文）。
                SetCaptionText(PromptText.AppendProse(GetCaptionText(), prose));
                var parsed = PromptText.Parse(GetCaptionText(), _vocab);
                _pendingTags = new List<string>(parsed.Tags);
                _proseTail = parsed.ProseTail;
                RefreshTagRows();
            }

            UndoTranslateBtn.IsEnabled = true;
            UndoMenuItem.IsEnabled = true;

            sw.Stop();
            var words = prose.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var sentences = prose.Count(c => c is '.' or '!' or '?');
            TranslateInfoText.Text =
                $"{(replaced ? "替换散文" : "追加散文")} {words} 词 / {sentences} 句 · " +
                $"{sw.ElapsedMilliseconds} ms";

            Log.Write($"prose translate: {words} words, {sentences} sentences, " +
                      $"{sw.ElapsedMilliseconds}ms, replaced={replaced}, model={_cfg.Model}");
        }
        catch (Exception ex)
        {
            Log.Write("translate FAILED: " + ex);
            await Info("翻译失败", ex.Message);
        }
        finally
        {
            BusyRing.IsActive = false;
            TranslateBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// 是否含 CJK 字符。
    ///
    /// 用来拦"英文里夹中文"：Anima 的 T5 词表 32100 个 piece 里 CJK 数量为 0，
    /// 中文整段会塌成 &lt;unk&gt;，所以夹在英文里的中文等于没写——但在编辑器里
    /// 一眼看不出来（那正是危险所在），必须程序化拦下。
    /// 范围同 VocabDb.HasCjk：部首扩展 ~ 中日韩统一表意、兼容表意、全角。
    /// </summary>
    private static bool ContainsCjk(string s)
    {
        foreach (var ch in s)
        {
            if (ch >= 0x2E80 && ch <= 0x9FFF) return true;
            if (ch >= 0xF900 && ch <= 0xFAFF) return true;
            if (ch >= 0xFF00 && ch <= 0xFFEF) return true;
        }
        return false;
    }

    /// <summary>挑出串里的 CJK 字符。用来在重试提示里"点名"是哪几个字——
    /// 实测笼统要求"全用英文"无效，点名才有效。</summary>
    private static IEnumerable<char> CjkChars(string s)
    {
        foreach (var ch in s)
        {
            if (ch >= 0x2E80 && ch <= 0x9FFF) yield return ch;
            else if (ch >= 0xF900 && ch <= 0xFAFF) yield return ch;
            else if (ch >= 0xFF00 && ch <= 0xFFEF) yield return ch;
        }
    }

    /// <summary>
    /// 清掉模型爱加的外壳：代码块围栏、成对引号、开头的 "English:" 之类标签、
    /// 以及中文残句。模型偶尔会把提示词里的说明也一起吐出来。
    /// </summary>
    private static string SanitizeProse(string raw)
    {
        var s = (raw ?? "").Trim();

        // ```...``` 围栏
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            var end = s.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) s = s[..end];
            s = s.Trim();
        }

        // 整段被引号包住
        if (s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\u201c' && s[^1] == '\u201d'))
            s = s[1..^1].Trim();

        // "English: ..." / "英文：..." 这种前缀
        foreach (var p in new[] { "English:", "English\uff1a", "\u82f1\u6587\uff1a", "\u8bd1\u6587\uff1a" })
            if (s.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                s = s[p.Length..].Trim();

        // 折成一行（caption 是单行格式）
        s = s.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        while (s.Contains("  ", StringComparison.Ordinal)) s = s.Replace("  ", " ");

        return s.Trim();
    }

    private void UndoTranslate_Click(object sender, RoutedEventArgs e)
    {
        if (_undoPrompt is null) return;

        // 恢复整段提示词，而不只是标签：翻译动的是散文尾巴，
        // 只还原标签的话「撤销」看不出任何变化。
        SetCaptionText(_undoPrompt);
        var parsed = PromptText.Parse(_undoPrompt, _vocab);
        _pendingTags = new List<string>(parsed.Tags);
        _proseTail = parsed.ProseTail;
        _undoPrompt = null;

        RefreshTagRows();
        UndoTranslateBtn.IsEnabled = false;
        UndoMenuItem.IsEnabled = false;
        TranslateInfoText.Text = "已撤销上次翻译。";
    }

    /// <summary>
    /// 提示词正文变化。
    /// 注意签名是 RoutedEventArgs 而不是 TextChangedEventArgs——RichEditBox 的
    /// TextChanged 事件委托类型就是 RoutedEventHandler（TextBox 的才是
    /// TextChangedEventHandler），写成后者编译期就会报没有匹配的重载。
    /// </summary>
    private void CaptionBox_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        // 每敲一个字都重新解析：用户可能正在往提示词里打字，标签区和散文尾巴的
        // 边界会随之移动，中间列表要跟着变。
        var raw = GetCaptionText();
        var parsed = PromptText.Parse(raw, _vocab);

        // 解析结果和上次一模一样就什么都不做。这一条很关键：改色本身会触发
        // TextChanged（RichEdit 的行为），没有这个提前返回就会一路重建列表。
        if (raw == _lastParsedRaw) return;
        _lastParsedRaw = raw;

        var tagsChanged = !parsed.Tags.SequenceEqual(_pendingTags, StringComparer.Ordinal);
        var proseChanged = parsed.ProseTail != _proseTail;

        _pendingTags = parsed.Tags;
        _proseTail = parsed.ProseTail;

        // 只有分类结果真的变了才重涂：每次按键都整篇重涂会明显卡顿，
        // 而绝大多数按键并不改变任何一段的归属。
        if (tagsChanged || proseChanged) ApplyPromptColors();
        if (tagsChanged) RefreshTagRows();
    }

    // ================= 设置 =================

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await ShowSettingsAsync();
    }

    /// <summary>供 --settings 启动开关请求打开设置对话框；真正的打开在 Initialize 之后。</summary>
    public void OpenSettingsForTest() => _settingsAfterInit = true;

    /// <summary>供 --help 启动开关请求打开帮助；真正的打开在 Initialize 之后。</summary>
    public void OpenHelpForTest() => _helpAfterInit = true;

    /// <summary>供 --tagsearch 启动开关请求打开标签搜索；真正的打开在 Initialize 之后。</summary>
    public void OpenTagSearchForTest() => _searchAfterInit = true;

    private async Task ShowSettingsAsync()
    {
        try
        {
            var dlg = new SettingsDialog(_cfg, _llm) { XamlRoot = Content.XamlRoot };
            var result = await dlg.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            dlg.ApplyTo(_cfg);
            _cfg.Save();
            Log.Write("settings applied: api=" + _cfg.ApiBaseUrl + " model=" + _cfg.Model +
                      " key=" + (string.IsNullOrEmpty(_cfg.ApiKey) ? "(none)" : "(set)") +
                      " colors=" + string.Join(",", TagSections.Colorable.Select(
                          s => s.Key + "=" + TagSections.NormalizeHex(_cfg.GetSectionColor(s.Key)))));

            // 配色改了要立刻反映到列表上，否则得重启才看得到
            LoadVocab();
            LoadFolder(_cfg.DatasetDir,
                       _index >= 0 && _index < _items.Count ? _items[_index].Name : null);
        }
        catch (Exception ex)
        {
            Log.Write("settings FAILED: " + ex);
            await Info("设置失败", ex.Message);
        }
    }

    private Task Info(string title, string message) =>
        Info(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });

    /// <summary>
    /// 信息对话框。正文一律套一层 ScrollViewer：ContentDialog 的高度受窗口限制，
    /// 内容超高时是**静默裁掉**的，界面上完全看不出还有下文（帮助页因此少了整整
    /// 两节，「标签统计」和「校验结果」也都在这个坑里）。
    /// 上限按 XamlRoot 的实际高度算，不能写死——写死会在小窗口上照样溢出。
    ///
    /// 滚动条是自己用 Thumb 画的，不是靠 ScrollViewer 自带的那根：
    /// WinUI 3 的 ScrollViewer 没有 ScrollingIndicatorMode（那是 UWP 的 API，编译期
    /// 就不存在），自带滚动条是浮层、会自动淡出，实测静止状态下截帧里一个像素都不画，
    /// 所以"下面还有内容"这件事在界面上根本看不出来——而这正是要解决的问题本身。
    /// 独立的 ScrollBar 控件也试过：MouseIndicator 只在鼠标交互时才画滑块，
    /// TouchIndicator 什么都不画。两条路都得不到"常驻可见"，所以自己画。
    /// </summary>
    private async Task Info(string title, UIElement content)
    {
        try
        {
            var scroll = new ScrollViewer
            {
                Content = content,
                // 自带的那根藏掉，用旁边自己画的那根
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollMode = ScrollMode.Auto,
                // 正文都是换行的，横向不该出现滚动条；禁用比隐藏更明确
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Disabled
            };

            // 高度上限按 XamlRoot 的实际高度算，不能写死——写死会在小窗口上照样溢出。
            // 留 240 DIP 给标题栏、按钮行、对话框内边距和阴影。
            var avail = Content.XamlRoot?.Size.Height ?? 0;
            if (avail > 0) scroll.MaxHeight = Math.Max(140, avail - 240);

            // 滚动条自己画。不用 ScrollBar 控件，也不用 ScrollViewer 自带的那根：
            // 自带的那根是浮层、会自动淡出；ScrollBar 的 IndicatorMode 两条路都试过——
            // MouseIndicator 只在鼠标交互时才画滑块（静止时截帧里一个像素都没有），
            // TouchIndicator 干脆什么都不画。两种都得不到"常驻可见"。
            // 所以用 Thumb 自己摆：位置和长度都按 ScrollableHeight 算，完全可控。
            // （Thumb 在本项目里已经用于分隔条，是验证过能用的控件。）
            var track = new Border
            {
                Width = 12,
                CornerRadius = new CornerRadius(6),
                Background = Application.Current.Resources["ControlFillColorSecondaryBrush"]
                             as Microsoft.UI.Xaml.Media.Brush,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var thumb = new Thumb
            {
                Width = 12,
                MinHeight = 28,
                CornerRadius = new CornerRadius(6),
                Background = Application.Current.Resources["TextFillColorTertiaryBrush"]
                              as Microsoft.UI.Xaml.Media.Brush,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            track.Child = thumb;

            var syncing = false;

            void SyncBar()
            {
                var viewport = scroll.ViewportHeight;
                var scrollable = scroll.ScrollableHeight;
                var trackH = track.ActualHeight;

                if (scrollable <= 0.5 || trackH <= 0)
                {
                    track.Visibility = Visibility.Collapsed;
                    return;
                }
                track.Visibility = Visibility.Visible;

                // 滑块长度按"可视区 / 总高"算，但给个下限，否则内容很长时滑块细得看不见
                var total = viewport + scrollable;
                var thumbH = Math.Max(28, trackH * (viewport / total));
                thumb.Height = thumbH;

                // 滑块位置：滚动比例映射到"轨道高 - 滑块高"这段可移动范围
                var travel = Math.Max(0, trackH - thumbH);
                var ratio = scrollable > 0 ? scroll.VerticalOffset / scrollable : 0;
                thumb.Margin = new Thickness(0, travel * ratio, 0, 0);
            }

            thumb.DragDelta += (_, e) =>
            {
                if (syncing) return;
                syncing = true;
                var viewport = scroll.ViewportHeight;
                var trackH = track.ActualHeight;
                var total = viewport + scroll.ScrollableHeight;
                var thumbH = Math.Max(28, trackH * (viewport / total));
                var travel = Math.Max(0, trackH - thumbH);
                if (travel > 0)
                {
                    var ratio = Math.Clamp(thumb.Margin.Top + e.VerticalChange, 0, travel) / travel;
                    scroll.ChangeView(null, scroll.ScrollableHeight * ratio, null, true);
                }
                syncing = false;
            };

            scroll.ViewChanged += (_, _) =>
            {
                if (syncing) return;
                syncing = true;
                SyncBar();
                syncing = false;
            };
            scroll.SizeChanged += (_, _) => SyncBar();

            // 打开时也要同步一次：ViewChanged 在首帧不一定会触发，
            // 只靠它的话初始滑块的位置和长度都是错的。
            scroll.Loaded += (_, _) =>
            {
                SyncBar();
                // 只在真的需要滚动时记一行：滑块长度/位置算错这个坑排查过很久，
                // 留一条可核对的数（内容不够长就不记，免得刷屏）。
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (scroll.ScrollableHeight <= 0.5) return;
                    Log.Write($"info '{title}' scrollable: trackH={track.ActualHeight:F0} "
                              + $"thumbH={thumb.Height:F0} top={thumb.Margin.Top:F0} "
                              + $"max={scroll.ScrollableHeight:F0} viewport={scroll.ViewportHeight:F0}");
                });
            };

            var host = new Grid();
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var barCol = new Border { Padding = new Thickness(6, 0, 0, 0), Child = track };
            Grid.SetColumn(scroll, 0);
            Grid.SetColumn(barCol, 1);
            host.Children.Add(scroll);
            host.Children.Add(barCol);

            var dlg = new ContentDialog
            {
                Title = title,
                Content = host,
                CloseButtonText = "好",
                XamlRoot = Content.XamlRoot
            };
            await dlg.ShowAsync();
            Log.Write($"info dialog '{title}': avail={avail:F0} maxHeight={scroll.MaxHeight:F0} "
                      + $"scrollable={scroll.ScrollableHeight:F0}");
        }
        catch (Exception ex) { Log.Write("Info dialog failed: " + ex.Message); }
    }
}
