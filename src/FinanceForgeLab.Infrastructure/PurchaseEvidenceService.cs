using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed record EvidenceMatch(long? ImportId, int? Ordinal, long? TransactionId, long? AccountId, DateOnly Date, string Description);
public sealed partial class BankImportService
{
    public async Task<List<PurchaseEvidenceSource>> EvidenceSourcesAsync(IReadOnlyCollection<long>? evidenceIds=null)
    {
        await using var db = open();
        var query=db.PurchaseEvidenceSources.AsNoTracking();
        if(evidenceIds is not null){var ids=evidenceIds.ToArray();query=query.Where(s=>ids.Contains(s.EvidenceId));}
        return await query.OrderBy(s => s.Id).Select(s=>new PurchaseEvidenceSource{Id=s.Id,HouseholdId=s.HouseholdId,EvidenceId=s.EvidenceId,SourceKey=s.SourceKey,Source=s.Source,PayloadJson=s.PayloadJson,DocumentName=s.DocumentName,DocumentSha256=s.DocumentSha256}).ToListAsync();
    }

    private static string? PurchaseIdentity(PurchaseEvidence row)
    {
        var channel = Normalize(row.Channel);
        if (IsAmazon(channel)) channel = "AMAZON";
        if (channel == "GOLD PET") channel = "GOLDPET";
        var reference = row.OrderReference.Trim().Replace(" ", "").ToUpperInvariant();
        return channel.Length >= 3 && reference.Length >= 3 ? channel + "|" + reference : null;
    }

    public async Task<List<PurchaseEvidence>> EvidenceAsync()
    {
        await using var db = open();
        return await db.PurchaseEvidence.AsNoTracking().OrderByDescending(e => e.Date).ThenByDescending(e => e.Id).ToListAsync();
    }

