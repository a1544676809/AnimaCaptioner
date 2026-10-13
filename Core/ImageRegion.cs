namespace AnimaCaptioner.Core;

/// <summary>
/// 一个像素矩形（左上角 + 宽高）。刻意不用 <c>Windows.Foundation.Rect</c>：
/// 那一套在 net10 的回归工程里编译不到，而这些数学**必须**能被测——
/// 坐标算错的表现是"裁出来的东西不是模型要的那块"，肉眼极难发现。
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// 图像区域工具用的几何：归一化坐标 → 像素矩形、网格切分，以及**EXIF 方向的反查**。
///
/// 全是纯函数，所以 tests/Agent.cs 能直接钉住它们（尤其旋转那几个方向：
/// 错一次就是"模型的坐标和它看到的画面对不上"，而界面上完全看不出来）。
///
/// 为什么要"反查"而不是解码时让解码器顺手转正：<c>BitmapTransform.Bounds</c> 与
/// <c>ExifOrientationMode.RespectExifOrientation</c> 一起用时，Bounds 到底是在
/// 旋转前还是旋转后的坐标系里解释，文档说得含糊、不同解码器还不一致。这里改成
/// **明确的两步**：Bounds 一律在不带方向的原始像素坐标系里裁（<see cref="ToRaw"/>），
/// 拿到像素后再由 <see cref="ImagePixels.Orient"/> 显式转正。
/// 两步各自的语义都是确定的，不依赖任何"先裁后转还是先转后裁"的约定。
/// </summary>
public static class ImageRegion
{
    /// <summary>
    /// 一次 view_region 最多回几块。**这个上限是承重的**：每块约 150 KB 且
    /// **各自算 token**（1024 长边约 850 token），4×4=16 块足以把上下文顶爆。
    /// </summary>
    public const int MaxTiles = 9;

    /// <summary>
    /// 归一化矩形 → 像素矩形。坐标一律取 0–1（模型看不到原始分辨率，让它填像素
    /// 等于让它猜），超界的值夹回来而不是报错——模型给 1.2 时它想表达的就是"到边上"。
    ///
    /// 用 floor/ceil 取整：左边界向下取、右边界向上取，宁可多含一个像素也不漏掉
    /// 模型要看的那条边。返回值保证在图像内且宽高至少 1。
    /// </summary>
    public static PixelRect Crop(int width, int height, double x, double y, double w, double h)
    {
        if (width <= 0 || height <= 0) return default;

        var left = Math.Clamp(x, 0, 1);
        var top = Math.Clamp(y, 0, 1);
        var right = Math.Clamp(x + w, 0, 1);
        var bottom = Math.Clamp(y + h, 0, 1);

        // 宽高给了负数就当方向反了（模型偶尔会写 x=0.6,w=-0.2 表达"往左一块"）
        if (right < left) (left, right) = (right, left);
        if (bottom < top) (top, bottom) = (bottom, top);

        var px = Math.Clamp(FloorPx(left, width), 0, width - 1);
        var py = Math.Clamp(FloorPx(top, height), 0, height - 1);
        var pw = Math.Clamp(CeilPx(right, width) - px, 1, width - px);
        var ph = Math.Clamp(CeilPx(bottom, height) - py, 1, height - py);
        return new PixelRect(px, py, pw, ph);
    }

    /// <summary>
    /// 归一化值 → 像素下标。那个 1e-6 是**消浮点噪声**用的，不是留余量：
    /// `0.6 - 0.2` 在 double 里是 0.39999999999999997，直接 floor 会掉到 399 去，
    /// 于是同一块区域"给 x=0.4"和"给 x=0.6,w=-0.2"会差一个像素。
    /// （负 w/h 是模型表达"往左一块"的方式，见 Crop。）
    /// </summary>
    private static int FloorPx(double v, int size) => (int)Math.Floor(v * size + 1e-6);

    /// <summary>同上，向上取整那一侧。见 <see cref="FloorPx"/>。</summary>
    private static int CeilPx(double v, int size) => (int)Math.Ceiling(v * size - 1e-6);

