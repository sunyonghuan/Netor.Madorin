using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;

namespace Netor.Cortana.Platform.Services.Settings;

public sealed class PlatformSiteSettingsService(PlatformDbContext dbContext)
{
    private const string DefaultSiteDomain = "aimdl.cn";

    private string? siteDomain;

    public async Task<string> GetSiteDomainAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(siteDomain))
        {
            return siteDomain;
        }

        var value = await dbContext.SystemSettings
            .AsNoTracking()
            .Where(x => x.Key == SiteSettingKeys.SiteDomain)
            .Select(x => x.Value)
            .FirstOrDefaultAsync(cancellationToken);

        siteDomain = NormalizeDomain(value);
        return siteDomain;
    }

    public async Task<string> CreateEmailAsync(string userName, CancellationToken cancellationToken = default)
    {
        var domain = await GetSiteDomainAsync(cancellationToken);
        return string.Concat(userName.Trim(), "@", domain);
    }

    private static string NormalizeDomain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultSiteDomain;
        }

        var domain = value.Trim();
        if (Uri.TryCreate(domain, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            domain = uri.Host;
        }

        domain = domain.Trim().TrimEnd('/');
        var slashIndex = domain.IndexOf('/', StringComparison.Ordinal);
        if (slashIndex >= 0)
        {
            domain = domain[..slashIndex];
        }

        var colonIndex = domain.LastIndexOf(':');
        if (colonIndex > 0)
        {
            domain = domain[..colonIndex];
        }

        return string.IsNullOrWhiteSpace(domain) ? DefaultSiteDomain : domain;
    }
}

public static class SiteSettingKeys
{
    public const string SiteDomain = "platform.site.domain";
}
