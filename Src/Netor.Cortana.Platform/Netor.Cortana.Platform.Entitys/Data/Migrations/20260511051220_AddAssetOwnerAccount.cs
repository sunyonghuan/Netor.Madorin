using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Netor.Cortana.Platform.Entitys.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetOwnerAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "No",
                table: "Managers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1778476340214L,
                comment: "会员编号",
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldDefaultValue: 1778139677634L,
                oldComment: "会员编号");

            migrationBuilder.AddColumn<string>(
                name: "OwnerAccountId",
                table: "Assets",
                type: "TEXT",
                maxLength: 32,
                nullable: true,
                comment: "归属账号ID");

            migrationBuilder.AlterColumn<long>(
                name: "No",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1778476340211L,
                comment: "会员编号",
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldDefaultValue: 1778139677631L,
                oldComment: "会员编号");

            migrationBuilder.CreateIndex(
                name: "IX_Assets_OwnerAccountId",
                table: "Assets",
                column: "OwnerAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_Assets_Accounts_OwnerAccountId",
                table: "Assets",
                column: "OwnerAccountId",
                principalTable: "Accounts",
                principalColumn: "ID",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Assets_Accounts_OwnerAccountId",
                table: "Assets");

            migrationBuilder.DropIndex(
                name: "IX_Assets_OwnerAccountId",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "OwnerAccountId",
                table: "Assets");

            migrationBuilder.AlterColumn<long>(
                name: "No",
                table: "Managers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1778139677634L,
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
                defaultValue: 1778139677631L,
                comment: "会员编号",
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldDefaultValue: 1778476340211L,
                oldComment: "会员编号");
        }
    }
}
