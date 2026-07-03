using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Entitys;
using Netor.Cortana.Store.Models;
using Netor.Cortana.Store.ViewModels;
using Netor.EventHub;
using System.Diagnostics;

namespace Netor.Cortana.Store.Views;

public partial class StoreWindow : Window
{
    private const double MarketScrollLoadThreshold = 48;
    private readonly StoreWindowViewModel? _viewModel;
    private readonly IServiceProvider? _serviceProvider;
    private readonly ISubscriber? _subscriber;
    private readonly Guid _sessionExpiredSubscriptionId;

    public StoreWindow()
    {
        InitializeComponent();
        ApplyBrandAssets();
    }

    public StoreWindow(StoreWindowViewModel viewModel, IServiceProvider serviceProvider, ISubscriber subscriber)
    {
        _viewModel = viewModel;
        _serviceProvider = serviceProvider;
        _subscriber = subscriber;
        DataContext = viewModel;
        InitializeComponent();
        ApplyBrandAssets();
        Opened += OnOpened;
        Closed += OnClosed;
        if (MarketScrollViewer is not null)
        {
            MarketScrollViewer.ScrollChanged += OnMarketScrollChanged;
        }
        _sessionExpiredSubscriptionId = _subscriber.Subscribe<PlatformSessionExpiredArgs>(
            StoreEvents.OnPlatformSessionExpired.Eventid,
            OnPlatformSessionExpired);
    }

    /// <summary>
    /// 从桌面品牌资源入口加载 Store 窗口使用的图标。
    /// </summary>
    private void ApplyBrandAssets()
    {
        FindIcon.Source = LoadBrandBitmap("find.png");
        ResourcesIcon.Source = LoadBrandBitmap("resourses.png");
        UpdateIcon.Source = LoadBrandBitmap("update.png");
        InstallIcon.Source = LoadBrandBitmap("install.png");
        AccountArrowIcon.Source = LoadBrandBitmap("up-w.png");
    }

    /// <summary>
    /// 加载主程序品牌程序集中的 Avalonia 位图资源。
    /// </summary>
    private static Bitmap LoadBrandBitmap(string assetPath)
    {
        using var stream = AssetLoader.Open(new Uri(AppBranding.AssetUri(assetPath)));
        return new Bitmap(stream);
    }

