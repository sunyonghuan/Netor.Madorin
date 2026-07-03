using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Netor.Cortana.Platform.Entitys.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCreatorReviewSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "No",
                table: "Managers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1780471211625L,
                comment: "会员编号",
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldDefaultValue: 1778476340214L,
                oldComment: "会员编号");

            migrationBuilder.AlterColumn<long>(
                name: "No",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1780471211622L,
                comment: "会员编号",
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldDefaultValue: 1778476340211L,
                oldComment: "会员编号");

            migrationBuilder.CreateTable(
                name: "AssetReviews",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    AssetId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "资源ID"),
                    AssetVersionId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "资源版本ID"),
                    SubmitterAccountId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "提交账号ID"),
                    Status = table.Column<int>(type: "INTEGER", nullable: false, comment: "审核状态"),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false, comment: "审核备注"),
                    ReviewerId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "审核人ID"),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true, comment: "审核时间"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetReviews", x => x.ID);
                    table.ForeignKey(
                        name: "FK_AssetReviews_Accounts_SubmitterAccountId",
                        column: x => x.SubmitterAccountId,
                        principalTable: "Accounts",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssetReviews_AssetVersions_AssetVersionId",
                        column: x => x.AssetVersionId,
                        principalTable: "AssetVersions",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssetReviews_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "资源审核记录");

            migrationBuilder.CreateTable(
                name: "CreatorProfiles",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    AccountId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "账号ID"),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "创作者名称"),
                    Bio = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false, comment: "简介"),
                    Status = table.Column<int>(type: "INTEGER", nullable: false, comment: "状态"),
                    ApprovedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true, comment: "通过时间"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreatorProfiles", x => x.ID);
                    table.ForeignKey(
                        name: "FK_CreatorProfiles_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "创作者资料");

            migrationBuilder.CreateTable(
                name: "CreatorSettlements",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    CreatorAccountId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创作者账号ID"),
                    AssetId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "资源ID"),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "订单ID"),
                    GrossAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false, comment: "订单金额"),
                    PlatformFeeAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false, comment: "平台服务费"),
                    NetAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false, comment: "创作者净收益"),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false, comment: "币种"),
                    Status = table.Column<int>(type: "INTEGER", nullable: false, comment: "状态"),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false, comment: "备注"),
                    SettledAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true, comment: "结算时间"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreatorSettlements", x => x.ID);
                    table.ForeignKey(
                        name: "FK_CreatorSettlements_Accounts_CreatorAccountId",
                        column: x => x.CreatorAccountId,
                        principalTable: "Accounts",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CreatorSettlements_Assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "Assets",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CreatorSettlements_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "创作者收益结算记录");

            migrationBuilder.CreateIndex(
                name: "IX_AssetReviews_AssetId_Status",
                table: "AssetReviews",
                columns: new[] { "AssetId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetReviews_AssetVersionId",
                table: "AssetReviews",
                column: "AssetVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetReviews_SubmitterAccountId_Status",
                table: "AssetReviews",
                columns: new[] { "SubmitterAccountId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CreatorProfiles_AccountId",
                table: "CreatorProfiles",
                column: "AccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreatorProfiles_Status",
                table: "CreatorProfiles",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CreatorSettlements_AssetId",
                table: "CreatorSettlements",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_CreatorSettlements_CreatorAccountId_Status",
                table: "CreatorSettlements",
                columns: new[] { "CreatorAccountId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CreatorSettlements_OrderId",
                table: "CreatorSettlements",
                column: "OrderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssetReviews");

            migrationBuilder.DropTable(
                name: "CreatorProfiles");

            migrationBuilder.DropTable(
                name: "CreatorSettlements");

            migrationBuilder.AlterColumn<long>(
                name: "No",
                table: "Managers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1778476340214L,
                comment: "会员编号",
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldDefaultValue: 1780471211625L,
                oldComment: "会员编号");

            migrationBuilder.AlterColumn<long>(
                name: "No",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1778476340211L,
                comment: "会员编号",
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldDefaultValue: 1780471211622L,
                oldComment: "会员编号");
        }
    }
}
