using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

using System.Diagnostics;

namespace Netor.Cortana.UI.Controls.Shared;

/// <summary>
/// 聊天资源右键菜单工厂，统一处理图片、视频和文件资源的常用操作。
/// </summary>
internal static class ResourceContextMenuFactory
{
    public static ContextMenu Create(
        Control owner,
        string? filePath,
        string displayName,
        bool isImage = false,
        Action? reload = null,
        Action<string?>? reportStatus = null)
    {
        var fullPath = NormalizePath(filePath);
        var menu = new ContextMenu();

        menu.Items.Add(CreateItem("打开", () => OpenFile(fullPath, reportStatus), enabled: HasExistingFile(fullPath)));
        menu.Items.Add(CreateItem("打开所在目录", () => OpenContainingFolder(fullPath, reportStatus), enabled: !string.IsNullOrWhiteSpace(fullPath)));

        if (isImage)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateItem("复制图片", async () => await CopyFileAsync(owner, fullPath, reportStatus), enabled: HasExistingFile(fullPath)));
            menu.Items.Add(CreateItem("查看原图", () => ViewOriginalImage(owner, fullPath, displayName, reportStatus), enabled: HasExistingFile(fullPath)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("复制文件", async () => await CopyFileAsync(owner, fullPath, reportStatus), enabled: HasExistingFile(fullPath)));
        menu.Items.Add(CreateItem("复制路径", async () => await CopyTextAsync(owner, fullPath ?? string.Empty, reportStatus), enabled: !string.IsNullOrWhiteSpace(fullPath)));
        menu.Items.Add(CreateItem("复制 Markdown 链接", async () => await CopyTextAsync(owner, BuildMarkdownLink(fullPath, displayName, isImage), reportStatus), enabled: !string.IsNullOrWhiteSpace(fullPath)));
        menu.Items.Add(CreateItem("另存为...", async () => await SaveAsAsync(owner, fullPath, displayName, reportStatus), enabled: HasExistingFile(fullPath)));

        if (reload is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateItem("重新加载", reload));
        }

        return menu;
    }

    private static MenuItem CreateItem(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = enabled,
        };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem CreateItem(string header, Func<Task> action, bool enabled = true)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = enabled,
        };
        item.Click += async (_, _) => await action();
        return item;
    }

    private static string? NormalizePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        if (filePath.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(filePath, UriKind.Absolute, out var uri))
        {
            return uri.LocalPath;
        }

        return filePath;
    }

    private static bool HasExistingFile(string? fullPath)
        => !string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath);

    private static void OpenFile(string? fullPath, Action<string?>? reportStatus)
    {
        if (!HasExistingFile(fullPath))
        {
            reportStatus?.Invoke("文件不存在");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(fullPath!) { UseShellExecute = true });
            reportStatus?.Invoke(null);
        }
        catch (Exception ex)
        {
            reportStatus?.Invoke($"无法打开：{ex.Message}");
        }
    }

    private static void OpenContainingFolder(string? fullPath, Action<string?>? reportStatus)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            reportStatus?.Invoke("没有可用路径");
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var argument = File.Exists(fullPath)
                    ? $"/select,\"{fullPath}\""
                    : $"\"{Path.GetDirectoryName(fullPath) ?? fullPath}\"";
                Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
            }
            else
            {
                var directory = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : fullPath;
                Process.Start(new ProcessStartInfo(directory ?? fullPath) { UseShellExecute = true });
            }

            reportStatus?.Invoke(null);
        }
        catch (Exception ex)
        {
            reportStatus?.Invoke($"无法打开目录：{ex.Message}");
        }
    }

    private static async Task CopyFileAsync(Control owner, string? fullPath, Action<string?>? reportStatus)
    {
        if (!HasExistingFile(fullPath))
        {
            reportStatus?.Invoke("文件不存在");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(owner);
        if (topLevel?.Clipboard is null)
        {
            reportStatus?.Invoke("剪贴板不可用");
            return;
        }

        try
        {
            var storageFile = topLevel.StorageProvider is null
                ? null
                : await topLevel.StorageProvider.TryGetFileFromPathAsync(fullPath!);

            if (storageFile is not null)
            {
                await topLevel.Clipboard.SetFilesAsync([storageFile]);
            }
            else
            {
                await topLevel.Clipboard.SetTextAsync(fullPath!);
            }

            reportStatus?.Invoke(null);
        }
        catch
        {
            await topLevel.Clipboard.SetTextAsync(fullPath!);
            reportStatus?.Invoke(null);
        }
    }

    private static async Task CopyTextAsync(Control owner, string text, Action<string?>? reportStatus)
    {
        var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
        if (clipboard is null)
        {
            reportStatus?.Invoke("剪贴板不可用");
            return;
        }

        await clipboard.SetTextAsync(text);
        reportStatus?.Invoke(null);
    }

    private static string BuildMarkdownLink(string? fullPath, string displayName, bool isImage)
    {
        var safeName = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileName(fullPath)
            : displayName;
        var uri = string.IsNullOrWhiteSpace(fullPath)
            ? string.Empty
            : new Uri(Path.GetFullPath(fullPath)).AbsoluteUri;

        return isImage ? $"![{safeName}]({uri})" : $"[{safeName}]({uri})";
    }

    private static async Task SaveAsAsync(Control owner, string? fullPath, string displayName, Action<string?>? reportStatus)
    {
        if (!HasExistingFile(fullPath))
        {
            reportStatus?.Invoke("文件不存在");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(owner);
        if (topLevel?.StorageProvider is null)
        {
            reportStatus?.Invoke("文件选择器不可用");
            return;
        }

        var suggestedName = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileName(fullPath)
            : displayName;
        var result = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "另存为",
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = await topLevel.StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(fullPath!) ?? string.Empty),
        });

        var targetPath = result?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        try
        {
            File.Copy(fullPath!, targetPath, overwrite: true);
            reportStatus?.Invoke(null);
        }
        catch (Exception ex)
        {
            reportStatus?.Invoke($"另存失败：{ex.Message}");
        }
    }

    private static void ViewOriginalImage(Control owner, string? fullPath, string displayName, Action<string?>? reportStatus)
    {
        if (!HasExistingFile(fullPath))
        {
            reportStatus?.Invoke("文件不存在");
            return;
        }

        try
        {
            var window = new Window
            {
                Title = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(fullPath) : displayName,
                Width = 960,
                Height = 720,
                MinWidth = 420,
                MinHeight = 320,
                Content = new ScrollViewer
                {
                    Content = new Image
                    {
                        Source = new Bitmap(fullPath!),
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(12),
                    },
                },
            };

            var parent = TopLevel.GetTopLevel(owner) as Window;
            if (parent is not null)
            {
                window.Show(parent);
            }
            else
            {
                window.Show();
            }

            reportStatus?.Invoke(null);
        }
        catch (Exception ex)
        {
            reportStatus?.Invoke($"无法查看原图：{ex.Message}");
        }
    }
}
