using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceForgeLab.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AssistantRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantRequests",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Notice = table.Column<string>(type: "TEXT", nullable: false),
                    Archived = table.Column<bool>(type: "INTEGER", nullable: false),
                    Round = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextCheckUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssistantAttachments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    Round = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantAttachments_AssistantRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "AssistantRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssistantOperations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    Round = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    CommandId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantOperations_AssistantRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "AssistantRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssistantTurns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    Round = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserText = table.Column<string>(type: "TEXT", nullable: false),
                    InputFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    DelegationId = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    QuestionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    PollCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantTurns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantTurns_AssistantRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "AssistantRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAttachments_RequestId_Sha256",
                table: "AssistantAttachments",
                columns: new[] { "RequestId", "Sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantOperations_RequestId_Key",
                table: "AssistantOperations",
                columns: new[] { "RequestId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantRequests_ClientId",
                table: "AssistantRequests",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTurns_ClientId",
                table: "AssistantTurns",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantTurns_RequestId_Round",
                table: "AssistantTurns",
                columns: new[] { "RequestId", "Round" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantAttachments");

            migrationBuilder.DropTable(
                name: "AssistantOperations");

            migrationBuilder.DropTable(
                name: "AssistantTurns");

            migrationBuilder.DropTable(
                name: "AssistantRequests");
        }
    }
}
