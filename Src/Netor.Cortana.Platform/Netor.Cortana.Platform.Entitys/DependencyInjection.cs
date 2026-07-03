using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
            Provider = section[nameof(DatabaseOptions.Provider)] ?? "SqlServer",
            ConnectionString = section[nameof(DatabaseOptions.ConnectionString)] ?? DatabaseOptions.DefaultConnectionString
        };

        ValidateSqlServerGlobalizationConfiguration(options);

        services.AddDbContext<PlatformDbContext>(builder =>
        {
            builder.ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.PendingModelChangesWarning));

            if (string.Equals(options.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                builder.UseSqlite(ResolveSqliteConnectionString(options.ConnectionString));
                return;
            }

            if (IsSqlServerProvider(options.Provider))
            {
                builder.UseSqlServer(options.ConnectionString);
                return;
            }

            throw new NotSupportedException($"Database provider '{options.Provider}' is not supported.");
        });

        return services;
    }

    private static void ValidateSqlServerGlobalizationConfiguration(DatabaseOptions options)
    {
        if (!IsSqlServerProvider(options.Provider))
        {
            return;
        }

        var invariantValue = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT");
        if (!IsInvariantGlobalizationEnabled(invariantValue))
        {
            return;
        }

        throw new InvalidOperationException(
            "检测到当前进程启用了 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1/true。"
            + " Microsoft.Data.SqlClient 不支持在 Globalization Invariant Mode 下连接 SQL Server。"
            + " 请移除该环境变量或将其设置为 0/false，并确保 Linux 运行环境已安装 ICU 库。"
            + " 在 .NET 9 及以上版本中，环境变量优先级高于项目文件和 runtimeconfig.json。");
    }

    private static bool IsSqlServerProvider(string? provider)
        => string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "Mssql", StringComparison.OrdinalIgnoreCase);

    private static bool IsInvariantGlobalizationEnabled(string? value)
        => value is not null
            && (string.Equals(value.Trim(), "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    private static string ResolveSqliteConnectionString(string connectionString)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
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