    /// <summary>
    /// <c>tiles</c> 的参数校验。返回空串 = 没问题，否则是**给模型看的一句话**——
    /// 说清上限是多少、它要的是多少，它下一轮就会改小。
    /// </summary>
    public static string TileProblem(int cols, int rows)
    {
        if (cols < 1 || rows < 1)
            return $"cols and rows must both be at least 1 (got cols={cols}, rows={rows}).";
        if (cols * rows > MaxTiles)
            return $"cols * rows must not exceed {MaxTiles} (asked {cols} x {rows} = " +
                   $"{cols * rows} tiles). Every tile costs tokens on its own, so ask for " +
                   "fewer, larger tiles — or call this tool again later for another part.";
        return "";
    }

    /// <summary>
    /// 把画面均分成 cols × rows 块（阅读顺序：先左到右，再上到下）。
    ///
    /// 边界用整数乘除算，所以**不重叠、不留缝**，也不依赖宽高能否整除：
    /// 第 c 块横跨 <c>[c*w/cols, (c+1)*w/cols)</c>。宽高比块数还小时把块数压到像素数，
    /// 免得出现 0 宽的块（那会让编码器直接报错）。
    /// </summary>
    public static IReadOnlyList<PixelRect> Tiles(int width, int height, int cols, int rows)
    {
        if (width <= 0 || height <= 0) return Array.Empty<PixelRect>();

        cols = Math.Clamp(cols, 1, width);
        rows = Math.Clamp(rows, 1, height);

        var list = new List<PixelRect>(cols * rows);
        for (var r = 0; r < rows; r++)
        {
            var y0 = (int)((long)r * height / rows);
            var y1 = (int)((long)(r + 1) * height / rows);
            if (y1 <= y0) y1 = Math.Min(height, y0 + 1);

            for (var c = 0; c < cols; c++)
            {
                var x0 = (int)((long)c * width / cols);
                var x1 = (int)((long)(c + 1) * width / cols);
                if (x1 <= x0) x1 = Math.Min(width, x0 + 1);
                list.Add(new PixelRect(x0, y0, x1 - x0, y1 - y0));
            }
        }
        return list;
    }

    /// <summary>
    /// 转正后的坐标系里的矩形 → **解码器原始像素**坐标系里的矩形。
    ///
    /// 这是整个裁剪里唯一容易做错的一步，所以单独一个函数、单独一组断言。
    /// EXIF 方向的含义是"从原始像素到正确显示要做什么"，这里要的是它的**逆**：
    /// 已知模型看到的那块区域在转正后的位置，反推它在原始像素里在哪。
    ///
    /// 用**边缘坐标**推（而不是像素下标）：矩形 <c>[x, x+w)</c> 映射过去仍是
    /// <c>[x', x'+w')</c> 这种形状，于是那个恼人的 <c>-1</c> 不会出现在任何一条里。
    /// 方向 5–8 会把宽高换过来（横竖对调），所以那几条里要 swap。
    /// </summary>
    public static PixelRect ToRaw(PixelRect r, int rawWidth, int rawHeight, ushort orientation)
    {
        if (rawWidth <= 0 || rawHeight <= 0) return default;

        int x, y, w = r.Width, h = r.Height;
        switch (orientation)
        {
            case 2:   // 左右镜像
                x = rawWidth - r.Right; y = r.Y; break;
            case 3:   // 旋转 180°
                x = rawWidth - r.Right; y = rawHeight - r.Bottom; break;
            case 4:   // 上下镜像
                x = r.X; y = rawHeight - r.Bottom; break;
            case 5:   // 主对角线转置
                x = r.Y; y = r.X; (w, h) = (h, w); break;
            case 6:   // 顺时针 90°
                x = r.Y; y = rawHeight - r.Right; (w, h) = (h, w); break;
            case 7:   // 副对角线转置
                x = rawWidth - r.Bottom; y = rawHeight - r.Right; (w, h) = (h, w); break;
            case 8:   // 逆时针 90°
                x = rawWidth - r.Bottom; y = r.X; (w, h) = (h, w); break;
            default:  // 1（正常），以及任何不认识的取值
                x = r.X; y = r.Y; break;
        }

        x = Math.Clamp(x, 0, rawWidth - 1);
        y = Math.Clamp(y, 0, rawHeight - 1);
        w = Math.Clamp(w, 1, rawWidth - x);
        h = Math.Clamp(h, 1, rawHeight - y);
        return new PixelRect(x, y, w, h);
    }
}
