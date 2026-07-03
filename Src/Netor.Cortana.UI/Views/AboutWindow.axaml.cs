using System.Reflection;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Netor.Cortana.UI.Views;

/// <summary>
/// 关于软件窗口。
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        LogoImage.Source = LoadLogoBitmap();
        AppNameText.Text = App.AppName;
        SubtitleText.Text = AppBranding.AssistantSubtitle;
        VersionText.Text = $"版本 {ResolveVersion()}";
        WorkspaceText.Text = $"工作目录：{App.WorkspaceDirectory}";
    }

    /// <summary>
    /// 从品牌默认入口加载关于页 Logo。
    /// </summary>
    private static Bitmap LoadLogoBitmap()
    {
        using var stream = AssetLoader.Open(new Uri(AppBranding.AssetUri(AppBranding.LogoImageFileName)));
        return new Bitmap(stream);
    }

    private static string ResolveVersion()
    {
        var assembly = typeof(App).Assembly;

        return assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "ProjectVersion")
            ?.Value
            ?? assembly.GetName().Version?.ToString(3)
            ?? "1.3.8";
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
