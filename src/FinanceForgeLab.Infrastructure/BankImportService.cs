using System.Security.Cryptography;
using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed partial class BankImportService(Func<FinanceDb> open, string archivePath)
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
        var documents = await db.BankImports.GroupBy(b => b.DocumentId).OrderByDescending(g => g.Max(b => b.Id)).Select(g => g.Key).ToListAsync();
        var records = await (from b in db.BankImports.Where(b => documents.Contains(b.DocumentId)).AsNoTracking() join d in db.BankDocuments.AsNoTracking() on b.DocumentId equals d.Id orderby b.Id descending select new { Import = b, d.Name, d.StatementJson }).ToListAsync();
        var accountNames = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name);
        var people = await db.People.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name);
        var registered = (await (from e in db.BankImportEntries join t in db.Transactions on e.TransactionId equals t.Id where t.Status == "Confirmed" select new { e.ImportId, e.Ordinal }).ToListAsync()).ToLookup(e => e.ImportId, e => e.Ordinal);
        var categories = await db.Categories.AsNoTracking().ToListAsync();
        var activeAccounts = await db.Accounts.AsNoTracking().Where(a => a.Active && a.Currency == "EUR").Select(a => a.Id).ToListAsync();
        return records.Select(x =>
        {
            var statement = JsonSerializer.Deserialize<BankStatement>(x.StatementJson)!;
            var s = statement.Accounts[x.Import.SectionIndex];
            var saved = string.IsNullOrEmpty(x.Import.ReviewJson) ? null : JsonSerializer.Deserialize<BankImportReview>(x.Import.ReviewJson);
            var accountId = x.Import.AccountId ?? saved?.AccountId;
            var personId = saved is null ? x.Import.PersonId : saved.PersonId;
            var recorded = registered[x.Import.Id].ToHashSet();
            var excluded = s.Rows.Where(r => !recorded.Contains(r.Ordinal) && (r.Units == 0 || saved?.Rows.Any(c => c.Ordinal == r.Ordinal && c.Decision == "Exclude") == true)).Select(r => r.Ordinal).ToHashSet();
            var remaining = saved?.Rows.Where(r => !recorded.Contains(r.Ordinal) && !excluded.Contains(r.Ordinal)).ToList() ?? [];
            var classified = remaining.Count(r => r.Decision is "Import" or "Pending" && (r.Kind == MovementKind.Transfer
                ? r.CounterpartyAccountId is {} other && other != accountId && activeAccounts.Contains(other)
                : categories.Any(c => c.Id == r.CategoryId && c.Active && c.Kind == r.Kind && (c.ParentId is null || categories.Any(p => p.Id == c.ParentId && p.Active)))));
            return new BankImportSummary(x.Import.Id, x.Name, s.DisplayReference, s.From, s.To, s.Rows.Count, x.Import.Status, x.Import.Version)
            {
                DocumentId = x.Import.DocumentId, SectionIndex = x.Import.SectionIndex, IsOnlineCopy = statement.ReaderVersion == OnlineBankStatementReader.Version,
                HasErrors = statement.Errors.Count > 0 || s.Errors.Count > 0,
                AccountId = accountId, AccountName = accountId is {} a ? accountNames.GetValueOrDefault(a) : null,
                RegisteredRows = recorded.Count, ExcludedRows = excluded.Count,
                ClassifiedRows = x.Import.Status == "Draft" ? classified : 0,
                AwaitingLinkRows = x.Import.Status == "Draft" ? remaining.Count(r => r.Decision == "Link") : 0,
                PersonName = personId is {} p ? people.GetValueOrDefault(p) : null
            };
        }).ToList();
    }
    public async Task<List<long>> StageAsync(string name, byte[] content) => await ReconcileStagedAsync(await StageCoreAsync(name, content, null));
    // Administrative ingestion of OCR results. The original PDF remains the document;
    // extracted lines are retained separately and parsed with the same balance checks.
    public async Task<List<long>> StageOcrAsync(string name, byte[] content, string expectedSha256, IReadOnlyList<BankPdfLine> lines)
    {
        var sha = Convert.ToHexString(SHA256.HashData(content));
        if (!sha.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase) || lines.Count is < 1 or > 20_000 || lines.Any(l => l.Page is < 1 or > 80 || l.Number < 1 || l.Cells.Length != 6))
            throw new FinanceRuleException("A extração OCR não corresponde ao PDF original.");
        return await ReconcileStagedAsync(await StageCoreAsync(name, content, lines));
    }
    private async Task<List<long>> StageCoreAsync(string name, byte[] content, IReadOnlyList<BankPdfLine>? ocrLines)
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
                await using var savedTx = await db.Database.BeginTransactionAsync();
                var saved = JsonSerializer.Deserialize<BankStatement>(savedJson)!;
                var active = await db.BankImports.Where(x => x.DocumentId == sha && x.Status != "Voided").ToListAsync();
                foreach (var (section, index) in saved.Accounts.Select((s, i) => (s, i)))
                    if (!active.Any(x => x.SectionIndex == index)) { var next = new BankImport { DocumentId = sha, SectionIndex = index, SourceReference = await CanonicalReferenceAsync(db, section) }; db.Add(next); active.Add(next); }
                await db.SaveChangesAsync();
                await AssociateAccountsAsync(db, saved, active);
                await db.SaveChangesAsync(); await savedTx.CommitAsync();
                return active.OrderBy(x => x.SectionIndex).Select(x => x.Id).ToList();
            }
            Directory.CreateDirectory(archivePath); var file = Archive(sha);
            if (!File.Exists(file)) { await using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write); await stream.WriteAsync(content); }
            await CheckArchiveAsync(sha);
            BankStatement statement;
            try
            {
                if (ocrLines is null) statement = await Task.Run(() => BankStatementReader.Read(file));
                else
                {
                    var original = BankPdfContent.Read(file);
                    if (ocrLines.Any(l => l.Page > original.Pages)) throw new FinanceRuleException("A extração OCR contém páginas inexistentes.");
                    statement = NovoBancoStatementReader.Parse(ocrLines);
                    statement.Sha256 = sha; statement.Pages = original.Pages; statement.ReaderVersion = "novobanco-ocr-1";
                    var proof = JsonSerializer.Serialize(new { OriginalSha256 = sha, Engine = "Tesseract", Lines = ocrLines });
                    var proofPath = file + ".ocr.json";
                    if (!File.Exists(proofPath)) await File.WriteAllTextAsync(proofPath, proof);
                    else if (await File.ReadAllTextAsync(proofPath) != proof) throw new FinanceRuleException("Já existe outra extração OCR para este PDF. Revê a versão arquivada.");
                }
            }
            catch (FinanceRuleException) { throw; }
            catch (Exception e) when (e is not OutOfMemoryException) { throw new FinanceRuleException("Não foi possível interpretar este PDF bancário. O ficheiro foi preservado para análise local."); }
            if (statement.Accounts.Count == 0) throw new FinanceRuleException(statement.Errors.FirstOrDefault() ?? "Não foram encontradas contas suportadas.");
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Add(new BankDocument { Id = sha, Name = Path.GetFileName(name)[..Math.Min(200, Path.GetFileName(name).Length)], ReaderVersion = statement.ReaderVersion, StatementJson = JsonSerializer.Serialize(statement), Content = content, CreatedUtc = DateTime.UtcNow });
            var batches = statement.Accounts.Select((account, i) => new BankImport { DocumentId = sha, SectionIndex = i, SourceReference = account.Reference }).ToList();
            foreach (var batch in batches)
                batch.SourceReference = await CanonicalReferenceAsync(db, statement.Accounts[batch.SectionIndex]);
            db.AddRange(batches); await db.SaveChangesAsync();
            await AssociateAccountsAsync(db, statement, batches);
            await db.SaveChangesAsync(); await tx.CommitAsync(); return batches.Select(x => x.Id).ToList();
        }
        finally { StageGate.Release(); }
    }
    private static async Task<string> CanonicalReferenceAsync(FinanceDb db, StatementAccount section)
    {
        var aliases = section.ReferenceAliases.Append(section.Reference).ToHashSet(StringComparer.Ordinal);
        var records = await (from b in db.BankImports join d in db.BankDocuments on b.DocumentId equals d.Id select new { b.SourceReference, b.SectionIndex, d.StatementJson }).ToListAsync();
        var matches = records.Where(r =>
        {
            var saved = JsonSerializer.Deserialize<BankStatement>(r.StatementJson)!.Accounts[r.SectionIndex];
            return aliases.Contains(r.SourceReference) || saved.ReferenceAliases.Append(saved.Reference).Any(aliases.Contains);
        }).Select(r => r.SourceReference).Distinct().ToList();
        if (matches.Count > 1) throw new FinanceRuleException("Os identificadores deste extrato estão associados a contas distintas. Revê as associações antes de importar.");
        return matches.SingleOrDefault() ?? section.Reference;
    }
    // BankImports retain the full reference even after a batch is voided. These durable
    // associations also cover accounts created from the existing statement archive.
    private static async Task AssociateAccountsAsync(FinanceDb db, BankStatement statement, List<BankImport> batches)
    {
        if (statement.Errors.Count > 0) return;
        foreach (var batch in batches.OrderBy(b => statement.Accounts[b.SectionIndex].From))
        {
            if (batch.Status != "Draft" || batch.AccountId is not null || batch.ReviewJson.Length > 0) continue;
            var section = statement.Accounts[batch.SectionIndex];
            if (section.Errors.Count > 0) continue;
            if (string.IsNullOrWhiteSpace(section.Reference) || section.OpeningUnits is null) continue;
            var known = await db.BankImports.Where(b => b.SourceReference == batch.SourceReference && b.AccountId != null).ToListAsync();
            var ids = known.Select(b => b.AccountId!.Value).Distinct().ToList();
            if (ids.Count > 1) throw new FinanceRuleException("Este número bancário está associado a várias contas. Revê as associações antes de carregar outro extrato.");
            Account account;
            if (ids.Count == 1)
            {
                account = await db.Accounts.SingleAsync(a => a.Id == ids[0]);
                var people = known.Where(b => b.PersonId != null).Select(b => b.PersonId).Distinct().ToList();
                if (people.Count == 1) batch.PersonId = people[0];
            }
            else
            {
                var household = await db.Households.SingleAsync();
                var product = section.Name.Contains("POUP", StringComparison.OrdinalIgnoreCase) ? "Poupança" : "Conta à ordem";
                var baseName = $"{statement.BankName} · {product} · {section.DisplayReference}";
                var names = (await db.Accounts.Select(a => a.Name).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var name = baseName;
                for (var suffix = 2; names.Contains(name); suffix++) name = $"{baseName} ({suffix})";
                account = new Account { HouseholdId = household.Id, Name = name, Type = product == "Poupança" ? "Savings" : "Bank", BalanceStartDate = section.From };
                db.Add(account); await db.SaveChangesAsync();
                long? openingId = null;
                if (section.OpeningUnits.Value != 0)
                {
                    var opening = new FinancialTransaction { HouseholdId = household.Id, Date = section.From, Kind = MovementKind.Opening, Description = $"Saldo de abertura conforme extrato {statement.BankName}", Origin = "BankStatement" };
                    db.Add(opening); await db.SaveChangesAsync(); openingId = opening.Id;
                    db.Add(new Posting { TransactionId = opening.Id, AccountId = account.Id, Date = section.From, AmountUnits = section.OpeningUnits.Value });
                }
                db.Add(new AuditChange { Entity = "Account", EntityId = account.Id, Action = "AutoCreateFromStatement", Actor = "Owner", CreatedUtc = DateTime.UtcNow,
                    DetailJson = JsonSerializer.Serialize(new { batch.DocumentId, batch.SectionIndex, section.Reference, section.Name, section.From, section.OpeningUnits, OpeningTransactionId = openingId }) });
            }
            batch.AccountId = account.Id; batch.Version++;
            db.Add(new AuditChange { Entity = "BankImport", EntityId = batch.Id, Action = "AssociateStatementAccount", Actor = "Owner", CreatedUtc = DateTime.UtcNow,
                DetailJson = JsonSerializer.Serialize(new { batch.AccountId, batch.PersonId, batch.SourceReference }) });
            // Persist each association so another section of this document can reuse it.
            await db.SaveChangesAsync();
        }
    }
    public async Task<BankImportPreview> PreviewAsync(long id, long? accountId = null)
    {
        await using var db = open(); var batch = await db.BankImports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id) ?? throw new FinanceRuleException("Importação não encontrada.");
        var savedJson = await db.BankDocuments.Where(x => x.Id == batch.DocumentId).Select(x => x.StatementJson).SingleAsync(); var statement = JsonSerializer.Deserialize<BankStatement>(savedJson)!; var section = statement.Accounts[batch.SectionIndex];
        var duplicates = await FindDuplicatesAsync(db, section, accountId ?? batch.AccountId);
        return new(batch, section, statement.Errors, duplicates, await db.People.AsNoTracking().OrderBy(x => x.Name).ToListAsync())
        {
            Notices = statement.Notices,
            ReviewWarnings = await FundingReviewWarningsAsync(db,section,accountId??batch.AccountId),
            RecordedTransactions = await (from e in db.BankImportEntries join t in db.Transactions on e.TransactionId equals t.Id where e.ImportId == id && t.Status == "Confirmed" select new { e.Ordinal, e.TransactionId }).ToDictionaryAsync(e => e.Ordinal, e => e.TransactionId),
            Suggestions = batch.Status == "Draft" ? await ClassifyAsync(db, section, accountId ?? batch.AccountId) : []
        };
    }
    public async Task<long?> SuggestedAccountAsync(long id)
    {
        await using var db = open(); var reference = await db.BankImports.Where(x => x.Id == id).Select(x => x.SourceReference).SingleAsync();
        return await db.BankImports.Where(x => x.SourceReference == reference && x.AccountId != null).Select(x => x.AccountId).FirstOrDefaultAsync();
    }
    public async Task SaveReviewAsync(BankImportReview review)
    {
        await using var db = open(); var batch = await db.BankImports.SingleOrDefaultAsync(x => x.Id == review.Id) ?? throw new FinanceRuleException("Importação não encontrada.");
        if (batch.Status != "Draft" || batch.Version != review.Version) throw new FinanceRuleException("A revisão foi alterada noutra sessão. Volta a abri-la.");
        batch.Version++; review.Version = batch.Version; batch.ReviewJson = JsonSerializer.Serialize(review); await db.SaveChangesAsync();
    }
    public Task<BankImportResult> ConfirmAsync(BankImportReview review) => ConfirmCoreAsync(review, null);
    // Confirm known rows while keeping the remaining review open. A durable source
    // entry prevents a later confirmation of the complete statement from duplicating them.
    public Task<BankImportResult> ConfirmRowsAsync(BankImportReview review, IEnumerable<int> ordinals)
    {
        var selected = ordinals.ToHashSet();
        if (selected.Count == 0 || selected.Any(o => !review.Rows.Any(r => r.Ordinal == o)))
            throw new FinanceRuleException("Seleciona linhas válidas do extrato.");
        var copy = JsonSerializer.Deserialize<BankImportReview>(JsonSerializer.Serialize(review))!;
        return ConfirmCoreAsync(copy, selected);
    }
    private async Task<BankImportResult> ConfirmCoreAsync(BankImportReview review, HashSet<int>? selected, FinanceDb? sharedDb = null)
    {
        await using var ownedDb = sharedDb is null ? open() : null;
        var db = sharedDb ?? ownedDb!;
        await using var tx = sharedDb is null ? await db.Database.BeginTransactionAsync() : null;
        var batch = await db.BankImports.SingleOrDefaultAsync(x => x.Id == review.Id) ?? throw new FinanceRuleException("Importação não encontrada.");
        var hash = LegacyPackage.Hash(JsonSerializer.Serialize(review));
        if (selected is null && batch.Status == "Imported" && batch.ReviewHash == hash) return await ResultAsync(db, batch.Id, review.Rows.Count);
        if (batch.Status != "Draft" || batch.Version != review.Version) throw new FinanceRuleException("Este lote já foi confirmado, anulado ou alterado. Volta a abri-lo.");
        var document = await db.BankDocuments.SingleAsync(x => x.Id == batch.DocumentId); var statement = Statement(document); var section = statement.Accounts[batch.SectionIndex];
        if (statement.Errors.Count > 0 || section.Errors.Count > 0) throw new FinanceRuleException("Resolve os erros de leitura e conferência antes de importar.");
        if (Convert.ToHexString(SHA256.HashData(document.Content)) != document.Id) throw new FinanceRuleException("A fonte original guardada foi alterada. A importação foi bloqueada.");
        if (review.Rows.Count != section.Rows.Count || review.Rows.Select(x => x.Ordinal).Distinct().Count() != section.Rows.Count || review.Rows.Any(x => !section.Rows.Any(r => r.Ordinal == x.Ordinal))) throw new FinanceRuleException("A revisão não corresponde a todas as linhas deste extrato.");
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.Id == review.AccountId && x.Active && x.Currency == "EUR") ?? throw new FinanceRuleException("Seleciona uma conta ativa em EUR.");
        if (await db.BankImports.AnyAsync(x => x.SourceReference == batch.SourceReference && x.AccountId != null && x.AccountId != account.Id)) throw new FinanceRuleException("Esta conta do banco já está associada a outra conta da aplicação.");
        if (await db.BankImports.AnyAsync(x => x.SourceReference != batch.SourceReference && x.AccountId == account.Id)) throw new FinanceRuleException("A conta selecionada já representa outro número de conta bancária.");
        if (review.PersonId is long personId && !await db.People.AnyAsync(x => x.Id == personId && x.HouseholdId == account.HouseholdId)) throw new FinanceRuleException("Pessoa inválida.");
        var categories = await db.Categories.ToListAsync(); var accounts = await db.Accounts.ToListAsync(); var linked = new HashSet<long>();
        var recorded = await db.BankImportEntries.Where(e => e.ImportId == batch.Id).ToDictionaryAsync(e => e.Ordinal);
        if (selected is not null) linked.UnionWith(recorded.Values.Where(e => !selected.Contains(e.Ordinal)).Select(e => e.TransactionId));
        if (recorded.Count > 0 && (batch.AccountId != review.AccountId || batch.PersonId != review.PersonId))
            throw new FinanceRuleException("Este extrato já tem linhas registadas. Mantém a conta e a pessoa associadas.");
        var processed = new List<(BankRowDecision Choice, long Id, int Version)>();
        var sameDocument = section.Rows.GroupBy(x => new { x.Date, x.Units }).Where(g => g.Count() > 1).SelectMany(g => g.Select(x => x.Ordinal)).ToHashSet();
        // Recheck against current data inside the confirmation transaction, including
        // movements entered manually since the review was opened.
        var duplicates = await FindDuplicatesAsync(db, section, account.Id);
        foreach (var choice in review.Rows.OrderBy(x => x.Ordinal))
        {
            if (selected is not null && !selected.Contains(choice.Ordinal)) continue;
            var row = section.Rows.Single(x => x.Ordinal == choice.Ordinal);
            if (recorded.TryGetValue(row.Ordinal, out var entry))
            {
                var recordedMovement = await db.Transactions.SingleAsync(t => t.Id == entry.TransactionId);
                if (choice.Decision != "Link" || choice.ExistingTransactionId != entry.TransactionId || choice.ExistingVersion != recordedMovement.Version || recordedMovement.Status != "Confirmed" ||
                    !await db.Postings.AnyAsync(p => p.TransactionId == recordedMovement.Id && p.AccountId == account.Id && p.AmountUnits == row.Units))
                    throw new FinanceRuleException($"Linha {row.Ordinal}: o movimento já registado foi alterado. Confere a associação existente.");
                if (!linked.Add(recordedMovement.Id)) throw new FinanceRuleException("O mesmo movimento foi associado a várias linhas do extrato.");
                entry.TransactionVersion = recordedMovement.Version;
                if(selected is not null&&recordedMovement.Kind is MovementKind.Income or MovementKind.Expense)
                {
                    choice.Kind=recordedMovement.Kind;
                    choice.CategoryId=(await db.Postings.SingleAsync(p=>p.TransactionId==recordedMovement.Id)).CategoryId;
                    choice.CounterpartyAccountId=null;
                }
                continue;
            }
            if (choice.Decision == "Exclude") continue;
            if (row.Units == 0) throw new FinanceRuleException($"Linha {row.Ordinal}: exclui a linha de valor zero; não representa uma entrada ou saída de dinheiro.");
            if (choice.Decision is not ("Import" or "Link")) throw new FinanceRuleException($"Resolve a decisão da linha {row.Ordinal}.");
            var matches = duplicates.GetValueOrDefault(row.Ordinal) ?? [];
            if (choice.Decision == "Link")
            {
                var match = matches.SingleOrDefault(x => x.TransactionId == choice.ExistingTransactionId && x.Version == choice.ExistingVersion);
                if (match is null || !linked.Add(match.TransactionId)) throw new FinanceRuleException($"Linha {row.Ordinal}: o movimento a associar não é válido, já foi usado nesta revisão ou foi alterado.");
                var linkedVersion = match.Version;
                var linkedMovement = await db.Transactions.SingleAsync(t => t.Id == match.TransactionId);
                if (selected is not null && linkedMovement.Kind is MovementKind.Income or MovementKind.Expense)
                {
                    var linkedPosting = await db.Postings.SingleAsync(p => p.TransactionId == linkedMovement.Id);
                    choice.Kind = linkedMovement.Kind; choice.CategoryId = linkedPosting.CategoryId; choice.CounterpartyAccountId = null;
                }
                else if (selected is not null && linkedMovement.Kind == MovementKind.Transfer)
                {
                    var posts = await db.Postings.Where(p=>p.TransactionId==linkedMovement.Id).ToListAsync();
                    if (posts.Count!=2 || !posts.Any(p=>p.AccountId==account.Id&&p.AmountUnits==row.Units) || posts.Sum(p=>p.AmountUnits)!=0)
                        throw new FinanceRuleException($"Linha {row.Ordinal}: a transferência existente não corresponde às duas contas e montantes.");
                    choice.Kind=MovementKind.Transfer; choice.CategoryId=null; choice.CounterpartyAccountId=posts.Single(p=>p.AccountId!=account.Id).AccountId;
                }
                // Confirming a legacy match identifies its missing bank account just
                // like a documented purchase; preserve the original classification.
                if (linkedMovement.Origin is "Purchase" or "Legacy" && linkedMovement.Kind is MovementKind.Expense or MovementKind.Income)
                {
                    var posting = await db.Postings.SingleAsync(p => p.TransactionId == linkedMovement.Id);
                    var purchaseEvidence = await db.PurchaseEvidence.SingleOrDefaultAsync(e => e.TransactionId == linkedMovement.Id);
                    if (posting.AccountId is null)
                    {
                        posting.AccountId = account.Id; linkedMovement.Version++; linkedVersion = linkedMovement.Version;
                        if (purchaseEvidence is not null) { purchaseEvidence.AccountId = account.Id; purchaseEvidence.Version++; }
                        db.Add(new AuditChange { Entity = "Transaction", EntityId = linkedMovement.Id, Action = linkedMovement.Origin == "Purchase" ? "IdentifyPurchaseAccount" : "IdentifyLegacyAccount", Actor = "Owner", CreatedUtc = DateTime.UtcNow,
                            DetailJson = JsonSerializer.Serialize(new { AccountId = account.Id, ImportId = batch.Id, row.Ordinal }) });
                    }
                }
                db.Add(new BankImportEntry { ImportId = batch.Id, Ordinal = row.Ordinal, TransactionId = match.TransactionId, TransactionVersion = linkedVersion, Created = false });
                processed.Add((choice, match.TransactionId, linkedVersion)); continue;
            }
            if (PossibleContinenteFunding(row) && !choice.ClassificationEdited &&
                !(await db.BankReferences.Where(r=>r.AccountId==account.Id&&r.Active&&r.CategoryId==choice.CategoryId&&r.PurchaseDate!=null).ToListAsync()).Any(r=>ReferenceMatches(r,row,categories)))
                throw new FinanceRuleException($"Linha {row.Ordinal}: possível carregamento Continente. Confere a finalidade e a classificação ou associa a compra existente antes de criar outra despesa.");
            if ((matches.Count > 0 || sameDocument.Contains(row.Ordinal)) && !choice.AcknowledgeDuplicate) throw new FinanceRuleException($"Linha {row.Ordinal}: confirma que o possível duplicado é um movimento distinto, ou associa o existente.");
            if (choice.Kind is not (MovementKind.Income or MovementKind.Expense or MovementKind.Transfer)) throw new FinanceRuleException($"Linha {row.Ordinal}: tipo inválido.");
            if (row.Description.Length > 500) throw new FinanceRuleException($"Linha {row.Ordinal}: a descrição excede 500 caracteres.");
            if (choice.Detail?.Length > 2000) throw new FinanceRuleException($"Linha {row.Ordinal}: o detalhe excede 2000 caracteres.");
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
            var movement = new FinancialTransaction { HouseholdId = account.HouseholdId, Date = row.Date, Kind = choice.Kind, Description = row.Description, Notes = string.IsNullOrWhiteSpace(choice.Detail) ? null : choice.Detail.Trim(), PersonId = review.PersonId, Origin = "BankImport" };
            db.Add(movement); await db.SaveChangesAsync();
            db.Add(new Posting { TransactionId = movement.Id, AccountId = account.Id, CategoryId = category?.Id, Date = row.Date, ValueDate = row.ValueDate, AmountUnits = row.Units });
            if (counterpart is not null) db.Add(new Posting { TransactionId = movement.Id, AccountId = counterpart.Id, Date = row.Date, AmountUnits = checked(-row.Units) });
            db.Add(new BankImportEntry { ImportId = batch.Id, Ordinal = row.Ordinal, TransactionId = movement.Id, TransactionVersion = movement.Version, Created = true });
            processed.Add((choice, movement.Id, movement.Version));
            db.Add(new AuditChange { Entity = "Transaction", EntityId = movement.Id, Action = "BankImport", Actor = "Owner", CreatedUtc = DateTime.UtcNow, DetailJson = JsonSerializer.Serialize(new { ImportId = batch.Id, Document = document.Id, Row = row, Kind = choice.Kind, choice.CategoryId, choice.CounterpartyAccountId }) });
        }
        batch.AccountId = account.Id; batch.PersonId = review.PersonId; batch.Version++;
        if (selected is null) { batch.Status = "Imported"; batch.ImportedUtc = DateTime.UtcNow; batch.ReviewHash = hash; }
        else foreach (var (choice, id, version) in processed)
        {
            choice.Decision = "Link"; choice.ExistingTransactionId = id; choice.ExistingVersion = version;
        }
        batch.ReviewJson = JsonSerializer.Serialize(review);
        db.Add(new AuditChange { Entity = "BankImport", EntityId = batch.Id, Action = selected is null ? "Confirm" : "ConfirmRows", Actor = "Owner", CreatedUtc = DateTime.UtcNow, DetailJson = JsonSerializer.Serialize(new { Rows = selected?.Order().ToArray(), Review = review }) });
        await db.SaveChangesAsync(); if (tx is not null) await tx.CommitAsync();
        return selected is null ? await ResultAsync(db, batch.Id, review.Rows.Count) : new(batch.Id,
            await db.BankImportEntries.CountAsync(e => e.ImportId == batch.Id && e.Created),
            await db.BankImportEntries.CountAsync(e => e.ImportId == batch.Id && !e.Created), review.Rows.Count(r => r.Decision == "Exclude"));
    }
    public async Task VoidAsync(long id, int version)
    {
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var batch = await db.BankImports.SingleOrDefaultAsync(x => x.Id == id) ?? throw new FinanceRuleException("Importação não encontrada.");
        if (batch.Status != "Imported" || batch.Version != version) throw new FinanceRuleException("O lote foi alterado. Volta a abri-lo.");
        var entries = await db.BankImportEntries.Where(x => x.ImportId == id && x.Created).ToListAsync();
        var transactionIds=entries.Select(e=>e.TransactionId).ToArray();
        if(await db.IndividualMovements.AnyAsync(m=>transactionIds.Contains(m.PaymentTransactionId)))throw new FinanceRuleException("O extrato tem movimentos individuais já tratados. Usa Editar / Anular na linha pretendida.");
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
