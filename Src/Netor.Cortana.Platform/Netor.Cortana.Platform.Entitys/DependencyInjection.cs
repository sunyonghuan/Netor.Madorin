using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Platform.Core.Options;
using Netor.Cortana.Platform.Entitys.Data;

namespace Netor.Cortana.Platform.Entitys;

public static class DependencyInjection
{
    public static IServiceCollection AddPlatformDbContext(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(DatabaseOptions.SectionName);
        var options = new DatabaseOptions
        {
            Provider = section[nameof(DatabaseOptions.Provider)] ?? "Sqlite",
            ConnectionString = section[nameof(DatabaseOptions.ConnectionString)] ?? DatabaseOptions.DefaultConnectionString
        };

        var connectionString = ResolveConnectionString(options.ConnectionString);

        services.AddDbContext<PlatformDbContext>(builder =>
        {
            builder.ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.PendingModelChangesWarning));

            if (string.Equals(options.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                builder.UseSqlite(connectionString);
                return;
            }

            throw new NotSupportedException($"Database provider '{options.Provider}' is not supported yet.");
        });

        return services;
    }

    private static string ResolveConnectionString(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(builder.DataSource))
        {
            return connectionString;
        }

        var dataSource = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, builder.DataSource));
        var directory = Path.GetDirectoryName(dataSource);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        builder.DataSource = dataSource;
        return builder.ConnectionString;
    }
}