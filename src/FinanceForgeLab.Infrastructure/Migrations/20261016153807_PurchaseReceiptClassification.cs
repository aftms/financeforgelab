using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceForgeLab.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseReceiptClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "DocumentContent",
                table: "PurchaseEvidenceSources",
                type: "BLOB",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<string>(
                name: "DocumentName",
                table: "PurchaseEvidenceSources",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DocumentSha256",
                table: "PurchaseEvidenceSources",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CategoryAllocationsJson",
                table: "PurchaseEvidence",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<long>(
                name: "FinancialUnits",
                table: "PurchaseEvidence",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethod",
                table: "PurchaseEvidence",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DocumentContent",
                table: "PurchaseEvidenceSources");

            migrationBuilder.DropColumn(
                name: "DocumentName",
                table: "PurchaseEvidenceSources");

            migrationBuilder.DropColumn(
                name: "DocumentSha256",
                table: "PurchaseEvidenceSources");

            migrationBuilder.DropColumn(
                name: "CategoryAllocationsJson",
                table: "PurchaseEvidence");

            migrationBuilder.DropColumn(
                name: "FinancialUnits",
                table: "PurchaseEvidence");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                table: "PurchaseEvidence");
        }
    }
}
