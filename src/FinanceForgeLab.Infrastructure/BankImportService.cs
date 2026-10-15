using System.Security.Cryptography;
using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed class BankImportService(Func<FinanceDb> open, string archivePath)
{
    private static readonly SemaphoreSlim StageGate = new(1, 1);
    public async Task<(byte[] Content, string Name)> OriginalAsync(long importId)
    {
        await using var db = open();
        var original = await (from b in db.BankImports join d in db.BankDocuments on b.DocumentId equals d.Id where b.Id == importId select new { d.Content, d.Name }).SingleOrDefaultAsync() ?? throw new FinanceRuleException("Extrato não encontrado.");
        return (original.Content, original.Name);
    }
    public async Task<List<BankImportSummary>> ListAsync()
    {
        await using var db = open();
        var records = await (from b in db.BankImports.AsNoTracking() join d in db.BankDocuments.AsNoTracking() on b.DocumentId equals d.Id orderby b.Id descending select new { Import = b, d.Name, d.StatementJson }).Take(100).ToListAsync();
        return records.Select(x => { var s = JsonSerializer.Deserialize<BankStatement>(x.StatementJson)!.Accounts[x.Import.SectionIndex]; return new BankImportSummary(x.Import.Id, x.Name, s.DisplayReference, s.From, s.To, s.Rows.Count, x.Import.Status, x.Import.Version); }).ToList();
    }
    public async Task<List<long>> StageAsync(string name, byte[] content)
    {
        if (content.Length is < 5 or > 20 * 1024 * 1024 || !content.AsSpan(0, 5).SequenceEqual("%PDF-"u8)) throw new FinanceRuleException("Escolhe um PDF com até 20 MB.");
        var sha = Convert.ToHexString(SHA256.HashData(content));
        await StageGate.WaitAsync();
        try
        {
            await using var db = open();
            var savedJson = await db.BankDocuments.Where(x => x.Id == sha).Select(x => x.StatementJson).SingleOrDefaultAsync();
            if (savedJson is not null)
            {
                var saved = JsonSerializer.Deserialize<BankStatement>(savedJson)!;
                var active = await db.BankImports.Where(x => x.DocumentId == sha && x.Status != "Voided").ToListAsync();
                foreach (var (section, index) in saved.Accounts.Select((s, i) => (s, i)))
                    if (!active.Any(x => x.SectionIndex == index)) { var next = new BankImport { DocumentId = sha, SectionIndex = index, SourceReference = section.Reference }; db.Add(next); active.Add(next); }
                await db.SaveChangesAsync(); return active.OrderBy(x => x.SectionIndex).Select(x => x.Id).ToList();
            }
            Directory.CreateDirectory(archivePath); var file = Archive(sha);
            if (!File.Exists(file)) { await using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write); await stream.WriteAsync(content); }
            await CheckArchiveAsync(sha);
            BankStatement statement;
            try { statement = await Task.Run(() => NovoBancoStatementReader.Read(file)); }
            catch (FinanceRuleException) { throw; }
            catch (Exception e) when (e is not OutOfMemoryException) { throw new FinanceRuleException("Não foi possível interpretar este PDF novobanco. O ficheiro foi preservado para análise local."); }
            if (statement.Accounts.Count == 0) throw new FinanceRuleException(statement.Errors.FirstOrDefault() ?? "Não foram encontradas contas suportadas.");
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Add(new BankDocument { Id = sha, Name = Path.GetFileName(name)[..Math.Min(200, Path.GetFileName(name).Length)], ReaderVersion = statement.ReaderVersion, StatementJson = JsonSerializer.Serialize(statement), Content = content, CreatedUtc = DateTime.UtcNow });
            var batches = statement.Accounts.Select((account, i) => new BankImport { DocumentId = sha, SectionIndex = i, SourceReference = account.Reference }).ToList();
            db.AddRange(batches); await db.SaveChangesAsync(); await tx.CommitAsync(); return batches.Select(x => x.Id).ToList();
        }
        finally { StageGate.Release(); }
    }
    public async Task<BankImportPreview> PreviewAsync(long id, long? accountId = null)
    {
        await using var db = open(); var batch = await db.BankImports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id) ?? throw new FinanceRuleException("Importação não encontrada.");
        var savedJson = await db.BankDocuments.Where(x => x.Id == batch.DocumentId).Select(x => x.StatementJson).SingleAsync(); var statement = JsonSerializer.Deserialize<BankStatement>(savedJson)!; var section = statement.Accounts[batch.SectionIndex];
        var duplicates = new Dictionary<int, List<BankDuplicate>>();
        if (accountId is > 0)
        {
            var existing = await (from p in db.Postings.AsNoTracking() join t in db.Transactions.AsNoTracking() on p.TransactionId equals t.Id where p.AccountId == accountId && t.Status == "Confirmed" && t.Date >= section.From && t.Date <= section.To && (t.Kind == MovementKind.Income || t.Kind == MovementKind.Expense || t.Kind == MovementKind.Transfer) select new { t.Id, t.Version, t.Description, t.Kind, t.Date, p.AmountUnits }).ToListAsync();
            foreach (var row in section.Rows)
            {
                var matches = existing.Where(x => x.Date == row.Date && x.AmountUnits == row.Units).Select(x => new BankDuplicate(x.Id, x.Version, x.Description ?? "", x.Kind)).ToList();
                if (matches.Count > 0) duplicates.Add(row.Ordinal, matches);
            }
        }
        return new(batch, section, statement.Errors, duplicates, await db.People.AsNoTracking().OrderBy(x => x.Name).ToListAsync());
    }
    public async Task<long?> SuggestedAccountAsync(long id)
    {
        await using var db = open(); var reference = await db.BankImports.Where(x => x.Id == id).Select(x => x.SourceReference).SingleAsync();
        return await db.BankImports.Where(x => x.SourceReference == reference && x.Status == "Imported" && x.AccountId != null).Select(x => x.AccountId).FirstOrDefaultAsync();
    }
    public async Task SaveReviewAsync(BankImportReview review)
    {
        await using var db = open(); var batch = await db.BankImports.SingleOrDefaultAsync(x => x.Id == review.Id) ?? throw new FinanceRuleException("Importação não encontrada.");
        if (batch.Status != "Draft" || batch.Version != review.Version) throw new FinanceRuleException("A revisão foi alterada noutra sessão. Volta a abri-la.");
        batch.Version++; review.Version = batch.Version; batch.ReviewJson = JsonSerializer.Serialize(review); await db.SaveChangesAsync();
    }
    public async Task<BankImportResult> ConfirmAsync(BankImportReview review)
    {
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var batch = await db.BankImports.SingleOrDefaultAsync(x => x.Id == review.Id) ?? throw new FinanceRuleException("Importação não encontrada.");
        var hash = LegacyPackage.Hash(JsonSerializer.Serialize(review));
        if (batch.Status == "Imported" && batch.ReviewHash == hash) return await ResultAsync(db, batch.Id, review.Rows.Count);
        if (batch.Status != "Draft" || batch.Version != review.Version) throw new FinanceRuleException("Este lote já foi confirmado, anulado ou alterado. Volta a abri-lo.");
        var document = await db.BankDocuments.SingleAsync(x => x.Id == batch.DocumentId); var statement = Statement(document); var section = statement.Accounts[batch.SectionIndex];
        if (statement.Errors.Count > 0 || section.Errors.Count > 0) throw new FinanceRuleException("Resolve os erros de leitura e conferência antes de importar.");
        if (Convert.ToHexString(SHA256.HashData(document.Content)) != document.Id) throw new FinanceRuleException("O PDF original guardado foi alterado. A importação foi bloqueada.");
        if (review.Rows.Count != section.Rows.Count || review.Rows.Select(x => x.Ordinal).Distinct().Count() != section.Rows.Count || review.Rows.Any(x => !section.Rows.Any(r => r.Ordinal == x.Ordinal))) throw new FinanceRuleException("A revisão não corresponde a todas as linhas deste extrato.");
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.Id == review.AccountId && x.Active && x.Currency == "EUR") ?? throw new FinanceRuleException("Seleciona uma conta ativa em EUR.");
        if (await db.BankImports.AnyAsync(x => x.SourceReference == batch.SourceReference && x.Status == "Imported" && x.AccountId != null && x.AccountId != account.Id)) throw new FinanceRuleException("Esta conta do banco já está associada a outra conta da aplicação.");
        if (await db.BankImports.AnyAsync(x => x.SourceReference != batch.SourceReference && x.Status == "Imported" && x.AccountId == account.Id)) throw new FinanceRuleException("A conta selecionada já representa outro número de conta bancária.");
        if (review.PersonId is long personId && !await db.People.AnyAsync(x => x.Id == personId && x.HouseholdId == account.HouseholdId)) throw new FinanceRuleException("Pessoa inválida.");
        var categories = await db.Categories.ToListAsync(); var accounts = await db.Accounts.ToListAsync(); var linked = new HashSet<long>();
        var sameDocument = section.Rows.GroupBy(x => new { x.Date, x.Units, x.Description }).Where(g => g.Count() > 1).SelectMany(g => g.Select(x => x.Ordinal)).ToHashSet();
        foreach (var choice in review.Rows.OrderBy(x => x.Ordinal))
        {
            var row = section.Rows.Single(x => x.Ordinal == choice.Ordinal);
            if (choice.Decision == "Exclude") continue;
            if (choice.Decision is not ("Import" or "Link")) throw new FinanceRuleException($"Resolve a decisão da linha {row.Ordinal}.");
            var matches = await (from p in db.Postings join t in db.Transactions on p.TransactionId equals t.Id where p.AccountId == account.Id && t.Date == row.Date && p.AmountUnits == row.Units && t.Status == "Confirmed" && (t.Kind == MovementKind.Income || t.Kind == MovementKind.Expense || t.Kind == MovementKind.Transfer) select t).ToListAsync();
            if (choice.Decision == "Link")
            {
                var match = matches.SingleOrDefault(x => x.Id == choice.ExistingTransactionId && x.Version == choice.ExistingVersion);
                if (match is null || !linked.Add(match.Id)) throw new FinanceRuleException($"Linha {row.Ordinal}: o movimento a associar não é válido ou foi alterado.");
                db.Add(new BankImportEntry { ImportId = batch.Id, Ordinal = row.Ordinal, TransactionId = match.Id, TransactionVersion = match.Version, Created = false }); continue;
            }
            if ((matches.Count > 0 || sameDocument.Contains(row.Ordinal)) && !choice.AcknowledgeDuplicate) throw new FinanceRuleException($"Linha {row.Ordinal}: confirma que o possível duplicado é um movimento distinto, ou associa o existente.");
            if (choice.Kind is not (MovementKind.Income or MovementKind.Expense or MovementKind.Transfer)) throw new FinanceRuleException($"Linha {row.Ordinal}: tipo inválido.");
            if (row.Description.Length > 500) throw new FinanceRuleException($"Linha {row.Ordinal}: a descrição excede 500 caracteres.");
            Category? category = null; Account? counterpart = null;
            if (choice.Kind == MovementKind.Transfer)
            {
                counterpart = accounts.SingleOrDefault(x => x.Id == choice.CounterpartyAccountId && x.Id != account.Id && x.Active && x.Currency == "EUR" && x.HouseholdId == account.HouseholdId) ?? throw new FinanceRuleException($"Linha {row.Ordinal}: escolhe a outra conta da transferência.");
            }
            else
            {
                category = categories.SingleOrDefault(x => x.Id == choice.CategoryId && x.Active && x.Kind == choice.Kind && x.HouseholdId == account.HouseholdId) ?? throw new FinanceRuleException($"Linha {row.Ordinal}: escolhe uma categoria ativa do mesmo tipo.");
                if (category.ParentId is long parentId && !categories.Any(x => x.Id == parentId && x.Active)) throw new FinanceRuleException($"Linha {row.Ordinal}: a categoria de raiz está inativa.");
            }
            var movement = new FinancialTransaction { HouseholdId = account.HouseholdId, Date = row.Date, Kind = choice.Kind, Description = row.Description, PersonId = review.PersonId, Origin = "BankImport" };
            db.Add(movement); await db.SaveChangesAsync();
            db.Add(new Posting { TransactionId = movement.Id, AccountId = account.Id, CategoryId = category?.Id, Date = row.Date, ValueDate = row.ValueDate, AmountUnits = row.Units });
            if (counterpart is not null) db.Add(new Posting { TransactionId = movement.Id, AccountId = counterpart.Id, Date = row.Date, AmountUnits = checked(-row.Units) });
            db.Add(new BankImportEntry { ImportId = batch.Id, Ordinal = row.Ordinal, TransactionId = movement.Id, TransactionVersion = movement.Version, Created = true });
            db.Add(new AuditChange { Entity = "Transaction", EntityId = movement.Id, Action = "BankImport", Actor = "Owner", CreatedUtc = DateTime.UtcNow, DetailJson = JsonSerializer.Serialize(new { ImportId = batch.Id, Document = document.Id, Row = row, Kind = choice.Kind, choice.CategoryId, choice.CounterpartyAccountId }) });
        }
        batch.AccountId = account.Id; batch.PersonId = review.PersonId; batch.Status = "Imported"; batch.Version++; batch.ImportedUtc = DateTime.UtcNow; batch.ReviewHash = hash; batch.ReviewJson = JsonSerializer.Serialize(review);
        db.Add(new AuditChange { Entity = "BankImport", EntityId = batch.Id, Action = "Confirm", Actor = "Owner", CreatedUtc = DateTime.UtcNow, DetailJson = batch.ReviewJson });
        await db.SaveChangesAsync(); await tx.CommitAsync(); return await ResultAsync(db, batch.Id, review.Rows.Count);
    }
    public async Task VoidAsync(long id, int version)
    {
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var batch = await db.BankImports.SingleOrDefaultAsync(x => x.Id == id) ?? throw new FinanceRuleException("Importação não encontrada.");
        if (batch.Status != "Imported" || batch.Version != version) throw new FinanceRuleException("O lote foi alterado. Volta a abri-lo.");
        var entries = await db.BankImportEntries.Where(x => x.ImportId == id && x.Created).ToListAsync();
        foreach (var entry in entries)
        {
            var movement = await db.Transactions.SingleAsync(x => x.Id == entry.TransactionId);
            if (movement.Version != entry.TransactionVersion || movement.Status != "Confirmed" || await (from e in db.BankImportEntries join b in db.BankImports on e.ImportId equals b.Id where e.ImportId != id && e.TransactionId == movement.Id && b.Status == "Imported" select e.Id).AnyAsync()) throw new FinanceRuleException("O lote contém movimentos alterados ou associados a outro extrato. Resolve esses movimentos individualmente antes de anular.");
            movement.Status = "Voided"; movement.Version++;
        }
        batch.Status = "Voided"; batch.Version++;
        db.Add(new AuditChange { Entity = "BankImport", EntityId = id, Action = "Void", Actor = "Owner", CreatedUtc = DateTime.UtcNow, DetailJson = JsonSerializer.Serialize(new { Transactions = entries.Select(x => x.TransactionId) }) });
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    private string Archive(string sha) => Path.Combine(archivePath, sha + ".pdf");
    private async Task CheckArchiveAsync(string sha)
    {
        var path = Archive(sha);
        if (!File.Exists(path)) throw new FinanceRuleException("O PDF original arquivado não está disponível.");
        await using var stream = File.OpenRead(path);
        if (Convert.ToHexString(await SHA256.HashDataAsync(stream)) != sha) throw new FinanceRuleException("O PDF arquivado foi alterado. A importação foi bloqueada.");
    }
    private static BankStatement Statement(BankDocument document) => JsonSerializer.Deserialize<BankStatement>(document.StatementJson) ?? throw new FinanceRuleException("A leitura guardada não está disponível.");
    private static async Task<BankImportResult> ResultAsync(FinanceDb db, long id, int rows) => new(id, await db.BankImportEntries.CountAsync(x => x.ImportId == id && x.Created), await db.BankImportEntries.CountAsync(x => x.ImportId == id && !x.Created), rows - await db.BankImportEntries.CountAsync(x => x.ImportId == id));
}
