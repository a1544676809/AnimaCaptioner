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

    // ================= 模式与当前文档 =================
    //
    // 设计要点：中栏（标签装配）与右栏（提示词原文 + 翻译器）**两个模式共用同一份
    // 控件和同一份 code-behind**，因为它们本来就不读当前图片——实测所有标签操作
    // （上移 TagOrder.Move、删除、校验、规范、模型补译、粘贴）只吃
    // _pendingTags + _vocab + _cfg，没有一处碰 _index / _items。
    //
    // 真正与图片耦合的原本只有三处：LoadCurrent（从 .txt 灌进 _pendingTags）、
    // SaveCurrent（写回 .txt）、以及两个"_index < 0 就报错"的守卫。
    // 把这两处按模式分派、把守卫改成"有没有文档"，写标签模式就自然成立。

    private PromptMode _mode = PromptMode.Label;

    /// <summary>
    /// 当前编辑的文档身份。**内容不在这里**——内容始终以提示词框为准
    /// （见 <see cref="PromptDoc"/> 的说明）。null 表示没有打开任何文档，
    /// 这时标签操作要拒绝并说明，而不是静默改一个不存在的东西。
    /// </summary>
    private PromptDoc? _doc;

    /// <summary>写标签模式的提示词库（历史 + 预设）。独立 JSON，不塞进 settings.json。</summary>
    private PromptLibraryStore _lib = new();

    /// <summary>写标签模式左栏的行对象。</summary>
    private readonly ObservableCollection<PromptLibRow> _libRows = new();

    /// <summary>列表重建期间抑制 SelectionChanged，避免程序性选中被当成用户点击。</summary>
    private bool _libGuard;

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
    /// 撤销历史的**分桶仓库**：训练集里每张图、提示词库里每条草稿各有一条
    /// 独立的撤销 / 重做记录，所以换文档不再丢撤销能力（见
    /// <see cref="PromptHistoryStore"/>）。
    /// </summary>
    private readonly PromptHistoryStore _histories = new();

    /// <summary>
    /// 当前文档的撤销 / 重做栈 = <see cref="_histories"/> 里当前身份那一桶。
    /// **每个会改动文档的命令在动手之前记一笔**（见 <see cref="PushUndo"/>），
    /// 撤销就是退回上一个状态。没有打开文档时它是一份不入桶的空栈。
    ///
    /// 与 <see cref="_undoPrompt"/> 的分工：「撤销翻译」是"跳回上次翻译之前"，
    /// 即使中间又做了别的操作也照样能跳；这个栈是"一步一步往回退"。
    /// 两条路都保留——前者是翻译专用的一键后悔，后者是通用的。
    /// </summary>
    private PromptHistory _history = new();

    /// <summary>
    /// 当前撤销历史所属的训练集根目录。换了一个数据集就清空历史：桶是按
    /// **绝对路径**分的，上一个数据集的那些实例在这个工作集里没有意义。
    /// 同一个数据集"重新载入"（Reload）不清——那正是要保住历史的场景。
    /// </summary>
    private string _historyRoot = "";

    /// <summary>
    /// --settings 启动开关请求的延迟动作。
    /// 不能在构造期直接开设置对话框：_cfg 要等 Initialize() 才赋值，
    /// 而 Initialize() 挂在 RootGrid.Loaded 上。构造期就开的话 _cfg 还是 null，
    /// SettingsDialog 构造函数里第一句取 cfg.ApiBaseUrl 就 NullReferenceException。
    /// </summary>
    private bool _settingsAfterInit;
    private bool _helpAfterInit;
    private bool _searchAfterInit;
    private bool _draftAfterInit;

    public MainWindow()
    {
        InitializeComponent();
        TagList.ItemsSource = _tagRows;
        ImageList.ItemsSource = _imageItems;
        PromptList.ItemsSource = _libRows;

        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(ico)) AppWindow.SetIcon(ico);
        }
        catch { }

        Title = "AnimaCaptioner";

        // Ctrl+S 的主操作按模式分派（标注 = 写回 .txt，写标签 = 复制走）。
        // 只挂一个加速键、在回调里分派，而不是按模式挂两个——挂两个会重复触发。
        AddAccel(Windows.System.VirtualKey.S, Windows.System.VirtualKeyModifiers.Control,
                 () => RunPrimaryAction(showDialog: true));
        AddAccel(Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.Menu,
                 () => Navigate(-1));
        AddAccel(Windows.System.VirtualKey.Right, Windows.System.VirtualKeyModifiers.Menu,
                 () => Navigate(1));

        // Ctrl+Z / Ctrl+Y 只在焦点**不在**文本输入控件里时才归我们。
        // 提示词框（RichEditBox）和两个输入框都有它们自己的撤销，抢过来会让
        // "在提示词框里打完字按 Ctrl+Z"变成撤销标签操作、而不是撤销刚打的字。
        // 所以这里刻意不吃这个键，让它继续走到焦点控件手里（见 AddAccelIf）。
        AddAccelIf(() => !TextInputFocused() && _history.CanUndo,
                   Windows.System.VirtualKey.Z, Windows.System.VirtualKeyModifiers.Control,
                   UndoTagEdit);
        AddAccelIf(() => !TextInputFocused() && _history.CanRedo,
                   Windows.System.VirtualKey.Y, Windows.System.VirtualKeyModifiers.Control,
                   RedoTagEdit);

        // 记住上次看到的文件，下次启动直接定位
        Closed += (_, _) =>
        {
            try
            {
                _cfg.LastMode = _mode == PromptMode.Draft ? "draft" : "label";
                if (_index >= 0 && _index < _items.Count)
                    _cfg.LastFile = _items[_index].ImagePath;
                _cfg.Save();
            }
            catch (Exception ex) { Log.Write("save last file failed: " + ex.Message); }

            // 预览是独立的顶层窗口，不随主窗口自动关闭。不显式关掉的话，
            // 关掉主窗口后预览还留在屏幕上，进程也不会退出。帮助窗口同理。
            try { _preview?.Close(); } catch (Exception ex) { Log.Write("close preview failed: " + ex.Message); }
            try { _help?.Close(); } catch (Exception ex) { Log.Write("close help failed: " + ex.Message); }
            try { _searchWin?.Close(); } catch (Exception ex) { Log.Write("close tag search failed: " + ex.Message); }

            // 写标签模式里最后一次编辑的草稿要落库，否则关窗口就丢了。
            try { FlushDraftToLibrary(); } catch (Exception ex) { Log.Write("flush draft failed: " + ex.Message); }
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
    /// 挂一个"只在轮到自己时才吃掉"的加速键。
    ///
    /// <paramref name="mine"/> 返回 false 时不置 Handled，按键继续往下走、
    /// 由焦点控件自己处理。这是 Ctrl+Z 能同时满足两种语义的关键：
    /// 焦点在标签列表/按钮上时是我们的撤销，焦点在提示词框里时是它自己的撤销。
    ///
    /// 对比 <see cref="AddAccel"/>：那个无条件吃键，适合 Ctrl+S 这种
    /// "到哪儿都是我们的事"的键。
    /// </summary>
    private void AddAccelIf(Func<bool> mine, Windows.System.VirtualKey key,
                            Windows.System.VirtualKeyModifiers mods, Action act)
    {
        var a = new KeyboardAccelerator { Key = key, Modifiers = mods };
        a.Invoked += (_, e) =>
        {
            if (!mine()) return;
            e.Handled = true;
            act();
        };
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

        // 撤销步数是用户配置，越界值由 setter 收敛（0 = 关闭撤销）。
        // 设在仓库上：它会把已经建好的每个桶都裁一遍，之后新建的桶也按这个值。
        _histories.MaxSteps = _cfg.UndoMaxSteps;
        DetachHistory();

        // 模型补译的缓存：它是数据不是配置，所以独立 sidecar 文件。
        // 词库加载前读出来，列表首次构建时就能带上。
        _gloss = ModelGlossStore.Load();

        // 提示词库同理：独立 sidecar，且**必须在 ApplyColumnRatios 之前**载入，
        // 因为分栏比例要按模式取不同的一组值。
        _lib = PromptLibraryStore.Load();

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

        // 恢复上次的模式。放在 LoadFolder 之后：标注模式下 LoadFolder 已经把
        // 当前图开好了（_doc 是 LabelDoc），这时如果上次停在写标签模式，
        // SwitchMode 会把它切过去。
        //
        // 必须在 _mode 还是初值时调用，否则 SwitchMode 的首个
        // `if (_mode == mode) return;` 会让它直接返回、界面停在标注模式。
        //
        // saveCurrent: false 是**必须**的：那一步在标注侧会走 SaveCurrent，
        // 而当前图如果还没有 .txt，它会凭空建一个空 caption 文件出来——
        // 启动时用户什么都没做，不该产生这种副作用。
        if (string.Equals(_cfg.LastMode, "draft", StringComparison.OrdinalIgnoreCase))
        {
            SwitchMode(PromptMode.Draft, saveCurrent: false);
        }
        else
        {
            // 标注模式是 XAML 里的初始状态，但标题和「主操作」文案仍要走一遍
            // 同一套外壳逻辑——否则启动后菜单写着「保存」而 Ctrl+S 的提示
            // 与实际行为是两个来源，改一处就会不一致。
            RebuildLibraryList();   // 写标签模式还没进，但库列表先备好
            ApplyModeChrome(PromptMode.Label);
        }

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

        // --draft 放在最后：它要覆盖"上次用的模式"（否则上次停在标注模式时
        // 这个开关就永远无效）。
        if (_draftAfterInit)
        {
            _draftAfterInit = false;
            SwitchMode(PromptMode.Draft, saveCurrent: false);
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
        // 换了训练集就清空历史。**必须在 Select 之前**：Select 会调
        // RememberCurrentDocument()，而 Remember 只改已有的桶（不新建），
        // 所以清完不会又被那份旧文档填回来。
        if (!string.Equals(_historyRoot, dir, StringComparison.OrdinalIgnoreCase))
        {
            Log.Write($"history cleared: dataset changed {_historyRoot} -> {dir}");
            _histories.Clear();
            _historyRoot = dir;
            // 写标签模式下 Select 会提前返回（不动当前草稿），所以"摘栈"不能只靠它；
            // 否则 _history 还指着刚被清空的旧桶，菜单状态会停在旧文案上。
            DetachHistory();
        }

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

    // ================= 模式切换 =================
    //
    // 两个模式共用中栏与右栏，差异集中在四处：
    //   1. 左栏显示谁（图片列表 / 提示词库）
    //   2. 当前文档是谁（LabelDoc 绑 .txt / DraftDoc 绑内存草稿）
    //   3. 主操作是什么（写盘 / 复制到剪贴板）
    //   4. 哪些菜单项有意义
    // 所以切换就是"换文档 + 刷界面"，没有第二套控件要维护。

    /// <summary>
    /// 切换模式。
    ///
    /// <paramref name="saveCurrent"/> 表示切换前要不要先把当前文档落定
    /// （标注侧静默保存 .txt，写标签侧把草稿写进库）。启动时恢复模式要传 false：
    /// 那时用户什么都没做，"保存"没有意义，而标注侧那条路会在当前图还没有
    /// .txt 时**凭空建一个空文件**（见 SaveCurrent 里对空正文的处理）。
    /// </summary>
    private void SwitchMode(PromptMode mode, bool saveCurrent = true)
    {
        if (_mode == mode) return;

        // 先把当前文档落定：切模式本身就是一次"离开"，
        // 不落定会丢掉刚才的编辑。
        if (saveCurrent) CommitCurrentDocument();

        var draft = mode == PromptMode.Draft;

        _mode = mode;

        // 分栏比例按模式各存一套。必须在改可见性之前换，否则会先按旧比例排一次版。
        ApplyColumnRatios();
        ApplyModeChrome(mode);

        // 打开目标模式的文档
        if (draft)
        {
            // **必须先重建库列表**：OpenDraft 要在 _libRows 里按 id 找到当前那条
            // 并把选中态标上去，列表还是空的就选不中（表现为"载入了提示词但左栏
            // 一行都没高亮"）。启动直接进写标签模式时这里最容易漏——那条路上
            // 没有任何别的地方会构建这个列表。
            RebuildLibraryList();
            OpenDraft(_cfg.DraftLastPromptId);
        }
        else
        {
            OpenLabelDocument();
        }

        _cfg.LastMode = draft ? "draft" : "label";
        _cfg.Save();

        // 状态栏与列表按新文档刷新
        RefreshTagRows(resetScroll: true);
        RefreshStatusBar();
        Log.Write($"mode switched: {_mode} doc={_doc?.Identity ?? "(none)"}");
    }

    /// <summary>
    /// 按模式切换界面外壳：哪些菜单/面板可见、窗口标题、提示词原文的去向说明。
    ///
    /// 抽成一个方法而不是在 SwitchMode 里内联，是因为启动恢复模式时也要走
    /// 同一套——两处各写一份必然漂移，而漂移的表现是"启动时标题不对、
    /// 或者该隐藏的菜单还露着"。
    /// </summary>
    private void ApplyModeChrome(PromptMode mode)
    {
        var draft = mode == PromptMode.Draft;

        ImagePane.Visibility = draft ? Visibility.Collapsed : Visibility.Visible;
        PromptPane.Visibility = draft ? Visibility.Visible : Visibility.Collapsed;
        BrowseMenu.Visibility = draft ? Visibility.Collapsed : Visibility.Visible;
        PromptMenu.Visibility = draft ? Visibility.Visible : Visibility.Collapsed;
        OpenFolderItem.Visibility = draft ? Visibility.Collapsed : Visibility.Visible;
        StatsItem.Visibility = draft ? Visibility.Collapsed : Visibility.Visible;
        AutoArrangeItem.Visibility = draft ? Visibility.Visible : Visibility.Collapsed;
        CopyPromptBtn.Visibility = draft ? Visibility.Visible : Visibility.Collapsed;
        // 转义开关只在写标签模式出现：标注模式的 caption 是直接喂给分词器训练的，
        // 反斜杠会变成垃圾 token 污染训练集。
        EscapeParensBox.Visibility = draft ? Visibility.Visible : Visibility.Collapsed;
        // 勾选状态直接反映配置。事件挂在 Click 上（不是 Checked/Unchecked），
        // 所以这里的程序性赋值不会回调、不会绕回来再重建一次。
        EscapeParensBox.IsChecked = _cfg.EscapeTagParens;
        PrevBtn.Visibility = NextBtn.Visibility = draft ? Visibility.Collapsed : Visibility.Visible;
        PreviewBtn.Visibility = draft ? Visibility.Collapsed : Visibility.Visible;

        // 单选菜单项要与实际模式一致：用户从 Ctrl+M 切过来时，
        // 菜单里的勾选不会自己跟着走。
        LabelModeItem.IsChecked = !draft;
        DraftModeItem.IsChecked = draft;

        Title = draft ? "AnimaCaptioner — 写标签" : "AnimaCaptioner — 标注";
        CaptionTargetRun.Text = draft ? "（复制走）" : "（写入 .txt）";
        PrimaryActionItem.Text = draft ? "复制提示词" : "保存";

        if (draft) UpdatePinMenuItemState();
    }

    /// <summary>
    /// 当前是否给标签里的圆括号转义（见 <see cref="TagEscape"/>）。
    ///
    /// **只在写标签模式生效，这一点不能松**：标注模式的 caption 是直接喂给分词器
    /// 训练的（<c>truncation=True</c> 那条路径），反斜杠会被切成垃圾 token 污染训练集。
    /// 转义是推理侧语法，不是训练侧。
    /// </summary>
    private bool EscapeEnabled => _mode == PromptMode.Draft && _cfg.EscapeTagParens;

    /// <summary>词库认不认得这个标签——转义的唯一判据，见 <see cref="TagEscape.ShouldEscape"/>。</summary>
    private bool IsKnownTag(string tag) => TagSections.IsKnownTag(tag, _vocab);

    /// <summary>
    /// 从提示词框读正文并按当前模式的转义设置解析。
    ///
    /// **所有"从文本框回读"的路径都必须走这里。** 转义只在写进框里时加，
    /// 回读时不还原的话，反斜杠会进到标签列表，下一次提交再转义一遍就变成
    /// 两层（<c>\\(</c>）——那时 ComfyUI 看到的是字面反斜杠。
    /// </summary>
    private PromptText.Parts ParseCaption(string text) =>
        PromptText.Parse(text, _vocab, EscapeEnabled);

    /// <summary>
    /// 把正文规范成"送进模型的线上格式"：给该转义的标签补上括号转义。
    ///
    /// <para>复制时也走一遍，是为了兜住**直接往提示词框里打字/粘贴**这条路——
    /// 那条路不经过标签列表，也就不会触发 <see cref="CommitPrompt"/>，
    /// 于是开关看起来"没生效"。标签列表那条路（搜索、翻译、粘贴标签、上移下移）
    /// 本来就会转义，走这里只是再确认一遍。</para>
    ///
    /// <para>**这一步是幂等的**：已经转义过的标签（<c>210 \(reverse:1999\)</c>）
    /// 带着反斜杠，词库查不到它，<see cref="TagEscape.ShouldEscape"/> 返回 false，
    /// 不会再加一层反斜杠。所以可以放心重复调用。</para>
    /// </summary>
    private string NormalizeEscapes(string text)
    {
        if (!EscapeEnabled) return text;
        var parts = PromptText.Parse(text, _vocab, unescapeTags: true);
        if (parts.Tags.Count == 0) return text;
        return PromptText.Rebuild(TagEscape.EscapeAll(parts.Tags, IsKnownTag), parts.ProseTail);
    }

    /// <summary>
    /// 转义开关。改完立刻按新设置重建正文——**关掉时要把已有的反斜杠收回来**，
    /// 否则框里会留着上一次的转义。
    ///
    /// 不用动标签列表：<c>_pendingTags</c> 里存的**始终是字面量**（回读时已经还原过），
    /// 所以重建一次就够。
    /// </summary>
    private void EscapeParensBox_Click(object sender, RoutedEventArgs e)
    {
        _cfg.EscapeTagParens = EscapeParensBox.IsChecked == true;
        _cfg.Save();

        CommitPrompt();
        Log.Write($"escape parens: {_cfg.EscapeTagParens} " +
                  $"affected={TagEscape.CountEscaped(_pendingTags, IsKnownTag)}");
    }

    /// <summary>把当前文档落定。标注 = 静默保存；写标签 = 把草稿写进库。</summary>
    private void CommitCurrentDocument()
    {
        if (_mode == PromptMode.Label) SaveCurrent(showDialog: false);
        else FlushDraftToLibrary();
    }

    /// <summary>打开标注模式下当前选中的那张图。</summary>
    private void OpenLabelDocument()
    {
        if (_index < 0 || _index >= _items.Count)
        {
            // 没有当前图片：把栈摘下来（撤销点属于某一张具体的图，不能留给"下一张"）
            RememberCurrentDocument();
            _doc = null;
            _pendingTags = new List<string>();
            _proseTail = "";
            SetCaptionText("");
            RefreshStatusBar();
            DetachHistory();
            return;
        }

        // 换文档前先记下这一份"离开时的内容"，供下次打开时校验历史是否还有效。
        // 位置很讲究：调用方（Next/Prev/JumpFirst）都是**先** SaveCurrent 再过来，
        // 所以此时 _pendingTags 正是刚写进盘里的那份内容。
        RememberCurrentDocument();

        _doc = new LabelDoc { Item = _items[_index], Index = _index };
        LoadCurrent();
    }

    /// <summary>
    /// 打开写标签模式下的一条草稿。<paramref name="id"/> 为空或找不到时新建一份空的。
    /// </summary>
    private void OpenDraft(string? id)
    {
        var entry = id is { Length: > 0 } ? _lib.Get(id) : null;
        var doc = DraftDoc.New();

        // 同 OpenLabelDocument：先记下离开时那份草稿的内容。
        RememberCurrentDocument();

        // 新建草稿前必须丢掉共用键上残留的历史。
        //
        // "尚未入库"这个身份是**所有新草稿共用**的，所以桶里可能躺着上一份草稿的
        // 撤销栈。正常路径上它已经随入库搬走了（FlushDraftToLibrary 里的 Migrate），
        // 但被放弃的那份（建了又没入库）只能在这里清。判断条件带上"当前文档不是
        // 新草稿"：否则会把**正在编辑的**这份新草稿的历史清掉。
        if (entry is null &&
            !string.Equals(_doc?.Identity, DraftDoc.NewIdentity, StringComparison.Ordinal))
        {
            _histories.Drop(DraftDoc.NewIdentity);
        }

        _loading = true;
        try
        {
            if (entry is not null)
            {
                doc.Id = entry.Id;
                doc.Title = entry.Title;
                // 库里的标题是既成事实（可能是用户起的，也可能是派生的），
                // 都当作"已有名字"，不再被下一次 Flush 重新派生覆盖。
                doc.HasCustomTitle = true;
                var raw = entry.Body;
                // 库里的正文存的是**线上格式**（带 \( \) 转义），和提示词框同一个口径，
                // 所以回读必须走 ParseCaption 还原成字面量。用裸 Parse 的话：
                //   1. 标签列表显示 `210 \(reverse:1999\)`，和"列表显示干净名字"的约定不符；
                //   2. 按身份分桶的撤销历史会因为"内容和离开时对不上"被当成外部修改丢掉。
                var parsed = ParseCaption(raw);
                _pendingTags = new List<string>(parsed.Tags);
                _proseTail = parsed.ProseTail;
                SetCaptionText(raw);

                var row = _libRows.FirstOrDefault(r => r.Id == entry.Id);
                _libGuard = true;
                try { PromptList.SelectedItem = row; } catch { }
                finally { _libGuard = false; }
            }
            else
            {
                // 新草稿：从"官方推荐前缀"起步，而不是全空。
                // 依据是官方模型卡的推荐串 `masterpiece, best quality, score_7, safe`
                // ——它是每一次生图都需要的那一段，空着反而每次都要手打。
                var seed = "masterpiece, best quality, score_7, safe";
                var parsed = PromptText.Parse(seed, _vocab);
                _pendingTags = new List<string>(parsed.Tags);
                _proseTail = "";
                SetCaptionText(seed);
                PromptList.SelectedItem = null;
            }
        }
        finally { _loading = false; }

        _doc = doc;
        RefreshStatusBar();
        // 挂到这条草稿自己的撤销历史上（与 LoadCurrent 同理）。
        AttachHistory();
    }

    /// <summary>
    /// 把当前草稿写进提示词库（去重后更新）。正文为空则不写——
    /// 库里堆一串空条目没有任何用处，而且会把真正的条目挤下去。
    /// </summary>
    private void FlushDraftToLibrary()
    {
        if (_mode != PromptMode.Draft) return;
        if (_doc is not DraftDoc doc) return;

        var body = GetCaptionText().Trim();
        if (body.Length == 0) return;

        // 只有用户真的命名过才把标题传下去，否则传 null 让库里派生。
        // **不能把占位名当标题传**：Update 见到非空标题就会覆盖，
        // 于是一条正文很具体的提示词会永远挂着「未命名草稿」这个名字
        // （实测踩到：Uses 累加到 2，标题却还是占位串）。
        var customTitle = doc.HasCustomTitle ? doc.Title : null;

        var before = doc.Id;
        var entry = doc.Id.Length > 0
            ? _lib.Update(doc.Id, body, customTitle)
            : null;
        entry ??= _lib.Add(body, pinned: false, title: customTitle);

        doc.Id = entry.Id;
        // 库里派生出来的标题要回写到文档上，否则左栏高亮的那条与
        // 窗口里显示的名字会不一致（重命名对话框也会带着占位串打开）。
        if (!doc.HasCustomTitle && entry.Title.Length > 0) doc.Title = entry.Title;

        // 首次入库会拿到 id，**文档身份随之改变**，所以撤销历史必须跟着搬过去
        // （_history 本身不用换：搬的是桶，栈对象还是同一个）。
        // 不搬的两个后果：那条草稿从库里重新打开时历史凭空消失；
        // 而"尚未入库"是个**共用键**，下一份全新草稿会继承这份的撤销栈。
        if (before != entry.Id)
        {
            _histories.Migrate(DraftDoc.IdentityOf(before), DraftDoc.IdentityOf(entry.Id));
            _cfg.DraftLastPromptId = entry.Id;
        }

        _lib.Save();
        RebuildLibraryList();

        // 首次入库会拿到 id，菜单项文案也随之确定下来。不刷新的话，
        // 正文恰好与一条**已钉住的**预设相同时，菜单仍写着「钉为预设」。
        UpdatePinMenuItemState();
    }

    /// <summary>刷新写标签模式左栏的列表。</summary>
    private void RebuildLibraryList()
    {
        var filter = LibFilterBox?.Text ?? "";
        var items = filter.Trim().Length > 0 ? _lib.Search(filter) : _lib.Snapshot();

        _libGuard = true;
        try
        {
            _libRows.Clear();
            foreach (var e in items) _libRows.Add(new PromptLibRow { Source = e, Vocab = _vocab });

            // 选中当前文档对应的那一行
            var current = (_doc as DraftDoc)?.Id;
            PromptList.SelectedItem = current is { Length: > 0 }
                ? _libRows.FirstOrDefault(r => r.Id == current)
                : null;
        }
        finally { _libGuard = false; }

        LibCountText.Text = _libRows.Count == 0
            ? ""
            : $"{_libRows.Count} 条 · 预设 {_lib.PinnedCount}";
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
        // 写标签模式的状态栏报的是 token 预算与安全档位，不是数据集统计。
        // 目录扫描（LoadFolder）也可能在切模式后被触发，那时不该把写标签的
        // 状态栏覆盖成数据集口径。
        if (_mode != PromptMode.Label)
        {
            RefreshStatusBar();
            return;
        }

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

    /// <summary>
    /// 按模式刷新底部状态栏。
    ///
    /// 两个模式要报的东西不同：标注关心"这个数据集标了多少"，写标签关心
    /// "这段提示词能不能用"——即 token 预算与安全档位。后两项在生图时比在
    /// 标注时更有用：超 512 训练侧会静默截断，安全词缺失/矛盾会让出图结果
    /// 和预期差一档。
    ///
    /// 刻意复用同一套算法（<see cref="PromptText.EstimateTokens"/>、
    /// <see cref="SafetyRating.Check"/>），而不是另写一份口径——两处口径不同
    /// 必然出现"状态栏说没超、实际超了"。
    /// </summary>
    private void RefreshStatusBar()
    {
        if (_mode == PromptMode.Label)
        {
            // 标注模式的状态栏由 UpdateStats / LoadCurrent / Select 维护，
            // 这里不插手，避免把它们的显示覆盖成写标签口径。
            return;
        }

        var raw = GetCaptionText();
        if (_doc is null || raw.Trim().Length == 0)
        {
            DatasetStatsText.Text = "没有提示词 · 从「提示词 → 新建提示词」(Ctrl+N) 开始";
            SelectionText.Text = "";
            return;
        }

        // 安全档位：既报"写了什么"，也报"按判据该是什么"。两者不一致时
        // 正是要提醒的时刻——而这个判断在标注侧只有点「校验」才看得到。
        var verdict = SafetyRating.Check(_pendingTags);
        var declared = verdict.Declared ?? "缺安全词";
        var safety = verdict.Notes.Count > 0
            ? $"{declared} → 判据 {verdict.Expected} ⚠"
            : declared;

        var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        // token 那一节直接取自 PromptText.Summarize —— 它与左栏列表行同源，
        // 免得两处各算一份、出现"列表说没超、状态栏说超了"。
        // 标签数同理（Summarize 内部走 PromptText.Parse），所以右格不再重复报它，
        // 免得出现"左边 3 tags、右边 4 tags"这种自相矛盾的显示（实测踩到）。
        DatasetStatsText.Text = PromptText.Summarize(raw, _vocab, EscapeEnabled) + $" · 安全 {safety}";
        SelectionText.Text = $"{words} 词 · {raw.Length} 字符";
    }

    // ================= 选择与载入 =================

    private void Select(int i)
    {
        // 写标签模式下**不能**动当前文档：那个模式正在编辑一段内存草稿，
        // 而 _index 只是数据集里的位置。_doc 若在这里被换成 LabelDoc（或置空），
        // 用户正在写的那段提示词就被一张图片的 caption 顶掉了。
        //
        // 界面上图片列表与「打开训练集」都已隐藏，但"最近打开"仍在文件菜单里，
        // 点它就会触发一次目录重载 —— 这条路必须挡住，所以守卫放在最前面，
        // 连 _items.Count == 0 那个清空分支也一并跳过。
        if (_mode != PromptMode.Label)
        {
            _index = _items.Count == 0 ? -1 : Math.Clamp(i, 0, _items.Count - 1);
            SyncListSelection();
            return;
        }

        // 换文档前记下这一份"离开时的内容"。**必须在 _doc 被替换之前**，
        // 而且位置正好：调用方（Next / Prev / JumpFirst / 首张）都是先
        // SaveCurrent 再 Select，所以此刻 _pendingTags 就是刚写进盘里的那份内容。
        RememberCurrentDocument();

        if (_items.Count == 0)
        {
            _index = -1;
            _doc = null;
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
            DetachHistory();
            return;
        }

        _index = Math.Clamp(i, 0, _items.Count - 1);

        // _doc 必须跟着 _index 走：标签操作的门禁看的是 _doc，而不是 _index。
        // 漏掉这一句的话，导航到第 2 张后 _doc 还指着第 1 张——身份对不上内容。
        _doc = new LabelDoc { Item = _items[_index], Index = _index };
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

        // 挂到这张图自己的撤销历史上。它上次离开时记的内容如果和刚读到的
        // 对得上，撤销栈就接着用（回到改错的那张图还能 Ctrl+Z）；
        // 对不上说明 .txt 被外部改过，Attach 会丢掉那份已经失效的历史。
        AttachHistory();

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
        // 转义只发生在**写进提示词框**这一步：_pendingTags 里始终存字面量，
        // 于是标签列表显示干净的名字（`210 (reverse:1999)`），而框里是送进模型的
        // 线上格式（`210 \(reverse:1999\)`）——两边各是各的用途，不互相污染。
        var tags = EscapeEnabled ? TagEscape.EscapeAll(_pendingTags, IsKnownTag) : _pendingTags;
        SetCaptionText(PromptText.Rebuild(tags, _proseTail));
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
                    foreach (var span in PromptText.ColorSpans(text, _vocab, EscapeEnabled))
                    {
                        var r = doc.GetRange(span.Start, span.Start + span.Length);
                        r.CharacterFormat.BackgroundColor = TagColors.TintOf(span.Section, _cfg, bg);
                        r.CharacterFormat.ForegroundColor = fg;
                    }

                    // 散文尾巴：整段一色，明确告诉用户"这块不是标签"
                    var parts = ParseCaption(text);
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

        // 写标签模式的状态栏报 token 预算与安全档位。放在这里是因为每次标签
        // 变动都会走到本方法，而这两项正是随标签变的。
        RefreshStatusBar();

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
    /// 收掉一个 AutoSuggestBox 的候选弹层。
    ///
    /// **必须在宿主控件离开可视树（或折叠）之前调用**：弹层是 AutoSuggestBox
    /// 模板里的 Popup，而 WinUI 3 的 Popup 是独立的顶层窗口
    /// （Microsoft.UI.Content.PopupWindowSiteBridge）。宿主被移出可视树之后，
    /// 那个顶层窗口**不会被隐藏**，就留下一个看不见、却仍然吃掉鼠标的窗口。
    ///
    /// 实测（2026-10-10）：一次就地编辑之后，候选弹层的 HWND 在标签列表上方
    /// 存活了 6 分钟；`WindowFromPoint` 命中的是它而不是列表——那一带的行点
    /// 不动、鼠标移上去也没反应，正是这个原因。窗口枚举确认会话里留下了三个
    /// 这样的窗口。用同一套手法也锤出过一次复现：残留窗口一旦出现，之后连续
    /// 8 次采样都还在（它不会自己消失，只能重启程序）。
    ///
    /// 这个竞态很窄（看起来只在弹层**首次展开**的那一瞬），锤出那一次之后
    /// 无法按需重放。所以这里是按机制兜底——凡是"丢弃/折叠候选弹层宿主"的
    /// 地方都先收干净——而不是依赖某一条能稳定复现的路径。
    /// </summary>
    private static void CloseSuggestions(AutoSuggestBox? box)
    {
        if (box is null) return;
        ForceClose(box);

        // 再补一跳：关闭可能正好落在弹层展开的中间，展开那一半随后才落地，
        // 弹层就留在屏幕上。这一跳排在当前这轮消息之后，能盖住控件自己的
        // Enter 处理与布局收尾。
        var q = box.DispatcherQueue;
        if (q is null) return;
        q.TryEnqueue(() =>
        {
            try { ForceClose(box); }
            catch { /* 宿主已被丢弃，弹层随之销毁 */ }
        });
    }

    /// <summary>
    /// 真正把候选弹层收掉。
    ///
    /// 只设 <c>IsSuggestionListOpen = false</c> 不够稳：那是控件级属性，它的
    /// 状态机在弹层展开的过程中可能吃掉这一下（展开随后落地，弹层就留在屏幕
    /// 上）。所以再补一手：把模板里那个 Popup 的 <c>IsOpen</c> 直接置 false
    /// ——它收的是窗口本身，不看控件的中间态。
    /// </summary>
    private static void ForceClose(AutoSuggestBox box)
    {
        box.IsSuggestionListOpen = false;
        box.ItemsSource = null;
        FindSuggestionsPopup(box)?.IsOpen = false;
    }

    /// <summary>模板里那个候选弹层。名字对不上就退回子树里的第一个 Popup。</summary>
    private static Microsoft.UI.Xaml.Controls.Primitives.Popup? FindSuggestionsPopup(DependencyObject root)
    {
        Microsoft.UI.Xaml.Controls.Primitives.Popup? first = null;
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.Primitives.Popup p)
            {
                if (p.Name == "SuggestionsPopup") return p;
                first ??= p;
            }
            if (FindSuggestionsPopup(child) is { } found) return found;
        }
        return first;
    }

    /// <summary>
    /// Shift 有没有按着。Shift+Tab 要往回切，而 KeyRoutedEventArgs 里只有
    /// 一个 VirtualKey，修饰键状态得另外问。
    /// </summary>
    private static bool ShiftDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                           .HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Ctrl 有没有按着。Ctrl+Z / Ctrl+Y 在输入框里要判修饰键。</summary>
    private static bool CtrlDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
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
            CloseSuggestions(sender);
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

    /// <summary>
    /// 加完标签后焦点仍留在这个框里（<see cref="AddTag"/> 会清空它），而
    /// "打完标签按回车、马上想反悔"正是最常按 Ctrl+Z 的时刻。框空着时输入框
    /// 自己的原生撤销无事可做，所以这时把 Ctrl+Z / Ctrl+Y 让给标签历史；
    /// 框里有字时仍然归输入框自己（那是用户在改刚打的字）。
    ///
    /// <para>**必须用 PreviewKeyDown（隧道）而不是 KeyDown（冒泡）**：AutoSuggestBox
    /// 内部的 TextBox 会把 Ctrl+Z 处理掉并标记 Handled，冒泡阶段的事件根本到不了
    /// 外层这个处理器。隧道是从根往下走，先经过这里。</para>
    /// </summary>
    private void AddTagBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!CtrlDown() || AddTagBox.Text.Length > 0) return;

        if (e.Key == Windows.System.VirtualKey.Z && _history.CanUndo)
        {
            UndoTagEdit();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Y && _history.CanRedo)
        {
            RedoTagEdit();
            e.Handled = true;
        }
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
    /// <paramref name="showDialog"/> 只控制"没有打开文档"要不要弹对话框：
    /// 主窗口里弹是对的（用户可能没意识到没打开），搜索窗口里弹会把主窗口
    /// 盖住、且消息本身返回给调用方了，所以不弹。
    ///
    /// 返回值语义：<c>Ok</c> 表示"这个标签现在在文档里了"——新加进去和本来就有
    /// 都算 true，只有"没有打开文档"才是 false。
    ///
    /// 门禁从"选中了图片"改成"打开了文档"：写标签模式没有图片，但有草稿文档，
    /// 而标签操作本来就不需要图片（只吃 _pendingTags + _vocab + _cfg）。
    /// </summary>
    private (bool Ok, string Message) AddTagCore(string text, bool showDialog)
    {
        if (_doc is null)
        {
            var m = _mode == PromptMode.Draft
                ? "先新建或载入一段提示词，再添加标签。"
                : "先选一张图再添加标签。";
            if (showDialog) _ = Info("没有打开文档", m);
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

        PushUndo("添加标签");
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
        PushUndo("规整大类顺序");
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
        // 写标签模式没有"上一张/下一张"的概念。它的按钮已经隐藏了，但
        // Alt+← / Alt+→ 是窗口级加速键，不挡住的话会在图片列表上乱走，
        // 而那个模式的 Ctrl+S 已经变成复制——两套导航语义混着用会误改图片。
        if (_mode != PromptMode.Label) return;

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
        // 写标签模式绝不能走这条路：这里会往 item.CaptionPath 写 .txt，
        // 而那个模式根本没有当前图片（_index 可能是上一次标注留下的残值）。
        // 那样会**静默改掉训练集里的某个 caption**。
        if (_mode != PromptMode.Label) return false;

        if (_index < 0 || _index >= _items.Count) return false;
        var item = _items[_index];

        // 从输入框重新解析（用户可能直接改过正文）。必须用 PromptText 而不是
        // 只切逗号：提示词里可能带一段自然语言尾巴，那些段不是标签，
        // 不能写进 _pendingTags，也不能在保存时被当成标签重排。
        // 走 ParseCaption 是为了和"所有从提示词框回读的路径"同一个口径
        // （标注模式下转义恒为关，所以这里与裸 Parse 等价，但规则只有一处）。
        var parsed = ParseCaption(GetCaptionText());
        var tags = parsed.Tags;
        _proseTail = parsed.ProseTail;
        _pendingTags = new List<string>(tags);

        try
        {
            if (item.Caption is null)
            {
                // 这张图还没有 .txt，而正文是空的：**不要落一个空文件**。
                //
                // 这条守卫修的是个真缺陷（不是为模式切换加的）：SaveCurrent 会在
                // "切到别的图"时被静默调用，所以以前只要在一张未标注的图上停留过
                // 再点下一张，就会凭空生成一个只有 CRLF 的 .txt。后果有三层——
                // 左栏把这张图标成「已标注」（0 tags）、「跳到下一个未标注」
                // 从此跳过它、而训练侧读到一个空白 caption。
                //
                // 注意只在"原本就没有文件"时跳过：已有文件的正文被用户清空是
                // 另一回事，那时应当如实写下去（SaveBody 内部对内容未变也已短路）。
                if (PromptText.Rebuild(tags, parsed.ProseTail, ", ").Trim().Length == 0)
                {
                    Log.Write($"skip creating empty caption for {Path.GetFileName(item.CaptionPath)}");
                    if (showDialog)
                        _ = Info("没有内容", "这一行还是空的，未创建 .txt。\n\n" +
                                             "先加几个标签或写一段描述，再保存。");
                    return false;
                }

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

    private void Save_Click(object sender, RoutedEventArgs e) => RunPrimaryAction(showDialog: true);

    // ================= 主操作（按模式分派）=================

    /// <summary>
    /// 主操作。标注模式写回 .txt，写标签模式复制到剪贴板。
    ///
    /// 这是两个模式唯一真正"不同"的行为，所以 Ctrl+S、菜单项、底部按钮
    /// 三处入口都收敛到这一个方法，而不是各自判断模式。
    /// </summary>
    private void RunPrimaryAction(bool showDialog)
    {
        if (_mode == PromptMode.Draft) CopyPromptToClipboard(showDialog);
        else SaveCurrent(showDialog);
    }

    /// <summary>
    /// 写标签模式的主操作：把整行提示词复制到剪贴板。
    ///
    /// 复制的是**提示词框里的原文**，不是重新拼一遍：用户在框里手改过的标点、
    /// 空格权重之类必须原样带走。这也是这个模式存在的理由——出图时要的是
    /// 一段可以直接粘进 ComfyUI 的文本。
    /// </summary>
    private void CopyPromptToClipboard(bool showDialog)
    {
        if (_doc is null)
        {
            TranslateInfoText.Text = "先新建或载入一段提示词。";
            return;
        }

        var raw = GetCaptionText();
        // 兜住"直接往提示词框里打字/粘贴"这条路（见 NormalizeEscapes）。
        var text = NormalizeEscapes(raw).Trim();
        // 写回框里，让"看到的"和"复制走的"始终是同一份。
        if (text != raw) SetCaptionText(text);

        if (text.Length == 0)
        {
            TranslateInfoText.Text = "提示词是空的，没有可复制的内容。";
            if (showDialog) _ = Info("没有内容", "这一段提示词还是空的。先在中间加几个标签，或在右上写一段英文。");
            return;
        }

        try
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
            Clipboard.Flush();   // 不 Flush 的话程序退出后剪贴板内容会丢

            // 复制即入库：这是"我现在在用这段提示词"最可靠的信号。
            FlushDraftToLibrary();

            // 预算按**编码器看到的**文本算：ComfyUI 会把 \( \) 的反斜杠吃掉再送分词器，
            // 直接拿转义后的正文估算会多算那些反斜杠（与 PromptText.Summarize 同一口径）。
            var visible = EscapeEnabled ? TagEscape.Unescape(text) : text;
            var tokens = PromptText.EstimateTokens(visible);
            TranslateInfoText.Text = $"已复制 {text.Length} 字符 · ≈{tokens} tokens。" +
                (tokens > PromptText.MaxTokens ? " ⚠ 超过 512，训练侧会被截断。" : "");
            Log.Write($"prompt copy: len={text.Length} tokens={tokens} doc={_doc.Identity}");
        }
        catch (Exception ex)
        {
            Log.Write("prompt copy failed: " + ex.Message);
            TranslateInfoText.Text = "复制失败：" + ex.Message;
        }
    }

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

    // ================= 菜单：模式 =================

    private void LabelMode_Click(object sender, RoutedEventArgs e) => SwitchMode(PromptMode.Label);
    private void DraftMode_Click(object sender, RoutedEventArgs e) => SwitchMode(PromptMode.Draft);

    private void ToggleMode_Click(object sender, RoutedEventArgs e)
        => SwitchMode(_mode == PromptMode.Draft ? PromptMode.Label : PromptMode.Draft);

    private void PrimaryAction_Click(object sender, RoutedEventArgs e) => RunPrimaryAction(showDialog: true);

    // ================= 菜单：提示词库 =================

    private void NewPrompt_Click(object sender, RoutedEventArgs e)
    {
        // 新建前先把当前草稿落库，否则它会被这份新草稿顶掉。
        FlushDraftToLibrary();

        _cfg.DraftLastPromptId = "";
        OpenDraft(null);
        RebuildLibraryList();
        RefreshTagRows(resetScroll: true);
        RefreshStatusBar();

        AddTagBox.Focus(FocusState.Programmatic);
        TranslateInfoText.Text = "已新建提示词（起点是官方推荐前缀）。";
        Log.Write("new prompt draft");
    }

    private void SavePromptToLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is not DraftDoc)
        {
            TranslateInfoText.Text = "只有在写标签模式下才能存进提示词库。";
            return;
        }

        var body = GetCaptionText().Trim();
        if (body.Length == 0)
        {
            TranslateInfoText.Text = "提示词是空的，没有可保存的内容。";
            return;
        }

        FlushDraftToLibrary();
        RebuildLibraryList();
        TranslateInfoText.Text = "已保存到提示词库。";
    }

    /// <summary>把当前草稿钉为预设 / 取消钉住。预设永不被历史上限清掉。</summary>
    private void PinPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is not DraftDoc doc || doc.Id.Length == 0)
        {
            TranslateInfoText.Text = "先保存到提示词库，再钉为预设。";
            return;
        }

        var entry = _lib.Get(doc.Id);
        if (entry is null) { TranslateInfoText.Text = "这条提示词已不在库里。"; return; }

        _lib.SetPinned(doc.Id, !entry.Pinned);
        _lib.Save();
        RebuildLibraryList();
        UpdatePinMenuItemState();
        TranslateInfoText.Text = entry.Pinned ? "已取消钉住（会随历史上限被清理）。" : "已钉为预设。";
    }

    private void RenamePrompt_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is not DraftDoc doc) { TranslateInfoText.Text = "先切到写标签模式。"; return; }
        _ = RenameCurrentPromptAsync(doc);
    }

    private async Task RenameCurrentPromptAsync(DraftDoc doc)
    {
        var box = new TextBox { Text = doc.Title, PlaceholderText = "提示词名字" };
        var dlg = new ContentDialog
        {
            Title = "重命名提示词",
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var name = box.Text.Trim();
        if (name.Length == 0) return;

        doc.Title = name;
        doc.HasCustomTitle = true;   // 从此不再让库里的派生标题覆盖它
        if (doc.Id.Length > 0)
        {
            _lib.Update(doc.Id, GetCaptionText().Trim(), name);
            _lib.Save();
            RebuildLibraryList();
        }
        TranslateInfoText.Text = $"已重命名为「{name}」。";
    }

    private void ClearPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        // 已经空了就不记撤销点，否则 Ctrl+Z 会退到一个同样空白的界面
        if (GetCaptionText().Trim().Length > 0) PushUndo("清空提示词");
        SetCaptionText("");
        _pendingTags = new List<string>();
        _proseTail = "";
        RefreshTagRows(resetScroll: true);
        RefreshStatusBar();
        TranslateInfoText.Text = "已清空当前提示词。";
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var n = _lib.ClearHistory();
        _lib.Save();
        RebuildLibraryList();
        TranslateInfoText.Text = n > 0
            ? $"已清空 {n} 条历史（预设保留）。"
            : "没有可清空的历史（预设不会被清）。";
    }

    /// <summary>「钉为预设」菜单项的文案跟着当前条目走。</summary>
    private void UpdatePinMenuItemState()
    {
        var pinned = (_doc as DraftDoc)?.Id is { Length: > 0 } id
                     && _lib.Get(id)?.Pinned == true;
        PinPromptItem.Text = pinned ? "取消钉住" : "钉为预设";
    }

    // ================= 写标签模式：左栏库列表 =================

    private void LibFilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        RebuildLibraryList();
    }

    /// <summary>
    /// 单击库里的一行就载入它。先落定当前草稿，否则刚改的内容会丢。
    /// </summary>
    private void PromptList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_libGuard) return;
        if (PromptList.SelectedItem is not PromptLibRow row) return;
        if ((_doc as DraftDoc)?.Id == row.Id) return;   // 已经在这条上

        FlushDraftToLibrary();

        _cfg.DraftLastPromptId = row.Id;
        OpenDraft(row.Id);
        RefreshTagRows(resetScroll: true);
        RefreshStatusBar();
        UpdatePinMenuItemState();
        Log.Write($"prompt loaded: {row.Id} '{row.Title}'");
    }

    /// <summary>右键库列表：钉住/取消、重命名、删除。</summary>
    private void PromptList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var row = (e.OriginalSource as FrameworkElement)?.DataContext as PromptLibRow;
        if (row is null) return;

        var menu = new MenuFlyout();

        var pin = new MenuFlyoutItem { Text = row.Pinned ? "取消钉住" : "钉为预设" };
        pin.Click += (_, _) =>
        {
            _lib.SetPinned(row.Id, !row.Pinned);
            _lib.Save();
            RebuildLibraryList();
            UpdatePinMenuItemState();
        };
        menu.Items.Add(pin);

        var rename = new MenuFlyoutItem { Text = "重命名…" };
        rename.Click += (_, _) =>
        {
            var entry = _lib.Get(row.Id);
            if (entry is null) return;
            var doc = new DraftDoc { Id = entry.Id, Title = entry.Title };
            _ = RenameCurrentPromptAsync(doc);
        };
        menu.Items.Add(rename);

        menu.Items.Add(new MenuFlyoutSeparator());

        var del = new MenuFlyoutItem { Text = "从库里删除", Icon = new SymbolIcon(Symbol.Delete) };
        del.Click += (_, _) =>
        {
            _lib.Remove(row.Id);
            _lib.Save();
            // 条目没了，它那份撤销历史也该丢——留着既没人能用，又占着 LRU 名额。
            _histories.Drop(DraftDoc.IdentityOf(row.Id));
            // 删掉的正是当前这条时要开一份新草稿，否则界面停在一个已不存在的文档上
            if ((_doc as DraftDoc)?.Id == row.Id)
            {
                _cfg.DraftLastPromptId = "";
                OpenDraft(null);
                RefreshTagRows(resetScroll: true);
                RefreshStatusBar();
            }
            RebuildLibraryList();
            TranslateInfoText.Text = $"已从库里删除「{row.Title}」。";
        };
        menu.Items.Add(del);

        menu.ShowAt(PromptList, new FlyoutShowOptions { Position = e.GetPosition(PromptList) });
        e.Handled = true;
    }

    /// <summary>
    /// 按热度自动排段内顺序。**只该在写标签模式用**：标注侧的人在手动调
    /// 段内顺序，Canonicalize 刻意保留人工顺序，自动重排会把调好的弄丢。
    /// </summary>
    private void AutoArrange_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingTags.Count == 0)
        {
            TranslateInfoText.Text = "没有标签可排。";
            return;
        }

        var before = new List<string>(_pendingTags);
        // Arrange 会按 prior 重排 body，并把头部控制词归到官方段序。
        // rating / artist 不传：它们已经在 _pendingTags 里，传了会重复。
        var after = VocabDb.Arrange(_pendingTags, _prior);

        if (before.SequenceEqual(after, StringComparer.Ordinal))
        {
            TranslateInfoText.Text = "顺序已经是按热度排好的。";
            return;
        }

        _undoPrompt = GetCaptionText();
        PushUndo("按热度排序");
        _pendingTags = after;
        CommitPrompt();
        UndoTranslateBtn.IsEnabled = true;
        UndoMenuItem.IsEnabled = true;

        var moved = 0;
        for (var i = 0; i < Math.Min(before.Count, after.Count); i++)
            if (!string.Equals(before[i], after[i], StringComparison.Ordinal)) moved++;

        TranslateInfoText.Text = $"已按热度重排段内顺序（{moved} 个标签换位，大类顺序按官方段序）。"
            + (_prior.Count == 0 ? " 注意：当前训练集没有可用于统计的 caption。" : "");
        Log.Write($"auto arrange: {before.Count} tags, {moved} moved, prior={_prior.Count}");
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

        PushUndo(delta < 0 ? "上移" : "下移");
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
        PushUndo("删除标签");
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
        if (_doc is null)
        {
            TranslateInfoText.Text = _mode == PromptMode.Draft
                ? "先新建或载入一段提示词，再粘贴标签。"
                : "先选一张图再粘贴标签。";
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

        PushUndo("粘贴标签");
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

        PushUndo("规范化标签");
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

    /// <summary>
    /// 容器被回收去显示另一行时，收掉它里面那个行内编辑框的候选弹层。
    ///
    /// 兜底用的第三条路：正在编辑的行被滚出视口时，ListView 会直接回收它的
    /// 容器，不走 EndEdit / AbandonEdit——那时弹层的宿主被移出可视树，同样会
    /// 留下一个吃掉鼠标的孤儿窗口（见 <see cref="CloseSuggestions"/>）。
    /// 不回收容器（首屏）时这个事件也会来，所以只在 InRecycleQueue 时动手。
    /// </summary>
    private void TagList_ContainerContentChanging(ListViewBase sender,
                                                  ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) CloseSuggestions(FindEditBox(args.ItemContainer));
    }

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
        CloseSuggestions(box);
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
            CloseSuggestions(sender);
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

        // 同 EndEdit：折叠输入框之前先收弹层，否则留下孤儿弹层窗口
        CloseSuggestions(_editingBox);
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

        // 收候选弹层要赶在 row.IsEditing = false 之前：那一下会把输入框折叠，
        // 宿主一离开可视树，弹层的顶层窗口就再也没人收，变成一个吃掉鼠标的
        // 孤儿窗口（见 CloseSuggestions 里的实测记录）。
        CloseSuggestions(_editingBox);
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

        // 名字没变就不记撤销点：TagEdit 规范化后与原写法相同是常见情况
        // （比如本来就已经是正典名），记了会让 Ctrl+Z 退到一个一模一样的界面。
        if (!string.Equals(oldTag, newTag, StringComparison.Ordinal)) PushUndo("重命名标签");

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
            var after = TagSections.Canonicalize(rest, _vocab);

            // 拖回原位是常见操作（手一抖就放下了），结果与现在相同就不记撤销点、
            // 也不重建列表——否则 Ctrl+Z 会退到一个看不出区别的界面。
            if (!after.SequenceEqual(_pendingTags, StringComparer.Ordinal))
            {
                PushUndo("拖动排序");
                _pendingTags = after;
                CommitPrompt();
            }

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
    /// 写标签模式的默认比例。左栏是提示词库（只有标题和一行摘要），
    /// 不需要标注模式给图片列表留的那么宽，所以左栏收到 1.4。
    /// </summary>
    private static readonly double[] DraftDefaultStars = { 1.4, 2, 3 };

    /// <summary>当前模式对应的默认比例。</summary>
    private double[] ModeDefaults => _mode == PromptMode.Draft ? DraftDefaultStars : DefaultStars;

    /// <summary>
    /// 读当前模式的那一组比例。**两个模式各存一套**：它们的分栏语义不同
    /// （标注是图片 / 标签 / 提示词，写标签是提示词库 / 标签 / 提示词），
    /// 共用一组值会让一个模式里拖过的布局把对方的布局改掉。
    ///
    /// 写标签那一组还没存过时（三个都是 0）回落到它自己的默认值，
    /// 而不是回落到标注模式的比例——否则第一次进写标签模式会继承一个
    /// 为图片列表调过的、偏宽的左栏。
    /// </summary>
    private double[] ReadColumnRatios()
    {
        if (_mode == PromptMode.Draft)
        {
            var d = new[] { _cfg.DraftLeftStar, _cfg.DraftMidStar, _cfg.DraftRightStar };
            if (d[0] <= 0 && d[1] <= 0 && d[2] <= 0) return (double[])DraftDefaultStars.Clone();
            return d;
        }
        return new[] { _cfg.LeftStar, _cfg.MidStar, _cfg.RightStar };
    }

    /// <summary>写当前模式的那一组比例。</summary>
    private void WriteColumnRatios(double left, double mid, double right)
    {
        if (_mode == PromptMode.Draft)
        {
            _cfg.DraftLeftStar = left;
            _cfg.DraftMidStar = mid;
            _cfg.DraftRightStar = right;
        }
        else
        {
            _cfg.LeftStar = left;
            _cfg.MidStar = mid;
            _cfg.RightStar = right;
        }
    }

    /// <summary>
    /// 把当前模式的分栏比例应用到三栏。
    /// 存 * 比例而不是像素：换窗口大小、换显示器后仍是同一个布局意图。
    /// 越界或损坏的值直接回落默认——否则某栏可能被压成 0 宽且拖不回来。
    /// </summary>
    private void ApplyColumnRatios()
    {
        var s = ReadColumnRatios();
        var ok = true;
        foreach (var v in s)
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0.1 || v > 100) { ok = false; break; }

        if (!ok)
        {
            Log.Write($"column ratios invalid ({string.Join(", ", s)}), using defaults");
            s = ModeDefaults;
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
                WriteColumnRatios(7.0 * l / total, 7.0 * m / total, 7.0 * r / total);
            }
            else
            {
                // 窗口还没布局完（ActualWidth 全为 0）时退回名义值
                WriteColumnRatios(ColLeft.Width.Value, ColMid.Width.Value, ColRight.Width.Value);
            }

            _cfg.Save();
            Log.Write($"column ratios saved [{_mode}] " +
                      $"{ReadColumnRatios()[0]:F2}:{ReadColumnRatios()[1]:F2}:{ReadColumnRatios()[2]:F2}");
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
            WriteColumnRatios(stars[0], stars[1], stars[2]);
            _cfg.Save();
            Log.Write($"column ratios saved (nominal) [{_mode}] " +
                      $"{stars[0]:F2}:{stars[1]:F2}:{stars[2]:F2}");
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

    private void ResetColumns() => ApplyColumns(ModeDefaults, save: true, nominal: true);

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
            PushUndo("翻译散文");

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
                var parsed = ParseCaption(GetCaptionText());
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

    // ================= 撤销 / 重做 =================

    /// <summary>
    /// 记一个撤销点。**所有会改动文档的命令在真正动手之前调它**，
    /// 参数是操作名（用于「已撤销：添加标签」）。
    ///
    /// 位置很讲究：必须放在各命令自己那句"没东西可改就提前返回"**之后**，
    /// 否则一次什么都没做的点击也会被记成一个撤销点，用户按 Ctrl+Z 会发现
    /// "撤销了但界面没变"。
    /// </summary>
    private void PushUndo(string label)
    {
        if (_history.Push(_pendingTags, _proseTail, label)) UpdateUndoMenuState();
    }

    private void UndoEdit_Click(object sender, RoutedEventArgs e) => UndoTagEdit();
    private void RedoEdit_Click(object sender, RoutedEventArgs e) => RedoTagEdit();

    private void UndoTagEdit()
    {
        var snap = _history.Undo(_pendingTags, _proseTail);
        if (snap is null) { TranslateInfoText.Text = "没有可撤销的操作。"; return; }
        ApplySnapshot(snap, "已撤销");
    }

    private void RedoTagEdit()
    {
        var snap = _history.Redo(_pendingTags, _proseTail);
        if (snap is null) { TranslateInfoText.Text = "没有可重做的操作。"; return; }
        ApplySnapshot(snap, "已重做");
    }

    /// <summary>
    /// 把一份快照恢复成当前状态。**只改内存**：不写 .txt、不写提示词库。
    /// 落盘仍旧走保存那条路——撤销顺手写文件会在用户没按保存的情况下
    /// 静默改掉训练集里的 caption。
    /// </summary>
    private void ApplySnapshot(PromptSnapshot snap, string verb)
    {
        _pendingTags = new List<string>(snap.Tags);
        _proseTail = snap.ProseTail;

        CommitPrompt();
        RefreshStatusBar();
        UpdateUndoMenuState();

        TranslateInfoText.Text = snap.Label.Length > 0 ? $"{verb}：{snap.Label}" : verb;
        Log.Write($"history {verb}: {snap.Label} -> {_pendingTags.Count} tags " +
                  $"undo={_history.UndoCount} redo={_history.RedoCount}");
    }

    /// <summary>按栈的现状刷新「编辑」菜单两项：能不能点、以及下一次退掉的是哪一步。</summary>
    private void UpdateUndoMenuState()
    {
        UndoEditItem.IsEnabled = _history.CanUndo;
        RedoEditItem.IsEnabled = _history.CanRedo;

        // 文案带上操作名，点之前就知道会退掉什么。
        UndoEditItem.Text = _history.CanUndo ? $"撤销（{_history.UndoLabel}）" : "撤销";
        RedoEditItem.Text = _history.CanRedo ? $"重做（{_history.RedoLabel}）" : "重做";
    }

    /// <summary>
    /// 记下当前文档"离开时的内容"，供下次打开时校验它的撤销历史还有没有效。
    ///
    /// <para>**只需要在离开这一刻记**：盘上 / 库里的内容正是这一刻写下去的，
    /// 所以它就是唯一正确的基线。位置也必须在这里——各调用方都是先
    /// <see cref="CommitCurrentDocument"/> 或 SaveCurrent 再换文档，
    /// 所以此刻 _pendingTags 才等于刚存下去的那份内容。</para>
    /// </summary>
    private void RememberCurrentDocument() =>
        _histories.Remember(_doc?.Identity, _pendingTags, _proseTail);

    /// <summary>
    /// 把撤销栈挂到当前文档那一桶上（打开文档后调）。
    ///
    /// 桶里记的"离开时的内容"和刚读到的对不上时，<see cref="PromptHistoryStore.Attach"/>
    /// 会丢掉那份已经失效的历史：撤销点指向的是已经不存在的内容，
    /// 留着会让 Ctrl+Z 把外部改动退掉，而离开时的自动保存又会把它写死。
    /// </summary>
    private void AttachHistory()
    {
        _history = _histories.Attach(_doc?.Identity, _pendingTags, _proseTail);
        UpdateUndoMenuState();
    }

    /// <summary>
    /// 摘下撤销栈（当前没有文档时：图片列表为空、数据集刚清空）。
    ///
    /// 替代的是原来"换文档就清空"的做法：现在每份文档各有一条独立历史，
    /// 所以既没有跨文档撤销的风险（一份快照只能被它自己的文档取用），
    /// 也不必丢掉"回到改错过的那张图还能撤销"这条能力。
    /// </summary>
    private void DetachHistory()
    {
        _history = _histories.NewDetached();
        UpdateUndoMenuState();
    }

    private void UndoTranslate_Click(object sender, RoutedEventArgs e)
    {
        if (_undoPrompt is null) return;

        // 「撤销翻译」本身也要可撤销：否则用户按了它之后没法反悔，
        // 而它动的是一整段译文，比一次标签操作损失大得多。
        PushUndo("撤销翻译");

        // 恢复整段提示词，而不只是标签：翻译动的是散文尾巴，
        // 只还原标签的话「撤销」看不出任何变化。
        SetCaptionText(_undoPrompt);
        var parsed = ParseCaption(_undoPrompt);
        _pendingTags = new List<string>(parsed.Tags);
        _proseTail = parsed.ProseTail;
        _undoPrompt = null;

        RefreshTagRows();
        RefreshStatusBar();
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
        var parsed = ParseCaption(raw);

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

        // 写标签模式的状态栏报 token 预算——它随**字数**变，而不只随标签变。
        // 只挂在 RefreshTagRows 上的话，在右上写一段散文时（标签没动）预算就不会
        // 更新，而超出 512 正是写散文最容易踩的坑。
        if (tagsChanged || proseChanged) RefreshStatusBar();
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

    /// <summary>供 --draft 启动开关请求以写标签模式启动；真正的切换在 Initialize 之后。</summary>
    public void OpenDraftModeForTest() => _draftAfterInit = true;

    private async Task ShowSettingsAsync()
    {
        try
        {
            var dlg = new SettingsDialog(_cfg, _llm) { XamlRoot = Content.XamlRoot };
            var result = await dlg.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            dlg.ApplyTo(_cfg);
            _cfg.Save();

            // 撤销步数改小要立刻生效：仓库会把**每一份文档的桶**都裁到新上限。
            // （每个实例各有一条独立历史，所以这里设的是仓库而不是当前那一桶。）
            _histories.MaxSteps = _cfg.UndoMaxSteps;
            UpdateUndoMenuState();
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
