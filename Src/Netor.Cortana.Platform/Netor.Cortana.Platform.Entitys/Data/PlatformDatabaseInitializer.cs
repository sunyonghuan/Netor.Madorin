using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Accounts;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Cortana.Platform.Entitys.Tables.Downloads;
using Netor.Cortana.Platform.Entitys.Tables.Subscriptions;

namespace Netor.Cortana.Platform.Entitys.Data;

public static class PlatformDatabaseInitializer
{
    public static async Task InitializeAsync(this IHost host, CancellationToken cancellationToken = default)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        await dbContext.Database.MigrateAsync(cancellationToken);
        await dbContext.EnsureSeededAsync(cancellationToken);
    }

    public static async Task EnsureSeededAsync(this PlatformDbContext dbContext, CancellationToken cancellationToken = default)
    {
        var officialAccount = await EnsureOfficialAccountAsync(dbContext, cancellationToken);
        var hasManager = await dbContext.Managers.AnyAsync(x => x.LoginUserName == "admin", cancellationToken);
        var hasSettings = await dbContext.SystemSettings.AnyAsync(x => x.Key == "platform.name", cancellationToken);
        var hasCategories = await dbContext.Categories.AnyAsync(cancellationToken);
        var hasAssets = await dbContext.Assets.AnyAsync(cancellationToken);
        var hasSubscriptions = await dbContext.Subscriptions.AnyAsync(cancellationToken);
        var hasPaidAsset = await dbContext.Assets.AnyAsync(x => x.Slug == "pro-workflow-agent", cancellationToken);
        var hasAssetsWithoutOwner = await dbContext.Assets.AnyAsync(x => x.OwnerAccountId == null, cancellationToken);

        if (hasManager && hasSettings && hasCategories && hasAssets && hasSubscriptions && hasPaidAsset && !hasAssetsWithoutOwner)
        {
            return;
        }

        if (!hasManager)
        {
            var managerRole = await dbContext.ManagerRoles.FirstOrDefaultAsync(x => x.Name == "超级管理员", cancellationToken);
            managerRole ??= dbContext.ManagerRoles.Add(new Tables.Managers.ManagerRole
            {
                Name = "超级管理员"
            }).Entity;

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

        if (!hasSettings)
        {
            dbContext.SystemSettings.AddRange(
                new SystemSetting { Key = "platform.name", Value = "Cortana 运营平台", Name = "平台名称", Display = "平台名称", Type = NetorDataType.Text, Group = "平台设置" },
                new SystemSetting { Key = "platform.storage.root", Value = "Data", Name = "本地存储根目录", Display = "本地存储根目录", Type = NetorDataType.Text, Group = "平台设置" },
                new SystemSetting { Key = "platform.download.anonymous", Value = "false", Name = "是否允许匿名下载", Display = "是否允许匿名下载", Type = NetorDataType.Boolean, Group = "下载设置" },
                new SystemSetting { Key = "finance.currency.unit", Value = "CNY", Name = "货币单位", Display = "货币单位", Type = NetorDataType.Text, Group = "财务设置" });
        }

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

        await dbContext.SaveChangesAsync(cancellationToken);
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
}