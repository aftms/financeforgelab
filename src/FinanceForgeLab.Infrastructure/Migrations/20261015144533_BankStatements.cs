using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceForgeLab.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BankStatements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BankDocuments",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ReaderVersion = table.Column<string>(type: "TEXT", nullable: false),
                    StatementJson = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BankImports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DocumentId = table.Column<string>(type: "TEXT", nullable: false),
                    SectionIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceReference = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<long>(type: "INTEGER", nullable: true),
                    PersonId = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewJson = table.Column<string>(type: "TEXT", nullable: false),
                    ReviewHash = table.Column<string>(type: "TEXT", nullable: false),
                    ImportedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankImports", x => x.Id);
                    table.CheckConstraint("CK_BankImport_Status", "Status IN ('Draft','Imported','Voided')");
                    table.ForeignKey(
                        name: "FK_BankImports_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankImports_BankDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "BankDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankImports_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BankImportEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ImportId = table.Column<long>(type: "INTEGER", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    TransactionId = table.Column<long>(type: "INTEGER", nullable: false),
                    TransactionVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Created = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankImportEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankImportEntries_BankImports_ImportId",
                        column: x => x.ImportId,
                        principalTable: "BankImports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankImportEntries_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankImportEntries_ImportId_Ordinal",
                table: "BankImportEntries",
                columns: new[] { "ImportId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankImportEntries_TransactionId",
                table: "BankImportEntries",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_BankImports_AccountId",
                table: "BankImports",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_BankImports_DocumentId_SectionIndex",
                table: "BankImports",
                columns: new[] { "DocumentId", "SectionIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankImports_PersonId",
                table: "BankImports",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_BankImports_SourceReference",
                table: "BankImports",
                column: "SourceReference");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankImportEntries");

            migrationBuilder.DropTable(
                name: "BankImports");

            migrationBuilder.DropTable(
                name: "BankDocuments");
        }
    }
}
