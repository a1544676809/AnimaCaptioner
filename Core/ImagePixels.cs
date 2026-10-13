namespace AnimaCaptioner.Core;

/// <summary>
/// 像素级的方向变换（BGRA8，紧排或带行距都吃）。
///
/// 为什么要有它：解码器那边有一套"顺手帮我转正"的开关
/// （<c>ExifOrientationMode.RespectExifOrientation</c>），但它和
/// <c>BitmapTransform.Bounds</c>（裁剪）一起用时，两者谁先谁后没有一句靠谱的话说清。
/// 与其赌某个解码器的实现，不如**只让解码器做它最确定的那件事**（按原始像素裁一块、
/// 缩到目标尺寸），旋转交给这里——一张 ≤1024×1024 的 BGRA 图最多 4 MB，
/// 在托管代码里转一次是毫秒级的事，而它的行为可以逐像素断言。
///
/// 8 个方向的含义照 EXIF 2.3：它们是"从原始像素到正确显示要做什么"，
/// 所以这里的映射是**同一个方向的逆**（输出 → 输入），和 <see cref="ImageRegion.ToRaw"/>
/// 配成一对：那边把区域反查回原始坐标系，这边把裁出来的原始像素转正。
/// </summary>
public static class ImagePixels
{
    /// <summary>BGRA8 每个像素的字节数。</summary>
    public const int Bpp = 4;

    /// <summary>
    /// 把 <paramref name="source"/> 里的一块 BGRA8 像素按 EXIF 方向转正。
    ///
    /// <paramref name="stride"/> 是**源**每行占的字节数（0 = 紧排，即 width × 4）。
    /// 必须有这个参数而不是假设紧排：解码器给的行距可以大于 width×4，
    /// 按 width×4 去索引会逐行错位，画面呈现为斜的彩色条纹——那种错一眼看不出成因。
    ///
    /// 返回的缓冲一律紧排。方向 5–8 会把宽高换过来。
    /// 数据长度不足时**抛异常**而不是返回一张错图：调用方（ImagePayload）会抓住它
    /// 并如实报错，比悄悄发一张方向不对的图强。
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) Orient(
        byte[] source, int width, int height, ushort orientation, int stride = 0)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("图片尺寸非法");
        if (stride <= 0) stride = width * Bpp;

        var need = (long)stride * height;
        if (source.LongLength < need)
            throw new ArgumentException($"像素缓冲不足：需要 {need} 字节，只有 {source.LongLength} 字节");

        var swap = orientation is >= 5 and <= 8;
        var outW = swap ? height : width;
        var outH = swap ? width : height;

        // 方向 1（正常）且源本来就是紧排：原样返回，不做无谓的拷贝
        if (!swap && (orientation is 1 or 0) && stride == width * Bpp)
            return (source, width, height);

        var dst = new byte[outW * outH * Bpp];
        for (var yo = 0; yo < outH; yo++)
        {
            var rowOut = yo * outW * Bpp;
            for (var xo = 0; xo < outW; xo++)
            {
                int xr, yr;
                switch (orientation)
                {
                    case 2: xr = width - 1 - xo; yr = yo; break;
                    case 3: xr = width - 1 - xo; yr = height - 1 - yo; break;
                    case 4: xr = xo; yr = height - 1 - yo; break;
                    case 5: xr = yo; yr = xo; break;
                    case 6: xr = yo; yr = height - 1 - xo; break;
                    case 7: xr = width - 1 - yo; yr = height - 1 - xo; break;
                    case 8: xr = width - 1 - yo; yr = xo; break;
                    default: xr = xo; yr = yo; break;
                }

                var si = yr * stride + xr * Bpp;
                var di = rowOut + xo * Bpp;
                dst[di] = source[si];
                dst[di + 1] = source[si + 1];
                dst[di + 2] = source[si + 2];
                dst[di + 3] = source[si + 3];
            }
        }

        return (dst, outW, outH);
    }

    /// <summary>
    /// 造一张 <paramref name="width"/>×<paramref name="height"/> 的 BGRA8 测试图：
    /// 第 n 个像素的四个字节取一个只由 n 决定的模式，于是"某个像素搬对了没有"
    /// 一眼可查（tests/Agent.cs 的方向断言用它，不依赖任何图片文件）。
    /// </summary>
    public static byte[] TestImage(int width, int height, int stride = 0)
    {
        if (stride <= 0) stride = width * Bpp;
        var buf = new byte[stride * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * Bpp;
                var n = y * width + x;
                buf[i] = (byte)(n & 0xFF);
                buf[i + 1] = (byte)((n >> 8) & 0xFF);
                buf[i + 2] = (byte)(0xA0 + (x & 0x0F));
                buf[i + 3] = 0xFF;
            }
        return buf;
    }

    /// <summary>读一个像素的 (B,G,R,A)——断言里用，省得每次都写四个下标。</summary>
    public static (int B, int G, int R, int A) Pixel(byte[] bgra, int width, int x, int y, int stride = 0)
    {
        if (stride <= 0) stride = width * Bpp;
        var i = y * stride + x * Bpp;
        return (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
    }
}
