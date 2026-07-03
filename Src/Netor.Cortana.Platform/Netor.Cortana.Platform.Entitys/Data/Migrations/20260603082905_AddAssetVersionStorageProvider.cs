using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Netor.Cortana.Platform.Entitys.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetVersionStorageProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StorageProvider",
                table: "AssetVersions",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Local",
                comment: "存储提供程序");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StorageProvider",
                table: "AssetVersions");
        }
    }
}
