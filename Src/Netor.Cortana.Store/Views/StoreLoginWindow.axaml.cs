using Avalonia.Controls;
using Avalonia.Input;
using Netor.Cortana.Store.Abstractions;
using Netor.Cortana.Store.Models;
using Netor.Cortana.Store.ViewModels;

namespace Netor.Cortana.Store.Views;

public partial class StoreLoginWindow : Window
{
    private readonly IPlatformMarketClient? _marketClient;
    private readonly IPlatformAccountStore? _accountStore;
    private readonly IExternalBrowserLauncher? _browserLauncher;
    private readonly StoreLoginViewModel? _viewModel;

    public StoreLoginWindow()
    {
        InitializeComponent();
    }

    public StoreLoginWindow(
        StoreLoginViewModel viewModel,
        IPlatformMarketClient marketClient,
        IPlatformAccountStore accountStore,
        IExternalBrowserLauncher browserLauncher)
    {
        _viewModel = viewModel;
        _marketClient = marketClient;
        _accountStore = accountStore;
        _browserLauncher = browserLauncher;
        DataContext = viewModel;
        InitializeComponent();
    }

    public bool LoginSucceeded { get; private set; }

    private async void OnLoginClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null || _marketClient is null || _accountStore is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_viewModel.UserName) || string.IsNullOrWhiteSpace(_viewModel.Password))
        {
            _viewModel.StatusText = "请输入账号和密码。";
            return;
        }

        if (!_viewModel.ValidateCaptcha())
        {
            return;
        }

        try
        {
            _viewModel.IsBusy = true;
            _viewModel.StatusText = string.Empty;
            var login = await _marketClient.LoginAsync(_viewModel.UserName.Trim(), _viewModel.Password);
            await _accountStore.SaveAsync(new PlatformAccountSession(
                _viewModel.BaseUrl.TrimEnd('/'),
                login.AccessToken,
                login.ExpiresAtUtc,
                login.Account));

            LoginSucceeded = true;
            Close();
        }
        catch (Exception ex)
        {
            _viewModel.StatusText = $"登录失败：{ex.Message}";
            _viewModel.RefreshCaptcha();
        }
        finally
        {
            _viewModel.IsBusy = false;
        }
    }

    private void OnRegisterClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.ValidateCaptcha())
        {
            return;
        }

        _browserLauncher?.Open($"{_viewModel.AccountPortalBaseUrl}/account/register");
        _viewModel.RefreshCaptcha();
    }

    private void OnForgotPasswordClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _browserLauncher?.Open($"{_viewModel?.AccountPortalBaseUrl}/account/forgot-password");

    private void OnUseLocalTestAccountClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel?.UseLocalTestAccount();

    private void OnRefreshCaptchaClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel?.RefreshCaptcha();

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close();

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button)
        {
            return;
        }

        if (e.GetPosition(this).Y > 34)
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