    public async Task<List<long>> StageEvidenceAsync(List<PurchaseEvidence> rows)
    {
        if (rows.Count is 0 or > 100) throw new FinanceRuleException("Guarda entre 1 e 100 registos de cada vez.");
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var household = await db.Households.Select(h => h.Id).SingleAsync();
        var result = new List<long>();
        foreach (var row in rows)
        {
            if(row.Email is {} email)EmailProvenance.Validate(email);
            row.HouseholdId=household;
            await ValidateAllocationsAsync(db,row);
            if (row.SourceKey.Length is < 3 or > 250 || row.Source.Length is < 3 or > 1000 || row.Description.Length is < 2 or > 2000 || row.Details.Length > 10000 || row.ReviewNotes.Length > 2000 || row.Units is <= 0 || row.Channel.Length > 150 || row.Merchant.Length > 200 || row.OrderReference.Length > 200)
                throw new FinanceRuleException("Dados da compra incompletos ou demasiado longos.");
            if (row.CategoryId is {} categoryId && !await db.Categories.AnyAsync(c => c.Id == categoryId && c.HouseholdId == household && c.Active && c.Kind == MovementKind.Expense))
                throw new FinanceRuleException("Categoria proposta inválida.");
            var source = await db.PurchaseEvidenceSources.SingleOrDefaultAsync(e => e.HouseholdId == household && e.SourceKey == row.SourceKey);
            if (source is not null)
            {
                var original = JsonSerializer.Deserialize<PurchaseEvidence>(source.PayloadJson)!;
                if (original.Date != row.Date || original.ExpectedPaymentDate != row.ExpectedPaymentDate || original.Units != row.Units || original.FinancialUnits!=row.FinancialUnits || PurchaseIdentity(original) != PurchaseIdentity(row)) throw new FinanceRuleException("Esta origem já foi guardada com outra data, montante ou encomenda. Confere os documentos.");
                result.Add(source.EvidenceId); continue;
            }
            var identity = PurchaseIdentity(row);
            var existing = identity is null ? null : await db.PurchaseEvidence.SingleOrDefaultAsync(e => e.HouseholdId == household && e.OrderIdentity == identity);
            if (existing is not null)
            {
                // The order identifies the purchase. Conflicting documents remain attached and visible,
                // but cannot silently change the approved amount/category or create another expense.
                var conflict = row.Units is not null && existing.Units is not null && row.Units != existing.Units
                    || row.Date is not null && existing.Date is not null && row.Date != existing.Date
                    || row.ExpectedPaymentDate is not null && existing.ExpectedPaymentDate is not null && row.ExpectedPaymentDate != existing.ExpectedPaymentDate
                    || row.FinancialUnits is not null && (existing.FinancialUnits is not null || existing.TransactionId is not null) && row.FinancialUnits!=(existing.FinancialUnits??existing.Units);
                existing.HasSourceConflict |= conflict;
                if (!conflict) existing.ExpectedPaymentDate ??= row.ExpectedPaymentDate;
                if (existing.BankReferenceId is null && existing.TransactionId is null)
                {
                    existing.Date ??= row.Date; existing.Units ??= row.Units; existing.CategoryId ??= row.CategoryId;
                    existing.FinancialUnits??=row.FinancialUnits;
                    if(existing.PaymentMethod.Length==0)existing.PaymentMethod=row.PaymentMethod;
                    if(existing.CategoryAllocationsJson=="[]")existing.CategoryAllocationsJson=row.CategoryAllocationsJson;
                }
                if (conflict && existing.BankReferenceId is {} referenceId)
                {
                    var reference = await db.BankReferences.SingleAsync(r => r.Id == referenceId);
                    reference.Active = false; reference.Version++;
                }
                existing.Version++;
                db.Add(new PurchaseEvidenceSource { HouseholdId = household, EvidenceId = existing.Id, SourceKey = row.SourceKey, Source = row.Source, PayloadJson = JsonSerializer.Serialize(row) });
                db.Add(new AuditChange { Entity = "PurchaseEvidence", EntityId = existing.Id, Action = "AttachSource", Actor = "Owner", CreatedUtc = DateTime.UtcNow, DetailJson = JsonSerializer.Serialize(new { row.SourceKey, Conflict = conflict }) });
                await db.SaveChangesAsync(); result.Add(existing.Id); continue;
            }
            var evidence = new PurchaseEvidence { HouseholdId = household, SourceKey = row.SourceKey, Source = row.Source, Channel = row.Channel,
                Merchant = row.Merchant, OrderReference = row.OrderReference, Date = row.Date, ExpectedPaymentDate = row.ExpectedPaymentDate, Units = row.Units, CategoryId = row.CategoryId,
                FinancialUnits=row.FinancialUnits,PaymentMethod=row.PaymentMethod,CategoryAllocationsJson=row.CategoryAllocationsJson,
                Description = row.Description, Details = row.Details, ReviewNotes = row.ReviewNotes, OrderIdentity = identity };
            db.Add(evidence); await db.SaveChangesAsync(); result.Add(evidence.Id);
            db.Add(new PurchaseEvidenceSource { HouseholdId = household, EvidenceId = evidence.Id, SourceKey = row.SourceKey, Source = row.Source, PayloadJson = JsonSerializer.Serialize(row) });
            db.Add(new AuditChange { Entity = "PurchaseEvidence", EntityId = evidence.Id, Action = "Stage", Actor = "Owner", CreatedUtc = DateTime.UtcNow, DetailJson = JsonSerializer.Serialize(evidence) });
            await db.SaveChangesAsync();
        }
        await db.SaveChangesAsync(); await tx.CommitAsync(); return result;
    }

