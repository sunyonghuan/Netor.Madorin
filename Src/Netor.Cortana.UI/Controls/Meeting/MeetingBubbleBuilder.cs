using System.Text;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

using Netor.Cortana.UI.Controls.Common;

namespace Netor.Cortana.UI.Controls.Meeting;

/// <summary>
/// 构建会议模式消息气泡，保持与专家模式的头像、名称、时间和 Markdown 气泡结构一致。
/// </summary>
public static class MeetingBubbleBuilder
{
    private static readonly Bitmap AiAvatarBitmap = LoadBitmap(AppBranding.AssetUri(AppBranding.LogoImageFileName));
    private static readonly Bitmap UserAvatarBitmap = LoadBitmap(AppBranding.AssetUri("mk.png"));
    private static readonly Bitmap HostAvatarBitmap = LoadBitmap(AppBranding.AssetUri("jl.png"));

    /// <summary>创建一条可流式更新的会议气泡。</summary>
    public static MeetingBubbleHandle Build(MeetingBubbleArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var isUser = string.Equals(args.SpeakerKind, "user", StringComparison.OrdinalIgnoreCase);
        var isSummary = string.Equals(args.MessageRole, "summary", StringComparison.OrdinalIgnoreCase);
        var isInquiry = string.Equals(args.MessageRole, "inquiry", StringComparison.OrdinalIgnoreCase);

        var avatar = BuildAvatar(args.SpeakerKind, isUser, args.AvatarPath);
        var renderer = new MarkdownRenderer
        {
            Markdown = args.Markdown ?? string.Empty,
        };
        var bubble = new Border
        {
            Background = BuildBubbleBrush(isUser, isSummary),
            BorderBrush = isUser
                ? new SolidColorBrush(Color.Parse("#34556f"))
                : isInquiry
                    ? new SolidColorBrush(Color.Parse("#A06CFF"))
                    : new SolidColorBrush(Color.Parse("#3c3c3c")),
            BorderThickness = isInquiry ? new Thickness(3, 1, 1, 1) : new Thickness(1),
            CornerRadius = isUser ? new CornerRadius(4, 5, 4, 4) : new CornerRadius(5, 4, 4, 4),
            Padding = new Thickness(14, 10),
            MinWidth = 120,
            Margin = isUser ? new Thickness(50, 0, 0, 0) : new Thickness(0, 0, 50, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Child = renderer,
        };

        var headerPanel = BuildHeader(args, isUser, isSummary, isInquiry);
        var partialChip = BuildPartialChip(args);
        if (partialChip is not null)
        {
            partialChip.SetValue(DockPanel.DockProperty, Dock.Right);
            headerPanel.Children.Add(partialChip);
        }

        var bubbleColumn = new StackPanel
        {
            Spacing = 0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        bubbleColumn.Children.Add(headerPanel);
        bubbleColumn.Children.Add(bubble);

        var row = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(0, 0, 0, 4),
        };
        avatar.SetValue(DockPanel.DockProperty, isUser ? Dock.Right : Dock.Left);
        row.Children.Add(avatar);
        row.Children.Add(bubbleColumn);

        return new MeetingBubbleHandle(
            args.MessageId,
            row,
            headerPanel,
            renderer,
            new StringBuilder(args.Markdown ?? string.Empty));
    }

    private static DockPanel BuildHeader(MeetingBubbleArgs args, bool isUser, bool isSummary, bool isInquiry)
    {
        var displayName = ResolveDisplayName(args, isUser, isSummary, isInquiry);
        var displayTime = args.Timestamp.ToLocalTime().ToString("HH:mm");
        var headerPanel = new DockPanel
        {
            Margin = isUser ? new Thickness(50, 0, 0, 2) : new Thickness(0, 0, 50, 2),
            LastChildFill = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };

        var leftHeader = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
        };
        leftHeader.Children.Add(new TextBlock
        {
            Text = displayName,
            FontSize = 11,
            FontWeight = FontWeight.Medium,
            Foreground = new SolidColorBrush(Color.Parse("#8f8f8f")),
        });
        leftHeader.Children.Add(new TextBlock
        {
            Text = displayTime,
            FontSize = 10,
            Opacity = 0.65,
            Foreground = new SolidColorBrush(Color.Parse("#8f8f8f")),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
        });
        leftHeader.SetValue(DockPanel.DockProperty, isUser ? Dock.Right : Dock.Left);
        headerPanel.Children.Add(leftHeader);
        return headerPanel;
    }

