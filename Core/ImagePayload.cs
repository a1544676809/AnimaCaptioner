using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AnimaCaptioner.Core;

/// <summary>
/// 一张图在磁盘上的事实（不含任何像素）。由 <see cref="ImagePayload.ReadFactsAsync"/>
/// 读出来，喂给 <see cref="ImageTools.Info"/> 变成给模型看的一段话。
/// </summary>
public sealed record FileFacts(string Name, string Format, long FileBytes,
                               int StoredWidth, int StoredHeight, ushort Orientation);

/// <summary>
/// 一张已经编成 JPEG、可以发出去的图（整图或裁出来的一块都一样）。
///
/// 刻意**不返回 <c>LlmClient.ChatImage</c>**：这一层是纯图像处理，不该为了一个
/// 记录类型去依赖整个接口客户端——tools/imgprobe 正是靠这条才只编译图像这几个文件，
/// 就能对着真实解码器验证裁剪。包成请求体的一部分是调用方一行的事。
/// </summary>
public sealed record EncodedImage(byte[] Jpeg, int Width, int Height);

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
    ///
    /// **缩放必须按"未转正"的宽高算**（<see cref="ScaledSize"/>）：这一路请解码器
    /// 帮忙转正（RespectExifOrientation），而实测（tools/imgprobe 的 aspect 实验）
    /// 解码器是**先按 ScaledWidth/Height 缩放、再转**，也就是那两个数字解释在
    /// 转正**之前**的坐标系里。按转正后的宽高给，竖拍照片会被横向拉伸一倍——
    /// 而它照样出图，只是模型看到的画面是变形的。
    /// </summary>
    public static async Task<EncodedImage?> LoadAsync(string path)
    {
        try
        {
            using var file = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(file);

            var orientation = await ReadOrientationAsync(decoder);
            var (tw, th) = ScaledSize((int)decoder.PixelWidth, (int)decoder.PixelHeight);
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

            var bytes = await EncodeJpegAsync(bmp);
            Log.Write($"agent image: {Path.GetFileName(path)} " +
                      $"{decoder.PixelWidth}x{decoder.PixelHeight} exif={orientation} -> " +
                      $"{bmp.PixelWidth}x{bmp.PixelHeight} {bytes.Length / 1024} KB");

            // 尺寸取**解码器实际给出来的**：转正会把宽高换过来，按 tw/th 报就报错了
            return new EncodedImage(bytes, bmp.PixelWidth, bmp.PixelHeight);
        }
        catch (Exception ex)
        {
            Log.Write($"agent image FAILED {Path.GetFileName(path)}: " +
                      $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 解码器那条"顺手帮我转正"的路上，ScaledWidth/ScaledHeight 该给多少。
    ///
    /// 给 `Fit(原始宽, 原始高)`，**不是** `Fit(转正后的宽, 转正后的高)`：
    /// 实测解码器先把整幅缩到这两个数字、再转 90°，所以它们说的是转正**之前**的尺寸。
    /// 两者只差一个交换（<see cref="ImageSizing.Fit"/> 的缩放比例只由长边决定，
    /// 交换宽高不改变比例），但给错就是拉伸——长宽比错了而图还能出，最难发现。
    /// </summary>
    private static (int Width, int Height) ScaledSize(int rawWidth, int rawHeight) =>
        ImageSizing.Fit(rawWidth, rawHeight);

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

    /// <summary>
    /// 读一张图的文件事实（**不解码像素**，所以很快）。读不出来时抛异常，
    /// 由调用方转成一句给模型看的话——静默返回默认值会让模型据此算出错误的裁剪。
    /// </summary>
    public static async Task<FileFacts> ReadFactsAsync(string path)
    {
        var size = new FileInfo(path).Length;

        using var file = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(file);
        var orientation = await ReadOrientationAsync(decoder);

        return new FileFacts(Path.GetFileNameWithoutExtension(path), FormatOf(decoder, path), size,
                             (int)decoder.PixelWidth, (int)decoder.PixelHeight, orientation);
    }

    /// <summary>MIME 类型；解码器没报就退回扩展名。</summary>
    private static string FormatOf(BitmapDecoder decoder, string path)
    {
        try
        {
            var mimes = decoder.DecoderInformation?.MimeTypes;
            if (mimes is { Count: > 0 } && !string.IsNullOrWhiteSpace(mimes[0])) return mimes[0];
        }
        catch { /* 下面退回扩展名 */ }

        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext.Length == 0 ? "unknown" : "image/" + ext;
    }

    /// <summary>
    /// 把若干块区域裁出来并发给模型。**一个文件只打开一次**（tiles 一次要 9 块，
    /// 每块都重开一次文件是白读盘）。
    ///
    /// <paramref name="rects"/> 是**转正之后**的坐标系里的矩形（模型的坐标就是那个坐标系）。
    /// 三个顺序都是实测逼出来的，改任何一个之前先看 tools/imgprobe 里那两个实验命令：
    ///
    /// 1. **先把矩形反查回原始像素坐标系**（<see cref="ImageRegion.ToRaw"/>），
    ///    再让解码器按原始像素裁。
    /// 2. **解码时绝不给 ScaledWidth/Height。** 实测（本机 JPEG 解码器）：缩放是先把
    ///    **整幅**缩到那个尺寸，Bounds 再在**缩放后**的坐标系里裁。于是
    ///    "缩到 300x200 且裁 (300,0,300,200)" 直接 `E_INVALIDARG`；而
    ///    "缩到 300x200 且裁 (0,0,300,200)" 拿到的是**整幅的缩小版**、不是左上那一块——
    ///    尺寸恰好也是 300x200，看尺寸看不出来，只有取像素才认得出（本项目的探针
    ///    就是这么抓到的）。所以缩放一律交给编码器，解码只管裁。
    /// 3. **旋转由 <see cref="ImagePixels.Orient"/> 在托管代码里做**，不依赖解码器的
    ///    方向开关。代价是几毫秒的像素拷贝，换来的是能逐像素断言的行为。
    ///
    /// 唯一的例外是"要的就是整幅"（模型给 x=0,y=0,w=1,h=1，或 tiles 1x1）：
    /// 那条走解码时就缩的老路，省下一整张全尺寸位图的内存（训练集单张 3258x3978，
    /// 全尺寸解码就是 52 MB）。那条路在 <see cref="LoadAsync"/> 里已经用得很久了。
    /// </summary>
    public static async Task<IReadOnlyList<EncodedImage>> CropManyAsync(
        string path, IReadOnlyList<PixelRect> rects, int maxEdge = ImageSizing.MaxEdge)
    {
        var result = new List<EncodedImage>(rects.Count);
        if (rects.Count == 0) return result;

        using var file = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(file);

        var orientation = await ReadOrientationAsync(decoder);
        var rawWidth = (int)decoder.PixelWidth;
        var rawHeight = (int)decoder.PixelHeight;

        foreach (var rect in rects)
        {
            var raw = ImageRegion.ToRaw(rect, rawWidth, rawHeight, orientation);
            var whole = raw.X == 0 && raw.Y == 0 &&
                        raw.Width == rawWidth && raw.Height == rawHeight;

            byte[] jpeg;
            int outWidth, outHeight;

            if (whole)
            {
                // 缩放按**未转正**的宽高算，见 ScaledSize 的注释（给错就是拉伸）
                var (tw, th) = ScaledSize(rawWidth, rawHeight);

                using var bmp = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    new BitmapTransform
                    {
                        ScaledWidth = (uint)tw,
                        ScaledHeight = (uint)th,
                        InterpolationMode = BitmapInterpolationMode.Fant,
                    },
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);

                jpeg = await EncodeJpegAsync(bmp);
                outWidth = bmp.PixelWidth;
                outHeight = bmp.PixelHeight;
            }
            else
            {
                using var bmp = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    new BitmapTransform
                    {
                        // 只有 Bounds，**没有** ScaledWidth/Height —— 见上面的第 2 条
                        Bounds = new BitmapBounds
                        {
                            X = (uint)raw.X, Y = (uint)raw.Y,
                            Width = (uint)raw.Width, Height = (uint)raw.Height,
                        },
                    },
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.ColorManageToSRgb);

                var (pixels, width, height, stride) = ReadPixels(bmp);
                var (oriented, ow, oh) = ImagePixels.Orient(pixels, width, height, orientation, stride);
                var (tw, th) = ImageSizing.Fit(ow, oh, maxEdge);

                jpeg = await EncodeJpegAsync(oriented, ow, oh, tw, th);
                outWidth = tw;
                outHeight = th;
            }

            Log.Write($"agent crop: {Path.GetFileName(path)} asked={rect.X},{rect.Y}," +
                      $"{rect.Width},{rect.Height} exif={orientation} raw={raw.X},{raw.Y}," +
                      $"{raw.Width},{raw.Height} whole={whole} -> {outWidth}x{outHeight} " +
                      $"{jpeg.Length / 1024} KB");
            result.Add(new EncodedImage(jpeg, outWidth, outHeight));
        }

        return result;
    }

    /// <summary>
    /// 读一块已解码的像素。行距（stride）**必须**从解码器问出来：解码器给的行距
    /// 可以大于 宽×4，按 宽×4 去索引会逐行错位，画面呈现为斜的彩色条纹——
    /// 那种错的成因一眼看不出来。
    /// </summary>
    private static (byte[] Pixels, int Width, int Height, int Stride) ReadPixels(SoftwareBitmap bmp)
    {
        int stride;
        using (var locked = bmp.LockBuffer(BitmapBufferAccessMode.Read))
            stride = locked.GetPlaneDescription(0).Stride;

        var buffer = new Windows.Storage.Streams.Buffer((uint)(stride * bmp.PixelHeight));
        bmp.CopyToBuffer(buffer);
        CryptographicBuffer.CopyToByteArray(buffer, out var bytes);
        return (bytes, bmp.PixelWidth, bmp.PixelHeight, stride);
    }

    /// <summary>
    /// 把一块紧排的 BGRA8 像素编成 JPEG，并**在编码时缩到 <paramref name="scaledWidth"/>×
    /// <paramref name="scaledHeight"/>**（给 0 = 不缩）。
    ///
    /// 缩放放在编码器上是**有意的**：编码器的这个变换没有 Bounds，
    /// 语义只有"整幅缩到这么大"一种解释，不存在解码器那边"先缩后裁还是先裁后缩"的坑
    /// （见 <see cref="CropManyAsync"/> 的第 2 条）。质量与 <see cref="LoadAsync"/> 一致。
    /// </summary>
    private static async Task<byte[]> EncodeJpegAsync(byte[] bgra, int width, int height,
                                                      int scaledWidth = 0, int scaledHeight = 0)
    {
        using var bmp = SoftwareBitmap.CreateCopyFromBuffer(
            CryptographicBuffer.CreateFromByteArray(bgra),
            BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        return await EncodeJpegAsync(bmp, scaledWidth, scaledHeight);
    }

    /// <summary>同上，直接编一张已经解码好的位图。</summary>
    private static async Task<byte[]> EncodeJpegAsync(SoftwareBitmap bmp,
                                                      int scaledWidth = 0, int scaledHeight = 0)
    {
        using var ms = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms);

        // 质量与缩放都要在 SetSoftwareBitmap **之前**设，否则不生效。
        if (scaledWidth > 0 && scaledHeight > 0 && (scaledWidth != bmp.PixelWidth ||
                                                    scaledHeight != bmp.PixelHeight))
        {
            encoder.BitmapTransform.ScaledWidth = (uint)scaledWidth;
            encoder.BitmapTransform.ScaledHeight = (uint)scaledHeight;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        await encoder.BitmapProperties.SetPropertiesAsync(
            new Dictionary<string, BitmapTypedValue>
            {
                ["ImageQuality"] = new BitmapTypedValue(Quality, PropertyType.Single),
            });

        encoder.SetSoftwareBitmap(bmp);
        await encoder.FlushAsync();
        return await ReadAllAsync(ms);
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
