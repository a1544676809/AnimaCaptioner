using System.Globalization;
using System.Text;
using AnimaCaptioner.Core;

namespace AnimaCaptioner.ImgProbe;

/// <summary>
/// 图像工具的**实测探针**：把裁剪结果落成 JPEG，供 check.py 取像素核对。
///
/// 存在的理由只有一个——裁剪那条路依赖两件文档说不清的事：
///   1. <c>BitmapTransform.Bounds</c> 是在原始像素坐标系里解释的吗？
///   2. <c>ScaledWidth/Height</c> 是作用在裁出来的那一块上吗？
/// 猜错的症状（"裁出来的是别处""方向转了 90°""被拉扁"）在界面上都看不出来，
/// 只会表现为"模型答得和没给图一样"。所以这里不猜，落盘看像素。
///
/// 用法见 imgprobe.csproj 顶部注释。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            return argv.Length > 0 ? argv[0] switch
            {
                "facts" => await Facts(argv[1]),
                "bounds" => await Bounds(argv),
                "crop" => await Crop(argv[1], argv[2], argv[3], argv[4], argv[5], argv[6]),
                "tiles" => await Tiles(argv[1], argv[2], argv[3], argv[4]),
                "payload" => await Payload(argv[1], argv[2]),
                _ => Usage(),
            } : Usage();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            用法:
              imgprobe facts   <img>
              imgprobe crop    <img> <x> <y> <w> <h> <out.jpg>     # x/y/w/h 归一化 0-1
              imgprobe tiles   <img> <cols> <rows> <outDir>
              imgprobe payload <img> <out.jpg>                    # 整图那条既有路径
            """);
        return 2;
    }

    /// <summary>读文件事实，并把三条尺寸都打出来（原始 / 转正 / 发给模型的）。</summary>
    private static async Task<int> Facts(string path)
    {
        var f = await ImagePayload.ReadFactsAsync(path);
        var displayed = ImageSizing.Oriented(f.StoredWidth, f.StoredHeight, f.Orientation);
        var view = ImageSizing.Fit(displayed.Width, displayed.Height);

        Console.WriteLine($"file        = {Path.GetFileName(path)}");
        Console.WriteLine($"format      = {f.Format}   bytes = {f.FileBytes}");
        Console.WriteLine($"stored      = {f.StoredWidth}x{f.StoredHeight}  exif = {f.Orientation}");
        Console.WriteLine($"displayed   = {displayed.Width}x{displayed.Height}");
        Console.WriteLine($"model view  = {view.Width}x{view.Height}");

        // 同一份事实喂给 image_info 的文案，顺带看一眼句子对不对
        var facts = new ImageFacts(f.Name, f.Format, f.FileBytes, f.StoredWidth, f.StoredHeight,
                                   f.Orientation, Attached: true, Index: 1, Total: 1, CaptionTags: 0);
        Console.WriteLine();
        Console.WriteLine(ImageTools.Info(facts));
        return 0;
    }

    /// <summary>
    /// 原始像素坐标系里的裸实验：<c>BitmapTransform.Bounds</c> 和
    /// <c>ScaledWidth/Height</c> 到底谁先谁后、Bounds 在哪个坐标系里。
    ///
    /// 这个命令存在的理由就是**文档说不清**：官方只说 Bounds"用来裁剪"、
    /// ScaledWidth"用来缩放"，没写两者的次序，而实测（2026-10，本机 JPEG 解码器）
    /// 是**先按 ScaledWidth/Height 缩整幅，再按 Bounds 在缩放后的坐标系里裁**——
    /// 也就是说 Bounds 只在"裁到缩放后的图里去"时才是对的，而这意味着
    /// "按原始像素裁一块再放大"根本做不到（想放大就得先把整幅缩到比裁块还小）。
    /// 所以真正的裁剪路径是：**不解码缩放，按原始像素裁，缩放交给编码器**。
    /// 这个命令就是用来复现这两条结论的，别删。
    ///
    /// 用法: imgprobe bounds &lt;img&gt; &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt; &lt;out.jpg&gt; [scaledW scaledH]
    ///       （x/y/w/h 是**原始像素**；scaledW/H 省略或给 0 = 不缩放）
    /// </summary>
    private static async Task<int> Bounds(string[] argv)
    {
        var path = argv[1];
        var x = uint.Parse(argv[2], CultureInfo.InvariantCulture);
        var y = uint.Parse(argv[3], CultureInfo.InvariantCulture);
        var w = uint.Parse(argv[4], CultureInfo.InvariantCulture);
        var h = uint.Parse(argv[5], CultureInfo.InvariantCulture);
        var outPath = argv[6];
        var sw = argv.Length > 7 ? uint.Parse(argv[7], CultureInfo.InvariantCulture) : 0;
        var sh = argv.Length > 8 ? uint.Parse(argv[8], CultureInfo.InvariantCulture) : 0;

        var transform = new Windows.Graphics.Imaging.BitmapTransform
        {
            Bounds = new Windows.Graphics.Imaging.BitmapBounds { X = x, Y = y, Width = w, Height = h },
            ScaledWidth = sw,
            ScaledHeight = sh,
            InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
        };

        Console.WriteLine($"bounds      = {x},{y} {w}x{h}   scaled = {(sw == 0 ? "(不缩放)" : $"{sw}x{sh}")}");

        using var file = await Windows.Storage.Streams.FileRandomAccessStream
            .OpenAsync(path, Windows.Storage.FileAccessMode.Read);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(file);

        using var bmp = await decoder.GetSoftwareBitmapAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, transform,
            Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.ColorManageToSRgb);

        Console.WriteLine($"decoded     = {bmp.PixelWidth}x{bmp.PixelHeight}");

        using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder
            .CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, ms);
        encoder.SetSoftwareBitmap(bmp);
        await encoder.FlushAsync();

        var size = (int)ms.Size;
        using var reader = new Windows.Storage.Streams.DataReader(ms.GetInputStreamAt(0));
        await reader.LoadAsync((uint)size);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        await File.WriteAllBytesAsync(outPath, bytes);

        Console.WriteLine($"out         = {outPath}  {bytes.Length / 1024} KB");
        return 0;
    }

    private static async Task<int> Crop(string path, string xs, string ys, string ws, string hs,
                                        string outPath)
    {
        var f = await ImagePayload.ReadFactsAsync(path);
        var (dw, dh) = ImageSizing.Oriented(f.StoredWidth, f.StoredHeight, f.Orientation);

        var x = D(xs);
        var y = D(ys);
        var w = D(ws);
        var h = D(hs);

        var rect = ImageRegion.Crop(dw, dh, x, y, w, h);
        var raw = ImageRegion.ToRaw(rect, f.StoredWidth, f.StoredHeight, f.Orientation);
        Console.WriteLine($"asked       = x={x} y={y} w={w} h={h}  (of displayed {dw}x{dh})");
        Console.WriteLine($"displayed   = {rect.X},{rect.Y} {rect.Width}x{rect.Height}");
        Console.WriteLine($"raw         = {raw.X},{raw.Y} {raw.Width}x{raw.Height}");

        var crops = await ImagePayload.CropManyAsync(path, new[] { rect });
        if (crops.Count == 0) { Console.WriteLine("FAILED: 没有产出"); return 1; }

        await File.WriteAllBytesAsync(outPath, crops[0].Jpeg);
        Console.WriteLine($"out         = {outPath}  {crops[0].Width}x{crops[0].Height} " +
                          $"{crops[0].Jpeg.Length / 1024} KB");
        Console.WriteLine("expect      = 见 check.py 里的象限期望");
        return 0;
    }

    private static async Task<int> Tiles(string path, string colss, string rowss, string outDir)
    {
        var f = await ImagePayload.ReadFactsAsync(path);
        var (dw, dh) = ImageSizing.Oriented(f.StoredWidth, f.StoredHeight, f.Orientation);

        var cols = int.Parse(colss, CultureInfo.InvariantCulture);
        var rows = int.Parse(rowss, CultureInfo.InvariantCulture);
        var problem = ImageRegion.TileProblem(cols, rows);
        if (problem.Length > 0) { Console.WriteLine("参数被拒: " + problem); return 2; }

        var rects = ImageRegion.Tiles(dw, dh, cols, rows);
        Console.WriteLine($"displayed   = {dw}x{dh} -> {cols}x{rows} = {rects.Count} 块");

        var crops = await ImagePayload.CropManyAsync(path, rects);
        Directory.CreateDirectory(outDir);
        for (var i = 0; i < crops.Count; i++)
        {
            var outPath = Path.Combine(outDir, $"tile-{i:00}.jpg");
            await File.WriteAllBytesAsync(outPath, crops[i].Jpeg);
            Console.WriteLine($"  [{i}] {rects[i].X},{rects[i].Y} {rects[i].Width}x{rects[i].Height}" +
                              $" -> {crops[i].Width}x{crops[i].Height} {crops[i].Jpeg.Length / 1024} KB");
        }
        return 0;
    }

    /// <summary>整图那条既有路径（RespectExifOrientation + 缩放），作对照用。</summary>
    private static async Task<int> Payload(string path, string outPath)
    {
        var img = await ImagePayload.LoadAsync(path);
        if (img is null) { Console.WriteLine("FAILED: 读不出来"); return 1; }
        await File.WriteAllBytesAsync(outPath, img.Jpeg);
        Console.WriteLine($"out         = {outPath}  {img.Width}x{img.Height}  {img.Jpeg.Length / 1024} KB");
        return 0;
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
