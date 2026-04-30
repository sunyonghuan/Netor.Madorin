using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Microsoft.Extensions.DependencyInjection;

namespace Netor.Cortana.AvaloniaUI.Views.Proxy;

/// <summary>
/// Ollama Proxy 小窗口。
/// 关闭仅隐藏窗口，不释放实例，方便托盘菜单再次打开。
/// </summary>
public partial class ProxyWindow : Window
{
    private readonly ProxyViewModel _viewModel;

    public ProxyWindow()
    {
        _viewModel = App.Services.GetRequiredService<ProxyViewModel>();
        DataContext = _viewModel;

        InitializeComponent();
        InitializeComboBoxes();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    public ProxyWindow(ProxyViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;

        InitializeComponent();
        InitializeComboBoxes();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    public void ShowProxyWindow()
    {
        _viewModel.Load();
        _viewModel.RefreshRuntimeState();
        RefreshOptionSources();

        if (!IsVisible)
        {
            PositionAtBottomRight();
            Show();
        }
        else
        {
            PositionAtBottomRight();
        }

        Activate();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        PositionAtBottomRight();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void InitializeComboBoxes()
    {
        RefreshOptionSources();
    }

    private void RefreshOptionSources()
    {
        ProviderBox.ItemsSource = _viewModel.Providers;
        ProviderBox.SelectedItem = _viewModel.Providers.FirstOrDefault(x => x.Id == _viewModel.ProviderId)
            ?? _viewModel.Providers.FirstOrDefault();
    }

    private void SyncSelectionsToViewModel()
    {
        if (ProviderBox.SelectedItem is OptionItem provider)
        {
            _viewModel.ProviderId = provider.Id;
        }
    }

    private async void OnEnabledToggled(object? sender, RoutedEventArgs e)
    {
        //if (!_initialized) return;
        //_viewModel.Enabled = !_viewModel.Enabled;
        SyncSelectionsToViewModel();
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000);
            await _viewModel.SaveAndApplyAsync().ConfigureAwait(false);
        });
        RefreshOptionSources();
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        SyncSelectionsToViewModel();
        await _viewModel.SaveAndApplyAsync();
        RefreshOptionSources();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void PositionAtBottomRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        var area = screen.WorkingArea;
        var x = area.X + area.Width - (int)Width - 260;
        var y = area.Y + area.Height - (int)Height - 260;
        Position = new Avalonia.PixelPoint(Math.Max(area.X, x), Math.Max(area.Y, y));
    }
}