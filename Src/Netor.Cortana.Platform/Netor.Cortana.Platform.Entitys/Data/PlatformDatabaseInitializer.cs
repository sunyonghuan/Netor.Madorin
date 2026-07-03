using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Accounts;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Cortana.Platform.Entitys.Tables.Docs;
using Netor.Cortana.Platform.Entitys.Tables.Downloads;
using Netor.Cortana.Platform.Entitys.Tables.Subscriptions;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Netor.Cortana.Platform.Entitys.Data;

public static class PlatformDatabaseInitializer
{
    private const int SuperAdminRolePower = 100;

    public static async Task InitializeAsync(this IHost host, CancellationToken cancellationToken = default)
    {
        PlatformNativeAotRoots.PreserveEfCoreGenericCode();

        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        await using var scope = host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        if (dbContext.Database.IsSqlServer() &&
            await IsLegacyEnsureCreatedSqlServerDatabaseAsync(dbContext, cancellationToken))
        {
            await EnsureMcpSchemaForLegacySqlServerAsync(dbContext, cancellationToken);
        }
        else
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
        }

        await dbContext.EnsureSeededAsync(cancellationToken);
    }

    private static async Task<bool> IsLegacyEnsureCreatedSqlServerDatabaseAsync(
        PlatformDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var hasAccountLayers = await TableExistsAsync(dbContext, "AccountLayers", cancellationToken);
        if (!hasAccountLayers)
        {
            return false;
        }

        var hasMigrationHistory = await TableExistsAsync(dbContext, "__EFMigrationsHistory", cancellationToken);
        if (!hasMigrationHistory)
        {
            return true;
        }

        var appliedMigrationCount = await dbContext.Database.SqlQueryRaw<int>(
                "SELECT COUNT(1) AS [Value] FROM [__EFMigrationsHistory]")
            .SingleAsync(cancellationToken);

        return appliedMigrationCount == 0;
    }

    private static async Task<bool> TableExistsAsync(
        PlatformDbContext dbContext,
        string tableName,
        CancellationToken cancellationToken)
    {
        var exists = await dbContext.Database.SqlQueryRaw<int>(
                "SELECT CASE WHEN OBJECT_ID({0}, 'U') IS NULL THEN 0 ELSE 1 END AS [Value]",
                tableName)
            .SingleAsync(cancellationToken);

        return exists == 1;
    }

    private static async Task EnsureMcpSchemaForLegacySqlServerAsync(
        PlatformDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'[ManagerAuditLogs]', N'U') IS NULL
            BEGIN
                CREATE TABLE [ManagerAuditLogs] (
                    [ID] nvarchar(32) NOT NULL,
                    [ManagerId] nvarchar(32) NOT NULL,
                    [TokenId] nvarchar(32) NULL,
                    [ToolName] nvarchar(128) NOT NULL,
                    [Source] nvarchar(16) NOT NULL,
                    [Ip] nvarchar(64) NULL,
                    [Ua] nvarchar(256) NULL,
                    [Success] bit NOT NULL,
                    [ErrorCode] nvarchar(64) NULL,
                    [DurationMs] int NOT NULL,
                    [CreatedUtc] datetimeoffset NOT NULL,
                    [Creator] nvarchar(32) NOT NULL DEFAULT N'',
                    [TimeStamp] bigint NOT NULL DEFAULT CAST(DATEDIFF_BIG(second, '1970-01-01T00:00:00', SYSUTCDATETIME()) AS bigint),
                    [Version] rowversion NULL,
                    CONSTRAINT [PK_ManagerAuditLogs] PRIMARY KEY ([ID]),
                    CONSTRAINT [FK_ManagerAuditLogs_Managers_ManagerId] FOREIGN KEY ([ManagerId]) REFERENCES [Managers] ([ID]) ON DELETE CASCADE
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_ManagerAuditLogs_ManagerId_CreatedUtc' AND [object_id] = OBJECT_ID(N'[ManagerAuditLogs]'))
                CREATE INDEX [IX_ManagerAuditLogs_ManagerId_CreatedUtc] ON [ManagerAuditLogs] ([ManagerId], [CreatedUtc]);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_ManagerAuditLogs_ToolName' AND [object_id] = OBJECT_ID(N'[ManagerAuditLogs]'))
                CREATE INDEX [IX_ManagerAuditLogs_ToolName] ON [ManagerAuditLogs] ([ToolName]);
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'[ManagerMcpTokens]', N'U') IS NULL
            BEGIN
                CREATE TABLE [ManagerMcpTokens] (
                    [ID] nvarchar(32) NOT NULL,
                    [ManagerId] nvarchar(32) NOT NULL,
                    [TokenHash] nvarchar(64) NOT NULL,
                    [TokenPrefix] nvarchar(12) NOT NULL,
                    [Note] nvarchar(64) NOT NULL,
                    [Enabled] bit NOT NULL,
                    [CreatedUtc] datetimeoffset NOT NULL,
                    [LastUsedUtc] datetimeoffset NULL,
                    [LastUsedIp] nvarchar(64) NULL,
                    [LastUsedUa] nvarchar(256) NULL,
                    [Creator] nvarchar(32) NOT NULL DEFAULT N'',
                    [TimeStamp] bigint NOT NULL DEFAULT CAST(DATEDIFF_BIG(second, '1970-01-01T00:00:00', SYSUTCDATETIME()) AS bigint),
                    [Version] rowversion NULL,
                    CONSTRAINT [PK_ManagerMcpTokens] PRIMARY KEY ([ID]),
                    CONSTRAINT [FK_ManagerMcpTokens_Managers_ManagerId] FOREIGN KEY ([ManagerId]) REFERENCES [Managers] ([ID]) ON DELETE CASCADE
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_ManagerMcpTokens_ManagerId' AND [object_id] = OBJECT_ID(N'[ManagerMcpTokens]'))
                CREATE INDEX [IX_ManagerMcpTokens_ManagerId] ON [ManagerMcpTokens] ([ManagerId]);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_ManagerMcpTokens_TokenHash' AND [object_id] = OBJECT_ID(N'[ManagerMcpTokens]'))
                CREATE UNIQUE INDEX [IX_ManagerMcpTokens_TokenHash] ON [ManagerMcpTokens] ([TokenHash]);
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'[IdempotencyRecords]', N'U') IS NULL
            BEGIN
                CREATE TABLE [IdempotencyRecords] (
                    [ID] nvarchar(32) NOT NULL,
                    [Hash] nvarchar(64) NOT NULL,
                    [ToolName] nvarchar(128) NOT NULL,
                    [ManagerId] nvarchar(32) NOT NULL,
                    [RequestId] nvarchar(128) NOT NULL,
                    [ResultJson] nvarchar(max) NOT NULL,
                    [CreatedUtc] datetimeoffset NOT NULL,
                    [Creator] nvarchar(32) NOT NULL DEFAULT N'',
                    [TimeStamp] bigint NOT NULL DEFAULT CAST(DATEDIFF_BIG(second, '1970-01-01T00:00:00', SYSUTCDATETIME()) AS bigint),
                    [Version] rowversion NULL,
                    CONSTRAINT [PK_IdempotencyRecords] PRIMARY KEY ([ID])
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_IdempotencyRecords_CreatedUtc' AND [object_id] = OBJECT_ID(N'[IdempotencyRecords]'))
                CREATE INDEX [IX_IdempotencyRecords_CreatedUtc] ON [IdempotencyRecords] ([CreatedUtc]);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_IdempotencyRecords_Hash' AND [object_id] = OBJECT_ID(N'[IdempotencyRecords]'))
                CREATE UNIQUE INDEX [IX_IdempotencyRecords_Hash] ON [IdempotencyRecords] ([Hash]);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_IdempotencyRecords_ManagerId_ToolName' AND [object_id] = OBJECT_ID(N'[IdempotencyRecords]'))
                CREATE INDEX [IX_IdempotencyRecords_ManagerId_ToolName] ON [IdempotencyRecords] ([ManagerId], [ToolName]);
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            IF COL_LENGTH(N'[Transactions]', N'ThirdNo') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_Transactions_ThirdNo' AND [object_id] = OBJECT_ID(N'[Transactions]'))
                CREATE INDEX [IX_Transactions_ThirdNo] ON [Transactions] ([ThirdNo]);
            """,
            cancellationToken);

        await dbContext.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'[DocCategories]', N'U') IS NULL
            BEGIN
                CREATE TABLE [DocCategories] (
                    [ID] nvarchar(32) NOT NULL,
                    [Name] nvarchar(64) NOT NULL,
                    [Slug] nvarchar(128) NOT NULL,
                    [Description] nvarchar(256) NULL,
                    [SortOrder] int NOT NULL,
                    [IsVisible] bit NOT NULL,
                    [Creator] nvarchar(32) NOT NULL DEFAULT N'',
                    [TimeStamp] bigint NOT NULL DEFAULT CAST(DATEDIFF_BIG(second, '1970-01-01T00:00:00', SYSUTCDATETIME()) AS bigint),
                    [Version] rowversion NULL,
                    CONSTRAINT [PK_DocCategories] PRIMARY KEY ([ID])
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_DocCategories_Slug' AND [object_id] = OBJECT_ID(N'[DocCategories]'))
                CREATE UNIQUE INDEX [IX_DocCategories_Slug] ON [DocCategories] ([Slug]);

            IF OBJECT_ID(N'[DocArticles]', N'U') IS NULL
            BEGIN
                CREATE TABLE [DocArticles] (
                    [ID] nvarchar(32) NOT NULL,
                    [CategoryId] nvarchar(32) NOT NULL,
                    [Title] nvarchar(128) NOT NULL,
                    [Slug] nvarchar(160) NOT NULL,
                    [Summary] nvarchar(256) NOT NULL,
                    [ContentMarkdown] nvarchar(max) NOT NULL,
                    [Keywords] nvarchar(512) NOT NULL,
                    [Status] int NOT NULL,
                    [SortOrder] int NOT NULL,
                    [PublishedAtUtc] datetimeoffset NULL,
                    [Creator] nvarchar(32) NOT NULL DEFAULT N'',
                    [TimeStamp] bigint NOT NULL DEFAULT CAST(DATEDIFF_BIG(second, '1970-01-01T00:00:00', SYSUTCDATETIME()) AS bigint),
                    [Version] rowversion NULL,
                    CONSTRAINT [PK_DocArticles] PRIMARY KEY ([ID]),
                    CONSTRAINT [FK_DocArticles_DocCategories_CategoryId] FOREIGN KEY ([CategoryId]) REFERENCES [DocCategories] ([ID]) ON DELETE NO ACTION
                );
            END;

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_DocArticles_CategoryId_Slug' AND [object_id] = OBJECT_ID(N'[DocArticles]'))
                CREATE UNIQUE INDEX [IX_DocArticles_CategoryId_Slug] ON [DocArticles] ([CategoryId], [Slug]);

            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_DocArticles_Status_SortOrder' AND [object_id] = OBJECT_ID(N'[DocArticles]'))
                CREATE INDEX [IX_DocArticles_Status_SortOrder] ON [DocArticles] ([Status], [SortOrder]);
            """,
            cancellationToken);
    }

    public static async Task EnsureSeededAsync(this PlatformDbContext dbContext, CancellationToken cancellationToken = default)
    {
        var officialAccount = await EnsureOfficialAccountAsync(dbContext, cancellationToken);
        var superAdminRoleChanged = await EnsureSuperAdminRolePowerAsync(dbContext, cancellationToken);
        var hasManager = await dbContext.Managers.AnyAsync(x => x.LoginUserName == "admin", cancellationToken);
        var hasSettings = await dbContext.SystemSettings.AnyAsync(x => x.Key == "platform.name", cancellationToken);
        var hasCategories = await dbContext.Categories.AnyAsync(cancellationToken);
        var hasAssets = await dbContext.Assets.AnyAsync(cancellationToken);
        var hasSubscriptions = await dbContext.Subscriptions.AnyAsync(cancellationToken);
        var hasPaidAsset = await dbContext.Assets.AnyAsync(x => x.Slug == "pro-workflow-agent", cancellationToken);
        var hasAssetsWithoutOwner = await dbContext.Assets.AnyAsync(x => x.OwnerAccountId == null, cancellationToken);
        var hasPackageStorageSettings = await dbContext.SystemSettings.AnyAsync(x => x.Key == "package.storage.provider", cancellationToken);
        var siteDomainSettingCount = await dbContext.SystemSettings.CountAsync(x => x.Key == "platform.site.domain", cancellationToken);
        var hasSiteDomainSetting = siteDomainSettingCount > 0;
        var hasDuplicateManagedSettings = siteDomainSettingCount > 1;
        var hasDocCategories = await dbContext.DocCategories.AnyAsync(cancellationToken);
        var hasDocArticles = await dbContext.DocArticles.AnyAsync(cancellationToken);
        var seedPackagesChanged = hasAssets && await EnsureSeedPackageVersionsAsync(dbContext, cancellationToken);

        if (hasManager && hasSettings && hasPackageStorageSettings && hasSiteDomainSetting && !hasDuplicateManagedSettings && hasCategories && hasAssets && hasSubscriptions && hasPaidAsset && hasDocCategories && hasDocArticles && !hasAssetsWithoutOwner && !seedPackagesChanged && !superAdminRoleChanged)
        {
            return;
        }

        if (!hasManager)
        {
            var managerRole = await dbContext.ManagerRoles.FirstOrDefaultAsync(x => x.Name == "超级管理员", cancellationToken);
            managerRole ??= dbContext.ManagerRoles.Add(new Tables.Managers.ManagerRole
            {
                Name = "超级管理员",
                Power = SuperAdminRolePower
            }).Entity;

            if (managerRole.Power < SuperAdminRolePower)
            {
                managerRole.Power = SuperAdminRolePower;
            }

            var manager = dbContext.Managers.Add(new Tables.Managers.Manager
            {
                Role = managerRole,
                LoginUserName = "admin",
                Phone = "13800138001",
                LoginPassword = "123456".MD5Encrypt(),
                SafePassword = "888888".MD5Encrypt()
            }).Entity;

            dbContext.Set<Tables.Managers.ManagerProperty>().AddRange(
                new Tables.Managers.ManagerProperty
                {
                    Account = manager,
                    Key = "platform.admin.theme",
                    Value = "layui",
                    Display = "后台主题"
                },
                new Tables.Managers.ManagerProperty
                {
                    Account = manager,
                    Key = "platform.admin.initialized",
                    Value = "true",
                    Display = "后台是否已初始化"
                });
        }

        await EnsureSystemSettingsAsync(dbContext, cancellationToken);

        if (!hasCategories)
        {
            dbContext.Categories.AddRange(CreateSeedCategories());
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (!hasAssets)
        {
            await EnsureSeedAssetsAsync(dbContext, officialAccount, cancellationToken);
        }

        if (hasAssetsWithoutOwner)
        {
            await BindOwnerToExistingAssetsAsync(dbContext, officialAccount.ID, cancellationToken);
        }

        if (!hasSubscriptions)
        {
            await EnsureSeedSubscriptionsAsync(dbContext, cancellationToken);
        }

        if (!hasPaidAsset)
        {
            await EnsurePaidSeedAssetAsync(dbContext, officialAccount, cancellationToken);
        }

        if (!hasDocCategories || !hasDocArticles)
        {
            await EnsureSeedDocsAsync(dbContext, cancellationToken);
        }

        seedPackagesChanged = await EnsureSeedPackageVersionsAsync(dbContext, cancellationToken) || seedPackagesChanged;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task<bool> EnsureSuperAdminRolePowerAsync(
        PlatformDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var managerRole = await dbContext.ManagerRoles
            .FirstOrDefaultAsync(x => x.Name == "超级管理员", cancellationToken);
        if (managerRole is null || managerRole.Power >= SuperAdminRolePower)
        {
            return false;
        }

        managerRole.Power = SuperAdminRolePower;
        return true;
    }

    private static async Task<bool> EnsureSeedPackageVersionsAsync(PlatformDbContext dbContext, CancellationToken cancellationToken)
    {
        var seedPaths = new Dictionary<string, (AssetType Type, string VersionName, string FilePath)>(StringComparer.OrdinalIgnoreCase)
        {
            ["google-search-plugin"] = (AssetType.Plugin, "1.0.34", "Data/packages/plugins/google-search/1.0.34/google-search.zip"),
            ["memory-organizer-skill"] = (AssetType.Skill, "1.0.0", "Data/packages/skills/memory-organizer/1.0.0/memory-organizer.zip"),
            ["work-assistant-agent"] = (AssetType.Agent, "1.0.0", "Data/packages/agents/work-assistant/1.0.0/work-assistant.zip"),
            ["starter-plugin-solution"] = (AssetType.Solution, "1.0.0", "Data/packages/solutions/starter-plugin-solution/1.0.0/starter-plugin-solution.zip"),
            ["pro-workflow-agent"] = (AssetType.Agent, "1.0.0", "Data/packages/agents/pro-workflow/1.0.0/pro-workflow.zip")
        };

        var assets = await dbContext.Assets
            .Include(x => x.Versions)
            .Where(x => seedPaths.Keys.Contains(x.Slug))
            .ToListAsync(cancellationToken);

        var changed = false;
        foreach (var asset in assets)
        {
            var seed = seedPaths[asset.Slug];
            var package = await EnsureSeedPackageFileAsync(asset, seed.FilePath, cancellationToken);
            var version = string.IsNullOrWhiteSpace(asset.CurrentVersionId)
                ? asset.Versions.OrderByDescending(x => x.TimeStamp).FirstOrDefault()
                : asset.Versions.FirstOrDefault(x => x.ID == asset.CurrentVersionId)
                  ?? asset.Versions.OrderByDescending(x => x.TimeStamp).FirstOrDefault();

            if (version is null)
            {
                version = new AssetVersion
                {
                    Asset = asset,
                    ReleaseNotes = "初始化种子版本。",
                    ManifestJson = "{}",
                    StorageProvider = "Local"
                };
                asset.Versions.Add(version);
                changed = true;
            }

            if (version.VersionName != seed.VersionName)
            {
                version.VersionName = seed.VersionName;
                changed = true;
            }

            if (version.FilePath != seed.FilePath)
            {
                version.FilePath = seed.FilePath;
                changed = true;
            }

            if (!string.Equals(version.PackageHash, package.Hash, StringComparison.OrdinalIgnoreCase))
            {
                version.PackageHash = package.Hash;
                changed = true;
            }

            if (version.PackageSize != package.Size)
            {
                version.PackageSize = package.Size;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(version.StorageProvider))
            {
                version.StorageProvider = "Local";
                changed = true;
            }

            if (asset.CurrentVersionId != version.ID)
            {
                asset.CurrentVersionId = version.ID;
                changed = true;
            }
        }

        return changed;
    }

    private static async Task<(string Hash, long Size)> EnsureSeedPackageFileAsync(Asset asset, string relativePath, CancellationToken cancellationToken)
    {
        var fullPath = ResolveSeedPackagePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var sourcePackage = asset.Slug.Equals("google-search-plugin", StringComparison.OrdinalIgnoreCase)
            ? FindRepositoryFile(Path.Combine("Plugins", "Releases", "GoogleSearch.v1.0.34.zip"))
            : null;

        if (sourcePackage is not null)
        {
            if (!File.Exists(fullPath) || File.GetLastWriteTimeUtc(fullPath) < File.GetLastWriteTimeUtc(sourcePackage))
            {
                File.Copy(sourcePackage, fullPath, overwrite: true);
            }
        }
        else if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
        {
            await CreateSeedPackageZipAsync(asset, fullPath, cancellationToken);
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (hash, bytes.LongLength);
    }

    private static async Task CreateSeedPackageZipAsync(Asset asset, string fullPath, CancellationToken cancellationToken)
    {
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var (manifestName, content) = CreateSeedPackageManifest(asset);
                var manifestEntry = archive.CreateEntry(manifestName, CompressionLevel.SmallestSize);
                await using (var entryStream = manifestEntry.Open())
                {
                    var bytes = Encoding.UTF8.GetBytes(content);
                    await entryStream.WriteAsync(bytes, cancellationToken);
                }

                var readmeEntry = archive.CreateEntry("README.md", CompressionLevel.SmallestSize);
                await using var readmeStream = readmeEntry.Open();
                var readmeBytes = Encoding.UTF8.GetBytes($"# {asset.Name}\n\n本地应用商店联调用种子资源包。\n");
                await readmeStream.WriteAsync(readmeBytes, cancellationToken);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static (string Name, string Content) CreateSeedPackageManifest(Asset asset)
        => asset.Type switch
        {
            AssetType.Plugin => ("plugin.json", $$"""
                {
                  "id": "netor.seed.{{asset.Slug}}",
                  "name": "{{asset.Name}}",
                  "version": "1.0.0",
                  "description": "{{asset.ShortDescription}}",
                  "runtime": "process",
                  "command": "seed-plugin.cmd"
                }
                """),
            AssetType.Skill => ("skill.md", $"# {asset.Name}\n\n{asset.ShortDescription}\n"),
            AssetType.Agent => ("agent.json", $$"""
                {
                  "id": "netor.seed.{{asset.Slug}}",
                  "name": "{{asset.Name}}",
                  "description": "{{asset.ShortDescription}}"
                }
                """),
            AssetType.Solution => ("solution.json", $$"""
                {
                  "id": "netor.seed.{{asset.Slug}}",
                  "name": "{{asset.Name}}",
                  "description": "{{asset.ShortDescription}}"
                }
                """),
            _ => throw new ArgumentOutOfRangeException(nameof(asset), asset.Type, "未知资源类型。")
        };

    private static string ResolveSeedPackagePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, normalized));
    }

    private static string? FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static async Task<Account> EnsureOfficialAccountAsync(PlatformDbContext dbContext, CancellationToken cancellationToken)
    {
        var account = await dbContext.Accounts.FirstOrDefaultAsync(x => x.LoginUserName == "official@netor.me", cancellationToken);
        if (account is not null)
        {
            return account;
        }

        account = dbContext.Accounts.Add(new Account
        {
            No = 900000000002,
            LoginUserName = "official@netor.me",
            Email = "official@netor.me",
            NickName = "Netor 官方",
            RealName = "Netor Official",
            Phone = "13800138002",
            LoginPassword = "123456".MD5Encrypt(),
            SafePassword = "888888".MD5Encrypt()
        }).Entity;

        dbContext.AccountWallets.Add(new AccountWallet
        {
            Account = account
        });

        return account;
    }

    private static async Task EnsureSystemSettingsAsync(PlatformDbContext dbContext, CancellationToken cancellationToken)
    {
        var existingSettings = await dbContext.SystemSettings
            .ToListAsync(cancellationToken);
        var existingKeys = existingSettings
            .Select(x => x.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var setting in CreateSeedSettings())
        {
            if (!existingKeys.Contains(setting.Key))
            {
                dbContext.SystemSettings.Add(setting);
            }
        }

        var storageRootSetting = existingSettings.FirstOrDefault(x => x.Key == "platform.storage.root");
        if (storageRootSetting is not null)
        {
            storageRootSetting.Display = "本地包存储根目录，支持绝对路径；多站点部署时请填写共享目录";
        }

        RemoveDuplicateSeedSettings(dbContext, existingSettings, "platform.site.domain");
    }

    private static void RemoveDuplicateSeedSettings(
        PlatformDbContext dbContext,
        IReadOnlyCollection<SystemSetting> existingSettings,
        params string[] keys)
    {
        var seedKeys = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicateSettings = existingSettings
            .Where(x => seedKeys.Contains(x.Key))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1)
            .SelectMany(x => x
                .OrderBy(i => string.IsNullOrWhiteSpace(i.Value))
                .ThenBy(i => i.ID, StringComparer.Ordinal)
                .Skip(1))
            .ToList();

        if (duplicateSettings.Count > 0)
        {
            dbContext.SystemSettings.RemoveRange(duplicateSettings);
        }
    }

    private static IEnumerable<SystemSetting> CreateSeedSettings()
    {
        return
        [
            new SystemSetting { Key = "platform.name", Value = "Madorin", Name = "平台名称", Display = "平台名称", Type = NetorDataType.Text, Group = "平台设置" },
            new SystemSetting { Key = "platform.site.domain", Value = "aimdl.cn", Name = "网站域名", Display = "官网展示和邮箱地址使用的网站域名", Type = NetorDataType.Text, Group = "平台设置" },
            new SystemSetting { Key = "platform.storage.root", Value = "Data", Name = "本地存储根目录", Display = "本地包存储根目录，支持绝对路径；多站点部署时请填写共享目录", Type = NetorDataType.Text, Group = "平台设置" },
            new SystemSetting { Key = "package.storage.provider", Value = "Local", Name = "包存储提供方", Display = "Local 或 S3", Type = NetorDataType.Text, Group = "对象存储" },
            new SystemSetting { Key = "package.storage.s3.endpoint", Value = string.Empty, Name = "S3 Endpoint", Display = "S3 兼容对象存储服务地址", Type = NetorDataType.Text, Group = "对象存储" },
            new SystemSetting { Key = "package.storage.s3.bucket", Value = string.Empty, Name = "S3 Bucket", Display = "资源包对象存储桶名称", Type = NetorDataType.Text, Group = "对象存储" },
            new SystemSetting { Key = "package.storage.s3.accessKeyId", Value = string.Empty, Name = "S3 AccessKey", Display = "对象存储访问密钥 ID", Type = NetorDataType.Text, Group = "对象存储", IsProtection = true },
            new SystemSetting { Key = "package.storage.s3.accessKeySecret", Value = string.Empty, Name = "S3 SecretKey", Display = "对象存储访问密钥 Secret", Type = NetorDataType.Text, Group = "对象存储", IsProtection = true },
            new SystemSetting { Key = "package.storage.s3.region", Value = "us-east-1", Name = "S3 Region", Display = "对象存储区域，MinIO 可保留默认值", Type = NetorDataType.Text, Group = "对象存储" },
            new SystemSetting { Key = "package.storage.s3.forcePathStyle", Value = "true", Name = "S3 PathStyle", Display = "MinIO 等兼容服务通常需要开启", Type = NetorDataType.Boolean, Group = "对象存储" },
            new SystemSetting { Key = "package.storage.publicBaseUrl", Value = string.Empty, Name = "包公开地址", Display = "用于展示对象存储包地址的公开域名", Type = NetorDataType.Text, Group = "对象存储" },
            new SystemSetting { Key = "platform.download.anonymous", Value = "false", Name = "是否允许匿名下载", Display = "是否允许匿名下载", Type = NetorDataType.Boolean, Group = "下载设置" },
            new SystemSetting { Key = "finance.currency.unit", Value = "CNY", Name = "货币单位", Display = "货币单位", Type = NetorDataType.Text, Group = "财务设置" }
        ];
    }

    private static IEnumerable<Category> CreateSeedCategories()
    {
        return
        [
            new Category { Name = "插件", Slug = "plugins", Description = "扩展 Cortana 能力的本地插件。", SortOrder = 10 },
            new Category { Name = "技能", Slug = "skills", Description = "面向对话和任务编排的可复用技能。", SortOrder = 20 },
            new Category { Name = "智能体", Slug = "agents", Description = "封装角色、工具和流程的智能体。", SortOrder = 30 },
            new Category { Name = "解决方案", Slug = "solutions", Description = "插件、技能和智能体组合打包方案。", SortOrder = 40 }
        ];
    }

    private static async Task EnsureSeedAssetsAsync(PlatformDbContext dbContext, Account officialAccount, CancellationToken cancellationToken)
    {
        var categories = await dbContext.Categories.ToDictionaryAsync(x => x.Slug, cancellationToken);

        Category GetCategory(string slug)
        {
            if (categories.TryGetValue(slug, out var category))
            {
                return category;
            }

            category = CreateSeedCategories().First(x => x.Slug == slug);
            dbContext.Categories.Add(category);
            categories[slug] = category;
            return category;
        }

        var now = new DateTimeOffset(2026, 5, 7, 0, 0, 0, TimeSpan.Zero);

        dbContext.Assets.AddRange(
            CreateSeedAsset(AssetType.Plugin, officialAccount, GetCategory("plugins"), "Google 搜索插件", "google-search-plugin", "提供 Google 搜索能力的基础插件。", "Data/packages/plugins/google-search/1.0.0/google-search.zip", now.AddDays(-8)),
            CreateSeedAsset(AssetType.Skill, officialAccount, GetCategory("skills"), "记忆整理技能", "memory-organizer-skill", "整理对话记忆和知识片段。", "Data/packages/skills/memory-organizer/1.0.0/memory-organizer.zip", now.AddDays(-6)),
            CreateSeedAsset(AssetType.Agent, officialAccount, GetCategory("agents"), "工作助手智能体", "work-assistant-agent", "面向日常办公和任务跟进的智能体。", "Data/packages/agents/work-assistant/1.0.0/work-assistant.zip", now.AddDays(-4)),
            CreateSeedAsset(AssetType.Solution, officialAccount, GetCategory("solutions"), "入门插件解决方案包", "starter-plugin-solution", "适合个人用户快速体验平台生态。", "Data/packages/solutions/starter-plugin-solution/1.0.0/starter-plugin-solution.zip", now.AddDays(-2)));
    }

    private static Asset CreateSeedAsset(AssetType type, Account officialAccount, Category category, string name, string slug, string shortDescription, string filePath, DateTimeOffset publishedAtUtc)
    {
        return new Asset
        {
            Type = type,
            Category = category,
            OwnerAccount = officialAccount,
            Name = name,
            Slug = slug,
            DeveloperName = "Netor 官方",
            ShortDescription = shortDescription,
            Description = "用于验证平台预览、订阅和下载机制的初始化种子数据。",
            Tags = type.ToString(),
            Status = AssetStatus.Published,
            PublishedAtUtc = publishedAtUtc,
            IsFeatured = true,
            Versions =
            [
                new AssetVersion
                {
                    VersionName = "1.0.0",
                    ReleaseNotes = "初始化种子版本。",
                    ManifestJson = "{}",
                    PackageHash = "seed-placeholder",
                    PackageSize = 0,
                    FilePath = filePath
                }
            ],
            PricingPlans =
            [
                new PricingPlan
                {
                    Name = "免费版",
                    PlanType = PricingPlanType.Free,
                    Price = 0,
                    Currency = "CNY",
                    DurationDays = 0,
                    IsActive = true
                }
            ]
        };
    }

    private static async Task BindOwnerToExistingAssetsAsync(PlatformDbContext dbContext, string officialAccountId, CancellationToken cancellationToken)
    {
        var assets = await dbContext.Assets
            .Where(x => x.OwnerAccountId == null)
            .ToListAsync(cancellationToken);

        foreach (var asset in assets)
        {
            asset.OwnerAccountId = officialAccountId;
            if (string.Equals(asset.DeveloperName, "Netor", StringComparison.OrdinalIgnoreCase))
            {
                asset.DeveloperName = "Netor 官方";
            }
        }
    }

    private static async Task EnsureSeedSubscriptionsAsync(PlatformDbContext dbContext, CancellationToken cancellationToken)
    {
        var account = await dbContext.Accounts.FirstOrDefaultAsync(x => x.LoginUserName == "demo@netor.me", cancellationToken);
        if (account is null)
        {
            account = dbContext.Accounts.Add(new Account
            {
                No = 900000000001,
                LoginUserName = "demo@netor.me",
                Email = "demo@netor.me",
                Phone = "13800138000",
                LoginPassword = "123456".MD5Encrypt(),
                SafePassword = "888888".MD5Encrypt()
            }).Entity;
        }

        var assets = await dbContext.Assets
            .Include(x => x.Versions)
            .Include(x => x.PricingPlans)
            .OrderBy(x => x.Name)
            .Take(3)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        foreach (var asset in assets)
        {
            var plan = asset.PricingPlans.FirstOrDefault();
            var version = asset.Versions.FirstOrDefault();
            if (plan is null || version is null)
            {
                continue;
            }

            var status = asset.Type switch
            {
                AssetType.Skill => SubscriptionStatus.Expired,
                AssetType.Agent => SubscriptionStatus.Canceled,
                _ => SubscriptionStatus.Active
            };

            var subscription = dbContext.Subscriptions.Add(new Subscription
            {
                Account = account,
                Asset = asset,
                PricingPlan = plan,
                Status = status,
                StartedAtUtc = now.AddDays(-20),
                ExpiresAtUtc = status == SubscriptionStatus.Expired ? now.AddDays(-1) : now.AddDays(40),
                CanceledAtUtc = status == SubscriptionStatus.Canceled ? now.AddDays(-2) : null
            }).Entity;

            dbContext.DownloadRecords.Add(new DownloadRecord
            {
                Account = account,
                Asset = asset,
                AssetVersion = version,
                Subscription = subscription,
                IpAddress = "127.0.0.1",
                UserAgent = "Cortana Admin Seed"
            });
        }
    }

    private static async Task EnsurePaidSeedAssetAsync(PlatformDbContext dbContext, Account officialAccount, CancellationToken cancellationToken)
    {
        var category = await dbContext.Categories.FirstOrDefaultAsync(x => x.Slug == "agents", cancellationToken);
        category ??= dbContext.Categories.Add(new Category { Name = "智能体", Slug = "agents", Description = "封装角色、工具和流程的智能体。", SortOrder = 30 }).Entity;

        dbContext.Assets.Add(new Asset
        {
            Type = AssetType.Agent,
            Category = category,
            OwnerAccount = officialAccount,
            Name = "专业流程智能体",
            Slug = "pro-workflow-agent",
            DeveloperName = "Netor 官方",
            ShortDescription = "面向长期任务编排和自动化执行的付费智能体。",
            Description = "用于验证付费订单、模拟支付和订阅生成流程的初始化种子数据。",
            Tags = "Agent,Pro,Paid",
            Status = AssetStatus.Published,
            PublishedAtUtc = new DateTimeOffset(2026, 5, 3, 0, 0, 0, TimeSpan.Zero),
            IsFeatured = true,
            Versions =
            [
                new AssetVersion
                {
                    VersionName = "1.0.0",
                    ReleaseNotes = "付费智能体初始化版本。",
                    ManifestJson = "{}",
                    PackageHash = "seed-paid-placeholder",
                    PackageSize = 0,
                    FilePath = "Data/packages/agents/pro-workflow/1.0.0/pro-workflow.zip"
                }
            ],
            PricingPlans =
            [
                new PricingPlan
                {
                    Name = "月度订阅",
                    PlanType = PricingPlanType.Monthly,
                    Price = 19.9m,
                    Currency = "CNY",
                    DurationDays = 30,
                    IsActive = true
                }
            ]
        });
    }

    private static async Task EnsureSeedDocsAsync(PlatformDbContext dbContext, CancellationToken cancellationToken)
    {
        var categories = await dbContext.DocCategories
            .Include(x => x.Articles)
            .ToDictionaryAsync(x => x.Slug, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var quickStart = EnsureDocCategory(categories, dbContext, "快速开始", "getting-started", "安装、登录和完成第一次工作流。", 10);
        var workflows = EnsureDocCategory(categories, dbContext, "工作流", "workflows", "会议模式、任务拆解和交付物沉淀。", 20);
        var resources = EnsureDocCategory(categories, dbContext, "资源中心", "resources", "插件、技能、智能体和解决方案的订阅与使用。", 30);

        EnsureDocArticle(quickStart, "下载并登录", "download-and-login", "安装桌面客户端并登录账号。", """
            # 下载并登录

            安装 Madorin 桌面客户端后，使用平台账号登录。登录状态用于资源订阅、下载授权和跨设备资源同步。

            ## 操作步骤

            1. 进入官网下载页面。
            2. 下载适合当前系统的安装包。
            3. 完成安装后启动客户端。
            4. 使用账号登录并确认资源中心可访问。
            """, "安装,登录,客户端", 10);

        EnsureDocArticle(quickStart, "提出目标", "describe-goal", "用自然语言描述你想完成的业务目标。", """
            # 提出目标

            直接描述要完成的业务目标，例如产品上线、客户维护、营销内容发布或软件开发。

            建议同时说明背景、期望结果、限制条件和已有材料。目标越明确，后续拆分出来的步骤越容易执行。
            """, "目标,任务,提示词", 20);

        EnsureDocArticle(workflows, "使用会议模式", "meeting-mode", "让多个专家智能体从不同角度参与讨论。", """
            # 使用会议模式

            会议模式适合需要多角色协作判断的任务。你可以让产品、研发、运营、法务等角色从各自角度补充观点。

            会议结束后，应把结论沉淀为明确的执行计划、风险清单和下一步动作。
            """, "会议模式,智能体,协作", 10);

        EnsureDocArticle(workflows, "进入工作流", "run-workflow", "把讨论结果拆成可持续推进的工作流。", """
            # 进入工作流

            工作流用于承接会议结论，把方案拆成步骤，并持续推进文档、脚本、检查项和交付动作。

            每个步骤都应该有明确目标、输入材料、输出物和完成标准。
            """, "工作流,计划,执行", 20);

        EnsureDocArticle(resources, "加载资源中心能力", "load-resources", "订阅并加载插件、技能、智能体或解决方案。", """
            # 加载资源中心能力

            订阅插件、技能、智能体或解决方案后，Madorin 可以按需加载能力，并在本地保留缓存。

            资源中心后续会提供更完整的版本说明、安装指南和排障文档。
            """, "资源中心,插件,技能,智能体", 10);
    }

    private static DocCategory EnsureDocCategory(
        IDictionary<string, DocCategory> categories,
        PlatformDbContext dbContext,
        string name,
        string slug,
        string description,
        int sortOrder)
    {
        if (categories.TryGetValue(slug, out var category))
        {
            return category;
        }

        category = new DocCategory
        {
            Name = name,
            Slug = slug,
            Description = description,
            SortOrder = sortOrder,
            IsVisible = true
        };
        dbContext.DocCategories.Add(category);
        categories[slug] = category;
        return category;
    }

    private static void EnsureDocArticle(
        DocCategory category,
        string title,
        string slug,
        string summary,
        string contentMarkdown,
        string keywords,
        int sortOrder)
    {
        if (category.Articles.Any(x => string.Equals(x.Slug, slug, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        category.Articles.Add(new DocArticle
        {
            Title = title,
            Slug = slug,
            Summary = summary,
            ContentMarkdown = contentMarkdown,
            Keywords = keywords,
            Status = DocArticleStatus.Published,
            SortOrder = sortOrder,
            PublishedAtUtc = DateTimeOffset.UtcNow
        });
    }
}
