namespace AnimaCaptioner.Core;

/// <summary>
/// 一张图的**文件事实**。字段全部与"怎么读文件"无关，所以这一份能在
/// net10 回归里直接构造与断言；真正去读 WinRT 的是 UI 侧的
/// <see cref="ImagePayload"/>（它把读出来的东西填进这个记录）。
/// </summary>
/// <param name="Name">显示名（不带扩展名），也是切图用的键。</param>
/// <param name="Format">MIME 类型，例如 image/jpeg；取不到就是扩展名。</param>
/// <param name="FileBytes">文件字节数。</param>
/// <param name="StoredWidth">解码器报的宽（**未**应用 EXIF 方向）。</param>
/// <param name="StoredHeight">解码器报的高（**未**应用 EXIF 方向）。</param>
/// <param name="Orientation">EXIF 方向，1 = 正常。</param>
/// <param name="Attached">这次对话里是否真的把图发给了模型。</param>
/// <param name="Index">在数据集里的序号（从 1 起显示；0 表示没有数据集）。</param>
/// <param name="Total">数据集总数。</param>
/// <param name="CaptionTags">已有标签数；0 = 还没有。</param>
public sealed record ImageFacts(
    string Name,
    string Format,
    long FileBytes,
    int StoredWidth,
    int StoredHeight,
    ushort Orientation,
    bool Attached,
    int Index,
    int Total,
    int CaptionTags)
{
    /// <summary>转正之后的长宽——模型看到的画面就是这个方向。</summary>
    public (int Width, int Height) Displayed =>
        ImageSizing.Oriented(StoredWidth, StoredHeight, Orientation);

    /// <summary>整图发给模型时的尺寸（长边 1024）。</summary>
    public (int Width, int Height) ModelView => ImageSizing.Fit(Displayed.Width, Displayed.Height);
}

/// <summary>
/// 两个图像工具回给模型的**文字**。放在 Core 里是为了可测——这些句子是模型唯一的
/// 判据来源（尤其"你看到的整图被缩到多小""这块比之前大多少倍"），凭感觉写很容易
/// 写出一个数字上讲不通的说明，而那会直接导致它反复裁同一块。
/// </summary>
public static class ImageTools
{
    /// <summary>
    /// `image_info` 的结果。
    ///
    /// 刻意把"你手里那张被缩到多小"讲清楚：模型看不见原始分辨率，而它要判断
    /// "这个细节看不清是不是因为我没放大"，只能靠这句话。
    /// </summary>
    public static string Info(ImageFacts f)
    {
        var (dw, dh) = f.Displayed;
        var (mw, mh) = f.ModelView;

        var sb = new System.Text.StringBuilder();
        sb.Append("Current image: ").Append(f.Name);
        if (f.Total > 0) sb.Append(" (").Append(f.Index).Append(" of ").Append(f.Total).Append(')');
        sb.Append(".\nFile: ").Append(f.Format).Append(", ").Append(ByteSize.Format(f.FileBytes));

        sb.Append(", stored ").Append(f.StoredWidth).Append('x').Append(f.StoredHeight);
        if (f.Orientation != 1)
            sb.Append(", EXIF orientation ").Append(f.Orientation)
              .Append(" -> displayed ").Append(dw).Append('x').Append(dh);
        else
            sb.Append(" (no EXIF rotation)");

        sb.Append(".\nCaption: ");
        sb.Append(f.CaptionTags > 0
            ? $"it already has {f.CaptionTags} tag(s)."
            : "it has no tags yet.");

        if (!f.Attached)
        {
            sb.Append("\nNO copy of this image is attached to this conversation: you cannot " +
                      "see it, and you cannot ask for a crop of it. Tell the user to turn on " +
                      "\"attach the current image\" if they want you to look at it.");
            return sb.ToString();
        }

        var scale = dw > 0 ? mw / (double)dw : 1;
        sb.Append("\nA copy is attached, scaled to ").Append(mw).Append('x').Append(mh);
        if (scale < 0.999)
            sb.Append(" (").Append(scale.ToString("0.00")).Append("x of ").Append(dw).Append('x')
              .Append(dh).Append("): anything smaller than about ")
              .Append(Math.Max(2, (int)Math.Round(1 / scale)))
              .Append(" original pixels is not resolvable in what you see. Use view_region to " +
                      "have a part of it enlarged before you guess at fine detail.");
        else
            sb.Append(": that is the full resolution, so no detail was lost on the way.");
        return sb.ToString();
    }

