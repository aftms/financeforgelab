using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed partial class BankImportService
{
    private static readonly SemaphoreSlim ReconciliationGate = new(1,1);
    private async Task<List<long>> ReconcileStagedAsync(List<long> ids)
    {
        await ReconcileExistingPurchasesAsync(ids);
        return ids;
    }

    // This only links existing expenses. It never creates a transaction or changes
    // classifications, totals or explicit review decisions. All checks share the write transaction.
    public async Task<int> ReconcileExistingPurchasesAsync(IEnumerable<long> importIds)
    {
        var ids=importIds.Distinct().ToArray();if(ids.Length==0)return 0;
        await ReconciliationGate.WaitAsync();
        try
        {
            await using var db=open();await using var tx=await db.Database.BeginTransactionAsync();
            var count=await ReconcileCoreAsync(db,ids,null);
            await tx.CommitAsync();return count;
        }
        finally{ReconciliationGate.Release();}
    }

    private async Task ReconcileNewPurchaseAsync(FinanceDb db, PurchaseEvidence evidence)
    {
        if(evidence.TransactionId is not {} purchaseId || evidence.AccountId is not null)return;
        var sources=await (from b in db.BankImports join d in db.BankDocuments on b.DocumentId equals d.Id
            where b.Status=="Draft"&&b.AccountId!=null select new{Batch=b,d.StatementJson}).ToListAsync();
        var ids=sources.Where(x=>JsonSerializer.Deserialize<BankStatement>(x.StatementJson)!.Accounts[x.Batch.SectionIndex].Rows.Any(r=>
            evidence.Date is {} date&&Math.Abs(r.Date.DayNumber-date.DayNumber)<=7 ||
            evidence.ExpectedPaymentDate is {} due&&Math.Abs(r.Date.DayNumber-due.DayNumber)<=7)).Select(x=>x.Batch.Id).ToArray();
        if(ids.Length>0)await ReconcileCoreAsync(db,ids,purchaseId);
    }

    private async Task<int> ReconcileCoreAsync(FinanceDb db,long[] ids,long? onlyPurchase)
    {
            var all=await (from b in db.BankImports join d in db.BankDocuments on b.DocumentId equals d.Id
                where b.Status!="Voided" select new{Batch=b,d.StatementJson}).ToListAsync();
            var targets=all.Where(x=>ids.Contains(x.Batch.Id)&&x.Batch.Status=="Draft"&&x.Batch.AccountId!=null).ToList();
            if(targets.Count==0)return 0;
            var sections=all.Select(x=>new{ x.Batch, Document=JsonSerializer.Deserialize<BankStatement>(x.StatementJson)!}).ToList();
            var targetRows=sections.Where(x=>ids.Contains(x.Batch.Id)).SelectMany(x=>x.Document.Accounts[x.Batch.SectionIndex].Rows).ToList();
            if(targetRows.Count==0)return 0;
            var first=targetRows.Min(r=>r.Date).AddDays(-7);var last=targetRows.Max(r=>r.Date).AddDays(7);
            var graph=new Dictionary<long,List<(long Import,int Ordinal)>>();
            var candidates=new Dictionary<long,Dictionary<int,List<BankDuplicate>>>();
            foreach(var x in sections)
            {
                var section=x.Document.Accounts[x.Batch.SectionIndex];
                if(x.Batch.AccountId is null||!x.Document.Valid||!section.Rows.Any(r=>r.Date>=first&&r.Date<=last))continue;
                var matches=await FindDuplicatesAsync(db,section,x.Batch.AccountId);candidates[x.Batch.Id]=matches;
                foreach(var row in matches)
                    foreach(var m in row.Value.Where(m=>m.Origin=="Purchase"&&m.Kind==MovementKind.Expense&&m.StrongMatch))
                    {
                        if(!graph.TryGetValue(m.TransactionId,out var places))graph[m.TransactionId]=places=[];
                        places.Add((x.Batch.Id,row.Key));
                    }
            }
            var count=0;
            foreach(var x in sections.Where(x=>ids.Contains(x.Batch.Id)&&x.Batch.Status=="Draft"&&x.Batch.AccountId!=null))
            {
                var batch=x.Batch;var section=x.Document.Accounts[batch.SectionIndex];
                if(!x.Document.Valid||!candidates.TryGetValue(batch.Id,out var matches))continue;
                var recorded=await db.BankImportEntries.Where(e=>e.ImportId==batch.Id).Select(e=>e.Ordinal).ToHashSetAsync();
                var review=string.IsNullOrEmpty(batch.ReviewJson)?new BankImportReview{Id=batch.Id,AccountId=batch.AccountId!.Value,PersonId=batch.PersonId,Rows=section.Rows.Select(r=>new BankRowDecision{Ordinal=r.Ordinal,Decision="Import",Kind=r.Units<0?MovementKind.Expense:MovementKind.Income}).ToList()}:JsonSerializer.Deserialize<BankImportReview>(batch.ReviewJson)!;
                if(review.AccountId!=batch.AccountId||review.PersonId!=batch.PersonId)continue;
                if(string.IsNullOrEmpty(batch.ReviewJson))
                {
                    var suggestions=await ClassifyAsync(db,section,batch.AccountId);
                    foreach(var row in review.Rows)
                        if(suggestions.TryGetValue(row.Ordinal,out var suggestion))
                        {row.Kind=suggestion.Kind;row.CategoryId=suggestion.CategoryId;row.CounterpartyAccountId=suggestion.CounterpartyAccountId;row.Detail=suggestion.Detail;}
                }
                review.Version=batch.Version;var selected=new HashSet<int>();
                foreach(var row in review.Rows)
                {
                    if(recorded.Contains(row.Ordinal)||row.ClassificationEdited||row.Decision is "Exclude" or "Pending")continue;
                    var list=matches.GetValueOrDefault(row.Ordinal)??[];
                    if(list.Count!=1)continue;var m=list[0];
                    if(onlyPurchase is {} purchaseId && m.TransactionId!=purchaseId)continue;
                    if(!m.StrongMatch||m.Origin!="Purchase"||m.Kind!=MovementKind.Expense||graph.GetValueOrDefault(m.TransactionId)?.Count!=1)continue;
                    if(row.Decision=="Link"&&row.ExistingTransactionId!=m.TransactionId)continue;
                    row.Decision="Link";row.ExistingTransactionId=m.TransactionId;row.ExistingVersion=m.Version;selected.Add(row.Ordinal);
                }
                if(selected.Count==0)continue;
                if(string.IsNullOrEmpty(batch.ReviewJson))
                    foreach(var row in review.Rows.Where(r=>!selected.Contains(r.Ordinal)&&matches.ContainsKey(r.Ordinal)))row.Decision="Pending";
                await ConfirmCoreAsync(review,selected,db);count+=selected.Count;
                db.Add(new AuditChange{Entity="BankImport",EntityId=batch.Id,Action="AutomaticPurchaseReconciliation",Actor="System",CreatedUtc=DateTime.UtcNow,DetailJson=JsonSerializer.Serialize(new{ManualVersion=FinanceOperatingManual.Version,Rows=selected.Order().ToArray(),Rule="Unique documented purchase across available statement rows and accounts"})});
                await db.SaveChangesAsync();
            }
            return count;
    }
}