    public async Task<Dictionary<long, List<EvidenceMatch>>> EvidenceMatchesAsync(IReadOnlyCollection<long>? evidenceIds=null)
    {
        await using var db = open();var query=db.PurchaseEvidence.AsNoTracking();
        if(evidenceIds is not null){var ids=evidenceIds.ToArray();query=query.Where(e=>ids.Contains(e.Id));}
        var evidence = await query.ToListAsync();
        var result = evidence.ToDictionary(e => e.Id, _ => new List<EvidenceMatch>());
        foreach(var e in evidence.Where(e=>e.PaymentMethod=="TopUp"&&e.TransactionId!=null&&!e.HasSourceConflict))
        {
            var t=await db.Transactions.AsNoTracking().SingleOrDefaultAsync(t=>t.Id==e.TransactionId&&t.Status=="Confirmed"&&t.Kind==MovementKind.Transfer);
            var posts=await db.Postings.AsNoTracking().Where(p=>p.TransactionId==e.TransactionId).ToListAsync();
            if(t is not null&&posts.Count==2&&posts.Sum(p=>p.AmountUnits)==0&&posts.Any(p=>p.AccountId==e.AccountId&&p.AmountUnits==e.Units))result[e.Id].Add(new(null,null,t.Id,e.AccountId,t.Date,t.Description??"Carregamento"));
        }
        var dated = evidence.Where(e => e.PaymentMethod!="TopUp"&&e.Date != null && (e.FinancialUnits??e.Units) > 0 && !e.HasSourceConflict).ToList(); if (dated.Count == 0) return result;
        var dates = dated.SelectMany(e => new[] { e.Date!.Value, e.ExpectedPaymentDate ?? e.Date.Value }).ToList();
        var firstDate = DateOnly.FromDayNumber(Math.Max(0,dates.Min().DayNumber-7)); var lastDate = DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber, dates.Max().DayNumber + 7));
        var transactions = await (from t in db.Transactions.AsNoTracking() join p in db.Postings.AsNoTracking() on t.Id equals p.TransactionId
            where t.Status == "Confirmed" && t.Kind == MovementKind.Expense && t.Date >= firstDate && t.Date <= lastDate && p.AmountUnits < 0
            select new { t.Id, t.HouseholdId, t.Date, t.Description, p.AmountUnits, p.AccountId }).ToListAsync();
        var drafts = await (from b in db.BankImports.AsNoTracking() join d in db.BankDocuments.AsNoTracking() on b.DocumentId equals d.Id
            join a in db.Accounts.AsNoTracking() on b.AccountId equals a.Id where b.Status == "Draft"
            select new { b.Id, b.AccountId, b.SectionIndex, d.StatementJson, a.HouseholdId }).ToListAsync();
        var sections=drafts.Select(d=>new{d.Id,d.AccountId,d.HouseholdId,Statement=JsonSerializer.Deserialize<BankStatement>(d.StatementJson)!.Accounts[d.SectionIndex]}).ToList();
        var paypalSources=await DocumentedPayPalAsync(db,dated);
        foreach (var item in dated)
        {
            foreach (var t in transactions.Where(t => t.HouseholdId == item.HouseholdId && Matches(item, t.Date, t.AmountUnits, t.Description ?? "", t.AccountId,paypalSources.Contains(item.Id))))
                result[item.Id].Add(new(null, null, t.Id, t.AccountId, t.Date, t.Description ?? ""));
            foreach (var draft in sections.Where(d => d.HouseholdId == item.HouseholdId))
            {
                foreach (var row in draft.Statement.Rows.Where(r => Matches(item, r.Date, r.Units, r.Description, draft.AccountId,paypalSources.Contains(item.Id))))
                    result[item.Id].Add(new(draft.Id, row.Ordinal, null, draft.AccountId, row.Date, row.Description));
            }
        }
        return result;
    }

    private static async Task<HashSet<long>> DocumentedPayPalAsync(FinanceDb db,IEnumerable<PurchaseEvidence> purchases)
    {
        var items=purchases.Where(e=>!e.HasSourceConflict&&e.Date!=null&&(e.FinancialUnits??e.Units)>0&&e.PaymentMethod is not ("Split" or "TopUp")).ToDictionary(e=>e.Id);
        var ids=items.Keys.ToArray();var result=new HashSet<long>();
        foreach(var source in await db.PurchaseEvidenceSources.AsNoTracking().Where(s=>ids.Contains(s.EvidenceId)).Select(s=>new{s.EvidenceId,s.HouseholdId,s.PayloadJson}).ToListAsync())
        {
            try{
                var proof=JsonSerializer.Deserialize<PurchaseEvidence>(source.PayloadJson);var item=items[source.EvidenceId];
                if(proof is not null&&source.HouseholdId==item.HouseholdId&&Normalize(proof.Channel)=="PAYPAL"&&proof.Date is {} paid&&
                    Math.Abs(paid.DayNumber-item.Date!.Value.DayNumber)<=7&&(proof.FinancialUnits??proof.Units)==(item.FinancialUnits??item.Units)&&
                    !proof.HasSourceConflict&&proof.PaymentMethod is not ("Split" or "TopUp" or "Authorization")&&
                    !Normalize(proof.Email?.Subject??"").Contains("AUTORIZ")&&!Normalize(proof.Email?.Subject??"").Contains("AUTHORIZED"))result.Add(item.Id);
            }catch(JsonException){ /* An unreadable source is not payment evidence. */ }
        }
        return result;
    }

    private static bool Matches(PurchaseEvidence item, DateOnly date, long units, string description, long? account,bool documentedPayPal=false)
    {
        // A receipt/invoice may be issued after payment (e.g. a Friday payment
        // invoiced on Monday). Keep both dates and require merchant and amount too.
        bool InWindow(DateOnly? start) => start is {} d && Math.Abs(date.DayNumber - d.DayNumber) <= 7;
        if ((item.FinancialUnits??item.Units) is not > 0 || (!InWindow(item.Date) && !InWindow(item.ExpectedPaymentDate)) || units != -(item.FinancialUnits??item.Units) || (item.AccountId != null && account != item.AccountId)) return false;
        var text = Normalize(description); var channel = Normalize(item.Channel);
        // Via Verde statements identify the actual debit day and payment card even
        // when the bank labels it only as a low-value card payment. Require all three
        // (day, amount, card); nearby dates alone must not match recurring charges.
        if (channel == "VIA VERDE" && text.StartsWith("PAGAMENTO BAIXO VALOR CART", StringComparison.Ordinal))
        {
            var bankCard = System.Text.RegularExpressions.Regex.Match(text, @"^PAGAMENTO BAIXO VALOR CART(?:AO|AEO) (\d{3,})$");
            var documentedCard = System.Text.RegularExpressions.Regex.Match(Normalize(item.Details), @"\bCARTAO TERMINADO EM (\d{4})\b");
            return item.Date == date && bankCard.Success && documentedCard.Success &&
                documentedCard.Groups[1].Value.EndsWith(bankCard.Groups[1].Value[^Math.Min(4, bankCard.Groups[1].Value.Length)..], StringComparison.Ordinal);
        }
        return documentedPayPal&&ContainsWords(text,"PAYPAL") || channel.Length >= 3 && (ContainsWords(text, channel) || channel == "AMAZON" && IsAmazon(text) ||
            channel == "GOLDPET" && ContainsWords(text,"GOLD PET") || channel == "GOLD PET" && ContainsWords(text,"GOLDPET"));
    }

    public async Task LinkEvidenceAsync(long id, int version, long transactionId)
    {
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var evidence = await db.PurchaseEvidence.SingleOrDefaultAsync(e => e.Id == id) ?? throw new FinanceRuleException("Compra não encontrada.");
        if(evidence.PaymentMethod=="TopUp")throw new FinanceRuleException("Um carregamento deve ser associado a uma transferência, nunca criado como despesa.");
        if (evidence.Version != version) throw new FinanceRuleException("A compra foi alterada. Atualiza a lista.");
        if (evidence.HasSourceConflict || evidence.Date is null || evidence.Units is not > 0) throw new FinanceRuleException("Resolve primeiro os dados da compra.");
        await ValidateAllocationsAsync(db,evidence);
        if (evidence.TransactionId is not null && evidence.TransactionId != transactionId) throw new FinanceRuleException("Esta compra já está associada a outro movimento.");
        if (await db.PurchaseEvidence.AnyAsync(e => e.TransactionId == transactionId && e.Id != id)) throw new FinanceRuleException("Este movimento já está associado a outra compra. Confere as duas compras.");
        var movement = await db.Transactions.SingleOrDefaultAsync(t => t.Id == transactionId && t.HouseholdId == evidence.HouseholdId && t.Status == "Confirmed" && t.Kind == MovementKind.Expense);
        var postings = await db.Postings.Where(p => p.TransactionId == transactionId && p.AmountUnits < 0).ToListAsync();
        var paypalSources=await DocumentedPayPalAsync(db,[evidence]);
        if (movement is null || postings.Count != 1 || !Matches(evidence, movement.Date, postings[0].AmountUnits, movement.Description ?? "", postings[0].AccountId,paypalSources.Contains(id)))
            throw new FinanceRuleException("O movimento já não corresponde à compra. Confere a entidade, a data, a conta e o montante.");
        evidence.TransactionId = transactionId; evidence.AccountId = postings[0].AccountId; evidence.Version++;
        db.Add(new AuditChange { Entity = "PurchaseEvidence", EntityId = id, Action = "LinkTransaction", Actor = "Owner", CreatedUtc = DateTime.UtcNow, DetailJson = JsonSerializer.Serialize(new { TransactionId = transactionId }) });
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    public async Task ResolveEvidenceSourceAsync(long id, int version, long sourceId)
    {
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var evidence = await db.PurchaseEvidence.SingleOrDefaultAsync(e => e.Id == id) ?? throw new FinanceRuleException("Compra não encontrada.");
        if (evidence.Version != version) throw new FinanceRuleException("A compra foi alterada. Atualiza a lista.");
        if (evidence.TransactionId is not null) throw new FinanceRuleException("A compra já está associada a um movimento. Confere o movimento antes de alterar os dados da compra.");
        var source = await db.PurchaseEvidenceSources.SingleOrDefaultAsync(s => s.Id == sourceId && s.EvidenceId == id) ?? throw new FinanceRuleException("Documento não encontrado.");
        var data = JsonSerializer.Deserialize<PurchaseEvidence>(source.PayloadJson)!;
        if (data.Date is null || data.Units is not > 0) throw new FinanceRuleException("Este documento não tem data e montante completos.");
        if(evidence.PaymentMethod=="Split")throw new FinanceRuleException("Esta encomenda já foi repartida por cobranças. Confere os respetivos movimentos.");
        evidence.Date = data.Date; evidence.ExpectedPaymentDate = data.ExpectedPaymentDate; evidence.Units = data.Units; evidence.FinancialUnits=data.FinancialUnits;evidence.PaymentMethod=data.PaymentMethod;evidence.CategoryAllocationsJson=data.CategoryAllocationsJson;
        await ValidateAllocationsAsync(db,evidence);evidence.HasSourceConflict = false; evidence.Version++;
        if (evidence.BankReferenceId is {} referenceId)
        {
            var reference = await db.BankReferences.SingleAsync(r => r.Id == referenceId);
            reference.Active = false; reference.Version++;
        }
        db.Add(new AuditChange { Entity = "PurchaseEvidence", EntityId = id, Action = "ResolveSource", Actor = "Owner", CreatedUtc = DateTime.UtcNow, DetailJson = JsonSerializer.Serialize(new { SourceId = sourceId, evidence.Date, evidence.Units }) });
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    public async Task UnlinkEvidenceAsync(long id, int version)
    {
        await using var db = open();
        var associated=await db.PurchaseEvidence.Where(e=>e.Id==id).Select(e=>e.TransactionId).SingleAsync();
        if(associated is {} transaction&&await db.IndividualMovements.AnyAsync(m=>m.PaymentTransactionId==transaction))throw new FinanceRuleException("Esta origem está associada a movimentos individuais. Edita o movimento pretendido na lista.");
        var evidence = await db.PurchaseEvidence.SingleOrDefaultAsync(e => e.Id == id) ?? throw new FinanceRuleException("Compra não encontrada.");
        if (evidence.Version != version) throw new FinanceRuleException("A compra foi alterada. Atualiza a lista.");
        var previous = evidence.TransactionId; evidence.TransactionId = null; evidence.Version++;
        db.Add(new AuditChange { Entity = "PurchaseEvidence", EntityId = id, Action = "UnlinkTransaction", Actor = "Owner", CreatedUtc = DateTime.UtcNow, DetailJson = JsonSerializer.Serialize(new { TransactionId = previous }) });
        await db.SaveChangesAsync();
    }

    // A complete purchase can become the financial record before any bank statement arrives.
    // Creation and the source link share a transaction, so a retry cannot create a second expense.
    public async Task<long> ConfirmEvidenceMovementAsync(long id, int version, long? accountId, long categoryId, bool checkedDetails, bool distinctPurchase = false)
    {
        if (!checkedDetails) throw new FinanceRuleException("Confirma os dados antes de criar o movimento.");
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var evidence = await db.PurchaseEvidence.SingleOrDefaultAsync(e => e.Id == id) ?? throw new FinanceRuleException("Compra não encontrada.");
        if(evidence.PaymentMethod=="TopUp")throw new FinanceRuleException("Um carregamento deve ser associado a uma transferência, nunca criado como despesa.");
        if((evidence.FinancialUnits??evidence.Units) is not > 0)throw new FinanceRuleException(evidence.PaymentMethod=="Split"?"Esta encomenda já está repartida por cobranças; usa os movimentos de cada envio.":"Este documento regista consumo de benefícios e não origina um débito bancário.");
        await ValidateAllocationsAsync(db,evidence);
        var financialUnits=(evidence.FinancialUnits??evidence.Units)!.Value;
        if (evidence.TransactionId is {} existingId)
        {
            if (evidence.HasSourceConflict || (accountId is not null && evidence.AccountId != accountId) || evidence.CategoryId != categoryId ||
                !await db.Transactions.AnyAsync(t => t.Id == existingId && t.Status == "Confirmed"))
                throw new FinanceRuleException("Esta compra já tem um movimento associado. Confere-o na lista de movimentos.");
            return existingId;
        }
        if (evidence.Version != version) throw new FinanceRuleException("A compra foi alterada. Atualiza a lista.");
        if (evidence.HasSourceConflict || evidence.Date is null || evidence.Units is not > 0 || Normalize(evidence.Channel).Length < 3)
            throw new FinanceRuleException("Confirma a data, o montante, a entidade e eventuais divergências entre documentos.");
        if (accountId is not null && !await db.Accounts.AnyAsync(a => a.Id == accountId && a.HouseholdId == evidence.HouseholdId && a.Active && a.Currency == "EUR"))
            throw new FinanceRuleException("Escolhe a conta do pagamento; não é necessário esperar pelo extrato.");
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == categoryId && c.HouseholdId == evidence.HouseholdId && c.Active && c.Kind == MovementKind.Expense)
            ?? throw new FinanceRuleException("Escolhe uma categoria de despesa ativa.");
        if (category.ParentId is {} parent && !await db.Categories.AnyAsync(c => c.Id == parent && c.Active)) throw new FinanceRuleException("A categoria principal está inativa.");
        var first = DateOnly.FromDayNumber(Math.Max(0, Math.Min(evidence.Date.Value.DayNumber, (evidence.ExpectedPaymentDate ?? evidence.Date.Value).DayNumber) - 7));
        var last = DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber, Math.Max(evidence.Date.Value.DayNumber, (evidence.ExpectedPaymentDate ?? evidence.Date.Value).DayNumber) + 7));
        var candidates = await (from t in db.Transactions join p in db.Postings on t.Id equals p.TransactionId
            where t.HouseholdId == evidence.HouseholdId && t.Status == "Confirmed" && t.Kind == MovementKind.Expense &&
                (accountId == null || p.AccountId == null || p.AccountId == accountId) && p.AmountUnits == -financialUnits && t.Date >= first && t.Date <= last
            select new { t.Id, t.Date, t.Description }).ToListAsync();
        var channel = Normalize(evidence.Channel);
        var paypalSources=await DocumentedPayPalAsync(db,[evidence]);
        var duplicates = candidates.Where(t => t.Date == evidence.Date || Matches(evidence,t.Date,-financialUnits,t.Description ?? "",accountId,paypalSources.Contains(id)) ||
            Math.Abs(t.Date.DayNumber-evidence.Date.Value.DayNumber)<=7 && (ContainsWords(Normalize(t.Description ?? ""), channel) || channel == "AMAZON" && IsAmazon(Normalize(t.Description ?? "")))).ToList();
        if (duplicates.Count > 0 && !distinctPurchase)
            throw new FinanceRuleException("Já existe um possível movimento desta compra (" + string.Join(", ", duplicates.Select(t => "#" + t.Id)) + "). Associa o existente ou confirma que é uma compra distinta.");
        var description = evidence.Channel + " · " + evidence.Description;
        var notes = (evidence.OrderReference.Length > 0 ? "Encomenda " + evidence.OrderReference + "\n" : "") + evidence.Details;
        var movement = new FinancialTransaction { HouseholdId = evidence.HouseholdId, Date = evidence.Date.Value,
            Kind = MovementKind.Expense, Origin = "Purchase", Description = description[..Math.Min(500, description.Length)], Notes = notes[..Math.Min(2000, notes.Length)] };
        db.Add(movement); await db.SaveChangesAsync();
        db.Add(new Posting { TransactionId = movement.Id, AccountId = accountId, CategoryId = categoryId, Date = movement.Date, AmountUnits = -financialUnits });
        evidence.TransactionId = movement.Id; evidence.AccountId = accountId; evidence.CategoryId = categoryId; evidence.Version++;
        db.Add(new AuditChange { Entity = "Transaction", EntityId = movement.Id, Action = "CreateFromPurchase", Actor = "Owner", CreatedUtc = DateTime.UtcNow,
            DetailJson = JsonSerializer.Serialize(new { EvidenceId = id, Sources = await db.PurchaseEvidenceSources.Where(s => s.EvidenceId == id).Select(s => s.Id).ToListAsync(), DistinctPurchase = distinctPurchase }) });
        db.Add(new AuditChange { Entity = "PurchaseEvidence", EntityId = id, Action = "CreateMovement", Actor = "Owner", CreatedUtc = DateTime.UtcNow, DetailJson = JsonSerializer.Serialize(evidence) });
        await db.SaveChangesAsync();
        await ReconcileNewPurchaseAsync(db,evidence);
        await tx.CommitAsync(); return movement.Id;
    }

    public async Task ApproveEvidenceAsync(long id, int version, long accountId, long categoryId, bool checkedDetails)
    {
        if (!checkedDetails) throw new FinanceRuleException("Confirma os dados e os avisos antes de aprovar a referência.");
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var evidence = await db.PurchaseEvidence.SingleOrDefaultAsync(e => e.Id == id) ?? throw new FinanceRuleException("Compra não encontrada.");
        if(evidence.PaymentMethod=="TopUp")throw new FinanceRuleException("Um carregamento deve ser associado a uma transferência, nunca criado como despesa.");
        if (evidence.Version != version) throw new FinanceRuleException("Os dados foram alterados. Atualiza a lista.");
        if (evidence.HasSourceConflict) throw new FinanceRuleException("Os documentos divergem. Escolhe primeiro a data e o montante corretos.");
        if((evidence.FinancialUnits??evidence.Units) is not > 0)throw new FinanceRuleException(evidence.PaymentMethod=="Split"?"Esta encomenda já está repartida; a referência bancária pertence à cobrança de cada envio.":"Consumo de benefícios não cria uma referência de débito bancário.");
        if (evidence.TransactionId is not null && evidence.AccountId != accountId) throw new FinanceRuleException("A conta deve ser a do movimento associado. Remove a associação se identificaste outro movimento.");
        if (evidence.Date == null || evidence.Units is not > 0 || Normalize(evidence.Channel).Length < 3) throw new FinanceRuleException("Falta confirmar a data, o montante ou o canal de pagamento.");
        if (!await db.Accounts.AnyAsync(a => a.Id == accountId && a.HouseholdId == evidence.HouseholdId && a.Active && a.Currency == "EUR")) throw new FinanceRuleException("Escolhe a conta bancária que pagou esta compra.");
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Id == categoryId && c.HouseholdId == evidence.HouseholdId && c.Kind == MovementKind.Expense && c.Active) ?? throw new FinanceRuleException("Escolhe uma categoria de despesa ativa.");
        if (category.ParentId is {} parent && !await db.Categories.AnyAsync(c => c.Id == parent && c.Active)) throw new FinanceRuleException("A categoria principal está inativa.");
        var detail = evidence.Description + (evidence.OrderReference.Length > 0 ? " · Encomenda " + evidence.OrderReference : "");
        if (evidence.BankReferenceId is null)
        {
            var reference = new BankReference { AccountId = accountId, CategoryId = categoryId, Merchant = Normalize(evidence.Channel),
                Description = detail, PurchaseDate = evidence.Date, AmountUnits = evidence.FinancialUnits??evidence.Units };
            db.Add(reference); await db.SaveChangesAsync(); evidence.BankReferenceId = reference.Id;
        }
        else
        {
            var reference = await db.BankReferences.SingleAsync(r => r.Id == evidence.BankReferenceId);
            reference.AccountId = accountId; reference.CategoryId = categoryId; reference.Active = true; reference.Version++;
            reference.PurchaseDate = evidence.Date; reference.AmountUnits = evidence.FinancialUnits??evidence.Units;
        }
        evidence.AccountId = accountId; evidence.CategoryId = categoryId; evidence.Version++;
        db.Add(new AuditChange { Entity = "PurchaseEvidence", EntityId = id, Action = "ApproveReference", Actor = "Owner", CreatedUtc = DateTime.UtcNow, DetailJson = JsonSerializer.Serialize(evidence) });
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
}
