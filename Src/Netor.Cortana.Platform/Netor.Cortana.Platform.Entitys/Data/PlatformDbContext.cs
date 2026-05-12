using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Accounts;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Cortana.Platform.Entitys.Tables.Downloads;
using Netor.Cortana.Platform.Entitys.Tables.Managers;
using Netor.Cortana.Platform.Entitys.Tables.Orders;
using Netor.Cortana.Platform.Entitys.Tables.Subscriptions;
using Netor.Database.SqlLiteDbContextAbstractions;

namespace Netor.Cortana.Platform.Entitys.Data;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options)
    : SqlLiteDbContextBase<Account, AccountRole, Manager, ManagerRole, AccountProperty, AccountRolePair, Order, Transaction, AccountWallet>(options)
{
    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<AssetVersion> AssetVersions => Set<AssetVersion>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<PricingPlan> PricingPlans => Set<PricingPlan>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<DownloadRecord> DownloadRecords => Set<DownloadRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Asset>(builder =>
        {
            builder.HasIndex(x => x.Slug).IsUnique();
            builder.HasIndex(x => new { x.Type, x.Status });
            builder.HasIndex(x => x.OwnerAccountId);
            builder.HasOne(x => x.OwnerAccount)
                .WithMany()
                .HasForeignKey(x => x.OwnerAccountId)
                .OnDelete(DeleteBehavior.SetNull);
            builder.HasOne(x => x.Category)
                .WithMany(x => x.Assets)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AssetVersion>(builder =>
        {
            builder.HasIndex(x => new { x.AssetId, x.VersionName }).IsUnique();
            builder.HasOne(x => x.Asset)
                .WithMany(x => x.Versions)
                .HasForeignKey(x => x.AssetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Category>(builder =>
        {
            builder.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<PricingPlan>(builder =>
        {
            builder.HasIndex(x => new { x.AssetId, x.PlanType });
            builder.Property(x => x.Price).HasPrecision(18, 2);
            builder.HasOne(x => x.Asset)
                .WithMany(x => x.PricingPlans)
                .HasForeignKey(x => x.AssetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Subscription>(builder =>
        {
            builder.HasIndex(x => new { x.AccountId, x.Status });
            builder.HasIndex(x => new { x.AccountId, x.AssetId, x.Status });
            builder.HasOne(x => x.Account)
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Asset)
                .WithMany()
                .HasForeignKey(x => x.AssetId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.PricingPlan)
                .WithMany()
                .HasForeignKey(x => x.PricingPlanId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(x => x.Order)
                .WithMany()
                .HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DownloadRecord>(builder =>
        {
            builder.HasIndex(x => x.AccountId);
            builder.HasIndex(x => x.AssetId);
            builder.HasOne(x => x.Account)
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Asset)
                .WithMany()
                .HasForeignKey(x => x.AssetId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.AssetVersion)
                .WithMany()
                .HasForeignKey(x => x.AssetVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(x => x.Subscription)
                .WithMany()
                .HasForeignKey(x => x.SubscriptionId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }

    protected override void OnDataGenerate()
    {
        var accountRole = AccountRole.Add(new AccountRole
        {
            Name = "个人用户"
        }).Entity;

        var account = Accounts.Add(new Account
        {
            No = 900000000001,
            LoginUserName = "demo@netor.me",
            Email = "demo@netor.me",
            Phone = "13800138000",
            LoginPassword = "123456".MD5Encrypt(),
            SafePassword = "888888".MD5Encrypt()
        }).Entity;

        var officialAccount = Accounts.Add(new Account
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

        AccountRolePairs.Add(new AccountRolePair
        {
            Account = account,
            Role = accountRole
        });

        AccountWallets.Add(new AccountWallet
        {
            Account = account
        });

        AccountWallets.Add(new AccountWallet
        {
            Account = officialAccount
        });

        var managerRole = ManagerRoles.Add(new ManagerRole
        {
            Name = "超级管理员"
        }).Entity;

        var manager = Managers.Add(new Manager
        {
            Role = managerRole,
            LoginUserName = "admin",
            Phone = "13800138001",
            LoginPassword = "123456".MD5Encrypt(),
            SafePassword = "888888".MD5Encrypt()
        }).Entity;

        Set<ManagerProperty>().AddRange(
            new ManagerProperty
            {
                Account = manager,
                Key = "platform.admin.theme",
                Value = "layui",
                Display = "后台主题"
            },
            new ManagerProperty
            {
                Account = manager,
                Key = "platform.admin.initialized",
                Value = "true",
                Display = "后台是否已初始化"
            });

        SeedCategories();
        SeedAssets(officialAccount);
        SeedSettings();
    }

    private void SeedCategories()
    {
        Categories.AddRange(
            new Category { Name = "插件", Slug = "plugins", Description = "扩展 Cortana 能力的本地插件。", SortOrder = 10 },
            new Category { Name = "技能", Slug = "skills", Description = "面向对话和任务编排的可复用技能。", SortOrder = 20 },
            new Category { Name = "智能体", Slug = "agents", Description = "封装角色、工具和流程的智能体。", SortOrder = 30 },
            new Category { Name = "解决方案", Slug = "solutions", Description = "插件、技能和智能体组合打包方案。", SortOrder = 40 });
    }

    private void SeedAssets(Account officialAccount)
    {
        var now = new DateTimeOffset(2026, 5, 7, 0, 0, 0, TimeSpan.Zero);

        AddAsset(officialAccount, AssetType.Plugin, "Google 搜索插件", "google-search-plugin", "提供 Google 搜索能力的基础插件。", "Data/packages/plugins/google-search/1.0.0/google-search.zip", now.AddDays(-8));
        AddAsset(officialAccount, AssetType.Skill, "记忆整理技能", "memory-organizer-skill", "整理对话记忆和知识片段。", "Data/packages/skills/memory-organizer/1.0.0/memory-organizer.zip", now.AddDays(-6));
        AddAsset(officialAccount, AssetType.Agent, "工作助手智能体", "work-assistant-agent", "面向日常办公和任务跟进的智能体。", "Data/packages/agents/work-assistant/1.0.0/work-assistant.zip", now.AddDays(-4));
        AddAsset(officialAccount, AssetType.Solution, "入门插件解决方案包", "starter-plugin-solution", "适合个人用户快速体验平台生态。", "Data/packages/solutions/starter-plugin-solution/1.0.0/starter-plugin-solution.zip", now.AddDays(-2));
    }

    private void AddAsset(Account officialAccount, AssetType type, string name, string slug, string shortDescription, string filePath, DateTimeOffset publishedAtUtc)
    {
        Assets.Add(new Asset
        {
            Type = type,
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
        });
    }

    private void SeedSettings()
    {
        SystemSettings.AddRange(
            new SystemSetting { Key = "platform.name", Value = "Cortana 运营平台", Name = "平台名称", Display = "平台名称", Type = NetorDataType.Text, Group = "平台设置" },
            new SystemSetting { Key = "platform.storage.root", Value = "Data", Name = "本地存储根目录", Display = "本地存储根目录", Type = NetorDataType.Text, Group = "平台设置" },
            new SystemSetting { Key = "platform.download.anonymous", Value = "false", Name = "是否允许匿名下载", Display = "是否允许匿名下载", Type = NetorDataType.Boolean, Group = "下载设置" },
            new SystemSetting { Key = "finance.currency.unit", Value = "CNY", Name = "货币单位", Display = "货币单位", Type = NetorDataType.Text, Group = "财务设置" });
    }

    public override Task<bool> IsSeededAsync(CancellationToken cancellationToken)
    {
        return SystemSettings.AnyAsync(x => x.Key == "platform.name", cancellationToken);
    }

    protected override void OnAfterDataCreate()
    {
    }
}