    /// <summary>`view_region` 的 crop 模式成功后回的那句话（图在紧随其后的一条 user 消息里）。</summary>
    public static string Crop(ImageFacts f, PixelRect src, int outWidth, int outHeight)
    {
        var (dw, dh) = f.Displayed;
        var factor = DetailFactor(f, src, outWidth, outHeight);

        return $"Enlarged region attached below as a NEW user message: x={Norm(src.X, dw)} " +
               $"y={Norm(src.Y, dh)} w={Norm(src.Width, dw)} h={Norm(src.Height, dh)} of " +
               $"\"{f.Name}\" (normalized 0-1), which is {src.Width}x{src.Height} pixels of the " +
               $"displayed {dw}x{dh}.\nWhat you now see is that region at {outWidth}x{outHeight}" +
               Detail(factor) + "\nLook at the new user message before you answer.";
    }

    /// <summary>`view_region` 的 tiles 模式成功后回的那句话。</summary>
    public static string Tiles(ImageFacts f, int cols, int rows, IReadOnlyList<PixelRect> tiles)
    {
        var widest = tiles.Count == 0 ? 0 : tiles.Max(t => t.Width);
        var tallest = tiles.Count == 0 ? 0 : tiles.Max(t => t.Height);
        var (ow, oh) = ImageSizing.Fit(widest, tallest);
        var factor = tiles.Count == 0 ? 1 : DetailFactor(f, tiles[0], ow, oh);

        return $"Split \"{f.Name}\" into {cols}x{rows} = {tiles.Count} tiles and attached them " +
               $"below as a NEW user message (reading order: left to right, then top to bottom). " +
               $"Each tile is at most {ow}x{oh}." +
               $"\nEach tile costs tokens of its own — {tiles.Count} images is about " +
               $"{tiles.Count}x the cost of one. Do not keep re-tiling; look at these first." +
               Detail(factor) + "\nLook at the new user message before you answer.";
    }

    /// <summary>没有图可谈（写标签模式）。</summary>
    public const string NoImage =
        "There is no image open: the editor is in prompt-writing mode, which has no dataset. " +
        "Work from what the user writes.";

    /// <summary>图在磁盘上，但这次没发给模型（用户关掉了「带当前图片」）。</summary>
    public const string NotAttached =
        "The current image is NOT attached to this conversation, so you cannot see it and you " +
        "cannot ask for a crop of it. Tell the user to turn on \"attach the current image\".";

    /// <summary>工具参数不对时回的话。参数名与取值范围都要写全，它下一轮才改得对。</summary>
    public static string ArgsProblem(string why) =>
        "view_region did not run: " + why +
        " Required for mode \"crop\": x, y, w, h — all normalized to 0-1 of the displayed image. " +
        "Required for mode \"tiles\": cols, rows — integers, cols * rows must not exceed " +
        ImageRegion.MaxTiles + ".";

    /// <summary>读图/裁剪失败时回的话（原始异常信息一并带上，便于用户查日志）。</summary>
    public static string Failed(string why) =>
        "Could not produce that crop: " + why + ". Tell the user plainly; do not guess at what " +
        "the region contains.";

    /// <summary>`image_info` 读不出文件时回的话。</summary>
    public static string InfoFailed(string why) =>
        "Could not read the image file: " + why + ". Do not guess its size, format or content; " +
        "tell the user instead.";

    /// <summary>
    /// 这块区域比"整图视图"里同一块大多少倍（**线性**倍率，按面积开方算）。
    ///
    /// 用它而不是"1024 / 原图长边"，是因为模型能比较的只有它手里那张整图：
    /// 说"大了 2.3 倍"它才知道有没有必要再裁；说"1024 像素"它无从判断。
    /// </summary>
    public static double DetailFactor(ImageFacts f, PixelRect src, int outWidth, int outHeight)
    {
        var (dw, dh) = f.Displayed;
        var (mw, mh) = f.ModelView;
        if (dw <= 0 || dh <= 0 || mw <= 0 || mh <= 0) return 1;

        var fraction = (double)src.Width * src.Height / ((double)dw * dh);
        var before = Math.Max(1.0, mw * (double)mh * fraction);
        var factor = Math.Sqrt(outWidth * (double)outHeight / before);
        return factor < 0.05 ? 0.05 : factor;
    }

    /// <summary>倍率那半句。接近 1 时**如实说"没变大"**——原图就那么大，再裁也没用。</summary>
    private static string Detail(double factor) =>
        factor >= 1.15
            ? $", about {factor:0.0}x more detail than the same area had in the whole-image view."
            : ", which is no larger than in the whole-image view: this image has no more detail " +
              "to give, so do not ask for the same region again.";

    /// <summary>像素位置写成归一化数字（保留三位，够模型复用）。</summary>
    private static string Norm(int px, int total) =>
        total <= 0 ? "0" : (px / (double)total).ToString("0.###");
}
