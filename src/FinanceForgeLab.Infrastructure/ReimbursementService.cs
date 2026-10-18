using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed record ReimbursementReceipt(long Id, long TransactionId, DateOnly Date, string Description, long Units, bool Valid);
public sealed record ReimbursementRow(ReimbursementClaim Claim, DateOnly Date, string Description, string? Account, long PaidUnits, bool NeedsReview, List<ReimbursementReceipt> Receipts)
{
    public long RemainingUnits => Math.Max(0, Claim.AmountUnits - PaidUnits);
}
public sealed record ReimbursementCandidate(long? TransactionId, long? ImportId, int? Ordinal, DateOnly Date, long Units, string Description, string Account);

public sealed class ReimbursementService(Func<FinanceDb> open)
{
    public async Task<long> RegisterAsync(long expenseId, string debtor)
    {
        debtor = debtor.Trim();
        if (debtor.Length is < 3 or > 200) throw new FinanceRuleException("Indica quem deve fazer o reembolso.");
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var (expense, posting) = await ExpenseAsync(db, expenseId);
        var old = await db.ReimbursementClaims.SingleOrDefaultAsync(c => c.ExpenseTransactionId == expenseId);
        if (old is not null)
        {
            if (old.Debtor != debtor || old.AmountUnits != -posting.AmountUnits) throw new FinanceRuleException("Este adiantamento já está registado com outros dados.");
            return old.Id;
        }
        var claim = new ReimbursementClaim { HouseholdId = expense.HouseholdId, ExpenseTransactionId = expenseId, Debtor = debtor, AmountUnits = checked(-posting.AmountUnits) };
        db.Add(claim); await db.SaveChangesAsync(); Audit(db, claim, "RegisterAdvance", claim);
        await db.SaveChangesAsync(); await tx.CommitAsync(); return claim.Id;
    }

