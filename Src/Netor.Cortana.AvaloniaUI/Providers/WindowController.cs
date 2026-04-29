using Avalonia.Threading;

using Netor.Cortana.AvaloniaUI.Views;

namespace Netor.Cortana.AvaloniaUI;

/// <summary>
/// 窗口控制的 Avalonia 实现，通过 DI 获取窗口实例。
/// 职责单一：仅负责窗口操作，与应用生命周期解耦。
/// </summary>
internal sealed class WindowController(
    IServiceProvider serviceProvider,
    IPublisher publisher) : IWindowController
{
    /// <inheritdoc />
    public void ShowMainWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var main = serviceProvider.GetRequiredService<MainWindow>();
            main.Show();
            main.Activate();
        });

        publisher.Publish(Events.OnMainWindowShown, new VoiceSignalArgs());
    }

    /// <inheritdoc />
    public void HideMainWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var main = serviceProvider.GetRequiredService<MainWindow>();
            main.Hide();
        });
    }

    /// <inheritdoc />
    public void ShowSettingsWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var settings = serviceProvider.GetRequiredService<SettingsWindow>();
            settings.Show();
            settings.Activate();
        });
    }

    /// <inheritdoc />
    public void ShowFloatWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            serviceProvider.GetRequiredService<FloatWindow>().Show();
        });
    }

    /// <inheritdoc />
    public void MoveFloatWindow(int x, int y)
    {
        Dispatcher.UIThread.Post(() =>
        {
            serviceProvider.GetRequiredService<FloatWindow>().Position = new PixelPoint(x, y);
        });
    }

    /// <inheritdoc />
    public bool IsMainWindowVisible()
    {
        // Avalonia 要求在 UI 线程上访问 Visual 属性
        return Dispatcher.UIThread.Invoke(() =>
            serviceProvider.GetRequiredService<MainWindow>().IsVisible);
    }
}
