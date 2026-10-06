namespace AnimaCaptioner.Core;

/// <summary>
/// 一个帮助模块（对应 docs 目录里的一个 .md 文件）。
/// 内容与程序解耦：磁盘上改 .md 即可，不必重新编译，也不必重启程序。
/// </summary>
public sealed class HelpModule
{
    /// <summary>稳定标识：文件名去掉 "NN-" 前缀与扩展名。用来记住"上次看的是哪一页"。</summary>
    public string Id { get; init; } = "";

    /// <summary>左侧目录显示的标题。取自正文第一行 "# "。</summary>
    public string Title { get; init; } = "";

    /// <summary>目录里显示的摘要（可选）。取自标题后紧跟的 "> " 引用块。</summary>
    public string Summary { get; init; } = "";

    public string FilePath { get; init; } = "";

    /// <summary>去掉标题与摘要之后的正文（Markdown）。</summary>
    public string Body { get; init; } = "";

    /// <summary>文件所在的目录。正文里的相对图片路径按它解析。</summary>
    public string Dir { get; init; } = "";

    public override string ToString() => Title;
}

/// <summary>
/// 帮助文档的定位与解析。**纯逻辑，不依赖任何 UI 类型**，所以能在 corecheck
/// 里直接跑回归（和 VocabDb / CaptionFile 同样的理由）。
///
/// 文档随程序分发：放在 exe 同级的 docs 子目录里，跟着程序一起走。
/// 运行时直接读那里的 .md，所以改文档点一下「重新载入」即可生效，不必重启程序。
/// </summary>
public static class HelpDocs
{
    /// <summary>文档目录：exe 同级的 docs。</summary>
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "docs");

    private static readonly string[] Exts = { ".md", ".markdown" };

    public static bool HasDocs()
    {
        try
        {
            return Directory.Exists(Dir)
                && Directory.EnumerateFiles(Dir)
                            .Any(f => Exts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>
    /// 读取全部模块，按文件名前缀的数字排序（01-、02- …）。
    /// 解析失败的文件不会让整个帮助页挂掉，而是收集成一条警告显示在正文末尾——
    /// 帮助页本身挂掉是最糟的结果，因为用户正是来看怎么排错的。
    /// </summary>
    public static List<HelpModule> Load(out List<string> warnings)
    {
        warnings = new List<string>();
        var list = new List<HelpModule>();
        if (!Directory.Exists(Dir)) return list;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(Dir)
                             .Where(f => Exts.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                             .ToList();
        }
        catch (Exception ex)
        {
            warnings.Add("读取目录失败：" + ex.Message);
            return list;
        }

        // 排序键：文件名开头的数字，没有数字的排在后面并按名字排。
        files.Sort((a, b) =>
        {
            var ka = SortKey(Path.GetFileName(a));
            var kb = SortKey(Path.GetFileName(b));
            var c = ka.Item1.CompareTo(kb.Item1);
            return c != 0 ? c
                 : string.Compare(ka.Item2, kb.Item2, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var f in files)
        {
            try
            {
                var text = File.ReadAllText(f);
                var (title, summary, body) = SplitHeader(text, Path.GetFileNameWithoutExtension(f));
                list.Add(new HelpModule
                {
                    Id = IdOf(f),
                    Title = title,
                    Summary = summary,
                    FilePath = f,
                    Body = body,
                    Dir = Path.GetDirectoryName(f) ?? Dir
                });
            }
            catch (Exception ex)
            {
                warnings.Add($"{Path.GetFileName(f)}：{ex.Message}");
            }
        }

        if (list.Count == 0 && warnings.Count == 0)
            warnings.Add("目录里没有 .md 文件：" + Dir);

        return list;
    }

    /// <summary>文件名 → 排序键（数字前缀, 去掉前缀的名字）。</summary>
    private static (int, string) SortKey(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var dash = name.IndexOf('-');
        if (dash > 0 && int.TryParse(name[..dash], out var n))
            return (n, name[(dash + 1)..]);
        return (int.MaxValue, name);
    }

    /// <summary>"01-shortcuts.md" → "shortcuts"。</summary>
    public static string IdOf(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        var dash = name.IndexOf('-');
        if (dash > 0 && int.TryParse(name[..dash], out _)) return name[(dash + 1)..];
        return name;
    }

    /// <summary>
    /// 把正文切成 标题 / 摘要 / 剩余正文。
    /// 约定：第一行非空行是 "# 标题"，紧随其后的 "> …" 引用块算摘要。
    /// 两者都从正文里摘掉——否则窗口顶部显示一次标题、正文里又出现一次。
    /// </summary>
    public static (string Title, string Summary, string Body) SplitHeader(string text, string fallbackTitle)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var title = fallbackTitle;
        var i = 0;

        // 找第一个非空行当标题；不是 "# " 开头就退回文件名
        for (; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var l = lines[i].TrimStart();
            if (l.StartsWith("# ", StringComparison.Ordinal))
                title = l[2..].Trim();
            break;
        }
        if (i < lines.Length) i++;   // 跳过标题行本身

        // 摘要：标题之后、第一个非空行若是 ">"，连续引用行都算摘要
        var sum = new List<string>();
        var j = i;
        for (; j < lines.Length; j++)
        {
            if (string.IsNullOrWhiteSpace(lines[j])) continue;
            if (lines[j].TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                sum.Add(lines[j].TrimStart().TrimStart('>').Trim());
                j++;
                for (; j < lines.Length; j++)
                {
                    var s = lines[j].TrimStart();
                    if (s.StartsWith(">", StringComparison.Ordinal))
                    {
                        sum.Add(s.TrimStart('>').Trim());
                        continue;
                    }
                    break;
                }
            }
            break;
        }
        if (sum.Count > 0) i = j;

        var body = string.Join("\n", lines.Skip(i)).TrimStart('\n');
        return (title, StripInlineMarks(string.Join(" ", sum)).Trim(), body);
    }

    /// <summary>
    /// 去掉行内 Markdown 标记。摘要显示在标题下面，是一行**纯文本**、不走
    /// Markdown 渲染，所以 `**加粗**` 会原样露出星号（实测截图里就是这样）。
    /// 只处理最常见的几种：链接语法、行内代码反引号、强调号、删除线。
    /// </summary>
    public static string StripInlineMarks(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;

        // [文字](链接) → 文字
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");
        s = s.Replace("`", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\*{1,3}([^*]+)\*{1,3}", "$1");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"_{1,3}([^_]+)_{1,3}", "$1");
        s = s.Replace("~~", "");
        return s.Trim();
    }

    /// <summary>把正文里引用的相对图片路径解析成绝对路径；解析不出来或联网地址返回 null。</summary>
    public static string? ResolveAsset(string moduleDir, string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return null;   // 联网图片不加载：帮助页要能离线看

            var rel = Uri.UnescapeDataString(url.Replace('/', Path.DirectorySeparatorChar));
            var full = Path.GetFullPath(Path.Combine(moduleDir, rel));

            // 限制在文档目录内：`../` 这类相对路径不该让帮助页去读别处
            var root = Path.GetFullPath(Dir);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

            return File.Exists(full) ? full : null;
        }
        catch { return null; }
    }
}
