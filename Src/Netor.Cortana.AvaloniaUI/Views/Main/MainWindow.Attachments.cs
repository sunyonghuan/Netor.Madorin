using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Extensions;

namespace Netor.Cortana.AvaloniaUI.Views;

/// <summary>
/// MainWindow — 附件选择/渲染/移除，以及输入框的拖放文件支持。
/// </summary>
public partial class MainWindow
{
    // 待发送的附件列表
    private readonly List<AttachmentInfo> _attachments = [];

    // 拖放 / 聚焦视觉样式
    private static readonly IBrush BorderNormal = SolidColorBrush.Parse("#3c3c3c");
    private static readonly IBrush BorderActive = SolidColorBrush.Parse("#007ACC");
    private static readonly IBrush BgNormal = SolidColorBrush.Parse("#252526");
    private static readonly IBrush BgDragOver = SolidColorBrush.Parse("#1a007ACC");
    private bool _isDragOver;

    // ──────── 附件管理 ────────

    /// <summary>
    /// 打开文件选择对话框，添加附件。
    /// </summary>
    private async void OnAttachClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var topLevel = GetTopLevel(this);
            if (topLevel?.StorageProvider is null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "选择附件",
                    AllowMultiple = true,
                    FileTypeFilter =
                    [
                        new Avalonia.Platform.Storage.FilePickerFileType("所有文件") { Patterns = ["*.*"] },
                        new Avalonia.Platform.Storage.FilePickerFileType("图片文件") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp"] },
                        new Avalonia.Platform.Storage.FilePickerFileType("文档文件") { Patterns = ["*.pdf", "*.doc", "*.docx", "*.txt", "*.md", "*.csv", "*.xlsx", "*.pptx"] },
                        new Avalonia.Platform.Storage.FilePickerFileType("脚本与源码") { Patterns = ["*.cs", "*.csx", "*.ps1", "*.psm1", "*.psd1", "*.py", "*.pyw", "*.cmd", "*.bat", "*.sh", "*.js", "*.ts", "*.json", "*.yml", "*.yaml", "*.xml"] },
                    ]
                });

            foreach (var file in files)
            {
                var path = file.Path.LocalPath;
                var name = file.Name;
                var mimeType = FileContentTypeResolver.GetMimeType(path);
                _attachments.Add(new AttachmentInfo(path, name, mimeType));
            }

            if (files.Count > 0)
            {
                RenderAttachments();
            }
        }
        catch (Exception ex)
        {
            var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();
            logger.LogError(ex, "打开文件选择器失败");
        }
    }

    /// <summary>
    /// 渲染附件预览列表。
    /// </summary>
    private void RenderAttachments()
    {
        AttachmentList.Items.Clear();

        for (int i = 0; i < _attachments.Count; i++)
        {
            var attachment = _attachments[i];
            var index = i;

            var removeBtn = new Button
            {
                Content = "✕",
                Background = Avalonia.Media.Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.Parse("#f14c4c")),
                FontSize = 11,
                Padding = new Thickness(2, 0),
                Margin = new Thickness(4, 0, 0, 0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                BorderThickness = new Thickness(0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Tag = index,
            };
            removeBtn.Click += OnRemoveAttachmentClick;

            var tag = new Border
            {
                Classes = { "attachment-tag" },
                Child = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"📎 {attachment.Name}",
                            FontSize = 12,
                            Foreground = new SolidColorBrush(Color.Parse("#cccccc")),
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                            MaxWidth = 180,
                            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                        },
                        removeBtn
                    }
                }
            };

            AttachmentList.Items.Add(tag);
        }
    }

    /// <summary>
    /// 移除指定附件。
    /// </summary>
    private void OnRemoveAttachmentClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int index && index >= 0 && index < _attachments.Count)
        {
            _attachments.RemoveAt(index);
            RenderAttachments();
        }
    }

    /// <summary>
    /// 清空所有附件。
    /// </summary>
    private void ClearAttachments()
    {
        _attachments.Clear();
        AttachmentList.Items.Clear();
    }

    /// <summary>
    /// 工作台文件树"发送到聊天附件"回调。
    /// </summary>
    private void OnWorkspaceAttachmentRequested(IReadOnlyList<string> filePaths)
    {
        var added = false;
        foreach (var path in filePaths)
        {
            if (!File.Exists(path)) continue;
            // 避免重复添加
            if (_attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            _attachments.Add(new AttachmentInfo(path, Path.GetFileName(path), FileContentTypeResolver.GetMimeType(path)));
            added = true;
        }
        if (added) RenderAttachments();
    }

    // ──────── 拖放文件 ────────

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;
        _isDragOver = true;
        InputBorder.BorderBrush = BorderActive;
        InputBorder.BorderThickness = new Thickness(2);
        InputBorder.Background = BgDragOver;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _isDragOver = false;
        RestoreInputBorder();
        e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _isDragOver = false;
        RestoreInputBorder();

        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        var added = false;
        foreach (var item in files)
        {
            var path = item.Path?.LocalPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;

            var name = Path.GetFileName(path);
            var mimeType = FileContentTypeResolver.GetMimeType(path);
            _attachments.Add(new AttachmentInfo(path, name, mimeType));
            added = true;
        }

        if (added)
        {
            RenderAttachments();
        }

        e.Handled = true;
    }
}
