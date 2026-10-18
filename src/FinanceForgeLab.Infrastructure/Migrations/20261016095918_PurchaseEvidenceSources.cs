using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceForgeLab.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseEvidenceSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasSourceConflict",
                table: "PurchaseEvidence",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OrderIdentity",
                table: "PurchaseEvidence",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TransactionId",
                table: "PurchaseEvidence",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PurchaseEvidenceSources",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HouseholdId = table.Column<long>(type: "INTEGER", nullable: false),
                    EvidenceId = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseEvidenceSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseEvidenceSources_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseEvidenceSources_PurchaseEvidence_EvidenceId",
                        column: x => x.EvidenceId,
                        principalTable: "PurchaseEvidence",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseEvidence_HouseholdId_OrderIdentity",
                table: "PurchaseEvidence",
                columns: new[] { "HouseholdId", "OrderIdentity" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseEvidence_TransactionId",
                table: "PurchaseEvidence",
                column: "TransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseEvidenceSources_EvidenceId",
                table: "PurchaseEvidenceSources",
                column: "EvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseEvidenceSources_HouseholdId_SourceKey",
                table: "PurchaseEvidenceSources",
                columns: new[] { "HouseholdId", "SourceKey" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseEvidence_Transactions_TransactionId",
                table: "PurchaseEvidence",
                column: "TransactionId",
                principalTable: "Transactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            // Preserve every previously staged document as the first source of its purchase.
            migrationBuilder.Sql("""
                INSERT INTO PurchaseEvidenceSources (HouseholdId, EvidenceId, SourceKey, Source, PayloadJson)
                SELECT HouseholdId, Id, SourceKey, Source,
                    json_object('SourceKey',SourceKey,'Source',Source,'Channel',Channel,'Merchant',Merchant,
                        'OrderReference',OrderReference,'Date',Date,'Units',Units,'CategoryId',CategoryId,
                        'Description',Description,'Details',Details,'ReviewNotes',ReviewNotes)
                FROM PurchaseEvidence;
                UPDATE PurchaseEvidence SET OrderIdentity=upper(trim(Channel)) || '|' || upper(replace(trim(OrderReference),' ',''))
                WHERE length(trim(Channel)) >= 3 AND length(trim(OrderReference)) >= 3;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseEvidence_Transactions_TransactionId",
                table: "PurchaseEvidence");

            migrationBuilder.DropTable(
                name: "PurchaseEvidenceSources");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseEvidence_HouseholdId_OrderIdentity",
                table: "PurchaseEvidence");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseEvidence_TransactionId",
                table: "PurchaseEvidence");

            migrationBuilder.DropColumn(
                name: "HasSourceConflict",
                table: "PurchaseEvidence");

            migrationBuilder.DropColumn(
                name: "OrderIdentity",
                table: "PurchaseEvidence");

            migrationBuilder.DropColumn(
                name: "TransactionId",
                table: "PurchaseEvidence");
        }
    }
}
