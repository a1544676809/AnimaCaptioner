namespace AnimaCaptioner.Core;

/// <summary>批量追加标签的候选项：一张图 + 它**盘上那份**标签。</summary>
/// <param name="Index">数据集下标。</param>
/// <param name="Name">显示名。</param>
/// <param name="Labeled">盘上有没有 .txt。</param>
/// <param name="Tags">盘上那份 caption 的标签（未标注时为空）。</param>
public sealed record AppendCandidate(int Index, string Name, bool Labeled,
                                     IReadOnlyList<string> Tags);

/// <summary>批量追加标签的选项。</summary>
/// <param name="IncludeUnlabeled">
/// 连还没有 .txt 的图也一起处理（给它建一份只有这几个标签的 caption）。
/// 默认 false——那种 caption 只有三五个标签、没有描述，多半不是用户想要的。
/// </param>
/// <param name="Limit">最多几张，0 = 不限。</param>
public sealed record AppendOptions(bool IncludeUnlabeled = false, int Limit = 0);

/// <summary>
/// **给多张图统一追加/补齐标签**。
///
/// 补的是一个真实的缺口（助手自己指出来过）：`label_images` 是"重新读图、重新描述、
/// 覆盖已有 .txt"，只能选"未标注"或"全部重做"，**没法只往里塞几个标签**；
/// 而逐张修改一次只动编辑器里那一张。于是"给所有缺 `you chan` 的图补上"这件事
/// 原本只能一张一张来。
///
/// 与批量打标的根本区别：**这一路不叫模型、不读图**。它就是文本操作——把几个标签
/// 并进已有的 caption，散文尾巴一字不动。所以它快（每张几毫秒）、便宜（零请求）、
/// 结果完全可预测（同一份输入永远得到同一份输出）。
///
/// 全是纯函数，所以"会动哪几张、每张加了什么"能在回归里直接断言——而这正是最该
/// 钉住的部分：它一次会改很多份训练集里的 .txt。
/// </summary>
public static class BatchTagAdd
{
    /// <summary>
    /// 按选项挑出**需要补**的图：已有 caption、且至少缺一个要加的标签。
    ///
    /// "已有 caption" 是默认的边界：往一张空图里只塞三个标签会得到一份没有描述的
    /// 残缺 caption，而用户说"补齐"时想的是给**已有**的 caption 补漏。
    /// 真要连未标注的也一起处理，得显式打开 <see cref="AppendOptions.IncludeUnlabeled"/>。
    ///
    /// 已经全都有了的图**不进列表**——它们会被算成"无需改动"，而不是"处理过"。
    /// 这一点直接影响用户看到的数字：把没动的也算进去，他会以为改了 53 张。
    /// </summary>
    public static List<AppendCandidate> Choose(IReadOnlyList<AppendCandidate> all,
                                               IReadOnlyList<string> addTags,
                                               AppendOptions options)
    {
        var picked = new List<AppendCandidate>();
        if (all is null || all.Count == 0 || addTags is null || addTags.Count == 0) return picked;

        foreach (var c in all)
        {
            if (!c.Labeled && !options.IncludeUnlabeled) continue;
            if (Missing(c.Tags, addTags).Count == 0) continue;

            picked.Add(c);
            if (options.Limit > 0 && picked.Count >= options.Limit) break;
        }
        return picked;
    }

    /// <summary>这些标签里，<paramref name="have"/> 缺了哪些（保持给定顺序，忽略大小写）。</summary>
    public static List<string> Missing(IReadOnlyList<string> have, IReadOnlyList<string> addTags)
    {
        var set = new HashSet<string>(have ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var t in addTags)
        {
            var tag = (t ?? "").Trim();
            if (tag.Length == 0) continue;
            if (set.Add(tag)) missing.Add(tag);   // Add 返回 true = 之前没有
        }
        return missing;
    }

    /// <summary>
    /// 把 <paramref name="addTags"/> 并进一份 caption 文本。
    ///
    /// 三件事必须做对：
    /// 1. **散文尾巴一字不动**（这是最容易出错的地方：重建时忘了带上，等于删掉描述）；
    /// 2. **已有的标签不重复加**（大小写不敏感——`Safe` 与 `safe` 是同一个）；
    /// 3. 加完**规整大类顺序**，与"在编辑器里加一个标签"的结果一致
    ///    （单张那条路就是自动落到它所属的段里）。
    /// </summary>
    public static (string Text, int Added, List<string> Skipped) Merge(
        string? existingText, IReadOnlyList<string> addTags, VocabDb? vocab)
    {
        var parts = PromptText.Parse(existingText, vocab);
        var tags = new List<string>(parts.Tags);
        var missing = Missing(tags, addTags);

        var skipped = (addTags ?? Array.Empty<string>())
            .Select(t => (t ?? "").Trim())
            .Where(t => t.Length > 0 && !missing.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missing.Count == 0)
            return (existingText ?? "", 0, skipped);

        tags.AddRange(missing);
        var ordered = TagSections.Canonicalize(tags, vocab);
        return (PromptText.Rebuild(ordered, parts.ProseTail), missing.Count, skipped);
    }

    /// <summary>一句话说清这次会动几张（给确认对话框与模型看）。</summary>
    public static string Describe(IReadOnlyList<AppendCandidate> picked,
                                  IReadOnlyList<string> addTags, int total)
    {
        var list = string.Join(", ", addTags ?? Array.Empty<string>());
        if (picked.Count == 0)
            return $"没有需要补的图：数据集共 {total} 张，都已经有这些标签（{list}）。";
        return $"将给 {picked.Count} 张（数据集共 {total} 张）补上：{list}。" +
               "每张只加它缺的那些，已有的不重复、英文描述不动。";
    }
}
