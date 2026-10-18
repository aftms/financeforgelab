using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceForgeLab.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AssistantSyncMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Workflow",
                table: "AssistantRequests",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "SourceCacheId",
                table: "AssistantAttachments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantSources",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: false),
                    CursorJson = table.Column<string>(type: "TEXT", nullable: false),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantSources", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantRequests_Workflow",
                table: "AssistantRequests",
                column: "Workflow");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAttachments_SourceCacheId",
                table: "AssistantAttachments",
                column: "SourceCacheId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantSources_Kind_Scope_SourceId_Fingerprint",
                table: "AssistantSources",
                columns: new[] { "Kind", "Scope", "SourceId", "Fingerprint" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AssistantAttachments_AssistantSources_SourceCacheId",
                table: "AssistantAttachments",
                column: "SourceCacheId",
                principalTable: "AssistantSources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AssistantAttachments_AssistantSources_SourceCacheId",
                table: "AssistantAttachments");

            migrationBuilder.DropTable(
                name: "AssistantSources");

            migrationBuilder.DropIndex(
                name: "IX_AssistantRequests_Workflow",
                table: "AssistantRequests");

            migrationBuilder.DropIndex(
                name: "IX_AssistantAttachments_SourceCacheId",
                table: "AssistantAttachments");

            migrationBuilder.DropColumn(
                name: "Workflow",
                table: "AssistantRequests");

            migrationBuilder.DropColumn(
                name: "SourceCacheId",
                table: "AssistantAttachments");
        }
    }
}
