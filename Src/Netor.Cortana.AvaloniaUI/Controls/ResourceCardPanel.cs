using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

using Netor.Cortana.Entitys;

using System.Diagnostics;

namespace Netor.Cortana.AvaloniaUI.Controls;

/// <summary>
/// 资源卡片面板，展示消息关联的文件/音频/视频资源。
/// 每个资源显示为一张可点击的小卡片（图标 + 文件名 + 大小）。
/// 点击后使用系统默认程序打开。
/// </summary>
internal sealed class ResourceCardPanel : WrapPanel
{
    private readonly string _resourcesRoot;

    public ResourceCardPanel(IReadOnlyList<ChatMessageAssetEntity> assets, string resourcesRoot)
    {
        _resourcesRoot = resourcesRoot;
        Orientation = Orientation.Horizontal;
        Margin = new Avalonia.Thickness(0, 6, 0, 0);

        foreach (var asset in assets)
        {
            // 图片已在 Markdown 内联渲染，跳过
            if (asset.AssetGroup == "images") continue;

            Children.Add(BuildCard(asset));
        }
    }

    private Control BuildCard(ChatMessageAssetEntity asset)
    {
        var icon = GetGroupIcon(asset.AssetGroup);
        var sizeText = FormatFileSize(asset.FileSizeBytes);

        var iconBlock = new TextBlock
        {
            Text = icon,
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 8, 0),
        };

        var nameBlock = new TextBlock
        {
            Text = asset.OriginalName,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 160,
        };

        var sizeBlock = new TextBlock
        {
            Text = sizeText,
            FontSize = 10,
            Opacity = 0.6,
        };

        var infoStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { nameBlock, sizeBlock },
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { iconBlock, infoStack },
        };

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(10, 8),
            Margin = new Avalonia.Thickness(0, 0, 8, 4),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = content,
        };

        card.PointerPressed += (_, _) => OpenResource(asset);
        card.PointerEntered += (_, _) => card.Opacity = 0.8;
        card.PointerExited += (_, _) => card.Opacity = 1.0;

        return card;
    }

    private void OpenResource(ChatMessageAssetEntity asset)
    {
        var fullPath = Path.Combine(_resourcesRoot, asset.RelativePath);
        if (!File.Exists(fullPath)) return;

        try
        {
            Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
        }
        catch { /* 系统无关联程序时静默失败 */ }
    }

    private static string GetGroupIcon(string assetGroup) => assetGroup switch
    {
        "audio" => "🎵",
        "video" => "🎬",
        "files" => "📄",
        _ => "📎",
    };

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
