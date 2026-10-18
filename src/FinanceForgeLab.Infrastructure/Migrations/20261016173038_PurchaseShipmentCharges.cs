using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceForgeLab.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseShipmentCharges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ParentEvidenceId",
                table: "PurchaseEvidence",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseEvidence_ParentEvidenceId",
                table: "PurchaseEvidence",
                column: "ParentEvidenceId");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseEvidence_PurchaseEvidence_ParentEvidenceId",
                table: "PurchaseEvidence",
                column: "ParentEvidenceId",
                principalTable: "PurchaseEvidence",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseEvidence_PurchaseEvidence_ParentEvidenceId",
                table: "PurchaseEvidence");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseEvidence_ParentEvidenceId",
                table: "PurchaseEvidence");

            migrationBuilder.DropColumn(
                name: "ParentEvidenceId",
                table: "PurchaseEvidence");
        }
    }
}
