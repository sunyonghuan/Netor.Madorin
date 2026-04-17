using Avalonia.Controls;
using Avalonia.Interactivity;

using Netor.Cortana.AvaloniaUI.Views.Settings;

namespace Netor.Cortana.AvaloniaUI.Views;

/// <summary>
/// 设置窗口 — 左侧导航 + 右侧页面切换。
/// </summary>
public partial class SettingsWindow : Window
{
    // 按导航顺序缓存页面实例，避免重复创建
    private readonly Control[] _pages;

    public SettingsWindow()
    {
        InitializeComponent();

        _pages =
        [
            new SystemSettingsPage(),
            new ProviderSettingsPage(),
            new ModelSettingsPage(),
            new AgentSettingsPage(),
            new McpServerSettingsPage(),
            new ToolManagementPage(),
        ];

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
    }

    private void OnWindowLoaded(object? sender, EventArgs e)
    {
        // 默认显示第一个页面
        PageHost.Content = _pages[0];
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_pages is null || NavList is null) return;
        if (NavList.SelectedIndex is >= 0 and var idx && idx < _pages.Length)
        {
            PageHost.Content = _pages[idx];
        }
    }
}
