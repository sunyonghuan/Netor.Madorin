using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Netor.Cortana.Platform.Entitys.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocsModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocCategories",
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
                    table.PrimaryKey("PK_DocCategories", x => x.ID);
                },
                comment: "官网文档分类");

            migrationBuilder.CreateTable(
                name: "DocArticles",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    CategoryId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "分类ID"),
                    Title = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "标题"),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false, comment: "标识"),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, comment: "摘要"),
                    ContentMarkdown = table.Column<string>(type: "TEXT", nullable: false, comment: "正文"),
                    Keywords = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false, comment: "搜索关键词"),
                    Status = table.Column<int>(type: "INTEGER", nullable: false, comment: "状态"),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false, comment: "排序"),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true, comment: "发布时间"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocArticles", x => x.ID);
                    table.ForeignKey(
                        name: "FK_DocArticles_DocCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "DocCategories",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "官网文档文章");

            migrationBuilder.CreateIndex(
                name: "IX_DocArticles_CategoryId_Slug",
                table: "DocArticles",
                columns: new[] { "CategoryId", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocArticles_Status_SortOrder",
                table: "DocArticles",
                columns: new[] { "Status", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_DocCategories_Slug",
                table: "DocCategories",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocArticles");

            migrationBuilder.DropTable(
                name: "DocCategories");
        }
    }
}
