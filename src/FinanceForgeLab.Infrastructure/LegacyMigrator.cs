using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed class LegacyMigrator(FinanceDb db)
{
    public async Task<MigrationReport> RunAsync(LegacyPackage p, string packageHash, string backupPath, string mode, CancellationToken ct = default)
    {
        var report = LegacyValidation.Validate(p);
        report.Mode = mode; report.PackageSha256 = packageHash;
        report.SchemaVersion = (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault() ?? "";
        await using (var file = File.OpenRead(backupPath))
            report.BackupVerified = file.Length == p.SourceLength && Convert.ToHexString(await SHA256.HashDataAsync(file, ct)) == p.SourceSha256;
        if (!report.BackupVerified) report.Issues.Add(new("Error", "SourceBackupMismatch", "", ""));
        if (report.HasErrors) return Finish(report, "Blocked");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var detectChanges = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var maps = await db.LegacyMaps.Where(x => x.DatasetId == p.DatasetId).ToDictionaryAsync(x => (x.Table, x.LegacyId), ct);
            var presentKeys = LegacyPackage.MappedTables.SelectMany(t => p.Tables[t].Select(r => (t, r["ID"]!))).ToHashSet();
            foreach (var key in maps.Keys.Where(k => !presentKeys.Contains(k)))
                report.Issues.Add(new("Error", "RemovedSourceConflict", key.Item1, key.Item2));
            // Verify all old identities before inserting anything. A changed source or edited destination is never overwritten.
            foreach (var table in LegacyPackage.MappedTables)
            foreach (var row in p.Tables[table])
            {
                var id = row["ID"]!;
                if (!maps.TryGetValue((table, id), out var map)) continue;
                if (map.RawSha256 != LegacyPackage.Hash(LegacyPackage.Canonical(row)))
                    report.Issues.Add(new("Error", "ChangedSourceConflict", table, id));
                else if (map.TargetSha256 != await TargetHashAsync(map, ct))
                    report.Issues.Add(new("Error", "ChangedDestinationConflict", table, id));
            }
            if (report.HasErrors) { await transaction.RollbackAsync(ct); return Finish(report, "Blocked"); }

            var dataset = await db.SourceDatasets.SingleOrDefaultAsync(x => x.Id == p.DatasetId, ct);
            if (dataset is null)
            {
                if (maps.Count != 0) throw new InvalidOperationException("OrphanedSourceMap");
                var household = await db.Households.SingleOrDefaultAsync(ct);
                if (household is null) { household = new Household(); db.Add(household); await db.SaveChangesAsync(ct); }
                dataset = new SourceDataset { Id = p.DatasetId, HouseholdId = household.Id, Currency = "EUR" };
                db.Add(dataset); await db.SaveChangesAsync(ct);
            }
            if (dataset.Currency != "EUR") throw new InvalidOperationException("DatasetCurrencyConflict");
            var snapshot = await db.SourceSnapshots.SingleOrDefaultAsync(x => x.DatasetId == p.DatasetId && x.PackageSha256 == packageHash, ct);
            Dictionary<(string, string), SourceRecord> raw;
            if (snapshot is null)
            {
                snapshot = new SourceSnapshot { DatasetId = p.DatasetId, SourceSha256 = p.SourceSha256,
                    PackageSha256 = packageHash, SchemaJson = p.Schema.GetRawText(), BackupPath = Path.GetFullPath(backupPath), CreatedUtc = DateTime.UtcNow };
                db.Add(snapshot); await db.SaveChangesAsync(ct);
                raw = [];
                foreach (var (table, rows) in p.Tables)
                foreach (var row in rows)
                {
                    var json = LegacyPackage.Canonical(row);
                    var record = new SourceRecord { SnapshotId = snapshot.Id, Table = table, LegacyId = row["ID"]!, RawJson = json, Sha256 = LegacyPackage.Hash(json) };
                    raw.Add((table, record.LegacyId), record); db.Add(record);
                }
                await db.SaveChangesAsync(ct); report.RawRowsArchived = raw.Count;
            }
            else
                raw = await db.SourceRecords.Where(x => x.SnapshotId == snapshot.Id).ToDictionaryAsync(x => (x.Table, x.LegacyId), ct);
            // Exact archive reconciliation, including tables and fields with no financial destination.
            if (raw.Count != p.Tables.Sum(x => x.Value.Count) || p.Tables.Any(t => t.Value.Any(r =>
                !raw.TryGetValue((t.Key, r["ID"]!), out var record) || record.RawJson != LegacyPackage.Canonical(r))))
                throw new InvalidOperationException("ArchiveReconciliationFailed");

            ImportBatch? batch = null;
            foreach (var table in LegacyPackage.MappedTables)
            {
                report.Inserted[table] = 0; report.Skipped[table] = 0;
                foreach (var row in p.Tables[table])
                {
                    var id = row["ID"]!;
                    if (maps.ContainsKey((table, id))) { report.Skipped[table]++; continue; }
                    if (batch is null)
                    {
                        batch = new ImportBatch { SnapshotId = snapshot.Id, Mode = mode, CreatedUtc = DateTime.UtcNow };
                        db.Add(batch); await db.SaveChangesAsync(ct);
                    }
                    long Reference(string sourceTable, string field) => maps[(sourceTable, LegacyPackage.Required(row, field))].TargetId;
                    object entity;
                    switch (table)
                    {
                        case "Elemento": entity = new Person { HouseholdId = dataset.HouseholdId, Name = LegacyPackage.Required(row, "Nome") }; break;
                        case "Categoria": entity = new Category { HouseholdId = dataset.HouseholdId, Name = LegacyPackage.Required(row, "Descricao"), Kind = LegacyPackage.Kind(row) }; break;
                        case "SubCategoria":
                            var parent = await db.Categories.FindAsync([Reference("Categoria", "IdCategoria")], ct) ?? throw new InvalidOperationException("MissingParent");
                            entity = new Category { HouseholdId = dataset.HouseholdId, Name = LegacyPackage.Required(row, "Descricao"), ParentId = parent.Id, Kind = parent.Kind }; break;
                        case "Movimento": entity = new FinancialTransaction { HouseholdId = dataset.HouseholdId, Date = LegacyPackage.Date(row), Kind = LegacyPackage.Kind(row),
                            PersonId = Reference("Elemento", "IdElemento"), Description = LegacyPackage.Text(row, "Observacoes"), Notes = LegacyPackage.Text(row, "Observacoes") }; break;
                        default: throw new InvalidOperationException("UnknownMapping");
                    }
                    db.Add(entity); await db.SaveChangesAsync(ct);
                    var targetId = (long)db.Entry(entity).Property("Id").CurrentValue!;
                    if (entity is FinancialTransaction movement)
                    {
                        db.Add(new Posting { TransactionId = targetId, CategoryId = Reference("SubCategoria", "IdSubCategoria"), Date = movement.Date,
                            AmountUnits = Money.SignedUnits(LegacyPackage.Amount(row), movement.Kind), Currency = "EUR" });
                        db.Add(new ImportRow { BatchId = batch.Id, SourceRecordId = raw[(table, id)].Id, TransactionId = targetId });
                        await db.SaveChangesAsync(ct);
                    }
                    var map = new LegacyMap { DatasetId = p.DatasetId, Table = table, LegacyId = id,
                        RawSha256 = raw[(table, id)].Sha256, TargetKind = entity.GetType().Name, TargetId = targetId };
                    map.TargetSha256 = await TargetHashAsync(map, ct);
                    db.Add(map); maps.Add((table, id), map); report.Inserted[table]++;
                }
            }
            if (batch is not null)
                db.Add(new AuditChange { Entity = nameof(ImportBatch), EntityId = batch.Id, Action = "LegacyImport", CreatedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
            await ReconcileAsync(p, maps, report, ct);
            report.IntegrityVerified = await DatabaseFiles.CheckIntegrityAsync(db, ct);
            if (!report.Reconciled || !report.IntegrityVerified) throw new InvalidOperationException("ReconciliationFailed");
            await transaction.CommitAsync(ct);
            return Finish(report, "Validated");
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            throw;
        }
        finally { db.ChangeTracker.AutoDetectChangesEnabled = detectChanges; }
    }

    private async Task<string> TargetHashAsync(LegacyMap map, CancellationToken ct)
    {
        object? target = map.TargetKind switch
        {
            nameof(Person) => await db.People.AsNoTracking().SingleOrDefaultAsync(x => x.Id == map.TargetId, ct),
            nameof(Category) => await db.Categories.AsNoTracking().SingleOrDefaultAsync(x => x.Id == map.TargetId, ct),
            nameof(FinancialTransaction) => await db.Transactions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == map.TargetId, ct),
            _ => null
        };
        if (target is null) return "missing";
        // Fingerprint v1 uses the original F1 fields. Additive schema upgrades must not invalidate every mapping.
        var json = target switch
        {
            Category c => JsonSerializer.Serialize(new { c.Id, c.HouseholdId, c.ParentId, c.Name, c.Kind, c.Active }),
            _ => JsonSerializer.Serialize(target, target.GetType())
        };
        if (target is FinancialTransaction)
            json += JsonSerializer.Serialize(await db.Postings.AsNoTracking().Where(x => x.TransactionId == map.TargetId).OrderBy(x => x.Id).ToListAsync(ct));
        return LegacyPackage.Hash(json);
    }

    private async Task ReconcileAsync(LegacyPackage p, Dictionary<(string, string), LegacyMap> maps, MigrationReport r, CancellationToken ct)
    {
        var transactions = await db.Transactions.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
        var postings = (await db.Postings.AsNoTracking().ToListAsync(ct)).ToLookup(x => x.TransactionId);
        var sourceSums = new Dictionary<string, decimal>(); var targetSums = new Dictionary<string, decimal>();
        void Add(Dictionary<string, decimal> sums, string key, decimal value) => sums[key] = checked(sums.GetValueOrDefault(key) + value);
        foreach (var row in p.Tables["Movimento"])
        {
            var movement = transactions[maps[("Movimento", row["ID"]!)].TargetId];
            var post = postings[movement.Id].Single();
            var expectedUnits = Money.SignedUnits(LegacyPackage.Amount(row), LegacyPackage.Kind(row));
            if (post.AmountUnits != expectedUnits || post.Currency != "EUR" || movement.Date != LegacyPackage.Date(row) || post.Date != movement.Date ||
                movement.Kind != LegacyPackage.Kind(row) || movement.PersonId != maps[("Elemento", row["IdElemento"]!)].TargetId ||
                post.CategoryId != maps[("SubCategoria", row["IdSubCategoria"]!)].TargetId || movement.Description != row["Observacoes"] || movement.Notes != row["Observacoes"])
                throw new InvalidOperationException("RecordReconciliationFailed");
            // Group IDs are source keys, so reports do not disclose names or descriptions.
            foreach (var key in new[] { "all", $"type:{row["IdTipo"]}", $"month:{movement.Date:yyyy-MM}/type:{row["IdTipo"]}",
                $"person:{row["IdElemento"]}/type:{row["IdTipo"]}", $"category:{row["IdCategoria"]}", $"subcategory:{row["IdSubCategoria"]}" })
            { Add(sourceSums, key, Money.FromUnits(expectedUnits)); Add(targetSums, key, Money.FromUnits(post.AmountUnits)); }
            if (post.AccountId is null) r.UnassignedAccountCount++;
        }
        // Require every operational mapping from this dataset to be represented; removed source rows are conflicts.
        r.Reconciled = p.Tables["Movimento"].Count == maps.Keys.Count(x => x.Item1 == "Movimento") && sourceSums.Count == targetSums.Count && sourceSums.All(x => targetSums[x.Key] == x.Value);
        r.SourceTotals = sourceSums.ToDictionary(x => x.Key, x => x.Value.ToString("0.0000", CultureInfo.InvariantCulture));
        r.TargetTotals = targetSums.ToDictionary(x => x.Key, x => x.Value.ToString("0.0000", CultureInfo.InvariantCulture));
    }

    private static MigrationReport Finish(MigrationReport r, string status) { r.Status = status; r.FinishedUtc = DateTime.UtcNow; return r; }
}
