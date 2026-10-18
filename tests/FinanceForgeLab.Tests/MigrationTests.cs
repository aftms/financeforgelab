using System.Security.Cryptography;
using System.Text.Json;
using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ffl-tests-" + Guid.NewGuid().ToString("N"));
    public MigrationTests() => Directory.CreateDirectory(folder);
    public void Dispose() { SqliteCleanup(); Directory.Delete(folder, true); }
    private static void SqliteCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    private string DbPath => Path.Combine(folder, "test.sqlite");

    [Theory]
    [InlineData("0.0001", 1L)]
    [InlineData("12.34", 123400L)]
    [InlineData("-12.34", -123400L)]
    [InlineData("922337203685477.5807", long.MaxValue)]
    [InlineData("-922337203685477.5808", long.MinValue)]
    public void MoneyRoundTripsExactly(string value, long expected)
    {
        var amount = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, Money.ToUnits(amount)); Assert.Equal(amount, Money.FromUnits(expected));
    }

    [Fact]
    public void MoneyRejectsRoundingAndOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.ToUnits(0.00001m));
        Assert.Throws<OverflowException>(() => Money.ToUnits(922337203685477.5808m));
        Assert.Equal(123400, Money.SignedUnits(-12.34m, MovementKind.Expense));
        Assert.Throws<OverflowException>(() => Money.SignedUnits(-922337203685477.5808m, MovementKind.Expense));
    }

    [Fact]
    public async Task ImportPreservesSourceFieldsSignsRelationsAndLegitimateDuplicates()
    {
        var (p, backup) = Fixture();
        await using var db = FinanceDb.Open(DbPath); await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        var result = await new LegacyMigrator(db).RunAsync(p, "package-1", backup, "Simulation");
        Assert.Equal("Validated", result.Status); Assert.True(result.BackupVerified); Assert.True(result.Reconciled); Assert.True(result.IntegrityVerified);
        Assert.Equal(3, await db.Transactions.CountAsync()); Assert.Equal(3, await db.Postings.CountAsync());
        Assert.Equal(1, result.DuplicateGroups); Assert.Equal(2, result.DuplicateCandidateRows);
        Assert.Equal(3, result.UnassignedAccountCount);
        Assert.Equal(2, await db.Postings.CountAsync(x => x.AmountUnits == -123400));
        Assert.Equal(1, await db.Postings.CountAsync(x => x.AmountUnits == 10000));
        Assert.All(await db.Postings.ToListAsync(), p => Assert.Null(p.AccountId));
        Assert.Equal("d'Água e ação", (await db.Transactions.FirstAsync()).Notes);
        Assert.Equal(p.Tables.Sum(x => x.Value.Count), await db.SourceRecords.CountAsync());
        var raw = await db.SourceRecords.SingleAsync(x => x.Table == "Elemento");
        Assert.Contains("DataNascimento", raw.RawJson);
        Assert.Equal(1, await db.SourceRecords.CountAsync(x => x.Table == "OtherModule"));
        Assert.Empty(await db.Accounts.ToListAsync());
    }

    [Fact]
    public async Task MovementListRetainsOriginalDetailAfterCategoryRenamingWithoutInventingDescriptions()
    {
        var (package, backup) = Fixture();
        await using (var db = FinanceDb.Open(DbPath))
        {
            await db.Database.MigrateAsync();
            await new LegacyMigrator(db).RunAsync(package, "package-1", backup, "Simulation");
            var parent = await db.Categories.SingleAsync(c => c.ParentId == null);
            var child = await db.Categories.SingleAsync(c => c.ParentId != null);
            parent.Name = "Habitação"; child.Name = "Serviços domésticos"; await db.SaveChangesAsync();
        }
        var service = new FinanceService(() => FinanceDb.Open(DbPath));
        var page = await service.MovementsAsync(new() { PageSize = 10 });
        Assert.Equal(3, page.Total);
        Assert.All(page.Rows, row =>
        {
            Assert.Equal("Casa", row.OriginalCategory); Assert.Equal("Água", row.OriginalSubcategory);
            Assert.Equal("Habitação", row.ParentCategory); Assert.Equal("Serviços domésticos", row.Category);
        });
        var blank = Assert.Single(page.Rows, row => row.Date == new DateOnly(2012, 2, 29));
        Assert.Null(blank.Description); Assert.Null(blank.Notes);
        Assert.All(page.Rows.Where(row => row.Id != blank.Id), row => Assert.Equal("d'Água e ação", row.Description));
        var ascending = await service.MovementsAsync(new() { Sort = MovementSort.Description, Descending = false });
        Assert.Equal(blank.Id, ascending.Rows[0].Id); Assert.Equal("Água", ascending.Rows[0].DisplayDescription);
        var descending = await service.MovementsAsync(new() { Sort = MovementSort.Description, Descending = true });
        Assert.Equal(blank.Id, descending.Rows[^1].Id);
        var csv = await service.ExportCsvAsync(new() { Sort = MovementSort.Description, Descending = true });
        Assert.StartsWith("2012-02-29;", csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
        await using var check = FinanceDb.Open(DbPath);
        Assert.Equal(3, await check.Transactions.CountAsync());
        Assert.Equal("Água", JsonSerializer.Deserialize<Dictionary<string, string?>>((await check.SourceRecords.SingleAsync(r => r.Table == "SubCategoria")).RawJson)!["Descricao"]);
    }

    [Fact]
    public async Task IdenticalRerunAddsNoOperationalRowsOrArchiveDuplicates()
    {
        var (p, backup) = Fixture();
        await using (var db = FinanceDb.Open(DbPath)) { await db.Database.MigrateAsync(); await new LegacyMigrator(db).RunAsync(p, "package-1", backup, "Simulation"); }
        await using var reopened = FinanceDb.Open(DbPath);
        var beforeArchive = await reopened.SourceRecords.CountAsync();
        var rerun = await new LegacyMigrator(reopened).RunAsync(p, "package-1", backup, "Simulation");
        Assert.Equal("Validated", rerun.Status); Assert.Equal(0, rerun.Inserted.Values.Sum());
        Assert.Equal(6, rerun.Skipped.Values.Sum()); Assert.Equal(0, rerun.RawRowsArchived);
        Assert.Equal(beforeArchive, await reopened.SourceRecords.CountAsync()); Assert.Equal(1, await reopened.ImportBatches.CountAsync());
    }

    [Theory]
    [InlineData("source")]
    [InlineData("destination")]
    [InlineData("removed")]
    public async Task ConflictsNeverOverwriteExistingData(string change)
    {
        var (p, backup) = Fixture();
        await using var db = FinanceDb.Open(DbPath); await db.Database.MigrateAsync();
        await new LegacyMigrator(db).RunAsync(p, "package-1", backup, "Simulation");
        if (change == "source") p.Tables["Movimento"][0]["Observacoes"] = "source edit";
        if (change == "removed") p.Tables["Movimento"].RemoveAt(0);
        if (change == "destination") { var entry = await db.Transactions.FirstAsync(); entry.Notes = "user edit"; await db.SaveChangesAsync(); }
        var result = await new LegacyMigrator(db).RunAsync(p, "package-2", backup, "Simulation");
        Assert.Equal("Blocked", result.Status); Assert.Contains(result.Issues, x => x.Severity == "Error");
        Assert.Equal(3, await db.Transactions.CountAsync()); Assert.Equal(1, await db.SourceSnapshots.CountAsync());
        if (change == "destination") Assert.Equal("user edit", (await db.Transactions.FirstAsync()).Notes);
    }

    [Fact]
    public async Task ANewSourceSnapshotCanAppendWithoutDuplicatingEarlierMovements()
    {
        var (p, backup) = Fixture();
        await using var db = FinanceDb.Open(DbPath); await db.Database.MigrateAsync();
        await new LegacyMigrator(db).RunAsync(p, "package-1", backup, "Simulation");
        var extra = new Dictionary<string, string?>(p.Tables["Movimento"][0]) { ["ID"] = "4", ["Dia"] = "2020-02-29T00:00:00.0000000" };
        p.Tables["Movimento"].Add(extra);
        var result = await new LegacyMigrator(db).RunAsync(p, "package-2", backup, "Simulation");
        Assert.Equal("Validated", result.Status); Assert.Equal(1, result.Inserted["Movimento"]);
        Assert.Equal(4, await db.Transactions.CountAsync()); Assert.Equal(2, await db.SourceSnapshots.CountAsync());
    }

    [Theory]
    [InlineData("IdElemento", "99")]
    [InlineData("IdTipo", "3")]
    [InlineData("Valor", "0")]
    [InlineData("Valor", "0.00001")]
    [InlineData("Dia", "2012-01-01T12:00:00.0000000")]
    public async Task InvalidRecordsBlockAllFinancialWrites(string field, string value)
    {
        var (p, backup) = Fixture(); p.Tables["Movimento"][0][field] = value;
        await using var db = FinanceDb.Open(DbPath); await db.Database.MigrateAsync();
        var result = await new LegacyMigrator(db).RunAsync(p, "package-1", backup, "Simulation");
        Assert.Equal("Blocked", result.Status); Assert.Equal(0, await db.Transactions.CountAsync()); Assert.Equal(0, await db.SourceDatasets.CountAsync());
    }

    [Fact]
    public async Task BackupHashMismatchBlocksImport()
    {
        var (p, backup) = Fixture(); await File.AppendAllTextAsync(backup, "tampered");
        await using var db = FinanceDb.Open(DbPath); await db.Database.MigrateAsync();
        var result = await new LegacyMigrator(db).RunAsync(p, "package-1", backup, "Simulation");
        Assert.Equal("Blocked", result.Status); Assert.False(result.BackupVerified); Assert.Equal(0, await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task FailureAfterArchiveAndReferentialsRollsBackEntireBatch()
    {
        var (p, backup) = Fixture();
        var options = new DbContextOptionsBuilder<FinanceDb>().UseSqlite($"Data Source={DbPath};Foreign Keys=True;Pooling=False")
            .AddInterceptors(new FailOnPosting()).Options;
        await using (var db = new FinanceDb(options))
        {
            await db.Database.MigrateAsync();
            await Assert.ThrowsAsync<IOException>(() => new LegacyMigrator(db).RunAsync(p, "package-1", backup, "Simulation"));
        }
        await using var check = FinanceDb.Open(DbPath);
        Assert.Equal(0, await check.SourceRecords.CountAsync()); Assert.Equal(0, await check.People.CountAsync());
        Assert.Equal(0, await check.Transactions.CountAsync()); Assert.Equal(0, await check.LegacyMaps.CountAsync());
    }

    [Fact]
    public async Task SqliteBackupRestoresCommittedDataAndNeverOverwritesFiles()
    {
        var (p, backup) = Fixture();
        await using var db = FinanceDb.Open(DbPath); await db.Database.MigrateAsync();
        await new LegacyMigrator(db).RunAsync(p, "package-1", backup, "Simulation");
        var dest = Path.Combine(folder, "restored.sqlite"); DatabaseFiles.Backup(DbPath, dest);
        await using var restored = FinanceDb.Open(dest);
        Assert.True(await DatabaseFiles.CheckIntegrityAsync(restored)); Assert.Equal(3, await restored.Transactions.CountAsync());
        Assert.Equal(await db.Postings.SumAsync(x => x.AmountUnits), await restored.Postings.SumAsync(x => x.AmountUnits));
        Assert.Throws<IOException>(() => DatabaseFiles.Backup(DbPath, dest));
    }

    [Fact]
    public async Task ForeignKeysAndConcurrencyProtectHistoricalData()
    {
        var (p, backup) = Fixture();
        await using var db = FinanceDb.Open(DbPath); await db.Database.MigrateAsync();
        await new LegacyMigrator(db).RunAsync(p, "package-1", backup, "Simulation");
        await using var second = FinanceDb.Open(DbPath);
        var first = await db.Transactions.FirstAsync(); var stale = await second.Transactions.SingleAsync(x => x.Id == first.Id);
        first.Version++; first.Notes = "first"; await db.SaveChangesAsync();
        stale.Version++; stale.Notes = "second";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        await using var deletion = FinanceDb.Open(DbPath);
        deletion.People.Remove(await deletion.People.FirstAsync());
        await Assert.ThrowsAsync<DbUpdateException>(() => deletion.SaveChangesAsync());
    }

    private sealed class FailOnPosting : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Posting>().Any(x => x.State == EntityState.Added)) throw new IOException("Simulated write failure");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task PackageManifestDetectsAlteredExportBeforeOpeningDestination()
    {
        var (p, _) = Fixture();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(p);
        var packagePath = Path.Combine(folder, "package.json");
        await File.WriteAllBytesAsync(packagePath, bytes);
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new {
            p.SourceSha256, PackageSha256 = Convert.ToHexString(SHA256.HashData(bytes)), p.DatasetId,
            Tables = p.Tables.Count, Records = p.Tables.Sum(x => x.Value.Count)
        }));
        Assert.Equal(p.DatasetId, (await PackageFiles.LoadAsync(folder)).Package.DatasetId);
        p.Tables["Movimento"][0]["Valor"] = "100";
        await File.WriteAllBytesAsync(packagePath, JsonSerializer.SerializeToUtf8Bytes(p));
        await Assert.ThrowsAsync<FormatException>(() => PackageFiles.LoadAsync(folder));
        Assert.False(File.Exists(DbPath));
    }

    private (LegacyPackage Package, string Backup) Fixture()
    {
        var backup = Path.Combine(folder, "source.accdb"); File.WriteAllText(backup, "synthetic source only");
        Dictionary<string, string?> Row(params string?[] pairs) => Enumerable.Range(0, pairs.Length / 2).ToDictionary(i => pairs[i * 2]!, i => pairs[i * 2 + 1]);
        var p = new LegacyPackage { DatasetId = "synthetic-source", SourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backup))),
            SourceLength = new FileInfo(backup).Length, Schema = JsonSerializer.SerializeToElement(new { Version = 1 }) };
        p.Tables["Elemento"] = [Row("ID","1","Nome","Pessoa de teste","DataNascimento","1980-01-01T00:00:00.0000000")];
        p.Tables["TipoMovimento"] = [Row("ID","1","Descricao","Receita"),Row("ID","2","Descricao","Despesa")];
        p.Tables["Categoria"] = [Row("ID","1","IdTipo","2","Descricao","Casa")];
        p.Tables["SubCategoria"] = [Row("ID","1","IdCategoria","1","Descricao","Água")];
        p.Tables["Movimento"] = [
            Row("ID","1","Dia","2012-01-01T00:00:00.0000000","IdTipo","2","IdElemento","1","IdCategoria","1","IdSubCategoria","1","Valor","12.34","Observacoes","d'Água e ação"),
            Row("ID","2","Dia","2012-01-01T00:00:00.0000000","IdTipo","2","IdElemento","1","IdCategoria","1","IdSubCategoria","1","Valor","12.34","Observacoes","d'Água e ação"),
            Row("ID","3","Dia","2012-02-29T00:00:00.0000000","IdTipo","2","IdElemento","1","IdCategoria","1","IdSubCategoria","1","Valor","-1.00","Observacoes",null)];
        p.Tables["Total"] = [Row("ID","1","Ano","2012","Mes","0","IdCategoria","0","IdSubCategoria","0","IdTipoMovimento","2","Valor","999")];
        p.Tables["OtherModule"] = [Row("ID","1","Extra","preserved, not interpreted")];
        return (p, backup);
    }
}
