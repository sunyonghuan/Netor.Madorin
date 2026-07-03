using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Store.Abstractions;
using Netor.Cortana.Store.Services;
using Netor.Cortana.Store.ViewModels;
using Netor.Cortana.Store.Views;

namespace Netor.Cortana.Store;

public static class DependencyInjection
{
    public static IServiceCollection AddCortanaStore(this IServiceCollection services)
    {
        services.AddHttpClient("CortanaStore");
        services.AddSingleton<IPlatformAccountStore, PlatformAccountStore>();
        services.AddSingleton<IInstalledAssetStore, InstalledAssetStore>();
        services.AddSingleton<IPlatformMarketClient, PlatformMarketClient>();
        services.AddSingleton<IPlatformConnectionTester, PlatformConnectionTester>();
        services.AddSingleton<IPackageInstallService, PackageInstallService>();
        services.AddSingleton<IExternalBrowserLauncher, DefaultExternalBrowserLauncher>();
        services.AddTransient<StoreWindowViewModel>();
        services.AddTransient<StoreWindow>();
        services.AddTransient<StoreLoginWindow>();

        return services;
    }
}
