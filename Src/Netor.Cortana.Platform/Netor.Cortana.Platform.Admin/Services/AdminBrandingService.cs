using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;

namespace Netor.Cortana.Platform.Admin.Services;

public sealed class AdminBrandingService(PlatformDbContext dbContext)
{
    private string? platformName;

    public async Task<string> GetPlatformNameAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(platformName))
        {
            return platformName;
        }

        platformName = await dbContext.SystemSettings
            .AsNoTracking()
            .Where(x => x.Key == "platform.name")
            .Select(x => x.Value)
            .FirstOrDefaultAsync(cancellationToken);

        platformName = string.IsNullOrWhiteSpace(platformName)
            ? "Madorin"
            : platformName.Trim();

        return platformName;
    }

    public async Task<string> GetAdminTitleAsync(CancellationToken cancellationToken = default)
    {
        var name = await GetPlatformNameAsync(cancellationToken);
        return string.Concat(name, " 后台");
    }

    public async Task<string> GetSystemCodeAsync(CancellationToken cancellationToken = default)
    {
        var name = await GetPlatformNameAsync(cancellationToken);
        return name.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
    }
}
