using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AnimaCaptioner.Core;

/// <summary>
/// 把训练集里的一张图缩成能安全发出去的字节（JPEG）。
///
/// 三个决定，都有具体理由：
///
/// 1. **解码时就缩，不是先解全图再缩。** 训练集单张是 8.4 MB / 3258×3978，
///    全尺寸解码一张就是约 52 MB 内存（和 ThumbnailLoader 同一个坑）。
///    这里用 <see cref="BitmapTransform"/> 让解码器直接出小图。
/// 2. **一律重编码成 JPEG，哪怕原图已经很小。** 原图可能是 8 MB 的 PNG，
///    内联成 base64 还要再涨三分之一；长边 1024 的 JPEG 大约 150 KB。
/// 3. **EXIF 方向要算进去。** 竖拍图的 EXIF 方向是 6/8，解码器报的宽高是
///    转置的（见 <see cref="ImageSizing.Oriented"/>）；不管它的话竖图的
///    长边会算错，模型看到的图还是躺着的。
///
/// 这个文件碰 WinRT，所以**不在 tests/corecheck.csproj 的编译清单里**
/// （和 ThumbnailLoader 一样）——尺寸那点数学单独放在 <see cref="ImageSizing"/> 里，
/// 那份是可测的。
/// </summary>
public static class ImagePayload
{
    public const string Mime = "image/jpeg";

    /// <summary>JPEG 质量。0.85 在 1024 长边下约 150 KB，肉眼看不出损失。</summary>
    private const double Quality = 0.85;

    /// <summary>
    /// 读一张图并缩好。失败时返回 null 并记日志——**图片读不出来不该让整个
    /// 功能失败**：不带图问一句仍然是有用的，界面会说明这次没带图。
    /// </summary>
    public static async Task<LlmClient.ChatImage?> LoadAsync(string path)
    {
        try
        {
            using var file = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(file);

            var orientation = await ReadOrientationAsync(decoder);
            var (ow, oh) = ImageSizing.Oriented((int)decoder.PixelWidth, (int)decoder.PixelHeight,
                                               orientation);
            var (tw, th) = ImageSizing.Fit(ow, oh);
            if (tw <= 0 || th <= 0) return null;

            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)tw,
                ScaledHeight = (uint)th,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };

            using var bmp = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            using var ms = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms);

            // 质量走 BitmapProperties 而不是某个 SetQuality 方法——JPEG 编码器
            // 的质量就是这条 `ImageQuality` 属性。必须在 SetSoftwareBitmap 之前设。
            await encoder.BitmapProperties.SetPropertiesAsync(
                new Dictionary<string, BitmapTypedValue>
                {
                    ["ImageQuality"] = new BitmapTypedValue(Quality, PropertyType.Single),
                });

            encoder.SetSoftwareBitmap(bmp);
            await encoder.FlushAsync();

            var bytes = await ReadAllAsync(ms);
            Log.Write($"agent image: {Path.GetFileName(path)} " +
                      $"{decoder.PixelWidth}x{decoder.PixelHeight} exif={orientation} -> " +
                      $"{tw}x{th} {bytes.Length / 1024} KB");
            return new LlmClient.ChatImage(Mime, bytes);
        }
        catch (Exception ex)
        {
            Log.Write($"agent image FAILED {Path.GetFileName(path)}: " +
                      $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// EXIF 方向。取不到就是 1（正常）。这条属性是 WinRT 的
    /// `System.Photo.Orientation`，拿它要 await 一次属性查询，所以单独一个方法。
    /// </summary>
    private static async Task<ushort> ReadOrientationAsync(BitmapDecoder decoder)
    {
        try
        {
            var props = await decoder.BitmapProperties.GetPropertiesAsync(
                new[] { "System.Photo.Orientation" });
            if (props.TryGetValue("System.Photo.Orientation", out var v) &&
                v.Value is ushort u && u is >= 1 and <= 8)
                return u;
        }
        catch (Exception ex)
        {
            // 没有这条属性很常见（PNG 就没有），不该升级成错误
            Log.Write("agent image: no exif orientation (" + ex.GetType().Name + ")");
        }
        return 1;
    }

    private static async Task<byte[]> ReadAllAsync(InMemoryRandomAccessStream ms)
    {
        var size = (int)ms.Size;
        using var reader = new DataReader(ms.GetInputStreamAt(0));
        await reader.LoadAsync((uint)size);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