    private static Border BuildAvatar(string speakerKind, bool isUser, string? avatarPath)
    {
        var image = !isUser && TryLoadAvatarBitmap(avatarPath) is { } customAvatar
            ? customAvatar
            : string.Equals(speakerKind, "host", StringComparison.OrdinalIgnoreCase)
            ? HostAvatarBitmap
            : isUser
                ? UserAvatarBitmap
                : AiAvatarBitmap;

        return new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            ClipToBounds = true,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = isUser ? new Thickness(10, 0, 6, 0) : new Thickness(0, 0, 10, 0),
            Padding = new Thickness(1),
            Background = isUser ? new SolidColorBrush(Color.Parse("#2d3a42")) : Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.Parse("#3c3c3c")),
            BorderThickness = new Thickness(1),
            Child = new Image
            {
                Source = image,
                Width = isUser ? 32 : 40,
                Height = isUser ? 32 : 40,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        };
    }

    private static IBrush BuildBubbleBrush(bool isUser, bool isSummary)
    {
        if (isUser)
        {
            return new SolidColorBrush(Color.Parse("#1f3a4d"));
        }

        return isSummary
            ? new SolidColorBrush(Color.FromArgb(50, 160, 108, 255))
            : new SolidColorBrush(Color.Parse("#2a2a2a"));
    }

    private static Border? BuildPartialChip(MeetingBubbleArgs args)
    {
        if (!args.IsPartial)
        {
            return null;
        }

        var chip = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#3a3a3a")),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1),
            Child = new TextBlock
            {
                Text = "未完成",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse("#bdbdbd")),
            },
        };

        if (!string.IsNullOrWhiteSpace(args.ErrorMessage))
        {
            ToolTip.SetTip(chip, args.ErrorMessage);
        }

        return chip;
    }

    private static string ResolveDisplayName(MeetingBubbleArgs args, bool isUser, bool isSummary, bool isInquiry)
    {
        if (isUser)
        {
            return "老板";
        }

        if (!string.Equals(args.SpeakerKind, "host", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(args.SpeakerName) ? "智能体" : args.SpeakerName;
        }

        if (isSummary)
        {
            return "主持人 · 会议总结";
        }

        if (isInquiry)
        {
            return "主持人 · 征询您的意见";
        }

        return "主持人";
    }

    private static Bitmap LoadBitmap(string uri)
    {
        using var stream = AssetLoader.Open(new Uri(uri));
        return new Bitmap(stream);
    }

    private static Bitmap? TryLoadAvatarBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return new Bitmap(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>会议气泡构建参数。</summary>
public sealed record MeetingBubbleArgs(
    string MessageId,
    string SpeakerKind,
    string SpeakerId,
    string SpeakerName,
    string? Markdown,
    string MessageRole,
    DateTimeOffset Timestamp,
    bool IsPartial = false,
    string? ErrorMessage = null,
    string? AvatarPath = null);

/// <summary>会议气泡的可变句柄，用于流式追加 Markdown。</summary>
public sealed class MeetingBubbleHandle
{
    public MeetingBubbleHandle(
        string messageId,
        Control root,
        DockPanel headerPanel,
        MarkdownRenderer markdownRenderer,
        StringBuilder markdownBuffer)
    {
        MessageId = messageId;
        Root = root;
        HeaderPanel = headerPanel;
        MarkdownRenderer = markdownRenderer;
        MarkdownBuffer = markdownBuffer;
    }

    public string MessageId { get; }

    public Control Root { get; }

    public DockPanel HeaderPanel { get; }

    public MarkdownRenderer MarkdownRenderer { get; }

    public StringBuilder MarkdownBuffer { get; }

    private Border? _partialChip;

    public void AppendMarkdown(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        MarkdownBuffer.Append(text);
        MarkdownRenderer.Markdown = MarkdownBuffer.ToString();
    }

    public void FlushMarkdown()
    {
        MarkdownRenderer.FlushRender();
    }

    public void MarkPartial(string? errorMessage)
    {
        if (_partialChip is null)
        {
            _partialChip = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#3a3a3a")),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1),
                Child = new TextBlock
                {
                    Text = "未完成",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.Parse("#bdbdbd")),
                },
            };
            _partialChip.SetValue(DockPanel.DockProperty, Dock.Right);
            HeaderPanel.Children.Add(_partialChip);
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            ToolTip.SetTip(_partialChip, errorMessage);
        }
    }
}
