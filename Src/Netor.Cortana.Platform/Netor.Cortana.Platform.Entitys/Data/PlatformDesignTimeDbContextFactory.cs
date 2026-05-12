using Microsoft.EntityFrameworkCore.Design;
using Netor.Cortana.Platform.Core.Options;

namespace Netor.Cortana.Platform.Entitys.Data;

public sealed class PlatformDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("CORTANA_PLATFORM_CS")
            ?? DatabaseOptions.DefaultConnectionString;
        var optionsBuilder = new DbContextOptionsBuilder<PlatformDbContext>();
        optionsBuilder.UseSqlite(connectionString);
        return new PlatformDbContext(optionsBuilder.Options);
    }
}