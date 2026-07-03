using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Accounts;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Cortana.Platform.Entitys.Tables.Creators;
using Netor.Cortana.Platform.Entitys.Tables.Docs;
using Netor.Cortana.Platform.Entitys.Tables.Downloads;
using Netor.Cortana.Platform.Entitys.Tables.Managers;
using Netor.Cortana.Platform.Entitys.Tables.Orders;
using Netor.Cortana.Platform.Entitys.Tables.Reviews;
using Netor.Cortana.Platform.Entitys.Tables.Settlements;
using Netor.Cortana.Platform.Entitys.Tables.Subscriptions;
using Netor.Database.SqlServerDbContextAbstractions;

namespace Netor.Cortana.Platform.Entitys.Data;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options)
    : SqlServerDbContextBase<Account, AccountRole, Manager, ManagerRole, AccountProperty, AccountRolePair, Order, Transaction, AccountWallet>(options)
{
    private const int SuperAdminRolePower = 100;

    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<AssetVersion> AssetVersions => Set<AssetVersion>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<PricingPlan> PricingPlans => Set<PricingPlan>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<DownloadRecord> DownloadRecords => Set<DownloadRecord>();

    public DbSet<CreatorProfile> CreatorProfiles => Set<CreatorProfile>();

    public DbSet<AssetReview> AssetReviews => Set<AssetReview>();

    public DbSet<CreatorSettlement> CreatorSettlements => Set<CreatorSettlement>();

    public DbSet<DocCategory> DocCategories => Set<DocCategory>();

    public DbSet<DocArticle> DocArticles => Set<DocArticle>();

    public DbSet<ManagerMcpToken> ManagerMcpTokens => Set<ManagerMcpToken>();

    public DbSet<ManagerAuditLog> ManagerAuditLogs => Set<ManagerAuditLog>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

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
            builder.Property(x => x.StorageProvider).HasDefaultValue("Local");
            builder.HasOne(x => x.Asset)
                .WithMany(x => x.Versions)
                .HasForeignKey(x => x.AssetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Category>(builder =>
        {
            builder.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<DocCategory>(builder =>
        {
            builder.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<DocArticle>(builder =>
        {
            builder.HasIndex(x => new { x.CategoryId, x.Slug }).IsUnique();
            builder.HasIndex(x => new { x.Status, x.SortOrder });
            builder.HasOne(x => x.Category)
                .WithMany(x => x.Articles)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Account>(builder =>
        {
            builder.Property(x => x.No).ValueGeneratedNever();
        });

        modelBuilder.Entity<Manager>(builder =>
        {
            builder.Property(x => x.No).ValueGeneratedNever();
        });

        modelBuilder.Entity<ManagerMcpToken>(builder =>
        {
            builder.HasIndex(x => x.TokenHash).IsUnique();
            builder.HasIndex(x => x.ManagerId);
            builder.Property(x => x.TokenHash).HasMaxLength(64);
            builder.Property(x => x.TokenPrefix).HasMaxLength(12);
            builder.Property(x => x.Note).HasMaxLength(64);
            builder.Property(x => x.LastUsedIp).HasMaxLength(64);
            builder.Property(x => x.LastUsedUa).HasMaxLength(256);
            builder.HasOne(x => x.Manager)
                .WithMany()
                .HasForeignKey(x => x.ManagerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ManagerAuditLog>(builder =>
        {
            builder.HasIndex(x => new { x.ManagerId, x.CreatedUtc });
            builder.HasIndex(x => x.ToolName);
            builder.Property(x => x.ToolName).HasMaxLength(128);
            builder.Property(x => x.SourceName).HasColumnName("Source").HasMaxLength(16);
            builder.Property(x => x.Ip).HasMaxLength(64);
            builder.Property(x => x.Ua).HasMaxLength(256);
            builder.Property(x => x.ErrorCode).HasMaxLength(64);
            builder.HasOne(x => x.Manager)
                .WithMany()
                .HasForeignKey(x => x.ManagerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IdempotencyRecord>(builder =>
        {
            builder.HasIndex(x => x.Hash).IsUnique();
            builder.HasIndex(x => x.CreatedUtc);
            builder.HasIndex(x => new { x.ManagerId, x.ToolName });
            builder.Property(x => x.Hash).HasMaxLength(64);
            builder.Property(x => x.ToolName).HasMaxLength(128);
            builder.Property(x => x.ManagerId).HasMaxLength(32);
            builder.Property(x => x.RequestId).HasMaxLength(128);
        });

        modelBuilder.Entity<Transaction>(builder =>
        {
            builder.HasIndex(x => x.ThirdNo);
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
                .OnDelete(DeleteBehavior.Restrict);
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
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CreatorProfile>(builder =>
        {
            builder.HasIndex(x => x.AccountId).IsUnique();
            builder.HasIndex(x => x.Status);
            builder.HasOne(x => x.Account)
                .WithMany()
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AssetReview>(builder =>
        {
            builder.HasIndex(x => new { x.AssetId, x.Status });
            builder.HasIndex(x => new { x.SubmitterAccountId, x.Status });
            builder.HasOne(x => x.Asset)
                .WithMany()
                .HasForeignKey(x => x.AssetId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.AssetVersion)
                .WithMany()
                .HasForeignKey(x => x.AssetVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(x => x.SubmitterAccount)
                .WithMany()
                .HasForeignKey(x => x.SubmitterAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CreatorSettlement>(builder =>
        {
            builder.HasIndex(x => new { x.CreatorAccountId, x.Status });
            builder.HasIndex(x => x.OrderId).IsUnique();
            builder.Property(x => x.GrossAmount).HasPrecision(18, 2);
            builder.Property(x => x.PlatformFeeAmount).HasPrecision(18, 2);
            builder.Property(x => x.NetAmount).HasPrecision(18, 2);
            builder.HasOne(x => x.CreatorAccount)
                .WithMany()
                .HasForeignKey(x => x.CreatorAccountId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Asset)
                .WithMany()
                .HasForeignKey(x => x.AssetId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Order)
                .WithMany()
                .HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        foreach (var foreignKey in modelBuilder.Model.GetEntityTypes().SelectMany(entityType => entityType.GetForeignKeys()))
        {
            if (foreignKey.DeleteBehavior is DeleteBehavior.Cascade or DeleteBehavior.SetNull)
            {
                foreignKey.DeleteBehavior = DeleteBehavior.Restrict;
            }
        }
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
            Name = "超级管理员",
            Power = SuperAdminRolePower
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
        SeedDocs();
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
            new SystemSetting { Key = "finance.currency.unit", Value = "CNY", Name = "货币单位", Display = "货币单位", Type = NetorDataType.Text, Group = "财务设置" });
    }

    private void SeedDocs()
    {
        var quickStart = new DocCategory { Name = "快速开始", Slug = "getting-started", Description = "安装、登录和完成第一次工作流。", SortOrder = 10 };
        var workflows = new DocCategory { Name = "工作流", Slug = "workflows", Description = "会议模式、任务拆解和交付物沉淀。", SortOrder = 20 };
        var resources = new DocCategory { Name = "资源中心", Slug = "resources", Description = "插件、技能、智能体和解决方案的订阅与使用。", SortOrder = 30 };

        AddDocArticle(quickStart, "下载并登录", "download-and-login", "安装桌面客户端并登录账号。", """
            # 下载并登录

            安装 Madorin 桌面客户端后，使用平台账号登录。登录状态用于资源订阅、下载授权和跨设备资源同步。

            ## 操作步骤

            1. 进入官网下载页面。
            2. 下载适合当前系统的安装包。
            3. 完成安装后启动客户端。
            4. 使用账号登录并确认资源中心可访问。
            """, "安装,登录,客户端", 10);

        AddDocArticle(quickStart, "提出目标", "describe-goal", "用自然语言描述你想完成的业务目标。", """
            # 提出目标

            直接描述要完成的业务目标，例如产品上线、客户维护、营销内容发布或软件开发。

            建议同时说明背景、期望结果、限制条件和已有材料。目标越明确，后续拆分出来的步骤越容易执行。
            """, "目标,任务,提示词", 20);

        AddDocArticle(workflows, "使用会议模式", "meeting-mode", "让多个专家智能体从不同角度参与讨论。", """
            # 使用会议模式

            会议模式适合需要多角色协作判断的任务。你可以让产品、研发、运营、法务等角色从各自角度补充观点。

            会议结束后，应把结论沉淀为明确的执行计划、风险清单和下一步动作。
            """, "会议模式,智能体,协作", 10);

        AddDocArticle(workflows, "进入工作流", "run-workflow", "把讨论结果拆成可持续推进的工作流。", """
            # 进入工作流

            工作流用于承接会议结论，把方案拆成步骤，并持续推进文档、脚本、检查项和交付动作。

            每个步骤都应该有明确目标、输入材料、输出物和完成标准。
            """, "工作流,计划,执行", 20);

        AddDocArticle(resources, "加载资源中心能力", "load-resources", "订阅并加载插件、技能、智能体或解决方案。", """
            # 加载资源中心能力

            订阅插件、技能、智能体或解决方案后，Madorin 可以按需加载能力，并在本地保留缓存。

            资源中心后续会提供更完整的版本说明、安装指南和排障文档。
            """, "资源中心,插件,技能,智能体", 10);

        DocCategories.AddRange(quickStart, workflows, resources);
    }

    private static void AddDocArticle(
        DocCategory category,
        string title,
        string slug,
        string summary,
        string contentMarkdown,
        string keywords,
        int sortOrder)
    {
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

    public override Task<bool> IsSeededAsync(CancellationToken cancellationToken)
    {
        return SystemSettings.AnyAsync(x => x.Key == "platform.name", cancellationToken);
    }

    protected override void OnAfterDataCreate()
    {
    }
}
