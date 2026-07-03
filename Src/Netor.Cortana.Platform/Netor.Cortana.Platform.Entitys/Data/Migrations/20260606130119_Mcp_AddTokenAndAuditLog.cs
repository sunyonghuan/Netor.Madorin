using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Netor.Cortana.Platform.Entitys.Data.Migrations
{
    /// <inheritdoc />
    public partial class Mcp_AddTokenAndAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccountWallets_Accounts_AccountID",
                table: "AccountWallets");

            migrationBuilder.DropForeignKey(
                name: "FK_AssetReviews_Accounts_SubmitterAccountId",
                table: "AssetReviews");

            migrationBuilder.DropForeignKey(
                name: "FK_AssetReviews_AssetVersions_AssetVersionId",
                table: "AssetReviews");

            migrationBuilder.DropForeignKey(
                name: "FK_AssetReviews_Assets_AssetId",
                table: "AssetReviews");

            migrationBuilder.DropForeignKey(
                name: "FK_Assets_Accounts_OwnerAccountId",
                table: "Assets");

            migrationBuilder.DropForeignKey(
                name: "FK_Assets_Categories_CategoryId",
                table: "Assets");

            migrationBuilder.DropForeignKey(
                name: "FK_AssetVersions_Assets_AssetId",
                table: "AssetVersions");

            migrationBuilder.DropForeignKey(
                name: "FK_CreatorProfiles_Accounts_AccountId",
                table: "CreatorProfiles");

            migrationBuilder.DropForeignKey(
                name: "FK_CreatorSettlements_Accounts_CreatorAccountId",
                table: "CreatorSettlements");

            migrationBuilder.DropForeignKey(
                name: "FK_CreatorSettlements_Assets_AssetId",
                table: "CreatorSettlements");

            migrationBuilder.DropForeignKey(
                name: "FK_CreatorSettlements_Orders_OrderId",
                table: "CreatorSettlements");

            migrationBuilder.DropForeignKey(
                name: "FK_DownloadRecords_Accounts_AccountId",
                table: "DownloadRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_DownloadRecords_Assets_AssetId",
                table: "DownloadRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_DownloadRecords_Subscriptions_SubscriptionId",
                table: "DownloadRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Accounts_AccountID",
                table: "Orders");

            migrationBuilder.DropForeignKey(
                name: "FK_PricingPlans_Assets_AssetId",
                table: "PricingPlans");

            migrationBuilder.DropForeignKey(
                name: "FK_Subscriptions_Accounts_AccountId",
                table: "Subscriptions");

            migrationBuilder.DropForeignKey(
                name: "FK_Subscriptions_Assets_AssetId",
                table: "Subscriptions");

            migrationBuilder.DropForeignKey(
                name: "FK_Subscriptions_Orders_OrderId",
                table: "Subscriptions");

            migrationBuilder.AlterColumn<long>(
                name: "No",
                table: "Managers",
                type: "INTEGER",
                nullable: false,
                comment: "会员编号",
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldDefaultValue: 1780475344793L,
                oldComment: "会员编号");

            migrationBuilder.AlterColumn<long>(
                name: "No",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                comment: "会员编号",
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldDefaultValue: 1780475344790L,
                oldComment: "会员编号");

            migrationBuilder.CreateTable(
                name: "ManagerAuditLogs",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    ManagerId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "管理员ID"),
                    TokenId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "MCP令牌ID"),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "工具或动作名称"),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "来源"),
                    Ip = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true, comment: "IP地址"),
                    Ua = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true, comment: "User-Agent"),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否成功"),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true, comment: "错误码"),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false, comment: "耗时毫秒"),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false, comment: "创建时间"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagerAuditLogs", x => x.ID);
                    table.ForeignKey(
                        name: "FK_ManagerAuditLogs_Managers_ManagerId",
                        column: x => x.ManagerId,
                        principalTable: "Managers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "平台管理员操作审计日志");

            migrationBuilder.CreateTable(
                name: "ManagerMcpTokens",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    ManagerId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "管理员ID"),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "令牌哈希"),
                    TokenPrefix = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false, comment: "令牌前缀"),
                    Note = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "备注"),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否启用"),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false, comment: "创建时间"),
                    LastUsedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true, comment: "最近使用时间"),
                    LastUsedIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true, comment: "最近使用IP"),
                    LastUsedUa = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true, comment: "最近使用UA"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagerMcpTokens", x => x.ID);
                    table.ForeignKey(
                        name: "FK_ManagerMcpTokens_Managers_ManagerId",
                        column: x => x.ManagerId,
                        principalTable: "Managers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "平台管理员 MCP 访问令牌");

            migrationBuilder.CreateIndex(
                name: "IX_ManagerAuditLogs_ManagerId_CreatedUtc",
                table: "ManagerAuditLogs",
                columns: new[] { "ManagerId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ManagerAuditLogs_ToolName",
                table: "ManagerAuditLogs",
                column: "ToolName");

            migrationBuilder.CreateIndex(
                name: "IX_ManagerMcpTokens_ManagerId",
                table: "ManagerMcpTokens",
                column: "ManagerId");

            migrationBuilder.CreateIndex(
                name: "IX_ManagerMcpTokens_TokenHash",
                table: "ManagerMcpTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AccountWallets_Accounts_AccountID",
                table: "AccountWallets",
                column: "AccountID",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AssetReviews_Accounts_SubmitterAccountId",
                table: "AssetReviews",
                column: "SubmitterAccountId",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AssetReviews_AssetVersions_AssetVersionId",
                table: "AssetReviews",
                column: "AssetVersionId",
                principalTable: "AssetVersions",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AssetReviews_Assets_AssetId",
                table: "AssetReviews",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Assets_Accounts_OwnerAccountId",
                table: "Assets",
                column: "OwnerAccountId",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Assets_Categories_CategoryId",
                table: "Assets",
                column: "CategoryId",
                principalTable: "Categories",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AssetVersions_Assets_AssetId",
                table: "AssetVersions",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CreatorProfiles_Accounts_AccountId",
                table: "CreatorProfiles",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CreatorSettlements_Accounts_CreatorAccountId",
                table: "CreatorSettlements",
                column: "CreatorAccountId",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CreatorSettlements_Assets_AssetId",
                table: "CreatorSettlements",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CreatorSettlements_Orders_OrderId",
                table: "CreatorSettlements",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DownloadRecords_Accounts_AccountId",
                table: "DownloadRecords",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DownloadRecords_Assets_AssetId",
                table: "DownloadRecords",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DownloadRecords_Subscriptions_SubscriptionId",
                table: "DownloadRecords",
                column: "SubscriptionId",
                principalTable: "Subscriptions",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Accounts_AccountID",
                table: "Orders",
                column: "AccountID",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PricingPlans_Assets_AssetId",
                table: "PricingPlans",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Subscriptions_Accounts_AccountId",
                table: "Subscriptions",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Subscriptions_Assets_AssetId",
                table: "Subscriptions",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Subscriptions_Orders_OrderId",
                table: "Subscriptions",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "ID",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AccountWallets_Accounts_AccountID",
                table: "AccountWallets");

            migrationBuilder.DropForeignKey(
                name: "FK_AssetReviews_Accounts_SubmitterAccountId",
                table: "AssetReviews");

            migrationBuilder.DropForeignKey(
                name: "FK_AssetReviews_AssetVersions_AssetVersionId",
                table: "AssetReviews");

            migrationBuilder.DropForeignKey(
                name: "FK_AssetReviews_Assets_AssetId",
                table: "AssetReviews");

            migrationBuilder.DropForeignKey(
                name: "FK_Assets_Accounts_OwnerAccountId",
                table: "Assets");

            migrationBuilder.DropForeignKey(
                name: "FK_Assets_Categories_CategoryId",
                table: "Assets");

            migrationBuilder.DropForeignKey(
                name: "FK_AssetVersions_Assets_AssetId",
                table: "AssetVersions");

            migrationBuilder.DropForeignKey(
                name: "FK_CreatorProfiles_Accounts_AccountId",
                table: "CreatorProfiles");

            migrationBuilder.DropForeignKey(
                name: "FK_CreatorSettlements_Accounts_CreatorAccountId",
                table: "CreatorSettlements");

            migrationBuilder.DropForeignKey(
                name: "FK_CreatorSettlements_Assets_AssetId",
                table: "CreatorSettlements");

            migrationBuilder.DropForeignKey(
                name: "FK_CreatorSettlements_Orders_OrderId",
                table: "CreatorSettlements");

            migrationBuilder.DropForeignKey(
                name: "FK_DownloadRecords_Accounts_AccountId",
                table: "DownloadRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_DownloadRecords_Assets_AssetId",
                table: "DownloadRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_DownloadRecords_Subscriptions_SubscriptionId",
                table: "DownloadRecords");

            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Accounts_AccountID",
                table: "Orders");

            migrationBuilder.DropForeignKey(
                name: "FK_PricingPlans_Assets_AssetId",
                table: "PricingPlans");

            migrationBuilder.DropForeignKey(
                name: "FK_Subscriptions_Accounts_AccountId",
                table: "Subscriptions");

            migrationBuilder.DropForeignKey(
                name: "FK_Subscriptions_Assets_AssetId",
                table: "Subscriptions");

            migrationBuilder.DropForeignKey(
                name: "FK_Subscriptions_Orders_OrderId",
                table: "Subscriptions");

            migrationBuilder.DropTable(
                name: "ManagerAuditLogs");

            migrationBuilder.DropTable(
                name: "ManagerMcpTokens");

            migrationBuilder.AlterColumn<long>(
                name: "No",
                table: "Managers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1780475344793L,
                comment: "会员编号",
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldComment: "会员编号");

            migrationBuilder.AlterColumn<long>(
                name: "No",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1780475344790L,
                comment: "会员编号",
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldComment: "会员编号");

            migrationBuilder.AddForeignKey(
                name: "FK_AccountWallets_Accounts_AccountID",
                table: "AccountWallets",
                column: "AccountID",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AssetReviews_Accounts_SubmitterAccountId",
                table: "AssetReviews",
                column: "SubmitterAccountId",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AssetReviews_AssetVersions_AssetVersionId",
                table: "AssetReviews",
                column: "AssetVersionId",
                principalTable: "AssetVersions",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AssetReviews_Assets_AssetId",
                table: "AssetReviews",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Assets_Accounts_OwnerAccountId",
                table: "Assets",
                column: "OwnerAccountId",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Assets_Categories_CategoryId",
                table: "Assets",
                column: "CategoryId",
                principalTable: "Categories",
                principalColumn: "ID",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AssetVersions_Assets_AssetId",
                table: "AssetVersions",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_CreatorProfiles_Accounts_AccountId",
                table: "CreatorProfiles",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_CreatorSettlements_Accounts_CreatorAccountId",
                table: "CreatorSettlements",
                column: "CreatorAccountId",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_CreatorSettlements_Assets_AssetId",
                table: "CreatorSettlements",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_CreatorSettlements_Orders_OrderId",
                table: "CreatorSettlements",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DownloadRecords_Accounts_AccountId",
                table: "DownloadRecords",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DownloadRecords_Assets_AssetId",
                table: "DownloadRecords",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DownloadRecords_Subscriptions_SubscriptionId",
                table: "DownloadRecords",
                column: "SubscriptionId",
                principalTable: "Subscriptions",
                principalColumn: "ID",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Accounts_AccountID",
                table: "Orders",
                column: "AccountID",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PricingPlans_Assets_AssetId",
                table: "PricingPlans",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Subscriptions_Accounts_AccountId",
                table: "Subscriptions",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Subscriptions_Assets_AssetId",
                table: "Subscriptions",
                column: "AssetId",
                principalTable: "Assets",
                principalColumn: "ID",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Subscriptions_Orders_OrderId",
                table: "Subscriptions",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "ID",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
