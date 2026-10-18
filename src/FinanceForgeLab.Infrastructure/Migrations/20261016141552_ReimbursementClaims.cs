using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceForgeLab.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReimbursementClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReimbursementClaims",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HouseholdId = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpenseTransactionId = table.Column<long>(type: "INTEGER", nullable: false),
                    Debtor = table.Column<string>(type: "TEXT", nullable: false),
                    AmountUnits = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReimbursementClaims", x => x.Id);
                    table.CheckConstraint("CK_ReimbursementClaim_Amount", "AmountUnits > 0");
                    table.ForeignKey(
                        name: "FK_ReimbursementClaims_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReimbursementClaims_Transactions_ExpenseTransactionId",
                        column: x => x.ExpenseTransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReimbursementAllocations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClaimId = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceiptTransactionId = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountUnits = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReimbursementAllocations", x => x.Id);
                    table.CheckConstraint("CK_ReimbursementAllocation_Amount", "AmountUnits > 0");
                    table.ForeignKey(
                        name: "FK_ReimbursementAllocations_ReimbursementClaims_ClaimId",
                        column: x => x.ClaimId,
                        principalTable: "ReimbursementClaims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReimbursementAllocations_Transactions_ReceiptTransactionId",
                        column: x => x.ReceiptTransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementAllocations_ClaimId_ReceiptTransactionId",
                table: "ReimbursementAllocations",
                columns: new[] { "ClaimId", "ReceiptTransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementAllocations_ReceiptTransactionId",
                table: "ReimbursementAllocations",
                column: "ReceiptTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementClaims_ExpenseTransactionId",
                table: "ReimbursementClaims",
                column: "ExpenseTransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReimbursementClaims_HouseholdId",
                table: "ReimbursementClaims",
                column: "HouseholdId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReimbursementAllocations");

            migrationBuilder.DropTable(
                name: "ReimbursementClaims");
        }
    }
}
