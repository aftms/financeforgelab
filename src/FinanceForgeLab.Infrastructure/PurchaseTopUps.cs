using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
namespace FinanceForgeLab.Infrastructure;

public sealed partial class BankImportService
{
    public async Task LinkTopUpAsync(long id,int version,long transactionId,long destinationAccountId)
    {
        await using var db=open();await using var tx=await db.Database.BeginTransactionAsync();
        var e=await db.PurchaseEvidence.SingleAsync(e=>e.Id==id);
        var t=await db.Transactions.SingleAsync(t=>t.Id==transactionId);
        var posts=await db.Postings.Where(p=>p.TransactionId==transactionId).ToListAsync();
        if(e.PaymentMethod!="TopUp"||e.HasSourceConflict||e.Units is not >0||e.Date is null||t.HouseholdId!=e.HouseholdId||t.Kind!=MovementKind.Transfer||t.Status!="Confirmed"||t.Date<e.Date||t.Date>e.Date.Value.AddDays(7)
            ||posts.Count!=2||posts.Sum(p=>p.AmountUnits)!=0||!posts.Any(p=>p.AccountId==destinationAccountId&&p.AmountUnits==e.Units)||posts.Any(p=>p.AccountId==null))
            throw new FinanceRuleException("O carregamento não corresponde à transferência e à conta de destino.");
        if(e.TransactionId==transactionId&&e.AccountId==destinationAccountId)return;
        if(e.Version!=version||e.TransactionId is not null)throw new FinanceRuleException("O carregamento foi alterado ou já está associado.");
        e.TransactionId=transactionId;e.AccountId=destinationAccountId;e.Version++;
        db.Add(new AuditChange{Entity="PurchaseEvidence",EntityId=id,Action="LinkTopUpTransfer",Actor="Owner",CreatedUtc=DateTime.UtcNow,DetailJson=JsonSerializer.Serialize(new{transactionId,destinationAccountId})});
        await db.SaveChangesAsync();await tx.CommitAsync();
    }
}
