using AnimaCaptioner.Core;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
// "Block" 同时存在于 Microsoft.UI.Xaml.Documents 与 Markdig.Syntax，
// 不消歧会 CS0104。别名只给 Markdig 的那个。
using MdBlock = Markdig.Syntax.Block;

namespace AnimaCaptioner;

/// <summary>
/// Markdig 的语法树 → WinUI 原生控件。**不使用 WebView**：WebView2 要拉整个
/// 浏览器渲染栈，滚动和选中都明显迟滞，而帮助页只需要静态排版。
///
/// 渲染策略：文档根是一个 StackPanel，每个"块"生成一个独立控件——
///   · 标题 / 段落 / 列表项 → RichTextBlock（可选中、可复制）
///   · 代码块 → Border + 等宽 RichTextBlock
///   · 表格 → Grid（真正的列对齐）
///   · 图片 → Image（只加载文档目录里的本地文件）
///
/// 为什么每段一个 RichTextBlock 而不是整篇一个：RichTextBlock 的 Blocks 只能
/// 装 Paragraph，装不下表格、边框、图片这些需要布局的东西；而把整篇塞进一个
/// 控件再自己画分隔线是做不到的。分段后每段仍然可以单独选中复制。
///
/// 注意：WinUI 3 没有 WPF 的 BlockUIContainer，也没有 SelectableTextBlock
/// （本文件所有用到的成员都用真实编译器验证过存在，见 README「踩过的坑」）。
/// </summary>
internal sealed class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline;
    private readonly string _baseDir;
    private readonly Action<string>? _onNavigate;   // 站内链接跳转（模块名）
    private readonly Action<string>? _onExternal;   // 外链交给系统浏览器
    private readonly List<string> _warnings = new();

    /// <summary>正文基准字号。标题按比例放大，不写死绝对值。</summary>
    private const double BodyFontSize = 14;
    private const double CodeFontSize = 12.5;

    public MarkdownRenderer(string baseDir,
                            Action<string>? onNavigate = null,
                            Action<string>? onExternal = null)
    {
        _baseDir = baseDir;
        _onNavigate = onNavigate;
        _onExternal = onExternal;
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()      // 表格、任务列表、删除线、自动链接…
            .UseSoftlineBreakAsHardlineBreak()
            .Build();
    }

    public IReadOnlyList<string> Warnings => _warnings;

    public UIElement Render(string markdown)
    {
        var root = new StackPanel { Padding = new Thickness(0, 0, 8, 24) };
        MarkdownDocument doc;
        try
        {
            doc = Markdown.Parse(markdown ?? "", _pipeline);
        }
        catch (Exception ex)
        {
            _warnings.Add("Markdown 解析失败：" + ex.Message);
            root.Children.Add(Text("（这篇文档的 Markdown 无法解析：" + ex.Message + "）", muted: true));
            return root;
        }

        // 列表缩进要跨块传递：ListItemBlock 里可能是段落、也可能是嵌套列表
        RenderBlocks(root, doc, 0);
        return root;
    }

    // ================= 块级 =================

    private void RenderBlocks(Panel host, ContainerBlock container, int depth)
    {
        foreach (var block in container)
            RenderBlock(host, block, depth);
    }

    private void RenderBlock(Panel host, MdBlock block, int depth)
    {
        switch (block)
        {
            case HeadingBlock h:
                host.Children.Add(Heading(h, depth));
                break;

            case ParagraphBlock p:
                host.Children.Add(Paragraph(p, depth));
                break;

            case ListBlock list:
                RenderList(host, list, depth);
                break;

            case FencedCodeBlock fenced:
                host.Children.Add(CodeBlock(fenced.Lines.ToString(), fenced.Info));
                break;

            case CodeBlock code:                       // 缩进式代码块（四个空格）
                host.Children.Add(CodeBlock(code.Lines.ToString(), null));
                break;

            case Table table:
                host.Children.Add(Table(table));
                break;

            case ThematicBreakBlock:
                host.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 12, 0, 12),
                    Background = Res("DividerStrokeColorDefaultBrush") ?? Muted()
                });
                break;

            case QuoteBlock quote:
                host.Children.Add(Quote(quote, depth));
                break;

            case HtmlBlock html:
                // 帮助文档都是 Markdown，HTML 块按原样显示比静默丢弃好：
                // 静默丢掉会让"我写的这段哪去了"变成查不出的问题。
                host.Children.Add(CodeBlock(html.Lines.ToString(), "html"));
                break;

            case ListItemBlock item:
                // 单独出现（父级没走 RenderList）时也要能渲染
                RenderBlocks(host, item, depth + 1);
                break;

            default:
                _warnings.Add("未支持的块：" + block.GetType().Name);
                break;
        }
    }

    // ---- 标题 ----

    private UIElement Heading(HeadingBlock h, int depth)
    {
        // 字号按级次递减；居中 12 段留白，让模块内部有清晰的层次
        var size = h.Level switch
        {
            1 => 22.0,
            2 => 17.5,
            3 => 15.5,
            4 => 14.5,
            _ => 14.0
        };
        var (top, bottom) = h.Level switch
        {
            1 => (0.0, 10.0),
            2 => (22.0, 6.0),
            3 => (16.0, 4.0),
            _ => (12.0, 3.0)
        };

        var rtb = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = size,
            FontWeight = h.Level <= 2 ? FontWeights.SemiBold : FontWeights.SemiBold,
            Margin = new Thickness(depth * 16, top, 0, bottom)
        };
        var para = new Paragraph { LineHeight = size * 1.35 };
        AddInlines(para.Inlines, h.Inline, size);
        rtb.Blocks.Add(para);

        // 一级标题带一条强调色底线，作为模块的分节标记
        if (h.Level == 1)
        {
            var wrap = new StackPanel { Margin = new Thickness(0) };
            wrap.Children.Add(rtb);
            wrap.Children.Add(new Border
            {
                Height = 2,
                CornerRadius = new CornerRadius(1),
                Margin = new Thickness(0, 0, 0, 4),
                Background = Res("AccentFillColorDefaultBrush")
                             ?? Res("SystemAccentColor") as Brush ?? Muted()
            });
            return wrap;
        }
        return rtb;
    }

    // ---- 段落 ----

    private UIElement Paragraph(ParagraphBlock p, int depth)
    {
        var rtb = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = BodyFontSize,
            Margin = new Thickness(depth * 16, 0, 0, 8)
        };
        var para = new Paragraph { LineHeight = BodyFontSize * 1.55 };
        AddInlines(para.Inlines, p.Inline, BodyFontSize);
        rtb.Blocks.Add(para);
        return rtb;
    }

    // ---- 引用块 ----

    private UIElement Quote(QuoteBlock quote, int depth)
    {
        var inner = new StackPanel();
        RenderBlocks(inner, quote, depth);

        return new Border
        {
            Margin = new Thickness(depth * 16 + 2, 4, 0, 10),
            Padding = new Thickness(12, 6, 10, 2),
            // 只画左边框：右侧和上下留白，视觉上就是一条竖线
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Res("AccentFillColorDefaultBrush") ?? Muted(),
            Background = Res("ControlFillColorSecondaryBrush"),
            CornerRadius = new CornerRadius(2),
            Child = inner
        };
    }

    // ---- 代码块 ----

    private UIElement CodeBlock(string text, string? info)
    {
        var lang = (info ?? "").Trim();
        // 围栏后的第一个词才是语言标记（```c# title=x 这种写法）
        var sp = lang.IndexOf(' ');
        if (sp > 0) lang = lang[..sp];

        var rtb = new RichTextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = CodeFontSize
        };
        var para = new Paragraph { LineHeight = CodeFontSize * 1.5 };
        para.Inlines.Add(new Run { Text = text.TrimEnd('\n', '\r') });
        rtb.Blocks.Add(para);

        // 长行不能撑破窗口：套一层横向 ScrollViewer，而不是让它溢出被裁掉
        var scroller = new ScrollViewer
        {
            Content = rtb,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled
        };

        var stack = new StackPanel();
        if (!string.IsNullOrEmpty(lang))
        {
            stack.Children.Add(new TextBlock
            {
                Text = lang,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 0, 0, 3),
                Foreground = Res("TextFillColorTertiaryBrush") ?? Muted()
            });
        }
        stack.Children.Add(scroller);

        return new Border
        {
            Margin = new Thickness(0, 4, 0, 10),
            Padding = new Thickness(10, 8, 10, 8),
            Background = Res("ControlFillColorSecondaryBrush"),
            BorderBrush = Res("CardStrokeColorDefaultBrush") ?? Muted(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = stack
        };
    }

    // ---- 列表 ----

    private void RenderList(Panel host, ListBlock list, int depth)
    {
        // OrderedStart 是 string（不是 int）——Markdig 1.x 里它保留了原始写法的形态
        var start = 1;
        if (list.IsOrdered && int.TryParse(list.OrderedStart, out var s)) start = s;

        var n = 0;
        foreach (var child in list)
        {
            if (child is not ListItemBlock item) continue;

            var marker = list.IsOrdered ? $"{start + n}." : "•";
            n++;

            // 列表项本身就是个小容器：第一段接在标记后面，嵌套列表另起
            var row = new Grid { Margin = new Thickness(depth * 16, 0, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bullet = new TextBlock
            {
                Text = marker,
                FontSize = BodyFontSize,
                MinWidth = list.IsOrdered ? 20 : 14,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Foreground = Res("TextFillColorSecondaryBrush") ?? Muted()
            };
            Grid.SetColumn(bullet, 0);
            row.Children.Add(bullet);

            var content = new StackPanel();
            Grid.SetColumn(content, 1);
            row.Children.Add(content);

            var first = true;
            foreach (var sub in item)
            {
                if (sub is ListBlock nested)
                {
                    RenderList(content, nested, 0);   // row 已经缩进过了
                    continue;
                }

                var el = RenderListItemBlock(sub, first);
                if (el is not null)
                {
                    content.Children.Add(el);
                    first = false;
                }
            }

            host.Children.Add(row);
        }
    }

    /// <summary>列表项里的块。任务列表的勾选框在这里还原成 ☑ / ☐。</summary>
    private UIElement? RenderListItemBlock(MdBlock block, bool first)
    {
        if (block is not ParagraphBlock p) return RenderBlockToElement(block, 0);
        if (p.Inline is null) return null;

        var rtb = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = BodyFontSize,
            Margin = new Thickness(0, 0, 0, first ? 3 : 5)
        };
        var para = new Paragraph { LineHeight = BodyFontSize * 1.55 };
        AddInlines(para.Inlines, p.Inline, BodyFontSize, inListItem: true);
        rtb.Blocks.Add(para);
        return rtb;
    }

    /// <summary>
    /// 渲染单个块，返回可以直接挂到别处的控件。
    ///
    /// 必须把元素从临时容器里**摘下来**再返回：WinUI 不允许一个已有父级的
    /// UIElement 被 Add 到第二个父级，否则抛 COMException 0x800F1000
    /// （"没有检测到已安装的组件"——这个提示和真实原因完全对不上，极难查）。
    /// </summary>
    private UIElement? RenderBlockToElement(MdBlock block, int depth)
    {
        var tmp = new StackPanel();
        RenderBlock(tmp, block, depth);

        if (tmp.Children.Count == 1 && tmp.Children[0] is UIElement only)
        {
            tmp.Children.RemoveAt(0);   // 断开父子关系，之后才能挂到别处
            return only;
        }
        return tmp;   // 多个子元素时整体返回（tmp 本身就是那唯一父级）
    }

    // ---- 表格 ----

    private UIElement Table(Table table)
    {
        var cols = table.ColumnDefinitions?.Count ?? 0;
        if (cols == 0)
        {
            // 没解析出列定义就退回代码块，总比什么都不显示好
            return CodeBlock(RawTableText(table), null);
        }

        var grid = new Grid();
        for (var c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var r = 0;
        foreach (var b in table)
        {
            if (b is not TableRow row) continue;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var isHeader = row.IsHeader;
            var c = 0;
            foreach (var cellObj in row)
            {
                if (cellObj is not TableCell cell) continue;
                if (c >= cols) break;

                var align = table.ColumnDefinitions is not null && c < table.ColumnDefinitions.Count
                    ? table.ColumnDefinitions[c].Alignment
                    : null;

                var content = new StackPanel();
                foreach (var inner in cell)
                {
                    var el = RenderBlockToElement(inner, 0);
                    if (el is not null) content.Children.Add(el);
                }

                var border = new Border
                {
                    Child = content,
                    Padding = new Thickness(10, 6, 10, 6),
                    // 用 1px 网格线拼出表格：只画右边和下边，靠外层边框补左上
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    BorderBrush = Res("CardStrokeColorDefaultBrush") ?? Muted(),
                    Background = isHeader ? Res("ControlFillColorSecondaryBrush") : null,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                // 列对齐：Markdig 的 TableColumnDefinition.Alignment 是 TableColumnAlign?
                if (content.Children.Count > 0)
                {
                    var ha = align switch
                    {
                        TableColumnAlign.Center => HorizontalAlignment.Center,
                        TableColumnAlign.Right => HorizontalAlignment.Right,
                        _ => HorizontalAlignment.Left
                    };
                    content.HorizontalAlignment = ha;
                }

                // 表头加粗。逐个给 TextBlock/RichTextBlock 设，因为单元格
                // 可能是任意块类型（一般就是段落，但不保证）。
                if (isHeader) MakeBold(border);

                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
                c++;
            }
            r++;
        }

        // 外层补左上两条边，这样内层只画右下即可得到完整网格。
        // 用 Stretch（默认）而不是 Left：Left 会让 Grid 在测量时把 Star 列
        // 退化成 Auto，列宽随内容变化；Stretch 下各列等分可用宽度，长文本正常换行，
        // 也不会把表格撑出可视区（帮助窗口的滚动区横向是禁用的）。
        return new Border
        {
            Margin = new Thickness(0, 4, 0, 12),
            BorderThickness = new Thickness(1, 1, 0, 0),
            BorderBrush = Res("CardStrokeColorDefaultBrush") ?? Muted(),
            CornerRadius = new CornerRadius(4),
            Child = grid
        };
    }

    /// <summary>
    /// 把单元格里的文字全部加粗（表头用）。
    ///
    /// 走**逻辑**树（Panel.Children / Border.Child / ContentControl.Content），
    /// 不能用 VisualTreeHelper：这时控件还没进可视树、也没跑过布局，
    /// 可视树子级数是 0，遍历会静默什么都不做。
    /// </summary>
    private static void MakeBold(DependencyObject root)
    {
        switch (root)
        {
            case TextBlock tb:
                tb.FontWeight = FontWeights.SemiBold;
                return;
            case RichTextBlock rtb:
                foreach (var b in rtb.Blocks)
                    if (b is Paragraph p) p.FontWeight = FontWeights.SemiBold;
                return;
            case Border bd when bd.Child is not null:
                MakeBold(bd.Child);
                return;
            case ContentControl cc when cc.Content is DependencyObject cdo:
                MakeBold(cdo);
                return;
            case Panel panel:
                foreach (var child in panel.Children)
                    if (child is DependencyObject d) MakeBold(d);
                return;
        }
    }

    private static string RawTableText(Table table)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in table)
        {
            if (b is not TableRow row) continue;
            var cells = new List<string>();
            foreach (var c in row)
                if (c is TableCell tc)
                    cells.Add(PlainText(tc));
            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
        }
        return sb.ToString();
    }

    private static string PlainText(ContainerBlock cb)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(ContainerBlock c)
        {
            foreach (var b in c)
            {
                if (b is LeafBlock leaf && leaf.Inline is not null) WalkInline(leaf.Inline);
                else if (b is ContainerBlock inner) Walk(inner);
            }
        }
        void WalkInline(ContainerInline ci)
        {
            foreach (var i in ci)
            {
                switch (i)
                {
                    case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                    case CodeInline code: sb.Append(code.Content); break;
                    case ContainerInline nested: WalkInline(nested); break;
                }
            }
        }
        Walk(cb);
        return sb.ToString().Trim();
    }

    // ================= 行内 =================

    private void AddInlines(InlineCollection target, ContainerInline? inline, double fontSize, bool inListItem = false)
    {
        if (inline is null) return;
        var para = target;   // 下文一律往这个集合里加

        foreach (var child in inline)
        {
            switch (child)
            {
                case TaskList tl:
                    // Markdig 把 "- [x]" 解析成一个 TaskList 内联节点
                    para.Add(new Run
                    {
                        Text = tl.Checked ? "☑ " : "☐ ",
                        FontFamily = new FontFamily("Segoe UI Symbol")
                    });
                    break;

                case LiteralInline lit:
                    para.Add(new Run { Text = lit.Content.ToString() });
                    break;

                case EmphasisInline em:
                    AddEmphasis(para, em, fontSize);
                    break;

                case CodeInline code:
                    // 行内代码用等宽 + 淡底。Run 本身没有背景，
                    // 所以只做等宽和着色，避免引入 InlineUIContainer 打断选中。
                    para.Add(new Run
                    {
                        Text = code.Content,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = fontSize - 0.5,
                        Foreground = Res("TextFillColorSecondaryBrush") ?? Muted()
                    });
                    break;

                case LinkInline link:
                    AddLink(para, link, fontSize);
                    break;

                case LineBreakInline br:
                    if (br.IsHard) para.Add(new LineBreak());
                    else para.Add(new Run { Text = " " });
                    break;

                case HtmlInline html:
                    // 行内 HTML 按字面显示，别静默吞掉（同上）
                    para.Add(new Run { Text = html.Tag });
                    break;

                case AutolinkInline auto:
                    AddHyperlink(para, auto.Url, auto.Url);
                    break;

                case ContainerInline nested:
                    AddInlines(para, nested, fontSize, inListItem);
                    break;

                default:
                    _warnings.Add("未支持的行内：" + child.GetType().Name);
                    break;
            }
        }
    }

    private void AddEmphasis(InlineCollection para, EmphasisInline em, double fontSize)
    {
        // 删除线：Markdig 用 '~' 两次表示
        if (em.DelimiterChar == '~' && em.DelimiterCount >= 2)
        {
            var strike = new Span();
            AddInlines(strike.Inlines, em, fontSize);
            strike.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
            para.Add(strike);
            return;
        }

        // 粗体/斜体：DelimiterCount 决定，** 或 __ 为粗体，* 或 _ 为斜体
        if (em.DelimiterCount >= 2)
        {
            var b = new Bold();
            AddInlines(b.Inlines, em, fontSize);
            para.Add(b);
        }
        else
        {
            var i = new Italic();
            AddInlines(i.Inlines, em, fontSize);
            para.Add(i);
        }
    }

    private void AddLink(InlineCollection para, LinkInline link, double fontSize)
    {
        if (link.IsImage)
        {
            AddImage(para, link);
            return;
        }

        var url = link.Url ?? "";
        var text = PlainTextOf(link);
        if (string.IsNullOrWhiteSpace(text)) text = url;
        AddHyperlink(para, url, text);
    }

    private void AddHyperlink(InlineCollection para, string url, string text)
    {
        var hyper = new Hyperlink();
        hyper.Inlines.Add(new Run { Text = text });

        // 站内 .md 链接 → 在帮助窗口里跳转到对应模块；其余交给系统浏览器
        var isDocLink = url.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                        || url.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

        // 只给站外链接设 NavigateUri。若给 .md 也设上，WinUI 会拿它去启动
        // 系统处理程序（结果是个打不开的本地相对路径），和我们的跳转逻辑打架。
        if (!isDocLink && Uri.TryCreate(url, UriKind.Absolute, out var abs) &&
            (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
        {
            hyper.NavigateUri = abs;
        }

        // 链接要有可见的指针反馈，否则用户不知道能点
        ToolTipService.SetToolTip(hyper, isDocLink ? $"跳转到 {text}" : url);

        hyper.Click += (_, _) =>
        {
            try
            {
                // 落一行日志：链接点击这种交互，自动化点不中时很难区分
                // 是"没点进去"还是"handler 没干活"，有这行就能分辨。
                Log.Write($"help link clicked: url='{url}' isDoc={isDocLink} text='{text}'");
                if (isDocLink && _onNavigate is not null) _onNavigate(url);
                else if (_onExternal is not null) _onExternal(url);
            }
            catch (Exception ex) { Log.Write("help link failed: " + ex.Message); }
        };
        para.Add(hyper);
    }

    private void AddImage(InlineCollection para, LinkInline link)
    {
        var url = link.Url ?? "";
        var path = HelpDocs.ResolveAsset(_baseDir, url);
        if (path is null)
        {
            // 联网图片不加载（帮助要能离线看），缺失的本地图给出可见说明
            para.Add(new Run
            {
                Text = $"[图片：{PlainTextOf(link)}]",
                Foreground = Res("TextFillColorTertiaryBrush") ?? Muted()
            });
            _warnings.Add("图片未找到：" + url);
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            using var stream = File.OpenRead(path);
            bmp.SetSource(stream.AsRandomAccessStream());

            // 图片宽度跟随栏宽靠 Stretch；这里只限高，避免竖图把版面撑爆
            var img = new Image
            {
                Source = bmp,
                Stretch = Stretch.Uniform,
                MaxHeight = 420,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            if (bmp.PixelWidth > 0) img.Width = Math.Min(bmp.PixelWidth, 620);

            para.Add(new InlineUIContainer
            {
                Child = new Border
                {
                    Margin = new Thickness(0, 4, 0, 8),
                    Child = img
                }
            });
        }
        catch (Exception ex)
        {
            _warnings.Add($"图片载入失败 {url}：{ex.Message}");
            para.Add(new Run { Text = $"[图片无法载入：{url}]" });
        }
    }

    private static string PlainTextOf(ContainerInline ci)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(ContainerInline c)
        {
            foreach (var i in c)
            {
                switch (i)
                {
                    case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                    case CodeInline code: sb.Append(code.Content); break;
                    case ContainerInline nested: Walk(nested); break;
                }
            }
        }
        Walk(ci);
        return sb.ToString().Trim();
    }

    // ================= 资源 =================

    private static TextBlock Text(string t, bool muted = false) => new()
    {
        Text = t,
        TextWrapping = TextWrapping.Wrap,
        FontSize = BodyFontSize,
        Foreground = muted ? (Res("TextFillColorSecondaryBrush") ?? Muted()) : null
    };

    private static Brush Muted() => new SolidColorBrush(Microsoft.UI.Colors.Gray);

    /// <summary>
    /// 取主题画刷。资源键拼错在编译期查不出来（就是个字符串），
    /// 所以取不到就返回 null 由调用方回落，而不是抛异常把帮助页整个弄挂。
    /// </summary>
    private static Brush? Res(string key)
    {
        try
        {
            return Application.Current?.Resources?[key] as Brush;
        }
        catch { return null; }
    }
}
