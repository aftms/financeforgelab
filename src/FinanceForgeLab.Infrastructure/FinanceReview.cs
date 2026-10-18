using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed partial class FinanceService
{
    // Documents with no financial charge (benefits or a split order's parent) are not unposted expenses.
    private static IQueryable<PurchaseEvidence> PendingPurchases(FinanceDb db)=>db.PurchaseEvidence.AsNoTracking().Where(e=>
        e.HasSourceConflict ||
        (!db.Transactions.Any(t=>t.Id==e.TransactionId&&t.Status=="Confirmed") &&
         e.PaymentMethod!="Benefits"&&e.PaymentMethod!="Split"&&
         ((e.FinancialUnits??e.Units)==null||(e.FinancialUnits??e.Units)!=0)));

    public async Task<HashSet<long>> PendingPurchaseIdsAsync()
    {
        await using var db=open();
        return await PendingPurchases(db).Select(e=>e.Id).ToHashSetAsync();
    }
}
