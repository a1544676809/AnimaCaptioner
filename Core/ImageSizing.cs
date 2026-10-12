namespace AnimaCaptioner.Core;

/// <summary>
/// 发图给模型之前的尺寸计算。**纯逻辑**，所以能进 net10 回归（tests/Agent.cs）——
/// 它看着简单，但比例算错会让模型看到的图被拉扁，而那种错在界面上完全看不出来。
///
/// 长边取 1024 的依据是实测出来的 token 代价（对 deepseek-v4.1-flash 量的）：
/// 图片的 token 数约等于像素数 / 1600，且每张图有一个约 200 token 的底。
/// 也就是说 1024×1024 约 850 token，再往上放大基本是线性涨；而训练集原图
/// 3258×3978 直接发，光图片就是 8000 token 量级，还会被很多接口按体积拒收。
/// </summary>
public static class ImageSizing
{
    /// <summary>长边上限。</summary>
    public const int MaxEdge = 1024;

    /// <summary>
    /// 按长边上限等比缩放，宽高都不小于 1。已经小于上限的**原样返回**——
    /// 不再放大：放大只会让模型看到插值出来的假细节。
    /// </summary>
    public static (int Width, int Height) Fit(int width, int height, int maxEdge = MaxEdge)
    {
        if (width <= 0 || height <= 0) return (0, 0);

        var longest = Math.Max(width, height);
        if (longest <= maxEdge) return (width, height);

        var scale = maxEdge / (double)longest;
        return (Math.Max(1, (int)Math.Round(width * scale)),
                Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// EXIF 方向里 5–8 表示"旋转 90/270 度"，此时解码器给出的宽高是**转置**的：
    /// 竖图会自称 4000×3000。按它算缩放会把长边算错，所以要换一下。
    /// </summary>
    public static (int Width, int Height) Oriented(int width, int height, ushort orientation)
    {
        var swap = orientation is >= 5 and <= 8;
        return swap ? (height, width) : (width, height);
    }
}
