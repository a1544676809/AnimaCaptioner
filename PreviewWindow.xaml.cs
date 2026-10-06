using AnimaCaptioner.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AnimaCaptioner;

/// <summary>
/// 独立的大图预览窗口。
///
/// 独立窗口而不是模态对话框有两个实际原因：一是可以边看大图边改 tag（模态会把
/// 主窗口锁住），二是预览窗口能自己记住尺寸和位置。
///
/// 缩放实现的关键：Image 显式设成图像的像素尺寸，这样 ScrollViewer 的
/// ZoomFactor 里 1.0 就精确等于「1 图像像素 = 1 DIP」，于是「适应窗口」和
/// 「1:1」都能算出确定的倍率。若让 Image Stretch=Uniform 填满容器，
/// 缩放基准就变成容器尺寸，两个按钮都会给出错误结果。
/// </summary>
public sealed partial class PreviewWindow : Window
{
    private IReadOnlyList<DatasetItem> _items = Array.Empty<DatasetItem>();
    private int _index = -1;
    private bool _fitMode = true;          // 适应窗口模式：窗口变化时自动重算倍率
    private int _imgW, _imgH;              // 当前图的原始像素尺寸
    private double? _wantZoom;             // 还没生效的目标缩放（见 TryApplyPendingZoom）
    private int _zoomRetry;
    private double _lastSetZoom = 1.0;     // 我们上次设下去的 ZoomFactor，用于识别用户手动缩放
    private int _loadSeq;                  // 丢弃过期的异步载入结果
    private AppSettings? _cfg;

    /// <summary>关闭窗口时通知主窗口把引用清掉（否则再按空格不会开新窗口）。</summary>
    public event Action? Closed2;

