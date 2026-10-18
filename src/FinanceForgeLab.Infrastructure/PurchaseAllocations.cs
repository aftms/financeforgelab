using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public static class PurchaseAllocationMath
{
    public static List<PurchaseCategoryAllocation> Read(string? json, long total, long financial)
    {
        if (json is null or "" or "[]") return [];
        if (json.Length > 100_000 || total <= 0 || financial < 0 || financial > total) throw new FinanceRuleException("A repartição da compra não é válida.");
        List<PurchaseCategoryAllocation> rows;
        try { rows = JsonSerializer.Deserialize<List<PurchaseCategoryAllocation>>(json) ?? []; }
        catch (JsonException) { throw new FinanceRuleException("A repartição da compra não é válida."); }
        if (rows.Count is < 1 or > 100 || rows.Any(r => r is null || r.CategoryId <= 0 || r.Units <= 0 || r.Description is null || r.Description.Length > 200) || rows.Select(r => r.CategoryId).Distinct().Count() != rows.Count || rows.Aggregate(BigInteger.Zero,(sum,r)=>sum+r.Units) != total)
            throw new FinanceRuleException("A soma das categorias deve coincidir com o total da fatura.");
        if (financial == total) return rows;
        if (financial == 0) return [];
        var step = financial % 100 == 0 && rows.All(r => r.Units % 100 == 0) ? 100L : 1L;
        var parts = rows.Select((r,i) => { var value=BigInteger.DivRem((BigInteger)r.Units*(financial/step),total,out var remainder);return new {Row=r,Index=i,Units=(long)value*step,Remainder=remainder};}).ToList();
        var extra=(financial-parts.Sum(r=>r.Units))/step;
        var increased=parts.OrderByDescending(r=>r.Remainder).ThenBy(r=>r.Index).Take((int)extra).Select(r=>r.Index).ToHashSet();
        return parts.Select(r=>r.Row with {Units=r.Units+(increased.Contains(r.Index)?step:0)}).Where(r=>r.Units>0).ToList();
    }
    public static bool ContainsCategory(string? json, long category, long documentTotal, long financialTotal)
    {
        try { var parts=Read(json,documentTotal,financialTotal);return parts.Count>0&&(category==0||parts.Any(p=>p.CategoryId==category)); }
        catch (FinanceRuleException) { return false; }
    }
}

public sealed partial class BankImportService
{
    private static async Task ValidateAllocationsAsync(FinanceDb db, PurchaseEvidence row)
    {
        if (row.PaymentMethod is not ("" or "Bank" or "Mixed" or "Benefits" or "Split" or "StoredValue" or "TopUp" or "Cancelled") || row.FinancialUnits < 0 || row.FinancialUnits > row.Units || (row.PaymentMethod is "Benefits" or "Split" or "Cancelled")&&row.FinancialUnits!=0)
            throw new FinanceRuleException("Confere o meio de pagamento e o valor bancário da compra.");
        if(row.PaymentMethod=="TopUp"&&(row.CategoryId is not null||row.CategoryAllocationsJson!="[]"||row.FinancialUnits!=row.Units))throw new FinanceRuleException("Um carregamento é uma transferência, sem categoria de despesa.");
        var parts=PurchaseAllocationMath.Read(row.CategoryAllocationsJson,row.Units??0,row.Units??0);
        var ids=parts.Select(p=>p.CategoryId).ToList();
        var valid=await db.Categories.Where(c=>ids.Contains(c.Id)&&c.HouseholdId==row.HouseholdId&&c.Active&&c.Kind==MovementKind.Expense).ToListAsync();
        if(valid.Count!=ids.Count || valid.Any(c=>c.ParentId is {} parent&&!db.Categories.Any(p=>p.Id==parent&&p.Active)))throw new FinanceRuleException("Escolhe categorias de despesa ativas para os artigos.");
    }
    public async Task SaveEvidenceAllocationsAsync(long id,int version,List<PurchaseCategoryAllocation> parts)
    {
        await using var db=open();await using var tx=await db.Database.BeginTransactionAsync();
        var row=await db.PurchaseEvidence.SingleAsync(e=>e.Id==id);
        if(row.TransactionId is {} transaction&&await db.IndividualMovements.AnyAsync(m=>m.PaymentTransactionId==transaction))throw new FinanceRuleException("Esta compra tem movimentos individuais. Edita cada movimento na lista; os valores originais do documento são preservados.");
        if(row.Version!=version||row.HasSourceConflict)throw new FinanceRuleException("Os dados foram alterados ou estão em conflito. Atualiza a compra.");
        var before=row.CategoryAllocationsJson;row.CategoryAllocationsJson=JsonSerializer.Serialize(parts);await ValidateAllocationsAsync(db,row);
        if(row.TransactionId is {} movementId)
        {
            var movement=await db.Transactions.SingleAsync(t=>t.Id==movementId);
            var postings=await db.Postings.Where(p=>p.TransactionId==movementId).ToListAsync();
            if(movement.Status!="Confirmed"||movement.Kind!=MovementKind.Expense||postings.Count!=1||postings[0].AmountUnits!=-(row.FinancialUnits??row.Units))throw new FinanceRuleException("O movimento já não confere com a compra. Revê o valor antes de repartir.");
            movement.Version++;
        }
        row.Version++;
        db.Add(new AuditChange{Entity="PurchaseEvidence",EntityId=id,Action="SaveCategoryAllocations",Actor="Owner",CreatedUtc=DateTime.UtcNow,DetailJson=JsonSerializer.Serialize(new{Before=before,After=row.CategoryAllocationsJson})});
        await db.SaveChangesAsync();await tx.CommitAsync();
    }
    public async Task AttachEvidencePdfAsync(string sourceKey,string name,byte[] content)
    {
        if(content.Length is < 5 or > 20*1024*1024||!content.AsSpan(0,5).SequenceEqual("%PDF-"u8))throw new FinanceRuleException("Escolhe uma fatura PDF até 20 MB.");
        var hash=Convert.ToHexString(SHA256.HashData(content));await using var db=open();
        var source=await db.PurchaseEvidenceSources.SingleAsync(s=>s.SourceKey==sourceKey);
        if(source.DocumentSha256.Length>0)
        {if(source.DocumentSha256!=hash||!content.SequenceEqual(source.DocumentContent))throw new FinanceRuleException("Esta origem já tem outro PDF. O original foi preservado.");return;}
        source.DocumentContent=content;source.DocumentSha256=hash;source.DocumentName=Path.GetFileName(name)[..Math.Min(200,Path.GetFileName(name).Length)];
        db.Add(new AuditChange{Entity="PurchaseEvidence",EntityId=source.EvidenceId,Action="ArchiveInvoicePdf",Actor="Owner",CreatedUtc=DateTime.UtcNow,DetailJson=JsonSerializer.Serialize(new{source.Id,source.SourceKey,hash})});await db.SaveChangesAsync();
    }
    public async Task<(byte[] Content,string Name)> EvidenceOriginalAsync(long sourceId)
    {
        await using var db=open();var source=await db.PurchaseEvidenceSources.SingleOrDefaultAsync(s=>s.Id==sourceId)??throw new FinanceRuleException("Documento não encontrado.");
        if(source.DocumentContent.Length==0||Convert.ToHexString(SHA256.HashData(source.DocumentContent))!=source.DocumentSha256)throw new FinanceRuleException("O PDF não está disponível ou não confere com o original.");
        return(source.DocumentContent,source.DocumentName);
    }
}
