using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;
namespace FinanceForgeLab.Infrastructure;

public sealed partial class BankImportService
{
    // Explicit correction after checking cancellation, authorization and bank sources.
    // A recorded bank payment requires refund reconciliation instead of this operation.
    public async Task CancelUnchargedEvidenceAsync(long id,int version,long cancellationSourceId,string reason)
    {
        if(string.IsNullOrWhiteSpace(reason)||reason.Length>1000)throw new FinanceRuleException("Indica o motivo do cancelamento sem cobrança.");
        await using var db=open();await using var tx=await db.Database.BeginTransactionAsync();
        var e=await db.PurchaseEvidence.SingleAsync(e=>e.Id==id);
        if(!await db.PurchaseEvidenceSources.AnyAsync(s=>s.Id==cancellationSourceId&&s.EvidenceId==id))throw new FinanceRuleException("Associa o comprovativo do cancelamento.");
        var movement=await db.Transactions.SingleOrDefaultAsync(t=>t.Id==e.TransactionId);
        if(e.PaymentMethod=="Cancelled"&&e.FinancialUnits==0&&movement?.Status=="Voided")return;
        if(e.Version!=version||e.HasSourceConflict||e.AccountId!=null||e.BankReferenceId!=null||movement is null||movement.Origin!="Purchase"||movement.Kind!=MovementKind.Expense||movement.Status!="Confirmed"||
            await db.BankImportEntries.AnyAsync(b=>b.TransactionId==movement.Id)||await db.IndividualMovements.AnyAsync(m=>m.PaymentTransactionId==movement.Id)||
            await db.ReimbursementClaims.AnyAsync(r=>r.ExpenseTransactionId==movement.Id))throw new FinanceRuleException("A compra exige reconciliação do pagamento ou reembolso antes de ser anulada.");
        var posts=await db.Postings.Where(p=>p.TransactionId==movement.Id).ToListAsync();
        if(posts.Count!=1||posts[0].AccountId!=null||posts[0].AmountUnits!=-(e.FinancialUnits??e.Units))throw new FinanceRuleException("Confere o pagamento antes de cancelar.");
        var before=JsonSerializer.SerializeToElement(new{Evidence=e,Movement=movement});
        movement.Status="Voided";movement.Version++;e.PaymentMethod="Cancelled";e.FinancialUnits=0;e.ReviewNotes=reason;e.Version++;
        db.Add(new AuditChange{Entity="Transaction",EntityId=movement.Id,Action="VoidCancelledPurchase",Actor="Owner",CreatedUtc=DateTime.UtcNow,DetailJson=JsonSerializer.Serialize(new{Before=before,After=new{Evidence=e,Movement=movement},CancellationSourceId=cancellationSourceId,Reason=reason})});
        await db.SaveChangesAsync();await tx.CommitAsync();
    }
}
