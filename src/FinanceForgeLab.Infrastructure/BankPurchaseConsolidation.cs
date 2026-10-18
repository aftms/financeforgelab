using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed partial class BankImportService
{
    // Explicit repair after checking both sources, never an automatic duplicate deletion.
    // Retain the purchase, its classifications and documents; retire the untouched bank copy.
    public async Task ConsolidateBankPurchaseAsync(long importId, int ordinal, long purchaseId, int purchaseVersion, long bankTransactionId, int bankVersion)
    {
        await using var db=open(); await using var tx=await db.Database.BeginTransactionAsync();
        var batch=await db.BankImports.SingleAsync(b=>b.Id==importId);
        var entry=await db.BankImportEntries.SingleAsync(e=>e.ImportId==importId&&e.Ordinal==ordinal);
        var purchase=await db.Transactions.SingleAsync(t=>t.Id==purchaseId);
        var bank=await db.Transactions.SingleAsync(t=>t.Id==bankTransactionId);
        if(entry.TransactionId==purchaseId&&!entry.Created&&bank.Status=="Voided")return;
        var evidence=await db.PurchaseEvidence.SingleOrDefaultAsync(e=>e.TransactionId==purchaseId);
        var paypalSources=await DocumentedPayPalAsync(db,evidence is null?[]:[evidence]);
        var purchasePosts=await db.Postings.Where(p=>p.TransactionId==purchaseId).ToListAsync();
        var bankPosts=await db.Postings.Where(p=>p.TransactionId==bankTransactionId).ToListAsync();
        var document=await db.BankDocuments.SingleAsync(d=>d.Id==batch.DocumentId);
        var statement=JsonSerializer.Deserialize<BankStatement>(document.StatementJson)!;
        var row=statement.Accounts[batch.SectionIndex].Rows.Single(r=>r.Ordinal==ordinal);
        if(batch.Status=="Voided"||!BankStatementConformance.CanImport(statement,statement.Accounts[batch.SectionIndex])||batch.AccountId is null||
            entry.TransactionId!=bankTransactionId||!entry.Created||entry.TransactionVersion!=bankVersion||
            bank.Version!=bankVersion||bankVersion!=1||bank.Status!="Confirmed"||bank.Origin!="BankImport"||bank.Kind!=MovementKind.Expense||
            purchase.Version!=purchaseVersion||purchase.Status!="Confirmed"||purchase.Origin!="Purchase"||purchase.Kind!=MovementKind.Expense||
            bank.HouseholdId!=purchase.HouseholdId||evidence is null||evidence.HasSourceConflict||
            purchasePosts.Count!=1||bankPosts.Count!=1||purchasePosts[0].AmountUnits!=row.Units||bankPosts[0].AmountUnits!=row.Units||
            bankPosts[0].AccountId!=batch.AccountId||purchasePosts[0].AccountId is not null||
            !Matches(evidence,row.Date,row.Units,row.Description,batch.AccountId,paypalSources.Contains(evidence.Id)))
            throw new FinanceRuleException("A compra e o movimento bancário não permitem uma consolidação inequívoca. Confere as origens e as versões.");
        if(await db.PurchaseEvidence.AnyAsync(e=>e.TransactionId==bankTransactionId)||
            await db.IndividualMovements.AnyAsync(m=>m.PaymentTransactionId==bankTransactionId)||
            await db.ReimbursementClaims.AnyAsync(r=>r.ExpenseTransactionId==bankTransactionId)||
            await db.ReimbursementAllocations.AnyAsync(r=>r.ReceiptTransactionId==bankTransactionId)||
            await db.BankImportEntries.CountAsync(e=>e.TransactionId==bankTransactionId)!=1||
            await db.BankImportEntries.AnyAsync(e=>e.TransactionId==purchaseId))
            throw new FinanceRuleException("Existem outras associações nos movimentos. Confere-as antes de consolidar.");
        var matches=await FindDuplicatesAsync(db,statement.Accounts[batch.SectionIndex],batch.AccountId);
        if((matches.GetValueOrDefault(ordinal)??[]).Any(m=>m.TransactionId!=purchaseId&&m.TransactionId!=bankTransactionId))
            throw new FinanceRuleException("Existe outra correspondência possível para esta linha.");
        purchasePosts[0].AccountId=batch.AccountId;purchase.Version++;
        evidence.AccountId=batch.AccountId;evidence.Version++;
        bank.Status="Voided";bank.Version++;
        entry.TransactionId=purchaseId;entry.TransactionVersion=purchase.Version;entry.Created=false;
        batch.Version++;batch.ReviewHash="";
        if(!string.IsNullOrEmpty(batch.ReviewJson))
        {
            var review=JsonSerializer.Deserialize<BankImportReview>(batch.ReviewJson)!;
            var choice=review.Rows.Single(r=>r.Ordinal==ordinal);
            choice.Decision="Link";choice.ExistingTransactionId=purchaseId;choice.ExistingVersion=purchase.Version;
            choice.Kind=MovementKind.Expense;choice.CategoryId=purchasePosts[0].CategoryId;
            review.Version=batch.Version;batch.ReviewJson=JsonSerializer.Serialize(review);
        }
        db.Add(new AuditChange{Entity="Transaction",EntityId=purchaseId,Action="ConsolidateBankPurchase",Actor="Owner",CreatedUtc=DateTime.UtcNow,
            DetailJson=JsonSerializer.Serialize(new{ImportId=importId,Ordinal=ordinal,AccountId=batch.AccountId,EvidenceId=evidence.Id,VoidedDuplicate=bankTransactionId,Units=row.Units,Preserved="Purchase date, amount, classifications and sources"})});
        db.Add(new AuditChange{Entity="Transaction",EntityId=bankTransactionId,Action="VoidDuplicate",Actor="Owner",CreatedUtc=DateTime.UtcNow,
            DetailJson=JsonSerializer.Serialize(new{RetainedTransaction=purchaseId,ImportId=importId,Ordinal=ordinal})});
        await db.SaveChangesAsync();await tx.CommitAsync();
    }
}
