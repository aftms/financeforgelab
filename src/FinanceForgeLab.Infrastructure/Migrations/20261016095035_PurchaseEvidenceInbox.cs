using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceForgeLab.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseEvidenceInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PurchaseEvidence",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HouseholdId = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", nullable: false),
                    Merchant = table.Column<string>(type: "TEXT", nullable: false),
                    OrderReference = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Units = table.Column<long>(type: "INTEGER", nullable: true),
                    CategoryId = table.Column<long>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Details = table.Column<string>(type: "TEXT", nullable: false),
                    ReviewNotes = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<long>(type: "INTEGER", nullable: true),
                    BankReferenceId = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseEvidence_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseEvidence_BankReferences_BankReferenceId",
                        column: x => x.BankReferenceId,
                        principalTable: "BankReferences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseEvidence_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseEvidence_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseEvidence_AccountId",
                table: "PurchaseEvidence",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseEvidence_BankReferenceId",
                table: "PurchaseEvidence",
                column: "BankReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseEvidence_CategoryId",
                table: "PurchaseEvidence",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseEvidence_HouseholdId_SourceKey",
                table: "PurchaseEvidence",
                columns: new[] { "HouseholdId", "SourceKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchaseEvidence");
        }
    }
}
