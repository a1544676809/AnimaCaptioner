namespace AnimaCaptioner.Core;

/// <summary>
/// 字节数 → 人能读的大小。单独一个文件而不是留在 <see cref="LlmClient"/> 里，
/// 是因为用它的人不止 LlmClient：图像工具那段说明（<see cref="ImageTools.Info"/>）
/// 也要说"这个文件 4.2 MB"，而那一层**不该为了一个三行的格式化函数**
/// 去依赖整个接口客户端（tools/imgprobe 就编译不到 LlmClient）。
/// </summary>
public static class ByteSize
{
    /// <summary>把字节数写成人类可读的大小。</summary>
    public static string Format(long bytes) =>
        bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("F2") + " GB"
        : bytes >= 1L << 20 ? (bytes / (double)(1L << 20)).ToString("F1") + " MB"
        : bytes + " B";
}
