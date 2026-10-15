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

        // 助手对话也一样独立存放，**启动就恢复**（用户要求：只有点「清空对话」才清）。
        _agentChat = AgentChatStore.Load();

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

        // 右栏下半部分：翻译器 / AI 助手，二选一（设置里决定）
        ApplyAgentChrome(draft);

        // 落盘的对话在这里恢复一次。**放在 ApplyAgentChrome 之后**：面板要先
        // 定下来是显示助手还是翻译器，否则往一个还折叠着的容器里画东西。
        RenderRestoredConversation();
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
    private string GetCaptionText() => TryGetCaptionText(out var t) ? t : "";

    /// <summary>
    /// 读提示词框的正文。**读失败时返回 false**，调用方必须据此放弃。
    ///
    /// 早先这里读失败只记一条日志、然后返回空串，于是"读不出来"和"文档本来就是空的"
    /// 完全无法区分。后果是实测过的数据丢失：一次跨线程读取抛
    /// <c>COMException 0x8001010E</c>（见 OnUiThread 的注释），空串被当成内容，
    /// 而 <c>SaveCurrent</c> 当时只在"原本没有 .txt"时挡了一道——已有 caption 的
    /// 那条例外，于是把一个 **44 个标签的文件写成了 2 字节的空文件**。
    /// 现在把失败如实交出去，由保存路径拒绝落盘。
    /// </summary>
    private bool TryGetCaptionText(out string text)
    {
        try
        {
            CaptionBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.None, out var t);
            // 文档末尾总带一个段落标记，去掉；其余换行折成空格
            text = (t ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("read caption box failed: " + ex.Message);
            text = "";
            return false;
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

    // ================= 批量自动打标 =================
    //
    // 无人值守地跑一遍"读图 → 咨询模型 → 写进 caption"。它和面板里的助手**共用
    // 同一个 TagAgent 循环**（同一套系统提示、同一套工具、同一套预算与保存逻辑），
    // 这里只负责"换图 / 喂输入 / 收结果"这三件事。**不另写一条打标路径**：
    // 两份实现迟早漂移，而漂移的后果是"手动打的和批量打的写得不一样"。

    /// <summary>批量循环正在跑。期间禁掉再入与换图，避免两处同时改同一个文档。</summary>
    private bool _batchRunning;

    private async void BatchLabel_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != PromptMode.Label) { await Info("只在标注模式下可用", "批量打标要有一个图片数据集。"); return; }
        if (_batchRunning) { await Info("正在跑", "上一批还没结束。"); return; }
        if (_items.Count == 0) { await Info("没有图片", "先打开一个训练集目录。"); return; }

        if (_llm.Caps(_cfg)?.SupportsImage == false)
        {
            await Info("这个模型不吃图", $"「{_cfg.Model}」在服务端声明为纯文本输入，" +
                "批量打标会变成凭空描述——那正是最该避免的一种「成功」。");
            return;
        }

        // ---- 范围与确认 ----
        var options = await AskBatchOptionsAsync();
        if (options is null) return;

        var all = BuildBatchCandidates();
        var picked = BatchPlan.Choose(all, options);
        if (picked.Count == 0) { await Info("没有需要处理的图片", BatchPlan.Describe(picked, all.Count)); return; }

        var confirm = new ContentDialog
        {
            Title = "开始批量打标",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = BatchPlan.Describe(picked, all.Count) + "\n\n" +
                       "每张都要发一次（或几次）请求，可能要几分钟到几十分钟。" +
                       "过程中可以随时点「停止」——已经写完的那几张会保留。\n\n" +
                       (options.OnlyUnlabeled
                           ? "只处理未标注的图片，已标注的不会被碰。"
                           : "⚠ 已取消「只处理未标注」，已有标注会被覆盖（不可逆）。"),
            },
            PrimaryButtonText = "开始",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        await RunBatchLabelAsync(picked, options);
    }

    /// <summary>范围选择对话框。返回 null = 用户取消。</summary>
    private async Task<BatchOptions?> AskBatchOptionsAsync()
    {
        var onlyUnlabeled = new CheckBox { Content = "只处理还没有 .txt 的图片（推荐）", IsChecked = true };
        var skipEmpty = new CheckBox { Content = "把「有 .txt 但一个标签都没有」也当成未标注", IsChecked = true };
        var limitBox = new NumberBox
        {
            Header = "最多处理几张（0 = 不限）",
            Minimum = 0,
            Maximum = 100000,
            Value = 0,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 240,
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(onlyUnlabeled);
        panel.Children.Add(skipEmpty);
        panel.Children.Add(limitBox);
        panel.Children.Add(new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            Text = "批量打标用的是助手那套提示词与工具（查词库、可放大细节），" +
                   "所以它给的是与手动打标同一种结果。写入会直接落盘（每张各写一个 .txt），" +
                   "Ctrl+Z 撤不回来——想先看效果就用面板里的助手处理一张。",
        });

        var dlg = new ContentDialog
        {
            Title = "批量自动打标",
            Content = panel,
            PrimaryButtonText = "下一步",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;

        var limit = double.IsNaN(limitBox.Value) ? 0 : (int)limitBox.Value;
        return new BatchOptions(
            OnlyUnlabeled: onlyUnlabeled.IsChecked == true,
            SkipEmptyCaptions: skipEmpty.IsChecked == true,
            Limit: Math.Max(0, limit));
    }

    /// <summary>把当前数据集折成批量处理的候选项（在 UI 线程上读）。</summary>
    private IReadOnlyList<BatchCandidate> BuildBatchCandidates()
    {
        var list = new List<BatchCandidate>(_items.Count);
        for (var i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            // 标签数取**盘上那份**（DatasetItem.Tags 会按需读盘，没读过就现读）：
            // 批量打标决策的是"这张要不要重做"，判据是盘上状态，不是编辑器里
            // 尚未保存的改动。
            var tagCount = it.HasCaption ? it.Tags.Count : 0;
            list.Add(new BatchCandidate(i, it.Name, it.HasCaption, tagCount));
        }
        return list;
    }

    /// <summary>
    /// 批量循环本体。
    ///
    /// 三处刻意的设计：
    /// 1. **每张图独立一轮对话**（历史只有这一句）。把上一张的上下文带过来会让模型
    ///    拿前一张的画面描述这一张——而它"看起来"还是在正常工作。
    /// 2. **每张都重新读图**：图片只在最后一轮挂着，所以必须现读。
    /// 3. **一张失败不中断**：记下来接着跑，最后统一报告。中途停下会留下
    ///    "跑到一半"的状态，而用户看到的只是一个红叉。
    /// </summary>
    private async Task RunBatchLabelAsync(IReadOnlyList<BatchCandidate> picked, BatchOptions options)
    {
        _batchRunning = true;
        var stop = new CancellationTokenSource();
        // **用户按了停止**与"循环正常跑完顺便取消掉 token"必须分开。
        // 判据只能是这个显式标记：`stop.Cancel()` 在 finally 里无论如何都会调，
        // 所以读 `IsCancellationRequested` 会**永远为真**——实测第一版就是这样，
        // 每次跑完都多一句"中途停止了"，而它明明跑完了。
        var userStopped = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 停止按钮：用一个可取消的对话框占位，跑的时候点它就能停。
        var stopDlg = new ContentDialog
        {
            Title = "正在批量打标",
            Content = new TextBlock { Text = "准备中…", TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "停止",
            XamlRoot = Content.XamlRoot,
        };
        var progress = (TextBlock)stopDlg.Content;

        // 不 await：对话框只是"进度 + 停止"的载体，循环在另一条路上跑。
        var dlgTask = stopDlg.ShowAsync().AsTask();
        stopDlg.CloseButtonClick += (_, _) => { userStopped = true; stop.Cancel(); };

        BatchReport report;
        try
        {
            // 与 `label_images` 工具**同一个核心循环**，只是进度贴到对话框上。
            report = await RunBatchCoreAsync(picked, stop.Token,
                t => { try { progress.Text = t; } catch { } });
        }
        finally
        {
            stop.Cancel();
            try { stopDlg.Hide(); } catch { }
            try { await dlgTask; } catch { }
            _batchRunning = false;
        }

        sw.Stop();
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = report.Summary() + $"\n用时 {sw.Elapsed.TotalSeconds:F0} 秒。" +
                   (userStopped ? "\n\n（中途停止了，已完成的那几张保留。）" : ""),
        });
        if (report.Notes.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Text = "需要你知道的：\n" + string.Join("\n", report.Notes.Take(30)),
            });
        }
        await Info("批量打标结束", body);
        Log.Write($"batch label done: {report.Summary()}");
    }

    /// <summary>
    /// 批量的**核心循环**。菜单入口和 `label_images` 工具都走这里——两份实现
    /// 迟早漂移，而漂移的后果是"助手批量打的和菜单打的不是同一种东西"。
    ///
    /// <paramref name="progress"/> 每张开始时回调一次（界面显示进度）。
    /// <paramref name="onProgress"/>/<paramref name="onToolCall"/> 透传给**内层每一轮**
    /// （对话入口用它把思考与工具调用实时贴出来；菜单入口传 null，进度在对话框里）。
    /// </summary>
    private async Task<BatchReport> RunBatchCoreAsync(
        IReadOnlyList<BatchCandidate> picked,
        CancellationToken stop,
        Action<string>? progress = null,
        Action<string, string>? onProgress = null,
        Action<string, string, string?>? onToolCall = null)
    {
        var report = new BatchReport();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (var n = 0; n < picked.Count; n++)
        {
            if (stop.IsCancellationRequested) break;

            var c = picked[n];
            progress?.Invoke($"第 {n + 1} / {picked.Count} 张：{c.Name}\n\n" +
                             $"已完成：{report.Summary()}\n已用 {sw.Elapsed.TotalSeconds:F0} 秒");

            var status = await LabelOneAsync(c.Index, stop, onProgress, onToolCall);
            report.Add(c.Name, status.Status, status.Note);
        }

        sw.Stop();
        Log.Write($"batch core done: {report.Summary()} in {sw.Elapsed.TotalSeconds:F0}s");
        return report;
    }

    /// <summary>
    /// `label_images`：**模型一句话下令，批量就跑起来**。
    ///
    /// 与菜单入口的区别只有"谁发起"：范围来自工具参数、进度贴在助手状态行上、
    /// 循环本体是同一个 <see cref="RunBatchCoreAsync"/>。
    ///
    /// 三件事必须在这里做完：
    /// 1. **自己跑完**——模型在等这次工具调用的结果，不能"启动后就返回"；
    /// 2. **期间不许再下发批量工具**（<c>_batchRunning</c> 被 CanBatchLabel 读到），
    ///    否则内层那一轮又会发起一次批量，一直套下去；
    /// 3. 结果如实分类回给模型，包括"中途叫停"。
    ///
    /// 另外两件是"看得见"的要求（用户原话：像调用其它工具一样实时输出思考、
    /// 并且能看到它在打哪张图）：
    /// - 内层每一轮都把**流式回调接上**（<see cref="AgentLiveProgress"/> /
    ///   <see cref="AgentToolCallProgress"/>），所以思考与工具调用实时出现在过程区；
    /// - 每张图开头往过程区插一行标题（图名），于是那一串思考有了归属。
    ///
    /// 这里要**存/恢复两个共享状态**（`_agentLastTool`、`_agentRoundText`）：
    /// 内层的工具调用会把 `_agentLastTool` 指到内层那个块上，而外层
    /// `label_images` 的结果是**批量跑完之后**才回来的——不恢复的话，批量的
    /// 总结会填进内层最后一个工具块，而 `label_images` 那一块永远停在"进行中"。
    /// </summary>
    private async Task<(string Message, bool Cancelled)> AgentLabelImagesAsync(
        string arguments, CancellationToken ct)
    {
        var req = AgentKit.ParseLabelRequest(arguments);
        if (!req.Ok) return (ImageTools.Failed(req.Problem), false);

        if (_mode != PromptMode.Label || _items.Count == 0)
            return ("There is no dataset open, so there is nothing to label. " +
                    "Switch to label mode / open a training folder first.", false);

        // 选项：与工具参数一致（scope=all 才重做全部）。**不问对话框**——
        // 用户已经在对话里下了命令，再弹一个确认框等于让他确认两次。
        var options = new BatchOptions(OnlyUnlabeled: !req.RedoAll,
                                       SkipEmptyCaptions: true,
                                       Limit: req.Limit);

        var all = OnUiThread(BuildBatchCandidates, (IReadOnlyList<BatchCandidate>)Array.Empty<BatchCandidate>());
        var picked = BatchPlan.Choose(all, options);
        if (picked.Count == 0)
            return ("Nothing to do: " + BatchPlan.Describe(picked, all.Count), false);

        Log.Write($"agent label_images: scope={req.Scope} limit={req.Limit} -> {picked.Count} 张");

        _batchRunning = true;
        var stop = new CancellationTokenSource();
        // 模型那一侧的 ct 被取消（用户关窗口/断开）时也要停。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);

        BatchReport report;
        // 内层的工具调用会把 `_agentLastTool` 指到内层那个块上，而外层
        // `label_images` 的结果要**批量跑完之后**才回来。不存/恢复的话，批量的总结
        // 会填进内层最后一个工具块，而 label_images 那一块永远停在"进行中"。
        var savedLastTool = OnUiThread(() => _agentLastTool, (Expander?)null);
        var savedRoundText = OnUiThread(() => _agentRoundText, (TextBlock?)null);
        try
        {
            report = await RunBatchCoreAsync(picked, linked.Token,
                // 进度：状态行 + 过程区一行标题（"正在打哪张"要看得见）
                t => OnUiThread(() =>
                     {
                         var first = t.Split('\n')[0];
                         SetAgentStatus(t.Replace("\n", " "));
                         AgentAddProcHeading("▶ " + first);
                         return true;
                     }, true),
                // 内层每一轮的流式思考与工具调用，实时贴进同一个过程区
                onProgress: AgentLiveProgress,
                onToolCall: AgentToolCallProgress);
        }
        finally
        {
            stop.Cancel();
            _batchRunning = false;
            OnUiThread(() => { _agentLastTool = savedLastTool; _agentRoundText = savedRoundText; return true; }, true);
        }

        var cancelled = ct.IsCancellationRequested || stop.IsCancellationRequested;
        var msg = report.Summary() +
                  (cancelled ? "\nThe user stopped it part-way; the images already written keep " +
                               "their captions." : "") +
                  (report.Notes.Count > 0
                      ? "\n" + string.Join("\n", report.Notes.Take(20))
                      : "");
        return (msg, cancelled);
    }

    /// <summary>
    /// 往过程区插一行**标题**（批量里"正在打哪张图"）。与思考块分开是有意的：
    /// 标题是"这一段的归属"，用正常前景色；思考是灰的。
    /// </summary>
    private void AgentAddProcHeading(string text)
    {
        void Render()
        {
            if (AgentFeed is null) return;
            EnsureAgentProc(this, expanded: true);
            // 标题前先断掉当前思考块：否则下一轮思考会追加到标题**上面**，
            // 顺序就反了（与 AgentToolCallProgress 同一条理由）。
            _agentRoundText = null;
            _agentProcBody!.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            AgentScrollToEnd();
        }

        if (DispatcherQueue.HasThreadAccess) Render();
        else DispatcherQueue.TryEnqueue(Render);
    }

    /// <summary>批量打标时往助手状态行贴进度（见 IHost.ReportBatchProgress）。</summary>
    private void AgentReportBatchProgress(string text) =>
        OnUiThread(() => { SetAgentStatus(text); return true; }, true);

    /// <summary>
    /// 处理一张：切到它 → 读图 → 跑一轮助手 → 落盘。
    ///
    /// 返回给记账用的状态。**"模型没提案"与"失败"分开**：前者是模型觉得这张图
    /// 没什么可写的（合法结果），后者才是出问题——混在一起用户就分不清
    /// "跑完了"和"跑挂了"。
    /// </summary>
    private async Task<(BatchStatus Status, string Note)> LabelOneAsync(
        int index, CancellationToken ct,
        Action<string, string>? onProgress = null,
        Action<string, string, string?>? onToolCall = null)
    {
        try
        {
            if (ct.IsCancellationRequested) return (BatchStatus.Failed, "已取消");

            // 切图必须回 UI 线程（会动列表选中与当前文档）。这会顺带把上一张
            // 静默保存——与用户自己翻图完全一致。
            // **这一步就是"看到它在打哪张图"**：列表选中跟着走、编辑器载入那张，
            // 预览窗口也一并跟过去（LoadCurrent 里会调 _preview.GoTo）。
            OnUiThread(() =>
            {
                if (index != _index) { SaveCurrent(showDialog: false); Select(index); }
                return true;
            }, false);

            var path = OnUiThread(() => CurrentImagePath(), (string?)null);
            if (path is null) return (BatchStatus.Failed, "找不到图片路径");

            var image = await ImagePayload.LoadAsync(path);
            if (image is null) return (BatchStatus.Failed, "图片读不出来");

            // 与手动路径同一条：不带历史，只这一句。
            const string ask =
                "请为这张图写标注：按你的规则先查词库，然后提出该加的标签和一段英文散文。" +
                "只要画面里真实存在的内容，不要编。";

            // **内层这一轮刻意不传批量的那两个委托**（canBatchLabel / labelImages）：
            // 于是它的 CanBatchLabel 恒为 false，模型看不到也调不动 label_images。
            // 这是防自我递归的第二道闸（第一道是 _batchRunning）——批量里再发起批量
            // 会一直套下去，而用户只会看到界面永远不停。
            var host = new AgentHost(_cfg, _llm, _search, _vocab,
                                     images: AgentImageList,
                                     selectImage: AgentSelectImage,
                                     imageInfo: () => AgentImageInfoAsync(image is not null),
                                     viewRegion: args => AgentViewRegionAsync(args, image is not null));

            // **流式回调接上**（对话入口才传）：思考与工具调用实时贴进过程区，
            // 就像用户自己问一句时那样。菜单入口传 null，它的进度在对话框里。
            var outcome = await _agent.RunAsync(
                host, new[] { new AgentTurn(true, ask) },
                new LlmClient.ChatImage(ImagePayload.Mime, image.Jpeg), ct,
                onProgress: onProgress, onToolCall: onToolCall,
                maxRounds: _cfg.AgentMaxRounds);

            if (!outcome.Ok) return (BatchStatus.Failed, outcome.Error);

            if (outcome.Proposal is not { } p || p.IsEmpty)
                return (BatchStatus.NoProposal, "");

            // 落进编辑器（与「完全权限」同一条路），再显式存盘。
            // **不依赖 save_caption 工具**：那是给模型决定"什么时候存"用的，
            // 而批量这条路是我们自己知道"现在必须存"。
            var (changed, refused) = OnUiThread(() =>
            {
                ApplyAgentProposal(p, single: false);
                var wrote = SaveCurrent(showDialog: false, out var r);
                return (wrote, r);
            }, (false, false));

            if (refused) return (BatchStatus.Failed, "保存被拒（读不到正文）");
            if (!changed)
            {
                // 内容没变也可能是"模型给的正好就是现有内容"——那是正常结果，
                // 不该报成失败。
                return (BatchStatus.Unchanged, "");
            }
            return (BatchStatus.Written, "");
        }
        catch (Exception ex)
        {
            Log.Write($"batch label failed at {index}: {ex}");
            return (BatchStatus.Failed, ex.Message);
        }
    }


    private bool SaveCurrent(bool showDialog) => SaveCurrent(showDialog, out _);

    /// <summary>
    /// 保存当前文档。<paramref name="refused"/> 为真表示**这次保存被守卫拒绝了**
    /// （读不到提示词框里的正文），文件**没有**被碰过——与"内容没变所以没写"
    /// 是完全不同的两件事，调用方必须分得开。
    ///
    /// 区分它是因为实测有过一句话造成的误解：助手那条路把两种都报成"已保存"，
    /// 而用户去看盘上文件没变，于是得到"它说保存了其实没有"。返回值只能是
    /// "文件字节变没变"，表达不了"为什么没变"。
    /// </summary>
    private bool SaveCurrent(bool showDialog, out bool refused)
    {
        refused = false;

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
        //
        // **读不出正文就绝不落盘。** 宁可这次不保存，也不能拿空内容覆盖已有 caption
        // ——把"读不到"当成"内容为空"写下去，正是把一次读取失败变成数据丢失的那一步。
        if (!TryGetCaptionText(out var captionText))
        {
            refused = true;
            Log.Write($"save skipped: cannot read the caption box; refused to overwrite " +
                      $"{Path.GetFileName(item.CaptionPath)}");
            if (showDialog)
                _ = Info("没有保存",
                    "读不到提示词框里的内容，所以这次没有写文件——\n" +
                    "以免用空内容覆盖掉这张图已有的标注。\n\n" +
                    "请再试一次；若反复出现，把日志里的 read caption box failed 发出来。");
            return false;
        }

        var parsed = ParseCaption(captionText);
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
                    refused = true;
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
            refused = true;
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

    // ---- AI 助手面板 ----
    //
    // 与上面那个翻译器的关系：**两条路并存，不是替换**。翻译器是四轮实测
    // 调出来的确定性转换（长度跟输入走、方向词不许丢、不许加质量词，
    // 见 LlmClient.ProseSystemPrompt 的长注释）；助手面板换来的是能读图、
    // 能查词库、能一次提出多项改动，代价是不再逐字可复现。所以面板只顶替
    // 右栏的**位置**，设置里关掉就退回翻译器，而面板里也留着一键翻译。

    private readonly TagAgent _agent = new();

    /// <summary>
    /// 助手对话。**落盘持久化，换文档/换页都不清**——只有「清空对话」按钮会清。
    ///
    /// 早先这里是内存里的 <c>List&lt;AgentTurn&gt;</c>，并在换文档时整段丢掉，
    /// 理由是"历史带着上一份文档的画面，跨文档继续问会把 A 图说成 B 图"。
    /// 那个顾虑是真的，但代价太大：切一下图、甚至只是打开设置页回来，
    /// 刚看到一半的回答和思考过程就没了。现在改成**换图时插一条边界说明**
    /// （见 <see cref="NoteDocumentSwitch"/>）——连续性保住了，模型也仍然
    /// 知道画面换了。
    /// </summary>
    private AgentChatStore _agentChat = new();

    /// <summary>正在等模型。期间禁掉发送，避免连点发出一串请求。</summary>
    private bool _agentBusy;

    /// <summary>
    /// 这次是否把图发给模型。写标签模式没有图片，恒为 false；标注模式下还要
    /// 勾了「带当前图片」。**图和设置里的开关是两件事**：开关是默认值，
    /// 复选框是这一屏的即时意愿。
    /// </summary>
    private bool AgentImageWanted =>
        AgentImageBox.IsChecked == true && CurrentImagePath() is not null;

    /// <summary>标注模式下当前这张图的路径；写标签模式或没选中图时返回 null。</summary>
    private string? CurrentImagePath() =>
        _mode == PromptMode.Label && _index >= 0 && _index < _items.Count
            ? _items[_index].ImagePath
            : null;

    /// <summary>按设置决定右栏下半部分露哪一个面板。跟着 ApplyModeChrome 一起走。</summary>
    private void ApplyAgentChrome(bool draft)
    {
        var on = _cfg.AgentEnabled;
        TranslatorPanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        AgentPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (!on) return;

        RefreshAgentModelChrome();
        ApplyAgentImageGate(draft);

        // 面板上的「完全权限」跟着配置走。设置页里也能改同一个值，所以两处必须一致
        // ——否则会出现"面板上显示开着、实际没开"这种最坏的错：用户以为模型不会
        // 自己动手，而它正在动手。
        AgentFullAccessBox.IsChecked = _cfg.AgentFullAccess;

        if (AgentFeed.Children.Count == 0)
            AgentAddNote("说一句要它做什么，或直接描述画面。它会先查词库，" +
                         "然后把「要加的标签 + 一段散文」摆出来——" +
                         // 这句必须跟着「完全权限」走：开着的时候还说"要你点应用"，
                         // 就是在明确地报一个错误的行为预期。
                         (_cfg.AgentFullAccess
                             ? "**完全权限已打开：它会直接改进提示词**，不问你要不要（改的是编辑器里那一段，" +
                               "**要按 Ctrl+S 才写进文件**；Ctrl+Z 可整体撤销）。"
                             : "**要你点应用才写进去**。") +
                         (AgentImageBox.IsChecked == true
                             ? "这一屏会带上当前这张图。"
                             : "没带图时它只会照你的文字写。"));

        // 顺手问一次"这个模型吃不吃图"（每个模型一次，之后走内存缓存）。
        _ = RefreshAgentImageGateAsync();
    }

    /// <summary>
    /// 「带当前图片」那个复选框的启用状态。
    ///
    /// 三重条件：有图可带（写标签模式没有）、服务端没说过这个模型只吃文本。
    /// **"不知道"一律放行**——`input_modalities` 是 OpenAI 兼容云服务的扩展，
    /// llama-server 这类老网关根本不报，把未知当"不支持"会把本来能用的配置判死。
    /// </summary>
    private void ApplyAgentImageGate(bool draft)
    {
        var caps = _llm.Caps(_cfg);
        var hasImage = !draft && _index >= 0 && _index < _items.Count;
        var textOnly = caps?.SupportsImage == false;

        AgentImageBox.IsEnabled = LlmClient.CanAttachImage(caps?.SupportsImage, hasImage);
        AgentImageBox.IsChecked = _cfg.AgentAttachImage && AgentImageBox.IsEnabled;

        ToolTipService.SetToolTip(AgentImageBox,
            textOnly
                ? $"「{_cfg.Model}」只接受文本输入（服务端 /models 声明 " +
                  $"{string.Join("/", caps!.InputModalities!)}），不能带图。"
                : hasImage
                    ? "把当前这张图一起发给模型（它会先查词库再给提案）。"
                    : "写标签模式没有图片；标注模式下要先选中一张图。");
    }

    /// <summary>
    /// 能力还没拿到时去拉一次 /models，拿到后重画上面那个复选框。
    ///
    /// 失败与"服务端没报"都静默放过：拉不到能力只是维持原样，不是错误。
    /// </summary>
    private async Task RefreshAgentImageGateAsync()
    {
        if (!_cfg.AgentEnabled || _llm.Caps(_cfg) is not null) return;
        try
        {
            var wasChecked = AgentImageBox.IsChecked == true;
            if (await _llm.EnsureCapsAsync(_cfg, CancellationToken.None) is null) return;
            ApplyAgentImageGate(_mode == PromptMode.Draft);

            // 能力是晚一步才知道的，而开头那句问候已经按"会带图"写了。这里补一句
            // 更正——不补的话界面会一边说"这一屏会带上当前这张图"，一边把复选框
            // 灰掉，用户只会觉得程序坏了。
            if (wasChecked && AgentImageBox.IsChecked != true)
                AgentAddNote($"「{_cfg.Model}」只接受文本输入（服务端 /models 声明 " +
                             $"{string.Join("/", _llm.Caps(_cfg)!.InputModalities!)}），这次不带图。");
        }
        catch (Exception ex) { Log.Write("agent image gate failed: " + ex.Message); }
    }

    private void AgentImageBox_Click(object sender, RoutedEventArgs e)
    {
        _cfg.AgentAttachImage = AgentImageBox.IsChecked == true;
        _cfg.Save();
        Log.Write($"agent attach image: {_cfg.AgentAttachImage}");
    }

    /// <summary>
    /// 面板上的「完全权限」。与设置页里那个是同一个配置项，改完立刻落盘。
    ///
    /// **打开时记一条日志**：从这一刻起模型可以不经确认写入提示词，而"什么时候
    /// 变成这样的"是事后复盘时第一个要回答的问题。
    /// </summary>
    private void AgentFullAccess_Click(object sender, RoutedEventArgs e)
    {
        _cfg.AgentFullAccess = AgentFullAccessBox.IsChecked == true;
        _cfg.Save();
        Log.Write($"agent full access: {_cfg.AgentFullAccess}" +
                  (_cfg.AgentFullAccess ? "（助手将直接写入提示词，不再等确认）" : ""));
    }

    // ---- 助手面板里的模型选择器 ----
    //
    // 为什么要在面板里再放一个（设置页已经能改）：对话中途想换模型是很自然的动作
    // ——"这个模型答得不好""忘了切到能读图的那个"。为此要打开设置、翻到模型页、
    // 点保存、再回来，代价远大于收益，结果是用户干脆不换。
    //
    // 选择器列的是**站点 × 档位**：站点本身就是"地址+密钥+模型名"那一组
    // （见 ModelProfile），档位又跟着服务走，所以两样一起给，选完立刻生效。

    /// <summary>刷新按钮上的文字与提示。站点名与档位一起显示，当前用的是什么一眼可见。</summary>
    private void RefreshAgentModelChrome()
    {
        if (AgentModelText is null) return;

        var p = _cfg.ActiveProfile;
        var effort = LlmClient.NormalizeEffort(_cfg.ReasoningEffort);
        var effortLabel = effort.Length == 0 ? "自动" : effort;

        AgentModelText.Text = p is null
            ? "未配置"
            : $"{p.DisplayName} · {effortLabel}";

        var caps = _llm.Caps(_cfg);
        var imageNote = caps?.SupportsImage == true ? "可读图"
                      : caps?.SupportsImage == false ? "仅文本" : "能力未知";
        ToolTipService.SetToolTip(AgentModelBtn,
            $"{p?.DisplayName ?? "(无站点)"}\n{p?.Subtitle ?? ""}\n{imageNote}\n\n" +
            "点这里切换站点与推理档位（改完立刻生效，不必打开设置）。");

        // 换模型可能换掉"能不能读图"，那个复选框要跟着重算
        ApplyAgentImageGate(_mode == PromptMode.Draft);
    }

    /// <summary>助手面板里那个模型选择器的弹出层。内容每次打开时重建（见下）。</summary>
    private Flyout? _agentModelFlyout;

    /// <summary>
    /// 选择器当前是否真的开着。**由 Opened/Closed 事件维护，不读 Flyout.IsOpen**：
    /// 点站点后 Hide() 是异步收尾的，紧接着读 IsOpen 可能仍是 true，于是"再点一次
    /// 打开"会被当成"再点一次关闭"而直接 return —— 界面看起来像按钮失灵。
    /// 实测这条竞态会让同一段自动化脚本时而正常、时而弹出层空白。
    /// </summary>
    private bool _agentModelOpen;

    /// <summary>
    /// 弹出模型选择器。做成 Flyout + 两组列表而不是再开一个对话框：
    /// ContentDialog 只能有一个在场，而这里要在助手面板正上方说话，
    /// 弹一个会把整个窗口盖住的模态框反而打断操作。
    /// </summary>
    private void AgentModel_Click(object sender, RoutedEventArgs e)
    {
        // 已经开着就关掉（按钮是切换语义）
        if (_agentModelOpen) { _agentModelFlyout?.Hide(); return; }

        // **内容每次打开都重建，不能建一次留着复用。** 那个 ✓ 标的是"当前活动站点"
        // 与"当前档位"，而这两样都会在别处变（设置页、上一次点选）。复用旧面板的话
        // ✓ 会永远停在第一次打开时的位置——界面在明确地报错误的当前状态，而用户
        // 就是靠它确认切没切过去。实测：切到本地 llama 后再次打开，✓ 仍在 DeepSeek 上。
        if (_agentModelFlyout is null)
        {
            _agentModelFlyout = new Flyout();
            _agentModelFlyout.Opened += (_, _) => _agentModelOpen = true;
            _agentModelFlyout.Closed += (_, _) => _agentModelOpen = false;
        }
        _agentModelFlyout.Content = BuildAgentModelPanel();
        _agentModelFlyout.ShowAt(AgentModelBtn);
        Log.Write($"agent model picker opened (active={_cfg.ActiveProfile?.DisplayName}, " +
                  $"effort='{LlmClient.NormalizeEffort(_cfg.ReasoningEffort)}', " +
                  $"profiles={_cfg.Profiles.Count})");
    }

    /// <summary>造出模型选择器的面板内容。每次打开都重新调，所以 ✓ 一定是当下的状态。</summary>
    private StackPanel BuildAgentModelPanel()
    {
        var panel = new StackPanel { Spacing = 4, MinWidth = 300 };

        panel.Children.Add(new TextBlock
        {
            Text = "模型站点",
            FontSize = 11,
            Margin = new Thickness(2, 2, 2, 0),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });

        // 站点点一下即切换 + 立即保存：这里没有"保存/取消"的概念（面板是即时生效的），
        // 拖一个未提交的状态反而会让用户以为没生效。
        foreach (var p in _cfg.Profiles)
        {
            var isActive = p.Id == _cfg.ActiveProfile?.Id;
            var row = new StackPanel { Spacing = 1 };
            row.Children.Add(new TextBlock { Text = (isActive ? "✓ " : "   ") + p.DisplayName });
            row.Children.Add(new TextBlock
            {
                Text = p.Subtitle,
                FontSize = 11,
                Margin = new Thickness(14, 0, 0, 0),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            });

            var btn = new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 6, 8, 6),
            };
            // 显式给自动化名称：内容是个 StackPanel 时 UIA 拿到的名字是空的，
            // 读屏念不出来、自动化也点不着。
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, "站点 " + p.DisplayName);
            var captured = p;
            btn.Click += (_, _) =>
            {
                _cfg.ActiveProfileId = captured.Id;
                _cfg.Save();
                Log.Write($"agent model -> {captured.DisplayName} ({captured.Model})");
                _agentModelFlyout?.Hide();
                RefreshAgentModelChrome();
                AgentAddNote($"已切到「{captured.DisplayName}」（{captured.Subtitle}）。");
            };
            panel.Children.Add(btn);
        }

        panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 4, 0, 4),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
        });

        panel.Children.Add(new TextBlock
        {
            Text = "推理档位（跟着站点走）",
            FontSize = 11,
            Margin = new Thickness(2, 0, 2, 0),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });

        foreach (var (value, label) in EffortChoices)
        {
            var isActive = string.Equals(value, LlmClient.NormalizeEffort(_cfg.ReasoningEffort),
                                         StringComparison.OrdinalIgnoreCase);
            var btn = new Button
            {
                Content = (isActive ? "✓ " : "   ") + label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 6, 8, 6),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, "档位 " + label);
            var capturedValue = value;
            btn.Click += (_, _) =>
            {
                _cfg.ReasoningEffort = capturedValue;
                _cfg.Save();
                Log.Write($"agent effort -> '{capturedValue}'");
                _agentModelFlyout?.Hide();
                RefreshAgentModelChrome();
            };
            panel.Children.Add(btn);
        }

        panel.Children.Add(new TextBlock
        {
            Text = "档位能不能真正下发取决于服务端；设置页的「模型接口」会说明当前这一档会不会生效。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 300,
            Margin = new Thickness(2, 4, 2, 0),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });

        return panel;
    }

    /// <summary>
    /// 档位取值与界面文案。**与设置页共用**（设置页那份 BuildEffortItems 也读它）：
    /// 两处各写一份的话，改一处漏一处会让同一个档位在面板和设置里叫不同名字。
    /// </summary>
    internal static readonly (string Value, string Label)[] EffortChoices =
    {
        ("", "自动（跟随服务端默认）"),
        ("none", "关闭思考（none）"),
        ("minimal", "最少（minimal）"),
        ("low", "少（low）"),
        ("medium", "中（medium）"),
        ("high", "高（high）"),
        ("xhigh", "很高（xhigh）"),
        ("ultra", "极高（ultra）"),
        ("max", "最高（max）"),
    };

    private void AgentClear_Click(object sender, RoutedEventArgs e)
    {
        // 这是**唯一**会清空对话的地方：换文档、换页、重启都不清（用户要求）。
        _agentChat.Clear();
        AgentFeed.Children.Clear();
        AgentStatusText.Text = "";
        Log.Write("agent: conversation cleared");
    }

    /// <summary>
    /// 换文档时**不再往对话里插任何东西**。
    ///
    /// 曾经插过一条"已切换到另一张图：xxx"的边界说明，用来防止模型把 A 图说成 B 图。
    /// 实际用起来是多余的：说明会落盘、连翻几张就攒成一串，而用户自己知道在看哪张图。
    /// 模型要的那点上下文改由 **`current_image` 工具**按需给它（见 AgentKit），
    /// 既没有噪音，也不会过时——插在历史里的说明过两轮就不再是"当前"了。
    /// </summary>
    private void NoteDocumentSwitch()
    {
        // 保留这个空方法而不是删掉调用点：AttachHistory 里那一行的位置有讲究
        // （换文档时必须走一趟），留着它比将来重新推一遍便宜。
        // 真正要做的事已经没有了——见上面的说明。
    }

    private void AgentInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Ctrl+Enter 发送、单个 Enter 换行：反过来会让"想写两段"变成"发了两条"。
        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        if (e.Key == Windows.System.VirtualKey.Enter && ctrl.HasFlag(CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            _ = AgentSendAsync();
        }
    }

    private async void AgentSend_Click(object sender, RoutedEventArgs e) => await AgentSendAsync();

    private async Task AgentSendAsync()
    {
        if (_agentBusy) return;

        var text = AgentInput.Text.Trim();
        if (text.Length == 0) return;

        if (_doc is null)
        {
            await Info("还没有打开的文档", "先打开一张图（标注模式）或一条草稿（写标签模式），" +
                                          "助手需要一个可以改动的地方。");
            return;
        }

        _agentBusy = true;
        AgentRing.IsActive = true;
        AgentSendBtn.IsEnabled = false;
        AgentInput.Text = "";
        SetAgentStatus("正在思考…");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var asked = new AgentTurn(true, text);
        AgentAddUserBubble(text);
        // 自己发的那句必须出现在眼前：**强制**滚到底并重新跟上。用户之前可能正在
        // 往上翻，但他刚发了话，说明要看的是接下来的回答。
        _agentFollowTail = true;
        AgentScrollToEnd(force: true);

        LlmClient.ChatImage? image = null;
        try
        {
            // 读图失败不阻断：不带图问一句仍然是有用的，但要如实说明这次没带图。
            if (AgentImageWanted)
            {
                // 服务端说过这个模型只吃文本就别发了——那是一次注定 400 的请求，
                // 而且图片要先缩、要 base64，白花时间。
                if (_llm.Caps(_cfg)?.SupportsImage == false)
                {
                    SetAgentStatus("这个模型不吃图，这次只发文字。");
                    Log.Write($"agent: skip image, {_cfg.Model} declared text-only");
                }
                else
                {
                    var path = CurrentImagePath();
                    if (path is not null)
                    {
                        var loaded = await ImagePayload.LoadAsync(path);
                        if (loaded is not null)
                            image = new LlmClient.ChatImage(ImagePayload.Mime, loaded.Jpeg);
                    }
                    if (image is null) SetAgentStatus("这张图读不出来，这次不带图。");
                }
            }

            // 送进模型的是**持久化对话的正文**（思考与工具记录不在其中，它们
            // 只给用户看；换图说明会被折成 user 消息）。
            var history = _agentChat.ToModelHistory();
            history.Add(asked);

            // 每轮现建 host：词库可能在这中间才加载完，而 host 是抓着 _vocab 的。
            // 图片那两项也现取（委托读的是当下的 _items/_index），否则模型看到的
            // 永远是发第一句时的那张图。
            var host = new AgentHost(_cfg, _llm, _search, _vocab,
                                     images: AgentImageList,
                                     selectImage: AgentSelectImage,
                                     imageInfo: () => AgentImageInfoAsync(image is not null),
                                     viewRegion: args => AgentViewRegionAsync(args, image is not null),
                                     // 批量：用户一句话下令，模型调 label_images，这里执行。
                                     // `_batchRunning` 是防自我递归的闸门——内层那几轮
                                     // 读到的 CanBatchLabel 会是 false。
                                     canBatchLabel: () => !_batchRunning && _mode == PromptMode.Label
                                                        && _items.Count > 0,
                                     labelImages: AgentLabelImagesAsync,
                                     batchProgress: AgentReportBatchProgress);

            var outcome = await _agent.RunAsync(host, history, image,
                                                ct: CancellationToken.None,
                                                onProgress: AgentLiveProgress,
                                                onToolCall: AgentToolCallProgress,
                                                maxRounds: _cfg.AgentMaxRounds);

            if (!outcome.Ok)
            {
                // 失败的一轮不进历史：留着会让下一轮带着一段没有答案的上下文，
                // 模型会以为它已经回过话了。改为留一条说明，让用户知道这次没成。
                _agentChat.Add(AgentChatStore.Kind.Note, "调用失败：" + outcome.Error);
                AgentAddNote("调用失败：" + outcome.Error);
                SetAgentStatus("失败：" + outcome.Error);
                Log.Write("agent failed: " + outcome.Error);
                return;
            }

            _agentChat.Add(AgentChatStore.Kind.User, text);
            _agentChat.Add(AgentChatStore.Kind.Assistant, outcome.Reply,
                           outcome.Reasoning, outcome.Log, outcome.ToolCalls);

            // 「完全权限」：直接落进提示词，不等用户点。
            //
            // 刻意**不给那张可点的提案卡片**：卡片上的「应用」再点一次会把散文**追加**
            // 第二遍（标签有去重、散文没有），而用户会以为那只是"再看看刚才写了什么"。
            // 所以自动写入时改成一条说明，写清楚到底写了什么。
            if (_cfg.AgentFullAccess && outcome.Proposal is { } autoProp && !autoProp.IsEmpty)
            {
                ApplyAgentProposal(autoProp, single: false);
                AgentAddAssistant(outcome.Reply, outcome.Reasoning, outcome.ToolCalls, proposal: null);

                // **完全权限 = 它把活干完，包括存盘。** 这里不等模型调 save_caption，
                // 而是直接存——实测它**靠不住**：明确说了"改完直接保存到文件"、
                // 工具也下发了，它两轮里一次都没调（日志 round=1 calls=4 / round=2 calls=1，
                // 里面没有 save_caption）。而"完全权限"这个开关对用户的承诺正是
                // "不用我管"，让结果取决于模型记不记得调某个工具，等于把承诺押在它身上。
                //
                // 模型**显式**调了 save_caption 也一样会走到这里（那条路只设标记），
                // 所以两种意图的结果一致：都落盘。
                var (ok, msg) = AgentSaveCurrent();
                AgentAddNote(ok
                    ? "已按「完全权限」写进提示词**并存到文件**：" + DescribeProposal(autoProp) +
                      "。Ctrl+Z 可撤销编辑器里的改动（文件不会跟着回滚，需要的话再存一次覆盖）。"
                    : "已按「完全权限」写进提示词，但**存文件没成功**：" + msg);
                Log.Write($"agent full-access write+save ok={ok} modelAsked={autoProp.SaveToDisk}: " +
                          DescribeProposal(autoProp));
            }
            else
            {
                AgentAddReply(outcome);
            }

            sw.Stop();
            SetAgentStatus($"{(image is not null ? "带图 · " : "")}{outcome.Rounds} 轮 · " +
                           $"{sw.Elapsed.TotalSeconds:F1} 秒" +
                           (outcome.Found.Count > 0 ? $" · 查过 {outcome.Found.Count} 条" : ""));
            Log.Write($"agent done: rounds={outcome.Rounds} proposal={(outcome.Proposal is not null)} " +
                      $"found={outcome.Found.Count} ms={sw.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            // 异常这一轮同样不留"没有答案的问句"——留一条说明即可。
            _agentChat.Add(AgentChatStore.Kind.Note, "出错了：" + ex.Message);
            AgentAddNote("出错了：" + ex.Message);
            SetAgentStatus("出错了：" + ex.Message);
            Log.Write("agent exception: " + ex);
        }
        finally
        {
            _agentBusy = false;
            AgentRing.IsActive = false;
            AgentSendBtn.IsEnabled = true;
            // 收掉实时块——**放在最后**，成功/失败/异常三条路都覆盖。
            // 正式呈现由 AgentAddReply 负责（思考默认折叠），实时块只是让过程可见。
            AgentClearLive();
        }
    }

    /// <summary>
    /// 把"要碰界面"的一段工作挪到 UI 线程上做，并等它出结果。
    ///
    /// **必须这么做。** agent 循环里有 <c>ConfigureAwait(false)</c>，所以工具调用
    /// 回到这里时**很可能已经不在 UI 线程上**；那时直接动控件（切图要改列表选中项、
    /// 还要保存当前文档）会抛 <c>COMException 0x8001010E</c>（RPC_E_WRONG_THREAD）。
    /// 实测就是这么炸的：日志里"找到了目标图"紧接着一个 COMException，
    /// 而单元测试抓不到它——假 host 根本不碰界面。
    ///
    /// 已在本线程时直接做（否则会自己等自己，死锁）。
    /// </summary>
    private T OnUiThread<T>(Func<T> work, T fallback)
    {
        if (DispatcherQueue.HasThreadAccess) return work();

        var result = fallback;
        using var done = new ManualResetEventSlim(false);
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try { result = work(); }
                catch (Exception ex) { Log.Write("agent ui work failed: " + ex.Message); }
                finally { done.Set(); }
            }))
            return fallback;

        // 等一等是有意的：模型需要"切成功了没有"这个结果才能继续。
        // 超时兜底，免得界面卡住时把整个 agent 循环挂死。
        return done.Wait(TimeSpan.FromSeconds(15)) ? result : fallback;
    }

    /// <summary>
    /// 给 agent 看的图片清单。写标签模式返回空——那里没有数据集，
    /// 两个图片工具随之不下发（让模型调必然失败的工具是白烧一轮预算）。
    /// </summary>
    private IReadOnlyList<AgentImage> AgentImageList() =>
        OnUiThread(BuildImageList, (IReadOnlyList<AgentImage>)Array.Empty<AgentImage>());

    private IReadOnlyList<AgentImage> BuildImageList()
    {
        if (_mode != PromptMode.Label || _items.Count == 0) return Array.Empty<AgentImage>();

        var list = new List<AgentImage>(_items.Count);
        for (var i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            var current = i == _index;

            // **当前这张要带上标签名，而且取的是编辑器里的状态（_pendingTags），
            // 不是盘上那份。** 两者可能不一致（用户改了还没保存），而模型要改的
            // 正是用户眼前那一段——按盘上报会让它去删一些编辑器里根本没有的标签。
            // 只有当前那张需要：其余图只用来"知道有个邻居可以切过去"。
            list.Add(new AgentImage(it.Name, i, it.HasCaption, it.Tags.Count, current,
                                    current ? new List<string>(_pendingTags) : null));
        }
        return list;
    }

    /// <summary>
    /// 模型要求切图。**这是 agent 循环里唯一的写操作。**
    ///
    /// 副作用要如实说：走的是和用户自己点图片列表完全相同的那条路，
    /// 因此会先静默保存当前 caption（编辑器换文档时一贯如此，避免切图丢改动）。
    /// 工具说明里已经写明"只在用户要求时用"，这里再记一条日志——
    /// 模型自己翻图是那种"事后看训练集才发现"的行为，留痕很重要。
    ///
    /// 整段必须在 UI 线程上跑（见 <see cref="OnUiThread"/>）。
    /// </summary>
    private (bool Ok, string Message) AgentSelectImage(string name) =>
        OnUiThread(() => AgentSelectImageCore(name),
                   (false, "Switching images is not available right now."));

    private (bool Ok, string Message) AgentSelectImageCore(string name)
    {
        if (_mode != PromptMode.Label || _items.Count == 0)
            return (false, "Switching images is not available: the editor has no dataset open.");

        var want = (name ?? "").Trim();
        if (want.Length == 0)
            return (false, "No file name was given.");

        // 允许带或不带扩展名：模型从 current_image 抄名字时经常把 .png 一起抄进来，
        // 而列表里存的是不带扩展名的显示名。
        var stem = Path.GetFileNameWithoutExtension(want);
        var hit = -1;
        for (var i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].Name, stem, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_items[i].Name, want, StringComparison.OrdinalIgnoreCase))
            {
                hit = i;
                break;
            }
        }

        if (hit < 0)
        {
            var names = string.Join(", ", _items.Take(20).Select(x => x.Name));
            return (false, $"No image named \"{want}\". Available: {names}");
        }

        if (hit == _index) return (true, $"Already on \"{_items[hit].Name}\".");

        Log.Write($"agent: select_image -> {_items[hit].Name} (was {_items[_index].Name})");
        SaveCurrent(showDialog: false);   // 与用户手动翻图一致：先保存当前那张
        Select(hit);
        return (true, $"Switched to \"{_items[hit].Name}\" (saved the previous image first).");
    }

    /// <summary>
    /// 一张图在这一刻的全部"跟界面有关"的事实。**一次快照**，之后的读盘与解码
    /// 都在后台线程上做——<c>_items</c>/<c>_index</c> 只能在这里读（见 <see cref="OnUiThread"/>）。
    /// 文件名不在这里：它从磁盘上读出来的那份事实里取（见 <see cref="ImagePayload.ReadFactsAsync"/>），
    /// 免得同一个名字有两个来源、哪天对不上。
    /// </summary>
    private sealed record AgentImageSnapshot(string Path, int Index, int Total, int CaptionTags);

    private AgentImageSnapshot? CaptureAgentImage()
    {
        if (_mode != PromptMode.Label || _index < 0 || _index >= _items.Count) return null;
        var it = _items[_index];
        return new AgentImageSnapshot(it.ImagePath, _index + 1, _items.Count, it.Tags.Count);
    }

    /// <summary>
    /// `image_info`：当前图的文件事实。读不出来就如实说读不出来——
    /// 编一个尺寸会让模型据此算出错误的裁剪区域。
    /// </summary>
    private async Task<string> AgentImageInfoAsync(bool attached)
    {
        var snap = OnUiThread(CaptureAgentImage, (AgentImageSnapshot?)null);
        if (snap is null) return ImageTools.NoImage;

        try
        {
            var f = await ImagePayload.ReadFactsAsync(snap.Path);
            return ImageTools.Info(ToFacts(f, snap, attached));
        }
        catch (Exception ex)
        {
            Log.Write("agent image_info failed: " + ex.Message);
            return ImageTools.InfoFailed(ex.Message);
        }
    }

    /// <summary>
    /// `view_region`：裁一块（或按网格切开）给模型看。
    ///
    /// 三件事按顺序把住：没有图 → 如实说；没带图 → 拒绝（否则等于绕过用户关掉的
    /// 「带当前图片」开关，悄悄把训练集的图发出去）；参数不合法 → 回一句话让它改。
    /// </summary>
    private async Task<(bool Ok, string Message, IReadOnlyList<LlmClient.ChatImage> Images)>
        AgentViewRegionAsync(string arguments, bool attached)
    {
        var empty = (IReadOnlyList<LlmClient.ChatImage>)Array.Empty<LlmClient.ChatImage>();

        var snap = OnUiThread(CaptureAgentImage, (AgentImageSnapshot?)null);
        if (snap is null) return (false, ImageTools.NoImage, empty);
        if (!attached) return (false, ImageTools.NotAttached, empty);

        var req = AgentKit.ParseRegion(arguments);
        if (!req.Ok)
        {
            Log.Write($"agent view_region: 参数不合法 - {req.Problem}");
            return (false, ImageTools.ArgsProblem(req.Problem), empty);
        }

        try
        {
            var f = await ImagePayload.ReadFactsAsync(snap.Path);
            var facts = ToFacts(f, snap, true);
            var (dw, dh) = facts.Displayed;

            var rects = req.Mode == "tiles"
                ? ImageRegion.Tiles(dw, dh, req.Cols, req.Rows)
                : new List<PixelRect> { ImageRegion.Crop(dw, dh, req.X, req.Y, req.W, req.H) };

            var crops = await ImagePayload.CropManyAsync(snap.Path, rects);
            if (crops.Count == 0) return (false, ImageTools.Failed("no region could be cut"), empty);

            var msg = req.Mode == "tiles"
                ? ImageTools.Tiles(facts, req.Cols, req.Rows, rects)
                : ImageTools.Crop(facts, rects[0], crops[0].Width, crops[0].Height);

            var images = crops.Select(c => new LlmClient.ChatImage(ImagePayload.Mime, c.Jpeg)).ToList();
            return (true, msg, images);
        }
        catch (Exception ex)
        {
            Log.Write("agent view_region failed: " + ex.Message);
            return (false, ImageTools.Failed(ex.Message), empty);
        }
    }

    private static ImageFacts ToFacts(FileFacts f, AgentImageSnapshot snap, bool attached) =>
        new(f.Name, f.Format, f.FileBytes, f.StoredWidth, f.StoredHeight, f.Orientation,
            attached, snap.Index, snap.Total, snap.CaptionTags);

    /// <summary>
    /// `save_caption`：把当前这张图的 caption 写进磁盘。
    ///
    /// **只有开了「完全权限」才会被调用**（关着时 TagAgent 不下发这个工具，
    /// 万一来调用也会被它回绝）。所以这里不再问一次——那正是「完全权限」的语义。
    ///
    /// 走的是和用户按 `Ctrl+S` **完全相同**的那条路（<c>SaveCurrent</c>），
    /// 不另写一份落盘逻辑：格式化、字节风格（BOM/行尾/分隔符）、空内容守卫
    /// 全在那一处，重复实现必然漂移——而漂移的后果是训练集的 .txt 被写坏。
    /// 整段在 UI 线程上跑（保存要读提示词框，见 <see cref="OnUiThread"/>）。
    /// </summary>
    private (bool Ok, string Message) AgentSaveCurrent()
    {
        var ui = OnUiThread(() =>
        {
            var path = CurrentImagePath();
            if (path is null)
                return (Ok: false, Message: "There is no image to save (the editor has no dataset open).");

            var changed = SaveCurrent(showDialog: false, out var refused);
            var name = Path.GetFileName(path);

            // **三种结果必须分开说。** 返回值只表达"文件字节变没变"，
            // 而"没变"有两种截然不同的原因：内容本来就是最新的（成功），
            // 以及保存被守卫拒绝、文件根本没碰（失败）。把后者说成"已保存"
            // 就是那句最难查的谎——用户以为写进去了，盘上其实没变。
            if (refused)
                return (Ok: false, Message:
                    $"\"{name}\" was NOT saved: the editor could not read the caption text, so " +
                    "the file was left untouched on purpose. Tell the user plainly; do not claim " +
                    "you saved it.");

            return (Ok: true, Message: changed
                ? $"Saved \"{name}\" to disk."
                : $"\"{name}\" was already up to date on disk — nothing needed writing.");
        }, (Ok: false, Message: "Saving is not available right now."));

        Log.Write($"agent save_caption: ok={ui.Ok} {ui.Message}");
        return ui;
    }

    /// <summary>
    /// 生成中的实时块（思考链 + 已吐出的正文）。**结束时会把它整个删掉**，
    /// 换成 <see cref="AgentAddReply"/> 那份默认折叠的正式版——所以这个块只是
    /// "过程可见"，不改变最终呈现。
    ///
    /// 从这里进来的调用**不在 UI 线程上**（流式读取跑在后台），所以每一次更新都要
    /// 投递回 UI 线程；节流已经在 <c>LlmClient</c> 里做过（约 10 次/秒），
    /// 这里再加一道时间闸，避免几百个分片把界面刷爆。
    /// </summary>
    private long _agentLiveAt;

    /// <summary>已经结束的轮次的思考（实时块里要留着，只增不减）。</summary>
    private readonly StringBuilder _agentLiveDone = new();

    /// <summary>当前这一轮已累积的思考，用来认出"换轮了"。</summary>
    private string _agentLiveCur = "";

    /// <summary>
    /// 过程块正文是**有序的元素流**：[第1轮思考][调用1][调用2][第2轮思考][调用3]…
    ///
    /// 这样工具调用就落在**它实际发起的位置**上（用户要求），而不是攒到最后补一段。
    /// 要点是每次工具调用发起时把"当前轮思考块"置空——下一次思考会另起一个块、
    /// 排在调用之后。共用一个文本块的话，新思考会追加到调用**上面**，顺序就反了。
    /// </summary>
    private StackPanel? _agentProcBody;
    private TextBlock? _agentRoundText;
    private Expander? _agentLastTool;
    private int _agentToolCount;

    private static TextBlock NewGrayText() => new()
    {
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        Foreground = AgentGray,
    };

    private static void EnsureAgentProc(MainWindow w, bool expanded)
    {
        if (w._agentProc is null) w.AgentFeed.Children.Add(w.CreateAgentProcess(expanded));
    }

    private void UpdateAgentProcHeader()
    {
        if (_agentProc?.Header is TextBlock h)
            h.Text = _agentToolCount > 0 ? $"过程 · {_agentToolCount} 步" : "过程";
    }

    private void AgentLiveProgress(string reasoning, string content)
    {
        var reason = (reasoning ?? "").Trim();
        var body = (content ?? "").Trim();
        if (reason.Length == 0 && body.Length == 0) return;

        var now = Environment.TickCount64;
        if (_agentProc is not null && now - _agentLiveAt < 80) return;
        _agentLiveAt = now;

        void Render()
        {
            if (AgentFeed is null) return;
            EnsureAgentProc(this, expanded: true);
            if (_agentRoundText is null)
            {
                _agentRoundText = NewGrayText();
                _agentProcBody!.Children.Add(_agentRoundText);
            }
            var sb = new StringBuilder(reason);
            if (body.Length > 0)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(body);
            }
            _agentRoundText.Text = sb.ToString();
            AgentScrollToEnd();
        }

        if (DispatcherQueue.HasThreadAccess) Render();
        else DispatcherQueue.TryEnqueue(Render);
    }

    /// <summary>
    /// 一次工具调用。<paramref name="result"/> 为 null = **刚发起**（先把块放上去），
    /// 非 null = 返回了（把结果填进那个块）。
    ///
    /// 分两步是有意的：模型查一个词可能要几秒，先显示"正在查什么"比等结果更诚实，
    /// 用户也不会以为它卡住了。
    /// </summary>
    private void AgentToolCallProgress(string name, string args, string? result)
    {
        void Render()
        {
            if (AgentFeed is null) return;
            EnsureAgentProc(this, expanded: true);

            if (result is null)
            {
                // 发起：结束当前思考块（后续思考另起一块，才会落在调用**下面**）
                _agentRoundText = null;
                _agentLastTool = BuildToolCallBlock(new TagAgent.ToolCallLog(name, args, ""));
                _agentProcBody!.Children.Add(_agentLastTool);
                _agentToolCount++;
                UpdateAgentProcHeader();
            }
            else if (_agentLastTool?.Content is StackPanel sp)
            {
                FillToolCallBlock(sp, args, result);
            }
            AgentScrollToEnd();
        }

        if (DispatcherQueue.HasThreadAccess) Render();
        else DispatcherQueue.TryEnqueue(Render);
    }

    /// <summary>一轮结束：不重建、不搬运，只把标题定下来并按需要收起。</summary>
    private void FinishAgentProcess()
    {
        UpdateAgentProcHeader();
        // 默认收起（思考是过程、回复才是结论）；但**用户自己开过就别动**——
        // 他可能正读着，这时收起等于把它抢走。
        if (!_agentProcTouched) SetAgentProcessExpanded(false);
    }

    private void AgentClearLive()
    {
        _agentRoundText = null;
        _agentLastTool = null;
        _agentToolCount = 0;
    }

    private void SetAgentStatus(string text) => AgentStatusText.Text = text;

    /// <summary>系统说明（不是模型说的话）。灰一点、小一点，别和回答混在一起。</summary>
    private void AgentAddNote(string text)
    {
        AgentFeed.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
        AgentScrollToEnd();
    }

    /// <summary>
    /// 把强调色按比例往背景方向调淡，用来做"自己的消息"那种浅色底。
    ///
    /// 不直接用 <c>AccentFillColorSecondaryBrush</c>：那个是**给按钮**设计的实色，
    /// 铺在一大块消息气泡上太重（实测深蓝底把黑字压得很难看，长回复尤其闷）。
    /// 这里按主题方向混合——亮色主题往白里调，暗色主题往黑里调——所以两种主题下
    /// 都是"淡淡的同色"，而不是只在一种主题里好看。
    /// </summary>
    private static SolidColorBrush AccentTint(double amount)
    {
        var accent = ThemeColor("SystemAccentColor",
                                Windows.UI.Color.FromArgb(255, 0, 120, 212));
        var light = IsLightTheme();
        // amount = 保留多少强调色（0 = 几乎全背景色，1 = 原色）
        var target = light ? 255.0 : 0.0;
        byte Mix(byte c) => (byte)Math.Clamp(
            Math.Round(target + (c - target) * amount), 0, 255);

        return new SolidColorBrush(Windows.UI.Color.FromArgb(
            255, Mix(accent.R), Mix(accent.G), Mix(accent.B)));
    }

    /// <summary>当前是不是亮色主题。气泡底色要跟着主题往相反方向调。</summary>
    private static bool IsLightTheme()
    {
        try
        {
            if (App.MainWindow?.Content is FrameworkElement fe &&
                fe.ActualTheme != ElementTheme.Default)
                return fe.ActualTheme == ElementTheme.Light;
        }
        catch { /* 窗口还没起来时按系统设置 */ }

        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { }
        return true;
    }

    private void AgentAddUserBubble(string text)
    {
        var body = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        AgentFeed.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 460,
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(8),
            // 0.18 保留一点强调色（看得出这是"我发的"），其余混进背景色。
            // 之前用的 AccentFillColorSecondaryBrush 是给按钮的实色，大面积铺开太重。
            Background = AccentTint(0.18),
            Child = body,
        });
        AgentScrollToEnd();
    }

    /// <summary>
    /// 「过程」折叠块：思考内容 + 工具调用记录。**默认折叠**、灰色小字。
    ///
    /// 三条都是刻意的：
    ///
    /// 1. **默认折叠**。思考链通常比答案长得多（工具循环每一轮都先想一遍，实测
    ///    四轮能到上万字），全铺开会把真正的回复顶出视野；但排障时又想看它到底
    ///    查了什么、为什么给出这个提案，所以留一个能展开的入口。
    /// 2. **灰色**。它和回复不是一回事：回复是结论，这里是过程。用同样颜色会让人
    ///    以为"模型就是这么说的"，而其中可能有它自我纠正掉的中间结论。
    /// 3. **只在 AI 助手面板里出现**。它不参与任何判定，也永远不会写进提示词或
    ///    翻译结果（<see cref="TagAgent.Outcome.Reasoning"/> 的注释写着同一条）。
    ///
    /// 没有可显示的内容时返回 null，调用方据此决定要不要加这个块。
    /// </summary>
    /// <summary>
    /// 流式期间与生成完成后**共用的同一个「过程」块**。
    ///
    /// 早先是两套控件：流式时铺一个不能折叠的灰块，结束时把它删掉、另建一个可折叠的
    /// Expander——视觉上就是"两个东西"，用户一眼就看出换过一次。现在从一而终：
    /// 生成中就是它（展开、你随时可以自己收起），完成后**原地**补上工具调用并收起。
    /// 全程不重建、不替换，所以不存在"闪一下"。
    /// </summary>
    private Expander? _agentProc;
    private TextBlock? _agentProcText;

    /// <summary>用户手动开合过没有。开过就别在结束时替他收起（那会把他正在看的收走）。</summary>
    private bool _agentProcTouched;

    /// <summary>程序正在改展开状态，用来把那次改动和"用户手动的"区分开。</summary>
    private bool _agentProcProgrammatic;

    private static Brush AgentGray =>
        (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

    private Expander CreateAgentProcess(bool expanded)
    {
        _agentProcText = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = AgentGray,
        };
        _agentProcBody = new StackPanel { Spacing = 8 };

        var ex = new Expander
        {
            Header = new TextBlock { Text = "过程", FontSize = 11, Foreground = AgentGray },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = _agentProcBody,
        };
        ex.Expanding += (_, _) => { if (!_agentProcProgrammatic) _agentProcTouched = true; };
        ex.Collapsed += (_, _) => { if (!_agentProcProgrammatic) _agentProcTouched = true; };

        _agentProcProgrammatic = true;
        ex.IsExpanded = expanded;
        _agentProcProgrammatic = false;

        _agentProc = ex;
        return ex;
    }

    private void SetAgentProcessExpanded(bool expanded)
    {
        if (_agentProc is null) return;
        _agentProcProgrammatic = true;
        _agentProc.IsExpanded = expanded;
        _agentProcProgrammatic = false;
    }

    /// <summary>过程块的正文：思考链（截断保护）+（生成中时）已吐出的正文。</summary>

    /// <summary>
    /// 一个工具调用的小折叠块：**标题是它干了什么，展开是"发过去的原文 + 回来的原文"**。
    ///
    /// 与原来那行一句话摘要（"查「x」→ 3 条"）的区别：摘要看不出"它到底拿什么去查的"，
    /// 而排查"模型为什么查错词"时恰恰要看那个。
    /// </summary>
    /// <summary>
    /// 工具调用块的正文：参数与返回。**发起时先只放参数**（返回还是空的，就不显示），
    /// 拿到结果后再调一次把它填上——同一个块，不重建。
    /// </summary>
    private static void FillToolCallBlock(StackPanel body, string args, string result)
    {
        body.Children.Clear();
        void Add(string label, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            body.Children.Add(new TextBlock
            {
                Text = label, FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = AgentGray,
            });
            body.Children.Add(new TextBlock
            {
                Text = text.Length > 4000 ? text[..4000] + "…" : text,
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true, Foreground = AgentGray,
            });
        }
        Add("参数", args);
        Add("返回", result);
    }

    private static Expander BuildToolCallBlock(TagAgent.ToolCallLog c)
    {
        var body = new StackPanel { Spacing = 6 };
        FillToolCallBlock(body, c.Arguments, c.Result);

        // 标题用工具名 + 参数里的关键词，一眼看出这次查的是什么
        var hint = c.Name;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(c.Arguments);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var key in new[] { "query", "name" })
                    if (doc.RootElement.TryGetProperty(key, out var v) &&
                        v.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        hint += " · " + v.GetString();
                        break;
                    }
        }
        catch { /* 参数不是 JSON 就只用工具名 */ }

        return new Expander
        {
            Header = new TextBlock { Text = hint, FontSize = 11, Foreground = AgentGray },
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = body,
        };
    }

    /// <summary>结束这一轮：把过程块补成最终样子，并按需要收起。</summary>

    /// <summary>思考内容显示上限（字符）。超过就截断并说明，避免展开时卡住界面。</summary>
    private const int ProcessCap = 20000;

    /// <summary>
    /// 模型这一轮的回复。有提案时**回复是配角**——真正要看的是下面那张提案卡片，
    /// 所以这里只放一句话，把版面让给卡片。
    /// </summary>
    private void AgentAddReply(TagAgent.Outcome outcome) =>
        AgentAddAssistant(outcome.Reply, outcome.Reasoning, outcome.ToolCalls, outcome.Proposal);

    /// <summary>
    /// 渲染一条助手回复。**实时那一轮和启动时恢复旧对话走的是同一条路**，
    /// 否则恢复出来的样子会和刚说完时不一致（那种不一致只有重启才看得到，
    /// 最容易长期漏掉）。
    ///
    /// <paramref name="proposal"/> 只在实时那一轮给：**提案卡片不恢复**——
    /// 它是可点的动作，重启后再点一次「应用」会把标签再加一遍，而用户以为
    /// 那只是"看看历史"。
    /// </summary>
    private void AgentAddAssistant(string reply, string reasoning,
                                   IReadOnlyList<TagAgent.ToolCallLog>? calls, AgentProposal? proposal)
    {
        // 过程块：流式那一轮**已经在流里了**（就是用户在看的那个，调用也已在各自
        // 位置插好），这里只把标题定下来并按需收起——**不重建**，重建就是
        // "两个控件"那种观感的来源。恢复历史时才新建一个（默认收起），
        // 顺序按"思考 + 各次调用"重建（历史里没存轮次边界，只能这样）。
        if (_agentProc is not null)
        {
            FinishAgentProcess();
        }
        else if ((reasoning ?? "").Trim().Length > 0 || (calls?.Count ?? 0) > 0)
        {
            var ex = CreateAgentProcess(expanded: false);
            var reason = (reasoning ?? "").Trim();
            if (reason.Length > 0)
            {
                _agentRoundText = NewGrayText();
                _agentRoundText.Text = reason.Length > ProcessCap
                    ? reason[..ProcessCap] + $"\n\n（过长，已截断；完整 {reason.Length:N0} 字）"
                    : reason;
                _agentProcBody!.Children.Add(_agentRoundText);
            }
            foreach (var c in calls ?? Array.Empty<TagAgent.ToolCallLog>())
                _agentProcBody!.Children.Add(BuildToolCallBlock(c));
            _agentToolCount = calls?.Count ?? 0;
            UpdateAgentProcHeader();
            AgentFeed.Children.Add(ex);
        }

        var text = (reply ?? "").Trim();
        if (text.Length > 0)
            AgentFeed.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });

        if (proposal is { } p && !p.IsEmpty)
            AgentFeed.Children.Add(BuildProposalCard(p));

        AgentScrollToEnd();
        AgentClearProcRefs();
    }

    /// <summary>一轮结束：放开对过程块的引用，下一轮会建新的。</summary>
    private void AgentClearProcRefs()
    {
        _agentProc = null;
        _agentProcBody = null;
        _agentProcText = null;
        _agentProcTouched = false;
        AgentClearLive();
    }

    /// <summary>
    /// 提案卡片。三项都在这里：要加的标签（逐个可加，也可一次全加）、
    /// 散文、以及"应用"——**应用是一次动作，所以是一个撤销点**（见 ApplyAgentProposal）。
    ///
    /// 词库查不到的标签**照旧列出来**并标成橙色，和中间那个标签列表的规则一致：
    /// 词库不是全集（实测 679 个真实标签里 74 个查不到，`@` 触发词、
    /// `2 fingers` 都在其中），静默丢掉等于偷改用户的提示词。
    /// </summary>
    private Border BuildProposalCard(AgentProposal p)
    {
        var body = new StackPanel { Spacing = 8 };
        var buttons = new List<Button>();

        // **先显示要删的。** 顺序与落地一致（先删后加），而且"覆盖"这个诉求下
        // 用户第一眼要确认的正是"哪些会没掉"——放在下面容易被当成又一批要加的。
        if (p.HasRemovals)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"将从提示词里删除 {p.Remove.Count} 个标签",
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
            });

            var removeList = new StackPanel { Spacing = 4 };
            foreach (var tag in p.Remove)
            {
                var cn = _vocab?.LookupCn(tag) ?? "";
                var label = new StringBuilder("－ ").Append(tag);
                if (cn.Length > 0) label.Append("  ·  ").Append(cn);

                removeList.Children.Add(new TextBlock
                {
                    Text = label.ToString(),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
                    IsTextSelectionEnabled = true,
                });
            }
            body.Children.Add(removeList);
        }

        if (p.HasTags)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"建议添加 {p.AddTags.Count} 个标签" +
                       (p.Unknown.Count > 0 ? $"（其中 {p.Unknown.Count} 个词库查不到）" : ""),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });

            var list = new StackPanel { Spacing = 4 };
            foreach (var tag in p.AddTags)
            {
                var known = p.Known.Any(k => string.Equals(k, tag, StringComparison.OrdinalIgnoreCase));
                var cn = _vocab?.LookupCn(tag) ?? "";
                var label = new StringBuilder("＋ ").Append(tag);
                if (cn.Length > 0) label.Append("  ·  ").Append(cn);

                var btn = new Button
                {
                    Content = label.ToString(),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(8, 4, 8, 4),
                    Foreground = known
                        ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                        : new SolidColorBrush(Microsoft.UI.Colors.DarkOrange),
                };
                ToolTipService.SetToolTip(btn, known
                    ? "词库里查得到这个标签"
                    : "词库查不到——加进去仍然会原样写进提示词（这个词可能只是词库没有）");

                var captured = tag;
                btn.Click += (_, _) =>
                {
                    ApplyAgentProposal(new AgentProposal(new[] { captured },
                        known ? new[] { captured } : Array.Empty<string>(),
                        known ? Array.Empty<string>() : new[] { captured },
                        "", false, ""), single: true);
                    btn.IsEnabled = false;
                };
                buttons.Add(btn);
                list.Children.Add(btn);
            }
            body.Children.Add(list);
        }

        if (p.HasProse)
        {
            body.Children.Add(new TextBlock
            {
                Text = p.ReplaceProse ? "英文散文（替换现有的）" : "英文散文（追加到末尾）",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            body.Children.Add(new Border
            {
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = new CornerRadius(6),
                Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
                Child = new TextBlock
                {
                    Text = p.Prose,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    FontFamily = new FontFamily("Consolas"),
                },
            });
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var applyBtn = new Button
        {
            Content = DescribeApply(p),
        };
        applyBtn.Click += (_, _) =>
        {
            ApplyAgentProposal(p, single: false);
            applyBtn.IsEnabled = false;
            foreach (var b in buttons) b.IsEnabled = false;
        };
        actions.Children.Add(applyBtn);
        body.Children.Add(actions);

        return new Border
        {
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Child = body,
        };
    }

    /// <summary>
    /// 「应用」按钮上的字。**删除必须写出来**：只写"应用（3 个标签）"而实际会删掉
    /// 13 个的话，用户点下去之前根本不知道自己要丢掉什么——而这一下是不可逆的
    /// （撤销只能退一步，且不会告诉他删了什么）。
    /// </summary>
    private static string DescribeApply(AgentProposal p)
    {
        var parts = new List<string>();
        if (p.HasRemovals) parts.Add($"删 {p.Remove.Count} 个");
        if (p.HasTags) parts.Add($"{p.AddTags.Count} 个标签");
        if (p.HasProse) parts.Add("散文");
        return parts.Count > 0 ? "应用（" + string.Join(" + ", parts) + "）" : "应用";
    }

    /// <summary>
    /// 一句话说清这次自动写入到底写了什么（日志与界面提示共用）。
    /// 只给摘要不给全文：散文可能很长，而界面那条说明已经指着文档了。
    /// </summary>
    private static string DescribeProposal(AgentProposal p)
    {
        var parts = new List<string>();
        if (p.HasRemovals) parts.Add($"删除 {p.Remove.Count} 个标签（" +
                                     $"{string.Join("、", p.Remove.Take(8))}" +
                                     (p.Remove.Count > 8 ? "…" : "") + "）");
        if (p.HasTags) parts.Add($"{p.AddTags.Count} 个标签（{string.Join("、", p.AddTags.Take(8))}" +
                                 (p.AddTags.Count > 8 ? "…" : "") + "）");
        if (p.HasProse) parts.Add($"一段散文（{p.Prose.Length} 字）");
        return parts.Count > 0 ? string.Join(" + ", parts) : "无";
    }

    /// <summary>
    /// 把一条提案落进当前文档。**一次调用 = 一个撤销点**，所以 Ctrl+Z 一下能全退。
    ///
    /// 刻意不走 AddTagCore：那个每次都会自己记一个撤销点，一条 8 个标签的提案
    /// 就会占掉 8 步。这里直接动 _pendingTags，只在开头记一笔。
    /// </summary>
    private void ApplyAgentProposal(AgentProposal p, bool single)
    {
        if (_doc is null) return;
        if (p.IsEmpty && !single) return;

        PushUndo("AI 助手");

        // 合并语义（先删后加、忽略大小写、既删又加判给加）在 AgentKit.MergeProposal 里，
        // 那边是纯函数、有断言；这里只负责把结果落到文档上，不另写一份规则
        // ——两份实现迟早漂移，而漂移的表现就是"界面上写的和实际落地的不一样"。
        var (merged, added, removed) = AgentKit.MergeProposal(_pendingTags, p);
        _pendingTags = merged;
        if (added > 0 || removed > 0) _pendingTags = TagSections.Canonicalize(_pendingTags, _vocab);
        CommitPrompt();

        if (p.HasProse)
        {
            // 与翻译器完全同一条落法（替换 -> 换掉散文尾巴；追加 -> AppendProse
            // 再回读），不另写一份——散文边界的规则在 PromptText 里，重复实现
            // 必然会漂移。
            if (p.ReplaceProse || _proseTail.Trim().Length == 0)
            {
                _proseTail = ". " + p.Prose;
                CommitPrompt();
            }
            else
            {
                SetCaptionText(PromptText.AppendProse(GetCaptionText(), p.Prose));
                var parsed = ParseCaption(GetCaptionText());
                _pendingTags = new List<string>(parsed.Tags);
                _proseTail = parsed.ProseTail;
                RefreshTagRows();
            }
        }

        Log.Write($"agent applied: tags+={added} tags-={removed} " +
                  $"prose={(p.HasProse ? (p.ReplaceProse ? "replace" : "append") : "none")} " +
                  $"total={_pendingTags.Count} single={single}");
    }

    /// <summary>
    /// 聊天的滚动条跟到最新一条——**但只在用户本来就在底部时**。
    ///
    /// 用户往前翻着看的时候不许把他拽回底部（那样根本读不了历史，而这条路径
    /// 生成期间会持续产出内容，一拽就是连环拽）。判据是"翻页前是否贴着底"，
    /// 由 <see cref="AgentScroll_ViewChanged"/> 维护。
    ///
    /// **要补一跳**：卡片是刚刚才 Add 进去的，第一跳时布局还没算完，
    /// ScrollableHeight 读到的还是旧值——实测结果是停在上一条的位置，
    /// 而最新那张提案卡片（要用户点应用的那张）正好在视野外。
    /// </summary>
    private void AgentScrollToEnd(bool force = false)
    {
        if (!force && !_agentFollowTail) return;
        DispatcherQueue.TryEnqueue(AgentScrollToEndNow);
        _ = AgentScrollToEndLaterAsync();
    }

    /// <summary>用户是否停留在底部（决定要不要跟着新内容走）。</summary>
    private bool _agentFollowTail = true;

    /// <summary>离底部多远还算"在底部"。太小会被一行文字的高度差判成"翻上去了"。</summary>
    private const double AgentTailSlack = 24;

    private void AgentScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        // 自己滚到底之后这里也会触发，结果是 followTail 保持 true——正是想要的。
        _agentFollowTail = AgentScroll.ScrollableHeight - AgentScroll.VerticalOffset <= AgentTailSlack;
    }

    private void AgentScrollToEndNow() =>
        AgentScroll.ChangeView(null, AgentScroll.ScrollableHeight, null);

    private async Task AgentScrollToEndLaterAsync()
    {
        await Task.Delay(150);
        DispatcherQueue.TryEnqueue(AgentScrollToEndNow);
    }

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

        // **这里不再清空对话**（用户要求：只有点「清空对话」才清）。
        // 换图改成插一条边界说明——既保住"接着问"的能力，又让模型知道画面换了，
        // 而那正是当年"换文档就整段丢掉"想解决的问题。
        NoteDocumentSwitch();

        // **两个方向都要走这里**：设置里把助手关掉时也要把翻译器放回来。
        // 早先这里写成"只在开着时刷新面板"，结果关掉之后面板会一直留在屏幕上
        // ——设置保存后恰好也走 LoadFolder → AttachHistory 这条路，所以它是唯
        // 一个能让改动立刻生效的地方。
        ApplyAgentChrome(_mode == PromptMode.Draft);
    }

    /// <summary>
    /// 把落盘的对话重新画进面板。启动时调一次。
    ///
    /// 走的是和实时那一轮**同一个** <see cref="AgentAddAssistant"/>，所以恢复出来
    /// 的样子和刚说完时一致。**提案卡片不在其中**：它是可点的动作，重启后再点一次
    /// 「应用」会把标签再加一遍，而用户以为那只是"看看历史"。
    /// </summary>
    private void RenderRestoredConversation()
    {
        AgentFeed.Children.Clear();
        if (_agentChat.IsEmpty) return;

        foreach (var e in _agentChat.Snapshot())
        {
            switch (e.KindOf())
            {
                case AgentChatStore.Kind.User:
                    AgentAddUserBubble(e.Text);
                    break;
                case AgentChatStore.Kind.Assistant:
                    AgentAddAssistant(e.Text, e.Reasoning, e.Calls, proposal: null);
                    break;
                default:
                    AgentAddNote(e.Text);
                    break;
            }
        }

        if (_agentChat.Trimmed)
            AgentAddNote("（更早的对话已超出保留上限，被移出）");

        // 恢复完之后不再需要记"当前是哪张图"——模型要的话自己调 current_image。
        AgentScrollToEnd();
        Log.Write($"agent: restored {_agentChat.Count} entries");
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