    private async void OnOpened(object? sender, System.EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StoreWindow] 初始化失败：{ex}");
        }
    }

    private async Task<bool> OnPlatformSessionExpired(EventHubContext context, PlatformSessionExpiredArgs args)
    {
        if (_viewModel is not null)
        {
            await _viewModel.MarkSessionExpiredAsync();
        }

        return false;
    }

    private void OnClosed(object? sender, System.EventArgs e)
    {
        if (MarketScrollViewer is not null)
        {
            MarketScrollViewer.ScrollChanged -= OnMarketScrollChanged;
        }

        _subscriber?.Unsubscribe(_sessionExpiredSubscriptionId);
    }

    private async void OnMarketScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is null || sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        if (!_viewModel.HasMoreMarketAssets || _viewModel.IsBusy || _viewModel.IsLoadingMoreAssets)
        {
            return;
        }

        var distanceToBottom = scrollViewer.Extent.Height - scrollViewer.Viewport.Height - scrollViewer.Offset.Y;
        if (distanceToBottom > MarketScrollLoadThreshold)
        {
            return;
        }

        await _viewModel.LoadMoreAssetsAsync();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetPosition(this).Y > 36)
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnViewSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewList is null || MarketView is null || MineView is null || UpdatesView is null || InstalledView is null)
        {
            return;
        }

        var selectedIndex = ViewList.SelectedIndex;
        MarketView.IsVisible = selectedIndex == 0;
        MineView.IsVisible = selectedIndex == 1;
        UpdatesView.IsVisible = selectedIndex == 2;
        InstalledView.IsVisible = selectedIndex == 3;
    }

    private void OnAllMarketFilterClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel?.SetMarketTypeFilter(null);

    private void OnPluginMarketFilterClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel?.SetMarketTypeFilter(AssetType.Plugin);

    private void OnSkillMarketFilterClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel?.SetMarketTypeFilter(AssetType.Skill);

    private void OnAgentMarketFilterClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel?.SetMarketTypeFilter(AssetType.Agent);

    private void OnSolutionMarketFilterClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel?.SetMarketTypeFilter(AssetType.Solution);

    private void OnPopularSortClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel?.ShowPopularAssets();

    private void OnShowAllAssetsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel?.ShowAllAssets();

    private async void OnRefreshAssetsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.RefreshStoreAsync();
        }
    }

    private async void OnLoginClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null || _serviceProvider is null)
        {
            return;
        }

        var dialog = ActivatorUtilities.CreateInstance<StoreLoginWindow>(
            _serviceProvider,
            new StoreLoginViewModel(_viewModel.BaseUrl));
        await dialog.ShowDialog(this);
        if (dialog.LoginSucceeded)
        {
            await _viewModel.RefreshSessionAsync();
            await _viewModel.RefreshAssetsAsync();
            await _viewModel.SyncAsync();
        }
    }

    private async void OnSyncClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.SyncAsync();
        }
    }

    private async void OnLogoutClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            await _viewModel.LogoutAsync();
        }
    }

    private async void OnInstallClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { DataContext: StoreMarketAssetRow row })
        {
            return;
        }

        var manifest = await _viewModel.GetInstallManifestAsync(row.Source);
        if (manifest is null || !await ConfirmInstallAsync(manifest, "安装"))
        {
            return;
        }

        await _viewModel.InstallAsync(manifest);
    }

    private async void OnMarketCardPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_viewModel is null || sender is not Control { DataContext: StoreMarketAssetRow row })
        {
            return;
        }

        await _viewModel.EnsureMarketAssetDetailAsync(row);
    }

    private async void OnToggleMarketDetailClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { DataContext: StoreMarketAssetRow row })
        {
            return;
        }

        await _viewModel.ToggleMarketAssetExpandedAsync(row);
    }

    private async void OnOwnedInstallClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { DataContext: StoreInstallAssetRow row })
        {
            return;
        }

        var manifest = row.Source;
        if (await ConfirmInstallAsync(manifest, "安装"))
        {
            await _viewModel.InstallAsync(manifest);
        }
    }

    private async void OnUpdateInstallClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { DataContext: StoreUpdateAssetRow row })
        {
            return;
        }

        var update = row.Source;
        if (await ConfirmInstallAsync(update.Current, "更新"))
        {
            await _viewModel.InstallAsync(update.Current);
        }
    }

    private void OnOpenInstalledDirectoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { DataContext: StoreInstalledAssetRow row })
        {
            return;
        }

        if (!Directory.Exists(row.InstalledDirectory))
        {
            _viewModel.ReportStatus($"安装目录不存在：{row.InstalledDirectory}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = row.InstalledDirectory,
                UseShellExecute = true
            });
            _viewModel.ReportStatus($"已打开安装目录：{row.Name}");
        }
        catch (Exception ex)
        {
            _viewModel.ReportStatus($"打开安装目录失败：{ex.Message}");
        }
    }

    private async void OnUninstallInstalledAssetClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { DataContext: StoreInstalledAssetRow row })
        {
            return;
        }

        if (await ConfirmUninstallAsync(row.Source))
        {
            await _viewModel.UninstallAsync(row.Source);
        }
    }

    private async Task<bool> ConfirmInstallAsync(StoreInstallManifest manifest, string actionText)
    {
        var sizeText = FormatSize(manifest.Version.PackageSize);
        var restartText = manifest.AssetType == AssetType.Plugin
            ? "插件会在安装完成后立即加载。"
            : "该资源写入磁盘后可能需要重启或在管理界面手动启用。";
        var baseBrush = this.FindResource("BaseBrush") as IBrush ?? Brushes.Transparent;
        var surface0Brush = this.FindResource("Surface0Brush") as IBrush ?? Brushes.Transparent;
        var surface1Brush = this.FindResource("Surface1Brush") as IBrush ?? Brushes.Transparent;
        var textBrush = this.FindResource("TextBrush") as IBrush ?? Foreground ?? Brushes.White;
        var subtextBrush = this.FindResource("SubtextBrush") as IBrush ?? Foreground ?? Brushes.White;
        var accentBrush = this.FindResource("AccentBrush") as IBrush ?? Foreground ?? Brushes.White;
        var crustBrush = this.FindResource("CrustBrush") as IBrush ?? Brushes.Black;

        var dialog = new Window
        {
            Title = $"{actionText}确认",
            Width = 420,
            Height = 240,
            MinWidth = 420,
            MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = baseBrush
        };

        var result = false;
        var cancelButton = new Button
        {
            Content = "取消",
            MinWidth = 88,
            Height = 38,
            Padding = new Thickness(18, 8),
            CornerRadius = new CornerRadius(4),
            Background = surface1Brush,
            Foreground = textBrush,
            BorderThickness = new Thickness(0)
        };
        var confirmButton = new Button
        {
            Content = actionText,
            MinWidth = 88,
            Height = 38,
            Padding = new Thickness(18, 8),
            CornerRadius = new CornerRadius(4),
            Background = accentBrush,
            Foreground = crustBrush,
            BorderThickness = new Thickness(0)
        };
        cancelButton.Click += (_, _) => dialog.Close();
        confirmButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };

        dialog.Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(18),
            Children =
            {
                new Border
                {
                    Background = surface0Brush,
                    BorderBrush = surface1Brush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14),
                    Child = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock { Text = manifest.AssetName, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = textBrush },
                            new TextBlock { Text = $"版本：{manifest.Version.VersionName}", Foreground = textBrush },
                            new TextBlock { Text = $"大小：{sizeText}", Foreground = textBrush },
                            new TextBlock { Text = restartText, TextWrapping = TextWrapping.Wrap, Foreground = subtextBrush }
                        }
                    }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 14, 0, 0),
                    Children = { cancelButton, confirmButton }
                }
            }
        };
        Grid.SetRow((Control)((Grid)dialog.Content).Children[1], 1);

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<bool> ConfirmUninstallAsync(InstalledAssetRecord asset)
    {
        var pluginText = asset.AssetType == AssetType.Plugin
            ? "卸载前会先停用插件并释放运行中的插件进程。"
            : asset.AssetType == AssetType.Solution
                ? "解决方案卸载会删除它带入的插件、技能和智能体；插件会先停用再删除。"
                : "该资源目录会从本机删除。";
        var baseBrush = this.FindResource("BaseBrush") as IBrush ?? Brushes.Transparent;
        var surface0Brush = this.FindResource("Surface0Brush") as IBrush ?? Brushes.Transparent;
        var surface1Brush = this.FindResource("Surface1Brush") as IBrush ?? Brushes.Transparent;
        var textBrush = this.FindResource("TextBrush") as IBrush ?? Foreground ?? Brushes.White;
        var subtextBrush = this.FindResource("SubtextBrush") as IBrush ?? Foreground ?? Brushes.White;
        var accentBrush = this.FindResource("AccentBrush") as IBrush ?? Foreground ?? Brushes.White;
        var crustBrush = this.FindResource("CrustBrush") as IBrush ?? Brushes.Black;

        var dialog = new Window
        {
            Title = "卸载确认",
            Width = 420,
            Height = 230,
            MinWidth = 420,
            MinHeight = 230,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = baseBrush
        };

        var result = false;
        var cancelButton = new Button
        {
            Content = "取消",
            MinWidth = 88,
            Height = 38,
            Padding = new Thickness(18, 8),
            CornerRadius = new CornerRadius(4),
            Background = surface1Brush,
            Foreground = textBrush,
            BorderThickness = new Thickness(0)
        };
        var confirmButton = new Button
        {
            Content = "卸载",
            MinWidth = 88,
            Height = 38,
            Padding = new Thickness(18, 8),
            CornerRadius = new CornerRadius(4),
            Background = accentBrush,
            Foreground = crustBrush,
            BorderThickness = new Thickness(0)
        };
        cancelButton.Click += (_, _) => dialog.Close();
        confirmButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };

        dialog.Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(18),
            Children =
            {
                new Border
                {
                    Background = surface0Brush,
                    BorderBrush = surface1Brush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14),
                    Child = new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock { Text = asset.AssetName, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = textBrush },
                            new TextBlock { Text = $"版本：{asset.VersionName}", Foreground = textBrush },
                            new TextBlock { Text = pluginText, TextWrapping = TextWrapping.Wrap, Foreground = subtextBrush }
                        }
                    }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 14, 0, 0),
                    Children = { cancelButton, confirmButton }
                }
            }
        };
        Grid.SetRow((Control)((Grid)dialog.Content).Children[1], 1);

        await dialog.ShowDialog(this);
        return result;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}
