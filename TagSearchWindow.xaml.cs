using AnimaCaptioner.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AnimaCaptioner;

/// <summary>
/// 标签搜索窗口：中英混合搜词库，或者用自然语言让模型去词库里查。
///
/// 独立窗口而不是对话框，理由和帮助窗口一样——搜到标签之后要做的下一件事
/// 就是把它加进当前图片，模态会把主窗口锁住，恰好挡住要看的东西。
///
/// 两种模式共用同一个行模板和同一个"加入"动作，因为用户在两处做的事完全相同：
/// 看一眼这个标签是什么，然后决定加不加。
///
/// **可点列表一律来自词库，不解析模型的话。** 模型回答里的标签名只用来显示，
/// 真正的候选列表取工具实际返回的行——这样点一下必然加进去一个真实存在的标签，
/// 而不是模型编出来的字符串。
/// </summary>
public sealed partial class TagSearchWindow : Window
{
    private AppSettings? _cfg;
    private VocabDb? _vocab;

    /// <summary>读当前图片的标签（实时读，不是打开时的快照）。</summary>
    private Func<IReadOnlyList<string>>? _currentTags;

    /// <summary>
    /// 加入一个标签。返回值语义：<c>Ok</c> 表示"这个标签现在在图片里了"——
    /// 新加进去和本来就有都算 true，只有"没有选中图片"之类才 false。
    /// </summary>
    private Func<string, (bool Ok, string Message)>? _addTag;

    private readonly LlmClient _llm = new();
    private TagSearchAgent? _agent;

    /// <summary>InitializeComponent 期间 XAML 会触发 Checked，那时后面的控件还没建出来。</summary>
    private bool _ready;

    /// <summary>程序性改 QueryBox.Text 时不要触发一次搜索。</summary>
    private bool _suppress;

    private bool _busy;

    /// <summary>
    /// 输入防抖。不是优化癖：复合中文查询会走"按词拆开匹配"那趟兜底，
    /// 实测 150–210 ms（要遍历 33 万行做十几次 Contains）。挂在每次按键上，
    /// 打「白色蕾丝连衣裙」会有四五次这样的停顿，手感就是"打字卡"。
    /// 200 ms 的防抖让中间那几次不跑，只搜最终那一下。
    ///
    /// 类型必须写全名：`using Windows.System`（VirtualKey 要用）也带进来一个
    /// 同名的 `DispatcherQueueTimer`，不限定就是 CS0104 二义性。
    /// </summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _debounce;

    /// <summary>大类 → (行底色, 强调色)。每次查询建几百个 SolidColorBrush 是白费，缓存一份。</summary>
    private readonly Dictionary<string, (Brush Tint, Brush Accent)> _palette = new(StringComparer.Ordinal);

    public event Action? Closed2;

