using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceForgeLab.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BankImportActiveReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BankImports_DocumentId_SectionIndex",
                table: "BankImports");

            migrationBuilder.CreateIndex(
                name: "IX_BankImports_DocumentId_SectionIndex",
                table: "BankImports",
                columns: new[] { "DocumentId", "SectionIndex" },
                unique: true,
                filter: "Status <> 'Voided'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BankImports_DocumentId_SectionIndex",
                table: "BankImports");

            migrationBuilder.CreateIndex(
                name: "IX_BankImports_DocumentId_SectionIndex",
                table: "BankImports",
                columns: new[] { "DocumentId", "SectionIndex" },
                unique: true);
        }
    }
}
