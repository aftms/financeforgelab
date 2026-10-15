using System.Globalization;
using System.Text;
using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class BankImportTests : IAsyncLifetime
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ffl-bank-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(folder, "test.sqlite");
    private FinanceDb Open() => FinanceDb.Open(DatabasePath);
    private BankImportService Imports => new(Open, Path.Combine(folder, "pdfs"));
    private FinanceService Finance => new(Open);
    private long account, savings, expense, income;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(folder); await using var db = Open(); await db.Database.MigrateAsync();
        var household = new Household(); db.Add(household); await db.SaveChangesAsync();
        var e = new Category { HouseholdId = household.Id, Name = "Casa", Kind = MovementKind.Expense };
        var i = new Category { HouseholdId = household.Id, Name = "Trabalho", Kind = MovementKind.Income };
        db.AddRange(e, i); await db.SaveChangesAsync(); expense = e.Id; income = i.Id;
        account = await Finance.SaveAccountAsync(new() { Name = "Conta", OpeningBalance = 1000, BalanceStartDate = new(2025, 1, 1) });
        savings = await Finance.SaveAccountAsync(new() { Name = "Poupanca", OpeningBalance = 200, BalanceStartDate = new(2025, 1, 1) });
    }
    public Task DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); return Task.CompletedTask; }
    private async Task<BankImportReview> Review(byte[]? pdf = null)
    {
        var id = (await Imports.StageAsync("synthetic.pdf", pdf ?? SyntheticPdf()))[0];
        var p = await Imports.PreviewAsync(id, account);
        return new() { Id = id, Version = p.Import.Version, AccountId = account, Rows = p.Statement.Rows.Select(r => new BankRowDecision { Ordinal = r.Ordinal, Kind = r.Units > 0 ? MovementKind.Income : MovementKind.Expense, CategoryId = r.Units > 0 ? income : expense }).ToList() };
    }
    [Fact] public async Task NativeReaderReconcilesPdfAndJoinsWrappedDescriptions()
    {
        var review = await Review(); var preview = await Imports.PreviewAsync(review.Id);
        Assert.Empty(preview.Statement.Errors); Assert.Equal(2, preview.Statement.Rows.Count);
        Assert.Equal("Compra Loja de teste", preview.Statement.Rows[0].Description);
        Assert.Equal(-100_000, preview.Statement.Rows[0].Units); Assert.Equal(100_000, preview.Statement.DebitTotalUnits);
        Assert.Equal(10_400_000, preview.Statement.ClosingUnits);
        await using var db = Open(); Assert.Equal(2, await db.Transactions.CountAsync()); // Opening balances only.
    }
    [Fact] public async Task ConfirmationIsAtomicRepeatableAndPreservesPdfAndValueDate()
    {
        var review = await Review(); var first = await Imports.ConfirmAsync(review); Assert.Equal(first, await Imports.ConfirmAsync(review));
        Assert.Equal(2, first.Created); Assert.Equal(0, first.Linked);
        await using var db = Open(); Assert.Equal(4, await db.Transactions.CountAsync());
        Assert.All(await db.Postings.Where(p => p.CategoryId != null).ToListAsync(), p => Assert.NotNull(p.ValueDate));
        Assert.Equal(SyntheticPdf(), (await db.BankDocuments.SingleAsync()).Content);
        var backup = Path.Combine(folder, "backup.sqlite"); DatabaseFiles.Backup(DatabasePath, backup);
        await using var restored = FinanceDb.Open(backup); Assert.Equal(SyntheticPdf(), (await restored.BankDocuments.SingleAsync()).Content); Assert.True(await DatabaseFiles.CheckIntegrityAsync(restored));
    }
    [Fact] public async Task ReuploadingSamePdfReturnsExistingReviewEvenAfterImport()
    {
        var review = await Review(); await Imports.ConfirmAsync(review);
        Assert.Equal(review.Id, Assert.Single(await Imports.StageAsync("renamed.pdf", SyntheticPdf())));
        await using var db = Open(); Assert.Single(await db.BankDocuments.ToListAsync()); Assert.Equal(2, await db.BankImportEntries.CountAsync());
    }
    [Fact] public async Task OverlappingStatementsRequireExplicitDuplicateDecision()
    {
        var first = await Review(); await Imports.ConfirmAsync(first);
        var second = await Review(SyntheticPdf("another-document"));
        Assert.Equal(2, (await Imports.PreviewAsync(second.Id, account)).Duplicates.Count);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(second));
        foreach (var row in second.Rows) row.AcknowledgeDuplicate = true;
        Assert.Equal(2, (await Imports.ConfirmAsync(second)).Created); // Equal legitimate movements can be kept explicitly.
    }
    [Fact] public async Task LinkingExistingMovementsDoesNotDuplicateBalancesAndProtectsTheOriginalBatch()
    {
        var first = await Review(); await Imports.ConfirmAsync(first);
        var second = await Review(SyntheticPdf("overlap")); var preview = await Imports.PreviewAsync(second.Id, account);
        foreach (var row in second.Rows) { var match = Assert.Single(preview.Duplicates[row.Ordinal]); row.Decision = "Link"; row.ExistingTransactionId = match.TransactionId; row.ExistingVersion = match.Version; }
        var result = await Imports.ConfirmAsync(second); Assert.Equal(0, result.Created); Assert.Equal(2, result.Linked);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.VoidAsync(first.Id, 2));
        await Imports.VoidAsync(second.Id, 2); // Unlinks this batch without voiding someone else's movements.
        Assert.Equal(10_400_000, (await Finance.AccountsAsync()).Single(a => a.Account.Id == account).BalanceUnits);
        await Imports.VoidAsync(first.Id, 2);
        Assert.Equal(10_000_000, (await Finance.AccountsAsync()).Single(a => a.Account.Id == account).BalanceUnits);
    }
    [Fact] public async Task FailedSecondRowRollsBackTheFirstRowAndAllLinks()
    {
        var review = await Review(); review.Rows[1].CategoryId = expense;
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(review));
        await using var db = Open(); Assert.Equal(2, await db.Transactions.CountAsync()); Assert.Empty(await db.BankImportEntries.ToListAsync()); Assert.Equal("Draft", (await db.BankImports.SingleAsync()).Status);
    }
    [Fact] public async Task ImportedTransferHasTwoPostingsAndIsExcludedFromExpenses()
    {
        var review = await Review(); review.Rows[0].Kind = MovementKind.Transfer; review.Rows[0].CounterpartyAccountId = savings;
        await Imports.ConfirmAsync(review); var dashboard = await Finance.DashboardAsync(2025);
        Assert.Equal(0, dashboard.ExpenseUnits); Assert.Equal(500_000, dashboard.IncomeUnits);
        Assert.Equal(2_100_000, (await Finance.AccountsAsync()).Single(a => a.Account.Id == savings).BalanceUnits);
        await Imports.VoidAsync(review.Id, 2);
        Assert.Equal(2_000_000, (await Finance.AccountsAsync()).Single(a => a.Account.Id == savings).BalanceUnits);
    }
    [Fact] public async Task EditsAfterImportBlockBatchVoidWithoutPartialChanges()
    {
        var review = await Review(); await Imports.ConfirmAsync(review);
        await using var db = Open(); var id = await db.BankImportEntries.Where(x => x.ImportId == review.Id && x.Ordinal == 2).Select(x => x.TransactionId).SingleAsync();
        var edit = await Finance.MovementAsync(id); edit.Description = "Alterado"; await Finance.SaveMovementAsync(edit);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.VoidAsync(review.Id, 2));
        Assert.Equal(4, await db.Transactions.CountAsync(t => t.Status == "Confirmed"));
    }
    [Fact] public async Task OneBankReferenceCannotBeMappedToDifferentApplicationAccounts()
    {
        var first = await Review(); await Imports.ConfirmAsync(first); var second = await Review(SyntheticPdf("new-statement")); second.AccountId = savings;
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(second));
    }
    [Fact] public async Task InvalidTotalsAndMissingDecisionsCannotCreateMovements()
    {
        var review = await Review(SyntheticPdf("bad", "1.040,01"));
        Assert.NotEmpty((await Imports.PreviewAsync(review.Id)).Statement.Errors);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(review));
        await using var db = Open(); Assert.Equal(2, await db.Transactions.CountAsync());
    }
    [Fact] public async Task MissingRowsAndStaleExistingMovementAreRejected()
    {
        var first = await Review(); var incomplete = await Review(); incomplete.Rows.RemoveAt(0);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(incomplete));
        await Imports.ConfirmAsync(first); var second = await Review(SyntheticPdf("stale")); var preview = await Imports.PreviewAsync(second.Id, account);
        foreach (var row in second.Rows) { var existing = preview.Duplicates[row.Ordinal][0]; row.Decision = "Link"; row.ExistingTransactionId = existing.TransactionId; row.ExistingVersion = existing.Version; }
        var edited = await Finance.MovementAsync(second.Rows[1].ExistingTransactionId!.Value); edited.Notes = "changed"; await Finance.SaveMovementAsync(edited);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(second));
        await using var db = Open(); Assert.Empty(await db.BankImportEntries.Where(x => x.ImportId == second.Id).ToListAsync());
    }
    [Fact] public async Task TamperedStoredPdfCannotBeImported()
    {
        var review = await Review(); await using (var db = Open()) { var document = await db.BankDocuments.SingleAsync(); document.Content = "%PDF-tampered"u8.ToArray(); await db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(review));
    }
    [Fact] public async Task DraftReviewPersistsWithoutFinancialEffectsAndRejectsStaleSave()
    {
        var review = await Review(); var stale = await Review(); review.Rows[0].Decision = "Exclude";
        await Imports.SaveReviewAsync(review); Assert.Equal(2, review.Version);
        var preview = await Imports.PreviewAsync(review.Id); Assert.Contains("Exclude", preview.Import.ReviewJson);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.SaveReviewAsync(stale));
        await using var db = Open(); Assert.Equal(2, await db.Transactions.CountAsync());
        Assert.Equal(1, (await Imports.ConfirmAsync(review)).Created);
    }
    [Fact] public async Task ReuploadAfterVoidCreatesNewReviewAndPreservesPreviousBatch()
    {
        var first = await Review(); await Imports.ConfirmAsync(first); await Imports.VoidAsync(first.Id, 2);
        var retry = await Review(); Assert.NotEqual(first.Id, retry.Id); retry.AccountId = savings;
        await Imports.ConfirmAsync(retry);
        await using var db = Open(); Assert.Equal("Voided", (await db.BankImports.FindAsync(first.Id))!.Status);
        Assert.Equal(4, await db.BankImportEntries.CountAsync()); Assert.Equal(2_400_000, (await Finance.AccountsAsync()).Single(x => x.Account.Id == savings).BalanceUnits);
    }
    [Fact] public async Task NonEuroTableIsBlockedEvenWhenAllNumbersReconcile()
    {
        var review = await Review(SyntheticPdf(currency: "USD"));
        Assert.Contains((await Imports.PreviewAsync(review.Id)).Statement.Errors, e => e.Contains("EUR"));
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(review));
    }
    private static byte[] SyntheticPdf(string marker = "synthetic", string closing = "1.040,00", string currency = "Euros")
    {
        static string Text(int x, int y, string text) => $"BT /F1 8 Tf {x} {y} Td ({text}) Tj ET\n";
        var content = Text(30, 760, "CONTA TESTE no 1111 2222 3333 de 01.01.2025 a 31.01.2025") + Text(30, 730, "Data") + Text(160, 730, "Descritivo") + Text(345, 730, "Debito") + Text(415, 730, "Credito") + Text(485, 730, "Saldo (" + currency + ")") +
            Text(27, 700, "01.01.25") + Text(103, 700, "SALDO ANTERIOR") + Text(500, 700, "1.000,00") +
            Text(27, 680, "02.01.25") + Text(65, 680, "02.01.25") + Text(103, 680, "Compra") + Text(350, 680, "10,00") + Text(500, 680, "990,00") + Text(103, 664, "Loja de teste") +
            Text(27, 645, "03.01.25") + Text(65, 645, "03.01.25") + Text(103, 645, "Salario") + Text(420, 645, "50,00") + Text(500, 645, "1.040,00") +
            Text(103, 620, "TOTAL") + Text(350, 620, "10,00") + Text(420, 620, "50,00") + Text(27, 600, "31.01.25") + Text(103, 600, "SALDO CONTABILISTICO") + Text(500, 600, closing);
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream" };
        var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString())); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = Encoding.ASCII.GetByteCount(pdf.ToString()); pdf.Append($"xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        pdf.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n%{marker}"); return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
