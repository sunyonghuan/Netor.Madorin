using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Platform.Core.Options;
using Netor.Cortana.Platform.Services.Auth;
using Netor.Cortana.Platform.Services.Creators;
using Netor.Cortana.Platform.Services.Docs;
using Netor.Cortana.Platform.Services.Downloads;
using Netor.Cortana.Platform.Services.Files;
using Netor.Cortana.Platform.Services.Market;
using Netor.Cortana.Platform.Services.Orders;
using Netor.Cortana.Platform.Services.Packages;
using Netor.Cortana.Platform.Services.Reviews;
using Netor.Cortana.Platform.Services.Risk;
using Netor.Cortana.Platform.Services.Settlements;
using Netor.Cortana.Platform.Services.Settings;
using Netor.Cortana.Platform.Services.Subscriptions;

namespace Netor.Cortana.Platform.Services;

public static class DependencyInjection
{
    public static IServiceCollection AddPlatformServices(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(FileStorageOptions.SectionName);
        var packageStorageSection = configuration.GetSection(PackageStorageOptions.SectionName);
        var providerName = packageStorageSection[nameof(PackageStorageOptions.Provider)] ?? "Local";

        services.Configure<FileStorageOptions>(options =>
        {
            options.RootPath =
                packageStorageSection[nameof(PackageStorageOptions.RootPath)] ??
                section[nameof(FileStorageOptions.RootPath)] ??
                "Data";
        });

        services.Configure<PackageStorageOptions>(options =>
        {
            options.Provider = providerName;
            options.RootPath =
                packageStorageSection[nameof(PackageStorageOptions.RootPath)] ??
                section[nameof(FileStorageOptions.RootPath)] ??
                "Data";
            options.Endpoint = packageStorageSection[nameof(PackageStorageOptions.Endpoint)] ?? string.Empty;
            options.BucketName = packageStorageSection[nameof(PackageStorageOptions.BucketName)] ?? string.Empty;
            options.AccessKeyId = packageStorageSection[nameof(PackageStorageOptions.AccessKeyId)] ?? string.Empty;
            options.AccessKeySecret = packageStorageSection[nameof(PackageStorageOptions.AccessKeySecret)] ?? string.Empty;
            options.Region = packageStorageSection[nameof(PackageStorageOptions.Region)] ?? "us-east-1";
            options.ForcePathStyle = bool.TryParse(packageStorageSection[nameof(PackageStorageOptions.ForcePathStyle)], out var forcePathStyle)
                ? forcePathStyle
                : true;
            options.PublicBaseUrl = packageStorageSection[nameof(PackageStorageOptions.PublicBaseUrl)] ?? string.Empty;
        });

        var docsMediaSection = configuration.GetSection(DocsMediaOptions.SectionName);
        services.Configure<DocsMediaOptions>(options =>
        {
            options.RootPath = docsMediaSection[nameof(DocsMediaOptions.RootPath)] ?? string.Empty;
            options.RequestPath = docsMediaSection[nameof(DocsMediaOptions.RequestPath)] ?? "/docs-media";
            options.MaxImageBytes = long.TryParse(docsMediaSection[nameof(DocsMediaOptions.MaxImageBytes)], out var maxImageBytes)
                ? maxImageBytes
                : 5 * 1024 * 1024;
        });

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<DocsMediaPathService>();
        services.AddScoped<AuthService>();
        services.AddScoped<MarketService>();
        services.AddScoped<SubscriptionService>();
        services.AddScoped<OrderService>();
        services.AddScoped<DownloadService>();
        services.AddScoped<CreatorService>();
        services.AddScoped<AssetReviewService>();
        services.AddScoped<CreatorSettlementService>();
        services.AddScoped<PlatformSiteSettingsService>();
        services.AddScoped<PackageRiskService>();
        services.AddScoped<PackageStorageSettingsService>();
        services.AddScoped<LocalFileService>();
        services.AddScoped<IPackageStorageProvider, LocalPackageStorageProvider>();
        services.AddScoped<IPackageStorageProvider, S3PackageStorageProvider>();
        services.AddScoped<PackageStorageService>();

        return services;
    }
}
