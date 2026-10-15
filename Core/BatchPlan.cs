namespace AnimaCaptioner.Core;

/// <summary>批量打标的候选项：一张图 + 它当前的标注状态。</summary>
/// <param name="Index">在数据集里的下标（从 0 起）。</param>
/// <param name="Name">显示名，用于日志与结果列表。</param>
/// <param name="Labeled">盘上是否已有 .txt。</param>
/// <param name="TagCount">已有标签数（0 也可能有 .txt，比如只写了散文）。</param>
public sealed record BatchCandidate(int Index, string Name, bool Labeled, int TagCount);

/// <summary>批量打标的范围选项。</summary>
/// <param name="OnlyUnlabeled">只处理"还没有 .txt"的图。默认 true。</param>
/// <param name="SkipEmptyCaptions">
/// 连"有 .txt 但一个标签都没有"的也算未标注（那种多半是上次跑失败的残留）。
/// </param>
/// <param name="Limit">最多处理几张，0 = 不限。</param>
public sealed record BatchOptions(bool OnlyUnlabeled = true, bool SkipEmptyCaptions = true,
                                  int Limit = 0);

/// <summary>
/// 批量打标的**选择与记账**。全是纯函数，所以"会打哪几张、结果怎么算"
/// 能在回归里直接断言——而这正是最该钉住的部分：
/// 选错了范围会**改写用户的训练集**，算错了账会让用户以为成功了几张。
///
/// 循环本身（切图、读图、调模型、落盘）在 MainWindow 里，因为它要动界面状态；
/// 这里只回答"该处理哪些、结果怎么数"。
/// </summary>
public static class BatchPlan
{
    /// <summary>
    /// 按选项挑出要处理的图（保持数据集原顺序）。
    ///
    /// 默认只挑**没有 .txt** 的：批量打标最常见的用法是"把新收的图过一遍"，
    /// 而默认重做全部等于把用户已经精修过的 caption 覆盖掉——那不可逆。
    /// 想重做全部得显式取消勾选。
    /// </summary>
    public static List<BatchCandidate> Choose(IReadOnlyList<BatchCandidate> all,
                                              BatchOptions options)
    {
        var picked = new List<BatchCandidate>();
        if (all is null || all.Count == 0) return picked;

        foreach (var c in all)
        {
            if (options.OnlyUnlabeled)
            {
                // "空 caption" 算不算已标注：有 .txt 但 0 个标签，多半是上次
                // 失败留下的空壳（SaveCurrent 有条守卫不许建空文件，但外部工具
                // 或旧版本可能留下）。SkipEmptyCaptions 为真时把它当成待办。
                var done = c.Labeled && !(options.SkipEmptyCaptions && c.TagCount == 0);
                if (done) continue;
            }

            picked.Add(c);
            if (options.Limit > 0 && picked.Count >= options.Limit) break;
        }
        return picked;
    }

    /// <summary>一句话说清这次会动几张、其中几张是重做（给确认对话框用）。</summary>
    public static string Describe(IReadOnlyList<BatchCandidate> picked, int total)
    {
        if (picked.Count == 0) return $"没有需要处理的图片（数据集共 {total} 张）。";

        var relabel = picked.Count(c => c.Labeled);
        var s = $"将处理 {picked.Count} 张（数据集共 {total} 张）";
        if (relabel > 0) s += $"，其中 {relabel} 张已有标注、会被**覆盖**";
        return s + "。";
    }
}

/// <summary>
/// 一次批量打标的记账。**每一张都要有结论**，包括"模型没给出提案"这种
/// ——把它并进"失败"会让用户以为出错了，并进"成功"又会让用户以为写上了。
/// </summary>
public sealed class BatchReport
{
    public int Attempted { get; private set; }
    public int Written { get; private set; }
    public int Unchanged { get; private set; }
    public int NoProposal { get; private set; }
    public int Failed { get; private set; }

    /// <summary>出错或需要用户知道的逐条说明（已截断，只留一句）。</summary>
    public List<string> Notes { get; } = new();

    /// <summary>
    /// 记一张的结果。<paramref name="written"/> 表示文件真的被写了，
    /// <paramref name="unchanged"/> 表示跑成功但盘上内容没变化（本来就一样）。
    /// </summary>
    public void Add(string name, BatchStatus status, string note = "")
    {
        Attempted++;
        switch (status)
        {
            case BatchStatus.Written: Written++; break;
            case BatchStatus.Unchanged: Unchanged++; break;
            case BatchStatus.NoProposal:
                NoProposal++;
                Notes.Add($"{name}：模型没有提出任何改动");
                break;
            case BatchStatus.Failed:
                Failed++;
                if (note.Length > 0) Notes.Add($"{name}：{note}");
                break;
        }
    }

    /// <summary>结果行（显示在状态栏或收尾对话框里）。</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (Written > 0) parts.Add($"写入 {Written}");
        if (Unchanged > 0) parts.Add($"无需改动 {Unchanged}");
        if (NoProposal > 0) parts.Add($"没提案 {NoProposal}");
        if (Failed > 0) parts.Add($"失败 {Failed}");
        return $"处理 {Attempted} 张" + (parts.Count > 0 ? "：" + string.Join(" · ", parts) : "");
    }
}

/// <summary>一张图的处理结论。</summary>
public enum BatchStatus
{
    /// <summary>跑成功，文件被写了。</summary>
    Written,

    /// <summary>跑成功，但内容与盘上一致（不需要写）。</summary>
    Unchanged,

    /// <summary>模型跑完了但没提出改动（不是错误）。</summary>
    NoProposal,

    /// <summary>出错（请求失败、读图失败等）。</summary>
    Failed,
}
