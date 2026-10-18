using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed partial class BankImportService
{
    private static async Task<Dictionary<int, List<BankDuplicate>>> FindDuplicatesAsync(FinanceDb db, StatementAccount section, long? accountId)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId);
        if (account is null || section.Rows.Count == 0) return [];
        // Include dates just outside the PDF period: manual entries may use the purchase
        // date while the bank uses the booking date. Never compare amounts by magnitude.
        var earliest = DateOnly.FromDayNumber(Math.Max(0, section.Rows.Min(r => Math.Min(r.Date.DayNumber, r.ValueDate.DayNumber)) - 7));
        var latest = DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber, section.Rows.Max(r => Math.Max(r.Date.DayNumber, r.ValueDate.DayNumber)) + 7));
        var purchases = await db.PurchaseEvidence.AsNoTracking().Where(e => e.HouseholdId == account.HouseholdId && (e.AccountId == null || e.AccountId == account.Id) && e.TransactionId != null && !e.HasSourceConflict && e.Date != null && e.Units > 0).ToListAsync();
        var paypalSources=await DocumentedPayPalAsync(db,purchases);
        var scheduledIds = purchases.Where(e => e.ExpectedPaymentDate >= earliest && e.ExpectedPaymentDate <= latest).Select(e => e.TransactionId!.Value).ToList();
        var existing = await (from p in db.Postings.AsNoTracking()
                              join t in db.Transactions.AsNoTracking() on p.TransactionId equals t.Id
                              where t.HouseholdId == account.HouseholdId && t.Status == "Confirmed" && p.Currency == "EUR" &&
                                  (p.AccountId == account.Id || (p.AccountId == null && (t.Origin == "Legacy" || t.Origin == "Purchase"))) &&
                                  (t.Date >= earliest && t.Date <= latest || scheduledIds.Contains(t.Id)) &&
                                  (t.Kind == MovementKind.Income || t.Kind == MovementKind.Expense || t.Kind == MovementKind.Transfer)
                              select new { t.Id, t.Version, t.Description, t.Kind, t.Date, t.Origin, p.AmountUnits, p.AccountId, p.ValueDate, p.CategoryId }).ToListAsync();
        var categories = await db.Categories.AsNoTracking().Where(c => c.HouseholdId == account.HouseholdId).ToDictionaryAsync(c => c.Id);
        List<string> Classifications(long transactionId, long? categoryId, long units)
        {
            var ids = categoryId is {} category ? new List<long> { category } : [];
            var evidence = purchases.FirstOrDefault(e => e.TransactionId == transactionId && -(e.FinancialUnits ?? e.Units) == units && e.CategoryAllocationsJson != "[]");
            if (evidence?.Units is {} total)
            {
                try
                {
                    var parts = PurchaseAllocationMath.Read(evidence.CategoryAllocationsJson, total, -units);
                    if (parts.Count > 0 && parts.All(p => categories.ContainsKey(p.CategoryId))) ids = parts.Select(p => p.CategoryId).ToList();
                }
                catch (FinanceRuleException) { /* Preserve the posting classification when allocations are invalid. */ }
            }
            return ids.Distinct().Where(categories.ContainsKey).Select(id =>
            {
                var c = categories[id];
                return c.ParentId is {} parent && categories.TryGetValue(parent, out var root) ? $"{root.Name} / {c.Name}" : c.Name;
            }).ToList();
        }
        var result = new Dictionary<int, List<BankDuplicate>>();
        foreach (var row in section.Rows)
        {
            var key = MerchantKey(row.Description);
            var matches = new List<BankDuplicate>();
            foreach (var candidate in existing.Where(e => e.AmountUnits == row.Units))
            {
                var sameDate = candidate.Date == row.Date;
                var valueDate = candidate.Date == row.ValueDate || candidate.ValueDate == row.ValueDate;
                var sameEntity = key.Length >= 4 && key == MerchantKey(candidate.Description ?? "");
                var nearDate = Math.Abs(candidate.Date.DayNumber - row.Date.DayNumber) <= 3 && sameEntity;
                var purchase = candidate.Kind == MovementKind.Expense && purchases.Any(e => e.TransactionId == candidate.Id && Matches(e, row.Date, row.Units, row.Description, account.Id,paypalSources.Contains(e.Id)));
                if (!sameDate && !valueDate && !nearDate && !purchase) continue;
                var strong = purchase || candidate.AccountId == account.Id && sameDate && sameEntity && Normalize(row.Description) == Normalize(candidate.Description ?? "");
                var reason = purchase ? "Compra já registada a partir de um documento; mesma entidade, valor e data compatível" : sameDate ? "Mesma data e montante" : valueDate ? "Mesmo montante e data-valor correspondente" : "Mesmo montante e entidade, com diferença de até 3 dias";
                if (candidate.AccountId is null) reason += candidate.Origin == "Purchase" ? "; confirmar a conta do pagamento" : "; histórico sem conta associada — confirmar a origem";
                else if (strong && !purchase) reason += "; descrição correspondente";
                else if (sameDate) reason += "; conferir a descrição";
                matches.Add(new(candidate.Id, candidate.Version, candidate.Description ?? "", candidate.Kind)
                {
                    Date = candidate.Date, Units = candidate.AmountUnits, AccountId = candidate.AccountId,
                    Origin = candidate.Origin, Reason = reason + ".", StrongMatch = strong,
                    Classifications = Classifications(candidate.Id, candidate.CategoryId, candidate.AmountUnits)
                });
            }
            if (matches.Count > 0) result[row.Ordinal] = matches.DistinctBy(m => m.TransactionId)
                .OrderByDescending(m => m.StrongMatch).ThenBy(m => Math.Abs(m.Date.DayNumber - row.Date.DayNumber)).ThenBy(m => m.TransactionId).ToList();
        }
        return result;
    }
}
