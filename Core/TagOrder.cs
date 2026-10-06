namespace AnimaCaptioner.Core;

/// <summary>
/// 对"待保存的标签序列"做结构性编辑：段内上移/下移、按位删除、跨段归位。
///
/// 单独抽出来是因为这些操作有几个容易搞错的地方，而且都值得脱离界面验证：
/// 列表是按大类重排后显示的，所以"显示下标"和"待保存下标"不是一回事；
/// 多选下移必须从后往前挪，否则会互相踩；跨大类的挪动会被 Canonicalize
/// 拨回去，必须提前拦住并如实告诉用户，而不是假装成功了。
///
/// 这个文件不引用任何 WinUI 类型，所以能被 net8 的回归程序直接编译。
/// </summary>
public static class TagOrder
{
    public readonly record struct MoveResult(
        List<string> Tags, List<int> MovedTo, int Moved, int Blocked);

    /// <summary>
    /// 把 <paramref name="pending"/> 里给定下标的标签整体挪 <paramref name="delta"/> 位。
    ///
    /// 只在同一大类内部生效：跨段的挪动会被按大类排序拨回去，所以这里直接
    /// 拦下并计入 <see cref="MoveResult.Blocked"/>，免得界面报"移动了"却看不出变化。
    /// </summary>
    public static MoveResult Move(List<string> pending, IEnumerable<int> indices,
                                  int delta, VocabDb? vocab)
    {
        var tags = new List<string>(pending);
        if (tags.Count == 0 || delta == 0) return new MoveResult(tags, new(), 0, 0);

        var secs = tags.Select(t => TagSections.IndexOf(TagSections.Classify(t, vocab))).ToList();

        var idxs = indices.Where(i => i >= 0 && i < tags.Count).Distinct().ToList();
        idxs.Sort();
        if (delta > 0) idxs.Reverse();   // 下移要从后往前挪，否则会互相踩

        var moved = 0;
        var blocked = 0;
        var landed = new List<int>();

        foreach (var i in idxs)
        {
            var j = i + delta;
            if (j < 0 || j >= tags.Count) { blocked++; landed.Add(i); continue; }
            if (secs[i] != secs[j]) { blocked++; landed.Add(i); continue; }   // 本段边界

            (tags[i], tags[j]) = (tags[j], tags[i]);
            (secs[i], secs[j]) = (secs[j], secs[i]);
            moved++;
            landed.Add(j);
        }

        return new MoveResult(tags, landed, moved, blocked);
    }

    /// <summary>
    /// 按"待保存下标"删除若干标签。
    /// 不从后往前删的话，前面删掉一个会让后面所有下标偏移，从而删错行。
    /// </summary>
    public static List<string> Delete(List<string> pending, IEnumerable<int> indices)
    {
        var tags = new List<string>(pending);
        var idxs = indices.Where(i => i >= 0 && i < tags.Count).Distinct()
                          .OrderByDescending(i => i).ToList();
        foreach (var i in idxs) tags.RemoveAt(i);
        return tags;
    }

    /// <summary>把拖拽结果落到某个位置：先把被拖的抽出去，再插到落点之前。</summary>
    public static List<string> Drop(List<string> pending, IReadOnlyList<string> moving, string? beforeTag)
    {
        var set = new HashSet<string>(moving, StringComparer.OrdinalIgnoreCase);
        var rest = pending.Where(t => !set.Contains(t)).ToList();

        var at = rest.Count;
        if (beforeTag is not null)
        {
            var found = rest.FindIndex(t => string.Equals(t, beforeTag, StringComparison.OrdinalIgnoreCase));
            if (found >= 0) at = found;
        }
        rest.InsertRange(at, moving);
        return rest;
    }
}
