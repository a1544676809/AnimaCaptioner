using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AnimaCaptioner.Core;

/// <summary>
/// 图片列表的缩略图解码。两条约束决定了这里的做法：
///
/// 1. 训练集单张图可达 8.4 MB / 3258×3978，全尺寸解码一张就是约 52 MB 内存
///    （3258×3978×4）。所以必须用 DecodePixelWidth 让解码器直接出小图，
///    而不是先解全图再缩——那在 53 张上就是 2.7 GB。
/// 2. ListView 随滚动反复回收容器，如果在模板里实时解码，滚动时会不停读盘。
///    所以结果缓存在 ImageItem 上；但行对象与列表同寿命，因此对总量设上限，
///    超出后释放最旧的（把 Thumbnail 置空，滚回来会重新加载）。
///
/// 解码一律在 UI 线程发起：BitmapImage 是 XAML 对象，在后台线程构造不安全。
/// 真正的读盘与解码由 SetSourceAsync 内部切到后台，await 期间不阻塞界面；
/// 并发数用信号量限制，避免快速滚动时同时开出几十个解码。
/// </summary>
public sealed class ThumbnailLoader
{
    private readonly SemaphoreSlim _gate;
    private readonly LinkedList<ImageItem> _order = new();
    private readonly int _decodeWidth;
    private readonly int _maxCached;

    /// <param name="decodeWidth">解码宽度（物理像素）。高度按原图比例，所以竖图也会一起变小。</param>
    /// <param name="maxCached">最多同时保留多少张缩略图，超出后释放最旧的。</param>
    /// <param name="concurrency">同时解码的数量上限。</param>
    public ThumbnailLoader(int decodeWidth = 160, int maxCached = 400, int concurrency = 4)
    {
        _decodeWidth = decodeWidth;
        _maxCached = maxCached;
        _gate = new SemaphoreSlim(concurrency);
    }

    /// <summary>为一行请求缩略图。已经加载过、正在加载、或已确认失败的行会直接跳过。</summary>
    public async void Request(ImageItem item)
    {
        if (item.Thumbnail is not null || item.ThumbLoading || item.ThumbFailed) return;
        item.ThumbLoading = true;

        await _gate.WaitAsync();
        try
        {
            using var stream = await Windows.Storage.Streams.FileRandomAccessStream
                .OpenAsync(item.Source.ImagePath, Windows.Storage.FileAccessMode.Read);

            var bmp = new BitmapImage
            {
                // 必须在 SetSourceAsync 之前设置，之后再改不会重新解码
                DecodePixelWidth = _decodeWidth,
                DecodePixelType = DecodePixelType.Physical
            };
            await bmp.SetSourceAsync(stream);

            item.Thumbnail = bmp;
            Touch(item);
        }
        catch (Exception ex)
        {
            // 记下来，别对同一个坏文件反复重试（滚动时会疯狂触发）
            item.ThumbFailed = true;
            Log.Write($"thumb FAILED {Path.GetFileName(item.Source.ImagePath)}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            item.ThumbLoading = false;
            _gate.Release();
        }
    }

    /// <summary>把刚加载的行移到最近使用的一侧，并在超限时释放最旧的。</summary>
    private void Touch(ImageItem item)
    {
        _order.Remove(item);
        _order.AddFirst(item);

        while (_order.Count > _maxCached)
        {
            var oldest = _order.Last;
            if (oldest is null) break;
            _order.RemoveLast();

            // 置空而不是移除行：滚回来时会重新加载
            oldest.Value.Thumbnail = null;
        }
    }

    /// <summary>清空缓存（重新载入目录时调用）。</summary>
    public void Clear() => _order.Clear();
}
