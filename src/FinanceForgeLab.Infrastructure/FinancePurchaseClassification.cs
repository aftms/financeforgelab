using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;
namespace FinanceForgeLab.Infrastructure;

public sealed partial class FinanceService
{
    private static async Task<List<MovementRow>> WithPurchaseClassificationAsync(FinanceDb db,List<MovementRow> rows)
    {
        var ids=rows.Where(r=>r.Kind==MovementKind.Expense&&r.Units<0).Select(r=>r.Id).ToArray();
        if(ids.Length==0)return rows;
        var evidence=await db.PurchaseEvidence.AsNoTracking().Where(e=>e.TransactionId!=null&&ids.Contains(e.TransactionId.Value)&&!e.HasSourceConflict).ToDictionaryAsync(e=>e.TransactionId!.Value);
        if(evidence.Count==0)return rows;
        var categories=await db.Categories.AsNoTracking().ToDictionaryAsync(c=>c.Id);
        return rows.Select(row=>
        {
            if(!evidence.TryGetValue(row.Id,out var e)||e.Units is null||-(e.FinancialUnits??e.Units)!=row.Units)return row;
            row=row with{EvidenceId=e.Id,EvidenceVersion=e.Version,Merchant=string.IsNullOrWhiteSpace(e.Merchant)?e.Channel:e.Merchant};
            List<PurchaseCategoryAllocation> parts;
            try {parts=PurchaseAllocationMath.Read(e.CategoryAllocationsJson,e.Units.Value,-row.Units);}
            catch(FinanceRuleException){return row;}
            if(parts.Count==0||parts.Any(p=>!categories.ContainsKey(p.CategoryId)))return row;
            var details=parts.Select(p=>new MovementCategoryPart(p.CategoryId,categories[p.CategoryId].Name,categories[p.CategoryId].ParentId is {} root?categories[root].Name:categories[p.CategoryId].Name,p.Units)).ToList();
            return row with{CategoryAllocations=details,Category=details.Count>1?"Várias categorias":details[0].Category,ParentCategory=details.Count>1?null:categories[details[0].CategoryId].ParentId is not null?details[0].Root:null};
        }).ToList();
    }
    // Persisted invoice allocations are the category records. Their payment is
    // retained once for bank reconciliation, never added alongside these amounts.
    private static List<MovementRow> CategoryEntries(IEnumerable<MovementRow> rows)=>rows.SelectMany(row=>
        row.CategoryAllocations.Count==0 ? new[]{row} : row.CategoryAllocations.Select(part=>row with
        {
            Description=string.IsNullOrWhiteSpace(row.Merchant)?row.Description:row.Merchant,
            CategoryId=part.CategoryId,SourceCategoryId=part.CategoryId,Category=part.Category,ParentCategory=part.Root==part.Category?null:part.Root,
            Units=checked(-part.Units),PaymentTotalUnits=row.Units,CategoryAllocations=[]
        })).ToList();

    private static async Task<List<MovementRow>> EntryRowsAsync(FinanceDb db,MovementFilter filter)
    {
        var rows=CategoryEntries(await WithOriginalClassificationAsync(db,await Query(db,filter).ToListAsync()));
        var individuals=await IndividualRowsAsync(db);
        bool Matches(MovementRow r)=>
            (filter.IncludeVoided||r.Status=="Confirmed")&&(filter.TransactionId==null||r.Id==filter.TransactionId)&&
            (filter.From==null||r.Date>=filter.From)&&(filter.To==null||r.Date<=filter.To)&&(filter.Kind==null||r.Kind==filter.Kind)&&
            (filter.PersonId is not >0||r.PersonId==filter.PersonId)&&
            (filter.AccountId==null||filter.AccountId==0||filter.AccountId==-1&&r.AccountId==null||r.AccountId==filter.AccountId||r.DestinationAccountId==filter.AccountId)&&
            (string.IsNullOrWhiteSpace(filter.Search)||System.Globalization.CultureInfo.GetCultureInfo("pt-PT").CompareInfo.IndexOf((r.Description??"")+" "+r.Notes,filter.Search.Trim(),System.Globalization.CompareOptions.IgnoreCase|System.Globalization.CompareOptions.IgnoreNonSpace)>=0);
        rows.AddRange(individuals.Where(Matches));
        if(filter.CategoryId is >0)
        {
            var children=await db.Categories.Where(c=>c.ParentId==filter.CategoryId).Select(c=>c.Id).ToListAsync();
            rows=rows.Where(r=>r.CategoryId==filter.CategoryId||r.CategoryId is {} id&&children.Contains(id)).ToList();
        }
        else if(filter.CategoryId==0)
        {
            var provisional=await db.Categories.Where(c=>c.Name=="Por classificar").Select(c=>c.Id).ToListAsync();
            rows=rows.Where(r=>r.Kind is MovementKind.Income or MovementKind.Expense&&(r.CategoryId is null||provisional.Contains(r.CategoryId.Value))).ToList();
        }
        var comparer=StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("pt-PT"),true);
        IOrderedEnumerable<MovementRow> Order<T>(Func<MovementRow,T> key)=>filter.Descending?rows.OrderByDescending(key):rows.OrderBy(key);
        var ordered=filter.Sort switch
        {
            MovementSort.Description=>filter.Descending?rows.OrderByDescending(r=>r.DisplayDescription,comparer):rows.OrderBy(r=>r.DisplayDescription,comparer),
            MovementSort.Category=>filter.Descending?rows.OrderByDescending(r=>r.ParentCategory??r.Category??"",comparer).ThenByDescending(r=>r.Category,comparer):rows.OrderBy(r=>r.ParentCategory??r.Category??"",comparer).ThenBy(r=>r.Category,comparer),
            MovementSort.Account=>Order(r=>r.Account??"Por associar"),
            MovementSort.Amount=>Order(r=>r.Units),
            MovementSort.Kind=>Order(r=>r.Status=="Voided"?"Anulado":r.Kind==MovementKind.Income?"Receita":r.Kind==MovementKind.Expense?"Despesa":r.Kind==MovementKind.Transfer?"Transferência":r.Kind==MovementKind.Opening?"Abertura":"Ajuste"),
            _=>Order(r=>r.Date)
        };
        return (filter.Descending?ordered.ThenByDescending(r=>r.Id):ordered.ThenBy(r=>r.Id)).ThenBy(r=>r.CategoryId).ToList();
    }
}
