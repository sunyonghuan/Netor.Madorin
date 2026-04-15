using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;

using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;

using AvaloniaRun = Avalonia.Controls.Documents.Run;
using MarkdigInline = Markdig.Syntax.Inlines.Inline;

namespace Netor.Cortana.AvaloniaUI.Controls;

/// <summary>
/// 基于 Markdig 解析 + Avalonia 原生控件树的 Markdown 渲染控件。
/// 每种 Markdown 块映射为独立原生控件，支持选中复制、表格、代码高亮、图片。
/// AOT 安全：不使用反射。
/// </summary>
public sealed class MarkdownRenderer : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownRenderer, string?>(nameof(Markdown));

    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UsePipeTables()
        .UseAutoLinks()
        .Build();

    private readonly StackPanel _rootPanel = new() { Spacing = 6 };
    private string? _lastRendered;

    // debounce 定时器，合并高频 token 更新
    private DispatcherTimer? _debounceTimer;

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownRenderer()
    {
        Content = _rootPanel;
    }

    static MarkdownRenderer()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownRenderer>(
            (r, _) => r.ScheduleRender());
    }

    /// <summary>
    /// 延迟 80ms 合并高频更新，避免每个 token 都 re-parse。
    /// </summary>
    private void ScheduleRender()
    {
        _debounceTimer?.Stop();
        _debounceTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(80),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _debounceTimer?.Stop();
                RenderMarkdown();
            });
        _debounceTimer.Start();
    }

    /// <summary>
    /// 立即渲染（跳过 debounce），用于 OnDone 最终刷新。
    /// </summary>
    public void FlushRender()
    {
        _debounceTimer?.Stop();
        RenderMarkdown();
    }

    private void RenderMarkdown()
    {
        var md = Markdown;
        if (md == _lastRendered) return;
        _lastRendered = md;

        _rootPanel.Children.Clear();
        if (string.IsNullOrEmpty(md)) return;

        var document = Markdig.Markdown.Parse(md, s_pipeline);
        RenderBlocks(document, _rootPanel);
    }

    // ───── 块级渲染 ─────

    /// <summary>
    /// 渲染块级元素，合并相邻的文本块（段落和标题）支持跨行选择。
    /// </summary>
    private void RenderBlocks(ContainerBlock container, Panel target)
    {
        var textBlockQueue = new List<Block>();  // 待合并的文本块

        foreach (var block in container)
        {
            switch (block)
            {
                case ParagraphBlock paragraph:
                    // 检查是否为独立图片
                    var imgBlock = TryCreateImageBlock(paragraph);
                    if (imgBlock is not null)
                    {
                        // 是图片块，先刷新待合并的文本块，再添加图片
                        if (textBlockQueue.Count > 0)
                        {
                            target.Children.Add(MergeTextBlocks(textBlockQueue));
                            textBlockQueue.Clear();
                        }
                        target.Children.Add(imgBlock);
                    }
                    else
                    {
                        // 是普通文本段落，加入待合并队列
                        textBlockQueue.Add(paragraph);
                    }
                    break;

                case HeadingBlock heading:
                    // 标题加入待合并队列
                    textBlockQueue.Add(heading);
                    break;

                case FencedCodeBlock fencedCode:
                    // 代码块遇到，先刷新待合并的文本块
                    if (textBlockQueue.Count > 0)
                    {
                        target.Children.Add(MergeTextBlocks(textBlockQueue));
                        textBlockQueue.Clear();
                    }
                    target.Children.Add(CreateCodeBlock(fencedCode));
                    break;

                case CodeBlock code:
                    if (textBlockQueue.Count > 0)
                    {
                        target.Children.Add(MergeTextBlocks(textBlockQueue));
                        textBlockQueue.Clear();
                    }
                    target.Children.Add(CreateCodeBlock(code));
                    break;

                case ListBlock list:
                    if (textBlockQueue.Count > 0)
                    {
                        target.Children.Add(MergeTextBlocks(textBlockQueue));
                        textBlockQueue.Clear();
                    }
                    target.Children.Add(CreateList(list, indent: 0));
                    break;

                case ThematicBreakBlock:
                    if (textBlockQueue.Count > 0)
                    {
                        target.Children.Add(MergeTextBlocks(textBlockQueue));
                        textBlockQueue.Clear();
                    }
                    target.Children.Add(CreateThematicBreak());
                    break;

                case QuoteBlock quote:
                    if (textBlockQueue.Count > 0)
                    {
                        target.Children.Add(MergeTextBlocks(textBlockQueue));
                        textBlockQueue.Clear();
                    }
                    target.Children.Add(CreateQuote(quote));
                    break;

                case Table table:
                    if (textBlockQueue.Count > 0)
                    {
                        target.Children.Add(MergeTextBlocks(textBlockQueue));
                        textBlockQueue.Clear();
                    }
                    target.Children.Add(CreateTable(table));
                    break;

                case ContainerBlock nested:
                    if (textBlockQueue.Count > 0)
                    {
                        target.Children.Add(MergeTextBlocks(textBlockQueue));
                        textBlockQueue.Clear();
                    }
                    RenderBlocks(nested, target);
                    break;
            }
        }

        // 最后刷新剩余的待合并文本块
        if (textBlockQueue.Count > 0)
        {
            target.Children.Add(MergeTextBlocks(textBlockQueue));
        }
    }

    /// <summary>
    /// 合并多个相邻的文本块（段落和标题）到单个 SelectableTextBlock 中，
    /// 支持跨行选择并保留所有格式（加粗、斜体、着色等）。
    /// </summary>
    private SelectableTextBlock MergeTextBlocks(List<Block> textBlocks)
    {
        var tb = new SelectableTextBlock
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#ffffff")),
            FontSize = 13,
            LineHeight = 20.8,
        };

        bool first = true;
        foreach (var block in textBlocks)
        {
            // 在块之间插入段落分隔（两个 LineBreak）
            if (!first)
            {
                tb.Inlines!.Add(new LineBreak());
                tb.Inlines!.Add(new LineBreak());
            }

            if (block is ParagraphBlock paragraph && paragraph.Inline is not null)
            {
                BuildInlines(paragraph.Inline, tb.Inlines!);
            }
            else if (block is HeadingBlock heading)
            {
                double fontSize = heading.Level switch
                {
                    1 => 18,
                    2 => 16,
                    3 => 15,
                    4 => 14,
                    5 => 13,
                    _ => 12,
                };

                // 记录当前的 Inlines 数量
                var startIdx = tb.Inlines!.Count;

                if (heading.Inline is not null)
                    BuildInlines(heading.Inline, tb.Inlines!);

                // 为新增的 Inlines 应用标题样式（加粗、字号）
                for (int i = startIdx; i < tb.Inlines!.Count; i++)
                {
                    if (tb.Inlines![i] is AvaloniaRun run)
                    {
                        run.FontWeight = FontWeight.Bold;
                        run.FontSize = fontSize;
                    }
                }
            }

            first = false;
        }

        return tb;
    }

    private SelectableTextBlock CreateParagraph(ParagraphBlock paragraph)
    {
        var tb = new SelectableTextBlock
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#ffffff")),
            FontSize = 13,
            LineHeight = 20.8,
        };
        BuildInlines(paragraph.Inline, tb.Inlines!);
        return tb;
    }

    private SelectableTextBlock CreateHeading(HeadingBlock heading)
    {
        double fontSize = heading.Level switch
        {
            1 => 18,
            2 => 16,
            3 => 15,
            4 => 14,
            5 => 13,
            _ => 12,
        };

        var tb = new SelectableTextBlock
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#ffffff")),
            FontSize = fontSize,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 4, 0, 2),
        };
        if (heading.Inline is not null)
            BuildInlines(heading.Inline, tb.Inlines!);
        return tb;
    }

    private Border CreateCodeBlock(LeafBlock codeBlock)
    {
        var code = codeBlock.Lines.ToString().TrimEnd('\n', '\r');
        string? lang = (codeBlock as FencedCodeBlock)?.Info;

        var inner = new StackPanel { Spacing = 0 };

        // 语言标签
        if (!string.IsNullOrWhiteSpace(lang))
        {
            inner.Children.Add(new TextBlock
            {
                Text = lang,
                FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#858585")),
                Margin = new Thickness(10, 6, 10, 2),
            });
        }

        // 代码正文（带语法高亮）
        var codeTb = new SelectableTextBlock
        {
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#ffffff")),
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            Padding = new Thickness(10, 4, 10, 8),
        };
        SimpleCodeHighlighter.Highlight(code, lang, codeTb.Inlines!);

        // 将代码正文放在 ScrollViewer 中，实现水平滚动
        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = codeTb,
        };

        inner.Children.Add(scrollViewer);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1e1e1e")),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 2),
            Child = inner,
        };
    }

    private Control CreateList(ListBlock list, int indent)
    {
        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(indent * 16, 0, 0, 0) };
        int index = 1;

        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem) continue;

            var prefix = list.IsOrdered ? $"{index++}. " : "• ";

            var contentPanel = new StackPanel { Spacing = 2 };
            foreach (var subBlock in listItem)
            {
                if (subBlock is ParagraphBlock paragraph)
                    contentPanel.Children.Add(CreateParagraph(paragraph));
                else if (subBlock is ListBlock nestedList)
                    contentPanel.Children.Add(CreateList(nestedList, indent + 1));
                else if (subBlock is ContainerBlock nested)
                    RenderBlocks(nested, contentPanel);
            }

            var itemRow = new DockPanel { LastChildFill = true };

            var prefixBlock = new TextBlock
            {
                Text = prefix,
                Foreground = new SolidColorBrush(Color.Parse("#007acc")),
                FontSize = 13,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 4, 0),
            };
            itemRow.Children.Add(prefixBlock);
            DockPanel.SetDock(prefixBlock, Avalonia.Controls.Dock.Left);

            itemRow.Children.Add(contentPanel);
            stack.Children.Add(itemRow);
        }

        return stack;
    }

    private Border CreateQuote(QuoteBlock quote)
    {
        var contentPanel = new StackPanel { Spacing = 4 };
        RenderBlocks(quote, contentPanel);

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#007acc")),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 4, 0, 4),
            Margin = new Thickness(0, 2),
            Child = contentPanel,
        };
    }

    private static Border CreateThematicBreak()
    {
        return new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.Parse("#3c3c3c")),
            Margin = new Thickness(0, 8),
        };
    }

    // ───── 内联渲染 ─────

    private void BuildInlines(ContainerInline? container, InlineCollection target)
    {
        if (container is null) return;

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new AvaloniaRun(literal.Content.ToString()));
                    break;

                case EmphasisInline emphasis:
                    BuildEmphasis(emphasis, target);
                    break;

                case CodeInline code:
                    target.Add(new AvaloniaRun($" {code.Content} ")
                    {
                        FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
                        Background = new SolidColorBrush(Color.Parse("#1e1e1e")),
                        Foreground = new SolidColorBrush(Color.Parse("#f14c4c")),
                    });
                    break;

                case LinkInline link when link.IsImage:
                    target.Add(CreateImagePlaceholder(link));
                    break;

                case LinkInline link:
                    target.Add(new AvaloniaRun(link.FirstChild?.ToString() ?? link.Url ?? "")
                    {
                        Foreground = new SolidColorBrush(Color.Parse("#007acc")),
                        TextDecorations = Avalonia.Media.TextDecorationCollection.Parse("Underline"),
                    });
                    break;

                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;

                case TaskList task:
                    target.Add(new AvaloniaRun(task.Checked ? "☑ " : "☐ ")
                    {
                        Foreground = new SolidColorBrush(Color.Parse(task.Checked ? "#73c991" : "#858585")),
                        FontSize = 14,
                    });
                    break;

                default:
                    target.Add(new AvaloniaRun(inline.ToString() ?? ""));
                    break;
            }
        }
    }

    private void BuildEmphasis(EmphasisInline emphasis, InlineCollection target)
    {
        foreach (var child in emphasis)
        {
            var run = child switch
            {
                LiteralInline lit => new AvaloniaRun(lit.Content.ToString()),
                CodeInline code => new AvaloniaRun($" {code.Content} ")
                {
                    FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
                    Background = new SolidColorBrush(Color.Parse("#1e1e1e")),
                    Foreground = new SolidColorBrush(Color.Parse("#f14c4c")),
                },
                EmphasisInline nested => CreateFlatEmphasisRun(nested),
                _ => new AvaloniaRun(child.ToString() ?? ""),
            };

            if (emphasis.DelimiterChar is '*' or '_')
            {
                if (emphasis.DelimiterCount == 2)
                    run.FontWeight = FontWeight.Bold;
                else
                    run.FontStyle = FontStyle.Italic;
            }
            if (emphasis.DelimiterChar == '~' && emphasis.DelimiterCount == 2)
                run.TextDecorations = Avalonia.Media.TextDecorationCollection.Parse("Strikethrough");

            target.Add(run);
        }
    }

    private static AvaloniaRun CreateFlatEmphasisRun(EmphasisInline emphasis)
    {
        var text = string.Concat(emphasis.Select(i => i.ToString()));
        var run = new AvaloniaRun(text);

        if (emphasis.DelimiterChar is '*' or '_')
        {
            if (emphasis.DelimiterCount == 2) run.FontWeight = FontWeight.Bold;
            else run.FontStyle = FontStyle.Italic;
        }
        if (emphasis.DelimiterChar == '~' && emphasis.DelimiterCount == 2)
            run.TextDecorations = Avalonia.Media.TextDecorationCollection.Parse("Strikethrough");

        return run;
    }

    // ───── 表格渲染 ─────

    private Control CreateTable(Table table)
    {
        int colCount = table.ColumnDefinitions?.Count ?? 0;
        if (colCount == 0) return new TextBlock { Text = "(空表格)" };

        var grid = new Grid { Margin = new Thickness(0, 4) };

        // 列定义：均分
        for (int c = 0; c < colCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        int rowIndex = 0;
        foreach (var rowObj in table)
        {
            if (rowObj is not TableRow row) continue;

            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            bool isHeader = row.IsHeader;

            for (int c = 0; c < row.Count && c < colCount; c++)
            {
                if (row[c] is not TableCell cell) continue;

                var cellTb = new SelectableTextBlock
                {
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#ffffff")),
                    FontSize = 13,
                    FontWeight = isHeader ? FontWeight.Bold : FontWeight.Normal,
                    Padding = new Thickness(8, 4),
                };

                // 提取 cell 内容
                foreach (var block in cell)
                {
                    if (block is ParagraphBlock p && p.Inline is not null)
                        BuildInlines(p.Inline, cellTb.Inlines!);
                }

                var cellBorder = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.Parse("#3c3c3c")),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Background = isHeader
                        ? new SolidColorBrush(Color.Parse("#252526"))
                        : new SolidColorBrush(Color.Parse("#1e1e1e")),
                    Child = cellTb,
                };

                Grid.SetRow(cellBorder, rowIndex);
                Grid.SetColumn(cellBorder, c);
                grid.Children.Add(cellBorder);
            }

            rowIndex++;
        }

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#3c3c3c")),
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Margin = new Thickness(0, 4),
            Child = grid,
        };
    }

    // ───── 图片占位（Inline 层） ─────

    private static AvaloniaRun CreateImagePlaceholder(LinkInline link)
    {
        var alt = link.FirstChild?.ToString() ?? "图片";
        return new AvaloniaRun($"[{alt}]")
        {
            Foreground = new SolidColorBrush(Color.Parse("#858585")),
            FontStyle = FontStyle.Italic,
        };
    }

    // ───── 图片块级渲染 ─────

    /// <summary>
    /// 检查段落是否为独立图片，若是则返回图片控件而非文本。
    /// 在 RenderBlocks 的 ParagraphBlock 分支前调用。
    /// </summary>
    private Control? TryCreateImageBlock(ParagraphBlock paragraph)
    {
        if (paragraph.Inline is null) return null;

        // 段落只含单个图片链接时，渲染为块级 Image
        var inlines = paragraph.Inline.ToList();
        if (inlines.Count == 1 && inlines[0] is LinkInline { IsImage: true } imgLink)
        {
            return CreateImageControl(imgLink);
        }
        return null;
    }

    private Control CreateImageControl(LinkInline imgLink)
    {
        var url = imgLink.Url;
        var alt = imgLink.FirstChild?.ToString() ?? "图片";

        var placeholder = new TextBlock
        {
            Text = $"⏳ 加载图片: {alt}",
            Foreground = new SolidColorBrush(Color.Parse("#858585")),
            FontStyle = FontStyle.Italic,
            FontSize = 12,
        };

        var container = new Border
        {
            Margin = new Thickness(0, 4),
            MaxWidth = 500,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Child = placeholder,
        };

        if (!string.IsNullOrEmpty(url))
            _ = LoadImageAsync(url, alt, container);

        return container;
    }

    private static async Task LoadImageAsync(string url, string alt, Border container)
    {
        try
        {
            Avalonia.Media.Imaging.Bitmap? bitmap = null;

            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                // Base64 data URI
                var commaIdx = url.IndexOf(',');
                if (commaIdx > 0)
                {
                    var base64 = url[(commaIdx + 1)..];
                    var bytes = Convert.FromBase64String(base64);
                    using var ms = new MemoryStream(bytes);
                    bitmap = new Avalonia.Media.Imaging.Bitmap(ms);
                }
            }
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                  || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // 网络图片
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(15);
                var bytes = await http.GetByteArrayAsync(url);
                using var ms = new MemoryStream(bytes);
                bitmap = new Avalonia.Media.Imaging.Bitmap(ms);
            }
            else
            {
                // 本地路径
                if (File.Exists(url))
                    bitmap = new Avalonia.Media.Imaging.Bitmap(url);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (bitmap is not null)
                {
                    container.Child = new Avalonia.Controls.Image
                    {
                        Source = bitmap,
                        MaxWidth = 500,
                        Stretch = Avalonia.Media.Stretch.Uniform,
                    };
                }
                else
                {
                    container.Child = new TextBlock
                    {
                        Text = $"❌ 图片加载失败: {alt}",
                        Foreground = new SolidColorBrush(Color.Parse("#f14c4c")),
                        FontSize = 12,
                    };
                }
            });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                container.Child = new TextBlock
                {
                    Text = $"❌ 图片无法加载: {alt}",
                    Foreground = new SolidColorBrush(Color.Parse("#f14c4c")),
                    FontSize = 12,
                };
            });
        }
    }

    // ───── 右键菜单 ─────

    /// <summary>
    /// 复制原始 Markdown 文本到剪贴板。
    /// </summary>
    public async void CopyMarkdownSource()
    {
        try
        {
            var md = Markdown;
            if (!string.IsNullOrEmpty(md))
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                    await clipboard.SetTextAsync(md);
            }
        }
        catch
        {
            // 静默处理
        }
    }
}
