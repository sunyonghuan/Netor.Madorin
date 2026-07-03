using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Netor.Cortana.Platform.Entitys.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialNetorPlatformSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountLayers",
                columns: table => new
                {
                    ID = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AccountNo = table.Column<long>(type: "INTEGER", nullable: false, comment: "当前用户编号"),
                    ParentNo = table.Column<long>(type: "INTEGER", nullable: false, comment: "父级用户编号"),
                    Index = table.Column<long>(type: "INTEGER", nullable: false, comment: "当前层数"),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间戳")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountLayers", x => x.ID);
                },
                comment: "用户层级关系");

            migrationBuilder.CreateTable(
                name: "AccountRole",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    Order = table.Column<int>(type: "INTEGER", nullable: false, comment: "排序"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本"),
                    Name = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false, comment: "角色名称"),
                    Power = table.Column<int>(type: "INTEGER", nullable: false, comment: "角色权限(越大权利越大)"),
                    Enabel = table.Column<bool>(type: "INTEGER", nullable: false, comment: "启用状态"),
                    Display = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false, comment: "详细介绍")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountRole", x => x.ID);
                },
                comment: "平台个人用户角色");

            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本"),
                    No = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 1778139677631L, comment: "会员编号"),
                    LoginUserName = table.Column<string>(type: "TEXT", maxLength: 126, nullable: false, comment: "登录账号"),
                    LoginPassword = table.Column<string>(type: "TEXT", maxLength: 126, nullable: false, comment: "登录密码"),
                    SafePassword = table.Column<string>(type: "TEXT", maxLength: 126, nullable: false, comment: "安全密码"),
                    SingleLoginEnabled = table.Column<bool>(type: "INTEGER", nullable: false, comment: "单线登录"),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "手机号码"),
                    Email = table.Column<string>(type: "TEXT", maxLength: 126, nullable: false, comment: "电子邮箱"),
                    EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false, comment: "邮箱验证"),
                    PhoneConfirmed = table.Column<bool>(type: "INTEGER", nullable: false, comment: "手机验证"),
                    Image = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "头像128"),
                    NickName = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, comment: "用户昵称"),
                    RealName = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, comment: "真实名称"),
                    Sex = table.Column<byte>(type: "INTEGER", nullable: false, comment: "性别"),
                    LoginIP = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "登录IP"),
                    RegestorIP = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "注册IP"),
                    LoginTimes = table.Column<int>(type: "INTEGER", nullable: false, comment: "登录次数"),
                    LastLoginTime = table.Column<DateTime>(type: "TEXT", nullable: false, comment: "登录时间"),
                    AccountType = table.Column<byte>(type: "INTEGER", nullable: false, comment: "账户类型"),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false, comment: "会员状态"),
                    Refer = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true, comment: "推荐人ID")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.ID);
                },
                comment: "平台个人用户账户");

            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "名称"),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "标识"),
                    Description = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true, comment: "描述"),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false, comment: "排序"),
                    IsVisible = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否显示"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.ID);
                },
                comment: "平台资源分类");

            migrationBuilder.CreateTable(
                name: "ManagerRoles",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本"),
                    Name = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false, comment: "角色名称"),
                    Power = table.Column<int>(type: "INTEGER", nullable: false, comment: "角色权限(越大权利越大)"),
                    Enabel = table.Column<bool>(type: "INTEGER", nullable: false, comment: "启用状态"),
                    Display = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false, comment: "详细介绍")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagerRoles", x => x.ID);
                },
                comment: "平台管理员角色");

            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    Name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "名称"),
                    Display = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "说明"),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "标识"),
                    Value = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false, comment: "值"),
                    Type = table.Column<byte>(type: "INTEGER", nullable: false, comment: "类型"),
                    IsProtection = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否保护"),
                    Group = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "分组设置"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.ID);
                },
                comment: "系统设置");

            migrationBuilder.CreateTable(
                name: "AccountPropertys",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本"),
                    AccountID = table.Column<string>(type: "TEXT", nullable: true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "属性键"),
                    Value = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false, comment: "属性值"),
                    Display = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "描述"),
                    Type = table.Column<byte>(type: "INTEGER", nullable: false, comment: "类型"),
                    Group = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "分组设置")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountPropertys", x => x.ID);
                    table.ForeignKey(
                        name: "FK_AccountPropertys_Accounts_AccountID",
                        column: x => x.AccountID,
                        principalTable: "Accounts",
                        principalColumn: "ID");
                },
                comment: "平台个人用户扩展属性");

            migrationBuilder.CreateTable(
                name: "AccountRolePairs",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本"),
                    AccountID = table.Column<string>(type: "TEXT", nullable: true),
                    RoleID = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountRolePairs", x => x.ID);
                    table.ForeignKey(
                        name: "FK_AccountRolePairs_AccountRole_RoleID",
                        column: x => x.RoleID,
                        principalTable: "AccountRole",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_AccountRolePairs_Accounts_AccountID",
                        column: x => x.AccountID,
                        principalTable: "Accounts",
                        principalColumn: "ID");
                },
                comment: "平台个人用户角色关联");

            migrationBuilder.CreateTable(
                name: "AccountWallets",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本"),
                    AccountID = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, comment: "名称"),
                    Money = table.Column<decimal>(type: "TEXT", precision: 18, scale: 5, nullable: false, comment: "金额"),
                    Type = table.Column<byte>(type: "INTEGER", nullable: false, comment: "钱包类型"),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false, comment: "状态")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountWallets", x => x.ID);
                    table.ForeignKey(
                        name: "FK_AccountWallets_Accounts_AccountID",
                        column: x => x.AccountID,
                        principalTable: "Accounts",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "平台个人用户钱包");

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    AssetId = table.Column<string>(type: "TEXT", nullable: true, comment: "资产ID"),
                    PricingPlanId = table.Column<string>(type: "TEXT", nullable: true, comment: "定价方案ID"),
                    Content = table.Column<string>(type: "TEXT", nullable: false, comment: "详细"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本"),
                    Title = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "交易名称"),
                    No = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "交易单号"),
                    Money = table.Column<decimal>(type: "TEXT", precision: 18, scale: 5, nullable: false, comment: "金额"),
                    Numbers = table.Column<int>(type: "INTEGER", nullable: false, comment: "交易数量"),
                    PayTime = table.Column<DateTime>(type: "TEXT", nullable: true, comment: "支付时间"),
                    PayStatus = table.Column<byte>(type: "INTEGER", nullable: false, comment: "支付状态"),
                    PayMethod = table.Column<byte>(type: "INTEGER", nullable: false, comment: "支付方式"),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false, comment: "订单状态"),
                    AccountID = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.ID);
                    table.ForeignKey(
                        name: "FK_Orders_Accounts_AccountID",
                        column: x => x.AccountID,
                        principalTable: "Accounts",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "平台销售单");

            migrationBuilder.CreateTable(
                name: "Transactions",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    OrderId = table.Column<string>(type: "TEXT", nullable: true, comment: "关联销售单ID"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本"),
                    AccountID = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "交易名称"),
                    No = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "交易单号"),
                    ThirdNo = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "三方单号"),
                    OrderNo = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "订单编号"),
                    Money = table.Column<decimal>(type: "TEXT", precision: 18, scale: 5, nullable: false, comment: "金额"),
                    RealMoney = table.Column<decimal>(type: "TEXT", precision: 18, scale: 5, nullable: false, comment: "实际金额"),
                    Numbers = table.Column<int>(type: "INTEGER", nullable: false, comment: "交易数量"),
                    Content = table.Column<string>(type: "TEXT", maxLength: 2096, nullable: false, comment: "交易内容(2096)"),
                    PayTime = table.Column<DateTime>(type: "TEXT", nullable: true, comment: "支付时间"),
                    Type = table.Column<byte>(type: "INTEGER", nullable: false, comment: "记录类型"),
                    PayStatus = table.Column<byte>(type: "INTEGER", nullable: false, comment: "支付状态"),
                    PayMethod = table.Column<byte>(type: "INTEGER", nullable: false, comment: "支付方式")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => x.ID);
                    table.ForeignKey(
                        name: "FK_Transactions_Accounts_AccountID",
                        column: x => x.AccountID,
                        principalTable: "Accounts",
                        principalColumn: "ID");
                },
                comment: "平台交易记录");

            migrationBuilder.CreateTable(
                name: "Assets",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "名称"),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false, comment: "标识"),
                    DeveloperName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "开发者"),
                    ShortDescription = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "简短描述"),
                    Description = table.Column<string>(type: "TEXT", nullable: false, comment: "详细描述"),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false, comment: "标签"),
                    IconUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true, comment: "图标地址"),
                    CoverUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true, comment: "封面地址"),
                    Type = table.Column<int>(type: "INTEGER", nullable: false, comment: "类型"),
                    Status = table.Column<int>(type: "INTEGER", nullable: false, comment: "状态"),
                    CategoryId = table.Column<string>(type: "TEXT", nullable: true, comment: "分类ID"),
                    CurrentVersionId = table.Column<string>(type: "TEXT", nullable: true, comment: "当前版本ID"),
                    IsFeatured = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否推荐"),
                    DownloadCount = table.Column<int>(type: "INTEGER", nullable: false, comment: "下载次数"),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true, comment: "发布时间"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assets", x => x.ID);
                    table.ForeignKey(
                        name: "FK_Assets_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.SetNull);
                },
                comment: "平台资源");

            migrationBuilder.CreateTable(
                name: "Managers",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    RoleID = table.Column<string>(type: "TEXT", nullable: true),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本"),
                    No = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 1778139677634L, comment: "会员编号"),
                    LoginUserName = table.Column<string>(type: "TEXT", maxLength: 126, nullable: false, comment: "登录账号"),
                    LoginPassword = table.Column<string>(type: "TEXT", maxLength: 126, nullable: false, comment: "登录密码"),
                    SafePassword = table.Column<string>(type: "TEXT", maxLength: 126, nullable: false, comment: "安全密码"),
                    SingleLoginEnabled = table.Column<bool>(type: "INTEGER", nullable: false, comment: "单线登录"),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "手机号码"),
                    Email = table.Column<string>(type: "TEXT", maxLength: 126, nullable: false, comment: "电子邮箱"),
                    EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false, comment: "邮箱验证"),
                    PhoneConfirmed = table.Column<bool>(type: "INTEGER", nullable: false, comment: "手机验证"),
                    Image = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "头像128"),
                    NickName = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, comment: "用户昵称"),
                    RealName = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, comment: "真实名称"),
                    Sex = table.Column<byte>(type: "INTEGER", nullable: false, comment: "性别"),
                    LoginIP = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "登录IP"),
                    RegestorIP = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "注册IP"),
                    LoginTimes = table.Column<int>(type: "INTEGER", nullable: false, comment: "登录次数"),
                    LastLoginTime = table.Column<DateTime>(type: "TEXT", nullable: false, comment: "登录时间"),
                    AccountType = table.Column<byte>(type: "INTEGER", nullable: false, comment: "账户类型"),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false, comment: "会员状态"),
                    Refer = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true, comment: "推荐人ID")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Managers", x => x.ID);
                    table.ForeignKey(
                        name: "FK_Managers_ManagerRoles_RoleID",
                        column: x => x.RoleID,
                        principalTable: "ManagerRoles",
                        principalColumn: "ID");
                },
                comment: "平台管理员账户");

            migrationBuilder.CreateTable(
                name: "AssetVersions",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    AssetId = table.Column<string>(type: "TEXT", nullable: false),
                    VersionName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "版本号"),
                    ReleaseNotes = table.Column<string>(type: "TEXT", nullable: false, comment: "发布说明"),
                    ManifestJson = table.Column<string>(type: "TEXT", nullable: false, comment: "清单JSON"),
                    PackageHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "包哈希"),
                    PackageSize = table.Column<long>(type: "INTEGER", nullable: false, comment: "包大小"),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false, comment: "文件路径"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetVersions", x => x.ID);
                    table.ForeignKey(
                        name: "FK_AssetVersions_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "平台资源版本");

            migrationBuilder.CreateTable(
                name: "PricingPlans",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    AssetId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "名称"),
                    PlanType = table.Column<int>(type: "INTEGER", nullable: false, comment: "方案类型"),
                    Price = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false, comment: "价格"),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "货币"),
                    DurationDays = table.Column<int>(type: "INTEGER", nullable: false, comment: "有效天数"),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否启用"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PricingPlans", x => x.ID);
                    table.ForeignKey(
                        name: "FK_PricingPlans_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "平台资源定价方案");

            migrationBuilder.CreateTable(
                name: "ManagerProperty",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本"),
                    AccountID = table.Column<string>(type: "TEXT", nullable: true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "属性键"),
                    Value = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false, comment: "属性值"),
                    Display = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "描述"),
                    Type = table.Column<byte>(type: "INTEGER", nullable: false, comment: "类型"),
                    Group = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "分组设置")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagerProperty", x => x.ID);
                    table.ForeignKey(
                        name: "FK_ManagerProperty_Managers_AccountID",
                        column: x => x.AccountID,
                        principalTable: "Managers",
                        principalColumn: "ID");
                },
                comment: "平台管理员扩展属性");

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    AssetId = table.Column<string>(type: "TEXT", nullable: false),
                    PricingPlanId = table.Column<string>(type: "TEXT", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CanceledAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.ID);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Subscriptions_PricingPlans_PricingPlanId",
                        column: x => x.PricingPlanId,
                        principalTable: "PricingPlans",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "平台订阅记录");

            migrationBuilder.CreateTable(
                name: "DownloadRecords",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    AssetId = table.Column<string>(type: "TEXT", nullable: false),
                    AssetVersionId = table.Column<string>(type: "TEXT", nullable: false),
                    SubscriptionId = table.Column<string>(type: "TEXT", nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadRecords", x => x.ID);
                    table.ForeignKey(
                        name: "FK_DownloadRecords_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DownloadRecords_AssetVersions_AssetVersionId",
                        column: x => x.AssetVersionId,
                        principalTable: "AssetVersions",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DownloadRecords_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DownloadRecords_Subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "Subscriptions",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.SetNull);
                },
                comment: "平台下载记录");

            migrationBuilder.CreateIndex(
                name: "IX_AccountLayers_AccountNo",
                table: "AccountLayers",
                column: "AccountNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountLayers_Index_AccountNo_ParentNo",
                table: "AccountLayers",
                columns: new[] { "Index", "AccountNo", "ParentNo" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountPropertys_AccountID",
                table: "AccountPropertys",
                column: "AccountID");

            migrationBuilder.CreateIndex(
                name: "IX_AccountRolePairs_AccountID",
                table: "AccountRolePairs",
                column: "AccountID");

            migrationBuilder.CreateIndex(
                name: "IX_AccountRolePairs_RoleID",
                table: "AccountRolePairs",
                column: "RoleID");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Email",
                table: "Accounts",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_No",
                table: "Accounts",
                column: "No",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Phone",
                table: "Accounts",
                column: "Phone");

            migrationBuilder.CreateIndex(
                name: "IX_AccountWallets_AccountID",
                table: "AccountWallets",
                column: "AccountID");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_CategoryId",
                table: "Assets",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_Slug",
                table: "Assets",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assets_Type_Status",
                table: "Assets",
                columns: new[] { "Type", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetVersions_AssetId_VersionName",
                table: "AssetVersions",
                columns: new[] { "AssetId", "VersionName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Categories_Slug",
                table: "Categories",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadRecords_AccountId",
                table: "DownloadRecords",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadRecords_AssetId",
                table: "DownloadRecords",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadRecords_AssetVersionId",
                table: "DownloadRecords",
                column: "AssetVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadRecords_SubscriptionId",
                table: "DownloadRecords",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_ManagerProperty_AccountID",
                table: "ManagerProperty",
                column: "AccountID");

            migrationBuilder.CreateIndex(
                name: "IX_Managers_Email",
                table: "Managers",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Managers_No",
                table: "Managers",
                column: "No",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Managers_Phone",
                table: "Managers",
                column: "Phone");

            migrationBuilder.CreateIndex(
                name: "IX_Managers_RoleID",
                table: "Managers",
                column: "RoleID");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_AccountID",
                table: "Orders",
                column: "AccountID");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_No",
                table: "Orders",
                column: "No");

            migrationBuilder.CreateIndex(
                name: "IX_PricingPlans_AssetId_PlanType",
                table: "PricingPlans",
                columns: new[] { "AssetId", "PlanType" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_AccountId_AssetId_Status",
                table: "Subscriptions",
                columns: new[] { "AccountId", "AssetId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_AccountId_Status",
                table: "Subscriptions",
                columns: new[] { "AccountId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_AssetId",
                table: "Subscriptions",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_OrderId",
                table: "Subscriptions",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_PricingPlanId",
                table: "Subscriptions",
                column: "PricingPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_SystemSettings_Key",
                table: "SystemSettings",
                column: "Key");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_AccountID",
                table: "Transactions",
                column: "AccountID");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_No_OrderNo_ThirdNo_Type",
                table: "Transactions",
                columns: new[] { "No", "OrderNo", "ThirdNo", "Type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountLayers");

            migrationBuilder.DropTable(
                name: "AccountPropertys");

            migrationBuilder.DropTable(
                name: "AccountRolePairs");

            migrationBuilder.DropTable(
                name: "AccountWallets");

            migrationBuilder.DropTable(
                name: "DownloadRecords");

            migrationBuilder.DropTable(
                name: "ManagerProperty");

            migrationBuilder.DropTable(
                name: "SystemSettings");

            migrationBuilder.DropTable(
                name: "Transactions");

            migrationBuilder.DropTable(
                name: "AccountRole");

            migrationBuilder.DropTable(
                name: "AssetVersions");

            migrationBuilder.DropTable(
                name: "Subscriptions");

            migrationBuilder.DropTable(
                name: "Managers");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "PricingPlans");

            migrationBuilder.DropTable(
                name: "ManagerRoles");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "Assets");

            migrationBuilder.DropTable(
                name: "Categories");
        }
    }
}
