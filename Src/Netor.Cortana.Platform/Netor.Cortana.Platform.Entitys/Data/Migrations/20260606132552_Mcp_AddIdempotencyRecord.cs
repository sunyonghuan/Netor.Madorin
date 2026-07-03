using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Netor.Cortana.Platform.Entitys.Data.Migrations
{
    /// <inheritdoc />
    public partial class Mcp_AddIdempotencyRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IdempotencyRecords",
                columns: table => new
                {
                    ID = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "主键"),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "幂等哈希"),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "工具名称"),
                    ManagerId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "管理员ID"),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, comment: "请求ID"),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false, comment: "结果JSON"),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false, comment: "创建时间"),
                    Creator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "创建者"),
                    TimeStamp = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间(秒)"),
                    Version = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true, comment: "并发版本")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdempotencyRecords", x => x.ID);
                },
                comment: "MCP 写工具幂等记录");

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_CreatedUtc",
                table: "IdempotencyRecords",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_Hash",
                table: "IdempotencyRecords",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdempotencyRecords_ManagerId_ToolName",
                table: "IdempotencyRecords",
                columns: new[] { "ManagerId", "ToolName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdempotencyRecords");
        }
    }
}