    public PreviewWindow()
    {
        InitializeComponent();

        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(ico)) AppWindow.SetIcon(ico);
        }
        catch { }

        // 键盘：翻页 + 缩放。放在 RootGrid 上，焦点在按钮上也照样生效。
        AddAccel(Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.None, () => Step(-1));
        AddAccel(Windows.System.VirtualKey.Right, Windows.System.VirtualKeyModifiers.None, () => Step(1));
        AddAccel(Windows.System.VirtualKey.Escape, Windows.System.VirtualKeyModifiers.None, Close);
        AddAccel(Windows.System.VirtualKey.Add, Windows.System.VirtualKeyModifiers.Control, () => Zoom(1.25));
        AddAccel(Windows.System.VirtualKey.Subtract, Windows.System.VirtualKeyModifiers.Control, () => Zoom(0.8));
        AddAccel(Windows.System.VirtualKey.Number0, Windows.System.VirtualKeyModifiers.Control, FitToWindow);

        Closed += (_, _) => Closed2?.Invoke();
        RootGrid.Loaded += (_, _) => ApplyWindowBounds();
    }

    private void AddAccel(Windows.System.VirtualKey key,
                          Windows.System.VirtualKeyModifiers mods, Action act)
    {
        var a = new KeyboardAccelerator { Key = key, Modifiers = mods };
        a.Invoked += (_, e) => { e.Handled = true; act(); };
        RootGrid.KeyboardAccelerators.Add(a);
    }

    // ================= 打开 =================

    /// <summary>载入数据集并定位到指定的一张。返回 false 表示没有可显示的内容。</summary>
    public bool Open(IReadOnlyList<DatasetItem> items, int index, AppSettings cfg)
    {
        _cfg = cfg;
        _items = items;
        if (_items.Count == 0)
        {
            Placeholder.Visibility = Visibility.Visible;
            PreviewImage.Source = null;
            PrevBtn.IsEnabled = NextBtn.IsEnabled = false;
            NavText.Text = "0 / 0";
            return false;
        }
        Show(Math.Clamp(index, 0, _items.Count - 1));
        return true;
    }

    private void Step(int delta)
    {
        if (_items.Count == 0) return;
        var next = _index + delta;
        if (next < 0 || next >= _items.Count) return;
        Show(next);
    }

    private void Show(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _index = index;
        var item = _items[_index];

        _fitMode = true;
        _ = LoadAsync(item);

        Title = item.Name + " — 预览";
        TitleText.Text = Path.GetFileName(item.ImagePath);

        NavText.Text = $"{_index + 1} / {_items.Count}";
        PrevBtn.IsEnabled = _index > 0;
        NextBtn.IsEnabled = _index < _items.Count - 1;

        var hasCap = item.HasCaption;
        InfoText.Text = hasCap
            ? $"{CaptionFile.Load(item.CaptionPath).Tags.Count} tags"
            : "未标注";
    }

    /// <summary>
    /// 载入原图。与主窗口同样的理由用 stream + SetSourceAsync：
    /// UriSource 的失败会静默走 ImageFailed，try/catch 抓不到。
    /// 这里不设 DecodePixelWidth——预览看的就是原始分辨率。
    /// </summary>
    private async Task LoadAsync(DatasetItem item)
    {
        var seq = ++_loadSeq;
        Busy.IsActive = true;
        try
        {
            using var stream = await Windows.Storage.Streams.FileRandomAccessStream
                .OpenAsync(item.ImagePath, Windows.Storage.FileAccessMode.Read);

            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            if (seq != _loadSeq) return;

            _imgW = bmp.PixelWidth;
            _imgH = bmp.PixelHeight;

            // 显式像素尺寸：ZoomFactor=1 就等于 1 图像像素 = 1 DIP
            PreviewImage.Width = bmp.PixelWidth;
            PreviewImage.Height = bmp.PixelHeight;
            PreviewImage.Source = bmp;
            Placeholder.Visibility = Visibility.Collapsed;

            var fi = new FileInfo(item.ImagePath);
            InfoText.Text = $"{bmp.PixelWidth}×{bmp.PixelHeight} · " +
                            $"{fi.Length / 1024.0 / 1024.0:F1} MB · " +
                            (item.HasCaption ? "有 caption" : "无 caption");

            // 换图后把待生效的缩放丢掉：它属于上一张图的布局
            _wantZoom = null;
            // 回到左上角，否则缩放到 100% 时会停在上次滚动的位置
            Scroll.ChangeView(0, 0, null, disableAnimation: true);

            // 等布局跑完再算倍率，否则 Viewport 还是 0（属性变化没有事件可靠）
            DispatcherQueue.TryEnqueue(() =>
            {
                if (seq != _loadSeq) return;
                RootGrid.UpdateLayout();
                if (_fitMode) FitToWindow();
                else UpdateZoomLabel();
            });
        }
        catch (Exception ex)
        {
            if (seq != _loadSeq) return;
            PreviewImage.Source = null;
            Placeholder.Visibility = Visibility.Visible;
            Placeholder.Text = "图片载入失败：" + ex.Message;
            Log.Write($"preview load FAILED {item.ImagePath}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (seq == _loadSeq) Busy.IsActive = false;
        }
    }

    // ================= 缩放 =================

    /// <summary>
    /// 屏幕的 DPI 缩放比（本机 150% 即 1.5）。
    ///
    /// 这个值必须参与缩放计算：ScrollViewer.ZoomFactor 的单位是 DIP 而不是物理像素，
    /// 所以 ZoomFactor=1.0 在 150% 缩放下是「1 图像像素 = 1.5 屏幕像素」，
    /// 「1:1」按钮就不成立了。Image 的宽高按图像像素设置，因此
    /// ZoomFactor 恰好等于「1 图像像素占几个 DIP」，乘上本值才是屏幕上的真实倍率。
    /// </summary>
    private double Scale
    {
        get
        {
            try { return Content?.XamlRoot?.RasterizationScale ?? 1.0; }
            catch { return 1.0; }
        }
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitToWindow();

    /// <summary>
    /// 1:1 = 一个图像像素对应一个**物理**屏幕像素。
    /// </summary>
    private void Actual_Click(object sender, RoutedEventArgs e)
    {
        _fitMode = false;
        SetZoom(1.0 / Scale);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(0.8);

    /// <summary>
    /// 按视口大小算出恰好放下整张图的倍率。
    /// ViewportWidth 与 Image.Width 都是 DIP，在同一单位体系里直接相比，
    /// 结果天然就是正确的 ZoomFactor（不需要再除 Scale）。
    /// </summary>
    private void FitToWindow()
    {
        if (_imgW <= 0 || _imgH <= 0) return;

        // 读 Viewport 之前必须先跑完布局，否则拿到的是刚设完 Source 那一刻的旧值
        try { Scroll.UpdateLayout(); } catch { }

        var vw = Scroll.ViewportWidth > 0 ? Scroll.ViewportWidth : Scroll.ActualWidth;
        var vh = Scroll.ViewportHeight > 0 ? Scroll.ViewportHeight : Scroll.ActualHeight;
        if (vw <= 1 || vh <= 1) return;

        // 留一点余量，避免贴边
        var k = Math.Min(vw / _imgW, vh / _imgH) * 0.98;
        _fitMode = true;
        SetZoom(k);
    }

    private void Zoom(double factor)
    {
        _fitMode = false;                      // 手动缩放后不再自动适应
        SetZoom(Scroll.ZoomFactor * factor);
    }

    /// <summary>
    /// 请求一个缩放倍率。
    ///
    /// ChangeView 并不保证立即生效：内容刚换过、还没测量完时它会被静默忽略
    /// （实测「适应窗口」算出 18%，画面却停在 100%，而标题已经写上了 18%——
    /// 标题于是在撒谎）。所以这里把请求值记下来，由 ViewChanged 与后续布局
    /// 校正；标题一律显示 Scroll.ZoomFactor 这个**实际**值。
    /// </summary>
    private void SetZoom(double k)
    {
        var lo = Scroll.MinZoomFactor;
        var hi = Scroll.MaxZoomFactor;
        if (k < lo) k = lo;
        if (k > hi) k = hi;

        _wantZoom = k;
        _zoomRetry = 0;
        _lastSetZoom = k;
        Scroll.ChangeView(null, null, (float)k, disableAnimation: true);
        UpdateZoomLabel();
        TryApplyPendingZoom();
    }

    /// <summary>在布局之后把没生效的缩放再补一次，最多补几次以免死循环。</summary>
    private void TryApplyPendingZoom()
    {
        if (_wantZoom is not double want) return;
        if (Math.Abs(Scroll.ZoomFactor - want) < 0.001) { _wantZoom = null; return; }
        if (_zoomRetry++ > 8) { _wantZoom = null; return; }   // 放弃，标签已显示实际值

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_wantZoom is not double w2) return;
            Scroll.ChangeView(null, null, (float)w2, disableAnimation: true);
            UpdateZoomLabel();
            TryApplyPendingZoom();
        });
    }

    private void Scroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateZoomLabel();

        // 程序请求的缩放已经落地，清掉待处理标记
        if (_wantZoom is double w && Math.Abs(Scroll.ZoomFactor - w) < 0.001)
            _wantZoom = null;

        // 判定这次变化是不是用户自己缩放的：没有待处理请求，且实际值与
        // 我们上次设下去的值不同，那就只可能是用户 Ctrl+滚轮。必须退出
        // 「适应窗口」模式，否则下一次窗口尺寸变化会把用户的缩放冲掉。
        // 用数值比较而不是计时标志：ViewChanged 的触发时机不确定，
        // 靠延时复位会在慢机器上漏判。
        if (_wantZoom is null && Math.Abs(Scroll.ZoomFactor - _lastSetZoom) > 0.001)
        {
            _fitMode = false;
            _lastSetZoom = Scroll.ZoomFactor;
        }
    }

    /// <summary>
    /// 标题里显示**屏幕上真实的倍率**（实际 ZoomFactor × DPI 缩放），
    /// 这样「100%」确实等于一个图像像素一个屏幕像素，与 1:1 按钮一致。
    /// </summary>
    private void UpdateZoomLabel()
    {
        var name = _index >= 0 && _index < _items.Count ? _items[_index].Name : "";
        var pct = Scroll.ZoomFactor * Scale * 100;
        Title = $"{name} — 预览  {pct:F0}%";
    }

    private void Scroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 只有「适应窗口」模式才跟着窗口走；手动缩放过就不该被拉回
        if (_fitMode) FitToWindow();
    }

    // ================= 窗口位置与大小 =================

    /// <summary>
    /// 恢复上次的窗口尺寸与位置。放在 Loaded 之后：
    /// 构造函数阶段 AppWindow 还拿不到真实显示器，Resize/Move 会被静默丢弃
    /// （主窗口踩过这个坑，停在 1000×600 的默认值）。
    /// </summary>
    private void ApplyWindowBounds()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var wa = area.WorkArea;

            var w = _cfg?.PreviewWidth ?? 0;
            var h = _cfg?.PreviewHeight ?? 0;

            if (w < 320 || h < 240)
            {
                // 没存过或数值不合理：给一个贴近工作区的大小
                w = (int)Math.Min(1200, wa.Width * 0.8);
                h = (int)Math.Min(900, wa.Height * 0.85);
            }
            if (w > wa.Width) w = wa.Width;
            if (h > wa.Height) h = wa.Height;

            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
            AppWindow.Move(new Windows.Graphics.PointInt32(
                wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2));

            Log.Write($"preview sized {w}x{h} (workarea {wa.Width}x{wa.Height})");
        }
        catch (Exception ex)
        {
            Log.Write("preview bounds failed: " + ex.Message);
        }
    }

    /// <summary>把当前尺寸存回配置，下次打开同一个大小。</summary>
    public void SaveBounds()
    {
        if (_cfg is null) return;
        try
        {
            var s = AppWindow.Size;
            if (s.Width >= 320 && s.Height >= 240)
            {
                _cfg.PreviewWidth = s.Width;
                _cfg.PreviewHeight = s.Height;
                _cfg.Save();
                Log.Write($"preview bounds saved {s.Width}x{s.Height}");
            }
        }
        catch (Exception ex) { Log.Write("preview save bounds failed: " + ex.Message); }
    }

    // ================= 按钮 =================

    private void Prev_Click(object sender, RoutedEventArgs e) => Step(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Step(1);

    /// <summary>供主窗口调用：切到某一张（例如主窗口换了选中项）。</summary>
    public void GoTo(int index) => Show(index);
}