    public async Task<List<ReimbursementRow>> ListAsync()
    {
        await using var db = open();
        var claims = await db.ReimbursementClaims.AsNoTracking().ToListAsync();
        var allocations = await db.ReimbursementAllocations.AsNoTracking().ToListAsync();
        var ids = claims.Select(c => c.ExpenseTransactionId).Concat(allocations.Select(a => a.ReceiptTransactionId)).Distinct().ToList();
        var movements = await db.Transactions.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id);
        var postings = await db.Postings.AsNoTracking().Where(p => ids.Contains(p.TransactionId)).ToListAsync();
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name);
        return claims.Select(claim =>
        {
            var expense = movements[claim.ExpenseTransactionId];
            var debit = postings.Where(p => p.TransactionId == expense.Id).ToList();
            var receipts = allocations.Where(a => a.ClaimId == claim.Id).Select(a =>
            {
                var receipt = movements[a.ReceiptTransactionId];
                var credit = postings.Where(p => p.TransactionId == receipt.Id).ToList();
                var valid = receipt.Status == "Confirmed" && receipt.Date >= expense.Date && receipt.HouseholdId == claim.HouseholdId &&
                    (receipt.Kind is MovementKind.Income or MovementKind.Expense) && credit.Count == 1 && credit[0].Currency == "EUR" &&
                    credit[0].AmountUnits >= allocations.Where(x => x.ReceiptTransactionId == receipt.Id).Sum(x => x.AmountUnits) &&
                    (debit.FirstOrDefault()?.AccountId is not {} bank || credit[0].AccountId == bank);
                return new ReimbursementReceipt(a.Id, a.ReceiptTransactionId, receipt.Date, receipt.Description ?? "Reembolso", a.AmountUnits, valid);
            }).ToList();
            var changed = expense.Status != "Confirmed" || expense.Kind != MovementKind.Expense || debit.Count != 1 || debit[0].AmountUnits != -claim.AmountUnits;
            changed |= receipts.Any(r => movements[r.TransactionId].Status == "Confirmed" && !r.Valid);
            var account = debit.FirstOrDefault()?.AccountId;
            return new ReimbursementRow(claim, expense.Date, expense.Description ?? "Adiantamento", account is {} id ? accounts.GetValueOrDefault(id) : null, receipts.Where(r=>r.Valid).Sum(r => r.Units), changed, receipts);
        }).OrderByDescending(r => r.Date).ThenByDescending(r => r.Claim.Id).ToList();
    }

    public async Task<List<ReimbursementCandidate>> CandidatesAsync(long claimId)
    {
        await using var db = open();
        var claim = await db.ReimbursementClaims.AsNoTracking().SingleAsync(c => c.Id == claimId);
        var (expense, debit) = await ExpenseAsync(db, claim.ExpenseTransactionId);
        var allocations = await db.ReimbursementAllocations.AsNoTracking().ToListAsync();
        var confirmed = await db.Transactions.AsNoTracking().Where(t => t.HouseholdId == claim.HouseholdId && t.Status == "Confirmed" && (t.Kind == MovementKind.Income || t.Kind == MovementKind.Expense)).ToDictionaryAsync(t => t.Id);
        long Allocated(long id) => allocations.Where(a => a.ReceiptTransactionId == id).Sum(a => a.AmountUnits);
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.HouseholdId == claim.HouseholdId).ToDictionaryAsync(a => a.Id);
        var credits = await db.Postings.AsNoTracking().Where(p => p.AmountUnits > 0 && p.Currency == "EUR" && p.AccountId != null).ToListAsync();
        var result = new List<ReimbursementCandidate>();
        foreach (var credit in credits)
        {
            if (!confirmed.TryGetValue(credit.TransactionId, out var movement) || movement.Date < expense.Date || !accounts.ContainsKey(credit.AccountId!.Value)) continue;
            if (debit.AccountId is {} account && credit.AccountId != account || !CounterpartyMatches(claim.Debtor, movement.Description ?? "")) continue;
            var available = credit.AmountUnits - Allocated(movement.Id);
            if (available > 0) result.Add(new(movement.Id, null, null, movement.Date, available, movement.Description ?? "", accounts[credit.AccountId!.Value].Name));
        }
        var imports = await db.BankImports.AsNoTracking().Where(b => b.Status == "Draft" && b.AccountId != null).ToListAsync();
        var documents = await db.BankDocuments.AsNoTracking().Where(d => imports.Select(b => b.DocumentId).Contains(d.Id)).ToDictionaryAsync(d => d.Id);
        foreach (var batch in imports)
        {
            if (!accounts.TryGetValue(batch.AccountId!.Value, out var account) || debit.AccountId is {} id && id != account.Id) continue;
            var statement = JsonSerializer.Deserialize<BankStatement>(documents[batch.DocumentId].StatementJson)!;
            if (!statement.Valid) continue;
            foreach (var row in statement.Accounts[batch.SectionIndex].Rows.Where(r => r.Units > 0 && r.Date >= expense.Date && CounterpartyMatches(claim.Debtor, r.Description)))
            {
                // A bank line already represented by a confirmed credit is offered through that movement only.
                if (credits.Any(p => p.AccountId == account.Id && p.AmountUnits == row.Units && confirmed.TryGetValue(p.TransactionId, out var t) && t.Date == row.Date && t.Description == row.Description)) continue;
                result.Add(new(null, batch.Id, row.Ordinal, row.Date, row.Units, row.Description, account.Name));
            }
        }
        return result.OrderBy(r => r.Date).ToList();
    }

    public async Task LinkAsync(long claimId, int version, long receiptId, long units)
    {
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var claim = await db.ReimbursementClaims.SingleAsync(c => c.Id == claimId);
        await AllocateAsync(db, claim, version, receiptId, units);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    public async Task<long> LinkStatementAsync(long claimId, int version, long importId, int ordinal, long units)
    {
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var claim = await db.ReimbursementClaims.SingleAsync(c => c.Id == claimId);
        var batch = await db.BankImports.SingleAsync(b => b.Id == importId);
        if (batch.Status == "Voided" || batch.AccountId is null) throw new FinanceRuleException("Confere a conta e o estado do extrato.");
        var account = await db.Accounts.SingleAsync(a => a.Id == batch.AccountId && a.HouseholdId == claim.HouseholdId && a.Currency == "EUR");
        var document = await db.BankDocuments.SingleAsync(d => d.Id == batch.DocumentId);
        if (Convert.ToHexString(SHA256.HashData(document.Content)) != document.Id) throw new FinanceRuleException("O PDF original foi alterado.");
        var statement = JsonSerializer.Deserialize<BankStatement>(document.StatementJson)!;
        if (!statement.Valid) throw new FinanceRuleException("Resolve os erros de leitura do extrato.");
        var row = statement.Accounts[batch.SectionIndex].Rows.Single(r => r.Ordinal == ordinal);
        if (row.Units <= 0 || !CounterpartyMatches(claim.Debtor, row.Description)) throw new FinanceRuleException("Esta linha não identifica um recebimento da entidade que deve o reembolso.");
        var source = $"{batch.DocumentId}:{batch.SectionIndex}:{ordinal}";
        var requestId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("reimbursement:" + source)))[..32].ToLowerInvariant();
        var command = await db.Commands.FindAsync(requestId);
        long receiptId;
        if (command is not null)
        {
            if (command.Action != "ReceiveReimbursement" || command.PayloadHash != source) throw new FinanceRuleException("Origem do reembolso em conflito.");
            receiptId = command.EntityId;
        }
        else
        {
            var matching = await (from p in db.Postings join t in db.Transactions on p.TransactionId equals t.Id
                where p.AccountId == account.Id && p.AmountUnits == row.Units && t.Date == row.Date && t.Description == row.Description && t.Status == "Confirmed" && t.HouseholdId == claim.HouseholdId
                select t.Id).Distinct().ToListAsync();
            if (matching.Count > 1) throw new FinanceRuleException("Há vários recebimentos possíveis. Escolhe o movimento existente.");
            if (matching.Count == 1) receiptId = matching[0];
            else
            {
                var (_, expensePosting) = await ExpenseAsync(db, claim.ExpenseTransactionId);
                var receipt = new FinancialTransaction { HouseholdId = claim.HouseholdId, Kind = MovementKind.Expense, Date = row.Date, Description = row.Description,
                    Origin = "Reimbursement", Notes = $"Reembolso de adiantamento a {claim.Debtor}. Extrato {document.Name}, conta {account.Name}, página {row.Page}, linha {row.Line}. Origem: {source}." };
                db.Add(receipt); await db.SaveChangesAsync(); receiptId = receipt.Id;
                db.Add(new Posting { TransactionId = receiptId, AccountId = account.Id, CategoryId = expensePosting.CategoryId, Date = row.Date, ValueDate = row.ValueDate, AmountUnits = row.Units });
            }
            db.Add(new CommandReceipt { Id = requestId, Action = "ReceiveReimbursement", PayloadHash = source, EntityId = receiptId });
            await db.SaveChangesAsync();
        }
        await AllocateAsync(db, claim, version, receiptId, units);
        await db.SaveChangesAsync(); await tx.CommitAsync(); return receiptId;
    }

    public async Task UnlinkAsync(long claimId, int version, long allocationId)
    {
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        var claim = await db.ReimbursementClaims.SingleAsync(c => c.Id == claimId);
        if (claim.Version != version) throw new FinanceRuleException("O adiantamento foi alterado. Atualiza a consulta.");
        var allocation = await db.ReimbursementAllocations.SingleAsync(a => a.Id == allocationId && a.ClaimId == claimId);
        db.Remove(allocation); claim.Version++; Audit(db, claim, "UnlinkReimbursement", allocation);
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    private static async Task AllocateAsync(FinanceDb db, ReimbursementClaim claim, int version, long receiptId, long units)
    {
        if (units <= 0) throw new FinanceRuleException("Indica um valor positivo de reembolso.");
        var old = await db.ReimbursementAllocations.SingleOrDefaultAsync(a => a.ClaimId == claim.Id && a.ReceiptTransactionId == receiptId);
        if (old is not null)
        {
            if (old.AmountUnits != units) throw new FinanceRuleException("Este recebimento já está associado com outro valor.");
            return;
        }
        if (claim.Version != version) throw new FinanceRuleException("O adiantamento foi alterado. Atualiza a consulta.");
        var (expense, debit) = await ExpenseAsync(db, claim.ExpenseTransactionId);
        if (debit.AmountUnits != -claim.AmountUnits) throw new FinanceRuleException("O valor do adiantamento mudou. Confere o movimento original.");
        var receipt = await db.Transactions.SingleOrDefaultAsync(t => t.Id == receiptId && t.HouseholdId == claim.HouseholdId && t.Status == "Confirmed" && (t.Kind == MovementKind.Income || t.Kind == MovementKind.Expense));
        var credits = await db.Postings.Where(p => p.TransactionId == receiptId).ToListAsync();
        if (receipt is null || receipt.Date < expense.Date || credits.Count != 1 || credits[0].Currency != "EUR" || credits[0].AmountUnits <= 0 || debit.AccountId is {} account && credits[0].AccountId != account)
            throw new FinanceRuleException("O recebimento não corresponde à data, conta ou entidade deste adiantamento.");
        var used = await db.ReimbursementAllocations.Where(a => a.ReceiptTransactionId == receiptId).SumAsync(a => (long?)a.AmountUnits) ?? 0;
        var paid = await (from a in db.ReimbursementAllocations join t in db.Transactions on a.ReceiptTransactionId equals t.Id where a.ClaimId == claim.Id && t.Status == "Confirmed" select (long?)a.AmountUnits).SumAsync() ?? 0;
        if (units > credits[0].AmountUnits - used || units > claim.AmountUnits - paid) throw new FinanceRuleException("O valor excede o recebimento disponível ou o reembolso em falta.");
        var allocation = new ReimbursementAllocation { ClaimId = claim.Id, ReceiptTransactionId = receiptId, AmountUnits = units };
        db.Add(allocation); claim.Version++; Audit(db, claim, "LinkReimbursement", new { receiptId, units });
    }

    private static async Task<(FinancialTransaction, Posting)> ExpenseAsync(FinanceDb db, long id)
    {
        var expense = await db.Transactions.SingleOrDefaultAsync(t => t.Id == id && t.Kind == MovementKind.Expense && t.Status == "Confirmed") ?? throw new FinanceRuleException("Escolhe uma despesa confirmada como adiantamento.");
        var posts = await db.Postings.Where(p => p.TransactionId == id).ToListAsync();
        if (posts.Count != 1 || posts[0].AmountUnits >= 0 || posts[0].Currency != "EUR") throw new FinanceRuleException("O adiantamento precisa de um pagamento em EUR.");
        return (expense, posts[0]);
    }

    private static bool CounterpartyMatches(string debtor, string description)
    {
        static string Fold(string value) => new(value.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ').ToArray());
        var generic = new HashSet<string> { "ASSOCIACAO", "CULTURAL", "DESPORTIVA", "RECREATIVA", "SOCIAL", "LDA", "LIMITADA", "PORTUGAL" };
        var tokens = Fold(debtor).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 2 && !generic.Contains(t)).Distinct().ToList();
        var words = Fold(description).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return tokens.Count > 0 && tokens.All(words.Contains);
    }
    private static void Audit(FinanceDb db, ReimbursementClaim claim, string action, object detail) => db.Add(new AuditChange { Entity = "ReimbursementClaim", EntityId = claim.Id, Action = action, Actor = "Owner", CreatedUtc = DateTime.UtcNow, DetailJson = JsonSerializer.Serialize(detail) });
}