    public TagSearchWindow()
    {
        InitializeComponent();

        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(ico)) AppWindow.SetIcon(ico);
        }
        catch { }

        Closed += (_, _) =>
        {
            try { _llm.Dispose(); } catch { }
            Closed2?.Invoke();
        };

        // 双击列表行 = 加入。单击只选中——搜索列表里误触一下就把标签写进
        // 训练集太危险，所以"加入"必须是明确动作（＋ 按钮或双击）。
        HitList.DoubleTapped += (_, e) => { AddSelected(HitList); e.Handled = true; };
        AiHitList.DoubleTapped += (_, e) => { AddSelected(AiHitList); e.Handled = true; };

        _debounce = DispatcherQueue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(200);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) =>
        {
            if (!_ready || _busy || ModeAi.IsChecked == true) return;
            Search();
        };

        RootGrid.Loaded += (_, _) =>
        {
            _ready = true;
            ApplyMode();
        };
    }

    // ================= 打开 =================

    public void Open(AppSettings cfg, VocabDb? vocab,
                     Func<IReadOnlyList<string>> currentTags,
                     Func<string, (bool Ok, string Message)> addTag)
    {
        _cfg = cfg;
        _vocab = vocab;
        _currentTags = currentTags;
        _addTag = addTag;
        _agent = new TagSearchAgent(_llm);
        _palette.Clear();   // 配色可能被改过，重新取

        ApplyWindowBounds();

        // 回到上次用的模式。默认词库搜索——它不依赖模型，永远可用。
        _suppress = true;
        var ai = string.Equals(cfg.TagSearchMode, "ai", StringComparison.OrdinalIgnoreCase);
        ModeAi.IsChecked = ai;
        ModeDb.IsChecked = !ai;
        _suppress = false;

        ApplyMode();
        QueryBox.Focus(FocusState.Programmatic);
        Log.Write($"tag search window opened (mode={(ai ? "ai" : "db")}, vocab={vocab?.TagCount ?? 0})");
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppress) return;
        ApplyMode();
    }

    private void ApplyMode()
    {
        // XAML 解析到 ModeDb 时 AiPane 还没建出来，Checked 就已经触发过一次了。
        if (AiPane is null) return;

        var ai = ModeAi.IsChecked == true;
        AiPane.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
        HitList.Visibility = ai ? Visibility.Collapsed : Visibility.Visible;
        RunBtn.Content = ai ? "问模型" : "搜索";
        QueryBox.PlaceholderText = ai
            ? "用一句话描述你想找什么，例如：有没有表现害羞的表情标签？"
            : "输入中文或英文关键词，也可以搜画师名或角色名";

        if (ai)
        {
            StatusText.Text = "问模型：它会去词库里查，再把查到的标签列在下面。" +
                              "能加进去的一定是库里真实存在的标签。";
        }
        else
        {
            _debounce.Stop();
            Search();   // 切回词库模式立刻按当前关键词搜一次
        }
    }

    // ================= 词库搜索 =================

    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _suppress || _busy) return;
        if (ModeAi.IsChecked == true) return;   // 问模型要按按钮，不是边打边问

        // Stop + Start 就是防抖：每敲一个字都把倒计时重置，只有停手 200 ms 才真搜。
        _debounce.Stop();
        _debounce.Start();
    }

    private void Query_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        _ = RunAsync();
    }

    private void Run_Click(object sender, RoutedEventArgs e) => _ = RunAsync();

    private Task RunAsync()
    {
        if (_busy) return Task.CompletedTask;
        _debounce.Stop();   // 手动触发就把待办的那次取消，免得刚搜完又被补搜一次
        if (ModeAi.IsChecked == true) return AskAsync();
        Search();
        return Task.CompletedTask;
    }

    private void Search()
    {
        var q = QueryBox.Text.Trim();
        if (q.Length == 0)
        {
            HitList.ItemsSource = null;
            StatusText.Text = "输入关键词开始搜索。中文、英文、画师名、角色名都能搜。";
            return;
        }
        if (_vocab is null)
        {
            HitList.ItemsSource = null;
            Warn("词库未加载", "请到「工具 → 设置」里确认词库路径，然后重新载入。");
            return;
        }

        var t0 = Environment.TickCount64;
        var res = TagSearch.QueryDetailed(_vocab, q, TagSearch.UiLimit);
        var hits = res.Hits;
        var cur = Snapshot();
        HitList.ItemsSource = hits.Select(h => MakeRow(h, cur)).ToList();
        var ms = Environment.TickCount64 - t0;

        if (hits.Count == 0)
        {
            StatusText.Text = $"没有匹配「{q}」的标签。换个说法，或者切到「问模型」让它去找。";
        }
        else
        {
            // 按词拆开匹配的结果只是"部分相关"，要说清楚——否则用户会以为
            // 这些标签就是他要找的东西（实测「量子计算机」会带出 computing 类噪声，
            // 过半门槛已经挡掉大部分，但拆词匹配本身仍然比整串匹配松）。
            var how = res.SplitMatch ? "整串没匹配到，已按词拆开匹配（部分相关）" : "整串匹配";
            StatusText.Text = $"{hits.Count} 个结果 · {how} · {ms} ms · " +
                              "点行尾的 ＋ 或双击加入当前图片" +
                              (hits.Count >= TagSearch.UiLimit ? "（已到上限，写更具体的关键词）" : "");
        }
    }

    // ================= 问模型 =================

    private async Task AskAsync()
    {
        var q = QueryBox.Text.Trim();
        if (q.Length == 0)
        {
            Warn("还没有问题", "先写一句话，例如「有没有表现害羞的表情标签？」");
            return;
        }
        if (_vocab is null)
        {
            Warn("词库未加载", "请到「工具 → 设置」里确认词库路径，然后重新载入。");
            return;
        }

        SetBusy(true);
        StatusText.Text = "正在问模型并查词库…（它可能要查好几次，十几秒到一分钟）";
        try
        {
            var res = await _agent!.AskAsync(_cfg!, _vocab, q);
            if (!res.Ok)
            {
                Warn("问模型失败", res.Error);
                StatusText.Text = "失败：" + res.Error;
                return;
            }

            RenderAnswer(res);

            var cur = Snapshot();
            AiHitList.ItemsSource = res.Found.Select(h => MakeRow(h, cur)).ToList();
            FoundLabel.Text = res.Found.Count > 0
                ? $"查到的标签（{res.Found.Count}，按引用数排序）"
                : "查到的标签（没有）";
            LogText.Text = res.Log.Count > 0
                ? $"检索记录：{string.Join("；", res.Log)}（{res.Rounds} 轮）"
                : "";

            StatusText.Text = res.Found.Count > 0
                ? $"模型查了 {res.Rounds} 轮，找到 {res.Found.Count} 个标签。" +
                  "下面这些来自词库，点 ＋ 或双击即可加入。"
                : $"模型查了 {res.Rounds} 轮，没有找到合适的标签。换个说法再问。";
            Log.Write($"tag search ask: rounds={res.Rounds} found={res.Found.Count} " +
                      $"suspicious={res.Suspicious.Count} q={q}");
        }
        catch (Exception ex)
        {
            Log.Write("tag search ask FAILED: " + ex);
            Warn("问模型失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// 把模型回答渲染出来。反引号包起来的是它声称的标签名——单独用等宽字体标出，
    /// 让"哪一段是标签"一眼可见；词库里查不到的那些用警示色标出来。
    ///
    /// 注意这里只是**显示**。真正能点着加进去的标签取自工具结果，不是这段文字。
    /// </summary>
    private void RenderAnswer(TagSearchAgent.Outcome res)
    {
        AnswerText.Blocks.Clear();
        AnswerLabel.Text = "模型回答（它写的话，可能不准）";

        var suspicious = new HashSet<string>(res.Suspicious, StringComparer.OrdinalIgnoreCase);
        var ok = TagColors.BrushOf(TagSections.General, _cfg);
        var bad = TagColors.BrushOf(TagSections.Meta, _cfg);

        var p = new Paragraph();
        var parts = (res.Answer ?? "").Split('`');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            if (i % 2 == 1)
            {
                p.Inlines.Add(new Run
                {
                    Text = parts[i],
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = suspicious.Contains(parts[i]) ? bad : ok,
                });
            }
            else
            {
                p.Inlines.Add(new Run { Text = parts[i] });
            }
        }
        AnswerText.Blocks.Add(p);

        if (res.Suspicious.Count > 0)
        {
            // 措辞要覆盖两种情况：模型编了个不存在的标签名，和它只是在建议搜索词。
            // 实测两种都出现过（`quantum symbol` 是编的；`量子`/`计算` 是建议的关键词）。
            AnswerHint.Text = $"⚠ 模型提到的 {string.Join("、", res.Suspicious)} 在词库里没有——" +
                              "可能是它编的，也可能只是它建议的搜索词。" +
                              "下面可点的列表只包含库里确实有的标签。";
            AnswerHint.Visibility = Visibility.Visible;
        }
        else if (res.Found.Count == 0)
        {
            AnswerHint.Text = "这次没查到任何标签。换个说法再问，或者先用「词库搜索」试关键词。";
            AnswerHint.Visibility = Visibility.Visible;
        }
        else
        {
            AnswerHint.Visibility = Visibility.Collapsed;
        }
    }

    // ================= 加入当前图片 =================

    private void Hit_Add_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TagHitRow row) AddOne(row);
    }

    private void AddSelected(ListView list)
    {
        if (list.SelectedItem is TagHitRow row) AddOne(row);
    }

    private void AddOne(TagHitRow row)
    {
        if (_addTag is null)
        {
            Warn("无法加入", "主窗口没有提供加入入口。");
            return;
        }

        var (ok, msg) = _addTag(row.Tag);
        StatusText.Text = msg;
        Log.Write($"tag search add: {row.Tag} ok={ok} -> {msg}");

        // 加成功就把这一行标成"已在图中"。刻意**不重跑搜索**：
        // 重搜会把列表滚回顶部、清掉选中，连加几个标签时很难受。
        if (ok) row.Already = true;
    }

    // ================= 辅助 =================

    /// <summary>当前图片的标签快照，按大小写不敏感比较。</summary>
    private HashSet<string> Snapshot()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var t in _currentTags?.Invoke() ?? Array.Empty<string>()) set.Add(t);
        }
        catch (Exception ex) { Log.Write("tag search snapshot failed: " + ex.Message); }
        return set;
    }

    private TagHitRow MakeRow(TagSearch.Hit h, HashSet<string> current)
    {
        var (tint, accent) = PaletteOf(h.Section);
        return new TagHitRow
        {
            Tag = h.Tag,
            Cn = h.Cn,
            Meta = TagSearch.MetaOf(h),
            Tint = tint,
            Accent = accent,
            Already = current.Contains(h.Tag),
        };
    }

    private (Brush Tint, Brush Accent) PaletteOf(string section)
    {
        if (_palette.TryGetValue(section, out var p)) return p;
        p = (TagColors.RowTintOf(section, _cfg), TagColors.BrushOf(section, _cfg));
        _palette[section] = p;
        return p;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RunBtn.IsEnabled = !busy;
        ModeDb.IsEnabled = !busy;
        ModeAi.IsEnabled = !busy;
    }

    private void Warn(string title, string detail)
    {
        ErrBar.Title = title;
        ErrBar.Message = detail;
        ErrBar.IsOpen = true;
        Log.Write($"tag search warn: {title} / {detail}");
    }

    // ================= 窗口位置与大小 =================

    private void ApplyWindowBounds()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var wa = area.WorkArea;

            var w = _cfg?.TagSearchWidth ?? 0;
            var h = _cfg?.TagSearchHeight ?? 0;

            if (w < 520 || h < 400)
            {
                w = (int)Math.Min(1000, wa.Width * 0.66);
                h = (int)Math.Min(820, wa.Height * 0.82);
            }
            if (w > wa.Width) w = wa.Width;
            if (h > wa.Height) h = wa.Height;

            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
            AppWindow.Move(new Windows.Graphics.PointInt32(
                wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2));

            Log.Write($"tag search window sized {w}x{h} (workarea {wa.Width}x{wa.Height})");
        }
        catch (Exception ex)
        {
            Log.Write("tag search bounds failed: " + ex.Message);
        }
    }

    public void SaveBounds()
    {
        if (_cfg is null) return;
        try
        {
            var s = AppWindow.Size;
            if (s.Width >= 520 && s.Height >= 400)
            {
                _cfg.TagSearchWidth = s.Width;
                _cfg.TagSearchHeight = s.Height;
                _cfg.TagSearchMode = ModeAi.IsChecked == true ? "ai" : "db";
                _cfg.Save();
                Log.Write($"tag search bounds saved {s.Width}x{s.Height} mode={_cfg.TagSearchMode}");
            }
        }
        catch (Exception ex) { Log.Write("tag search save bounds failed: " + ex.Message); }
    }
}
