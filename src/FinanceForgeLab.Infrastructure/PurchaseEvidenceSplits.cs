using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed partial class BankImportService
{
    // A single order can be charged separately for each shipment. Keep the original
    // order as the aggregate source, and book only its documented component charges.
    public async Task<List<long>> SplitEvidenceAsync(long id,int version,List<PurchaseEvidence> charges)
    {
        await using var db=open();await using var tx=await db.Database.BeginTransactionAsync();
        var parent=await db.PurchaseEvidence.SingleOrDefaultAsync(e=>e.Id==id)??throw new FinanceRuleException("Compra não encontrada.");
        if(charges.Count<2||parent.TransactionId!=null||parent.ParentEvidenceId!=null||parent.HasSourceConflict||parent.Date is null||parent.Units is not >0)
            throw new FinanceRuleException("Confere a encomenda e as cobranças antes de repartir.");
        if(charges.Any(c=>c.Date is null||c.Units is not >0||c.CategoryId is null||c.SourceKey.Length is <3 or >250||c.Source.Length is <3 or >1000||c.OrderReference.Length>200||c.Description.Length is <2 or >2000||c.Details.Length>10000||c.OrderReference==parent.OrderReference||c.ParentEvidenceId!=null||c.Channel!=parent.Channel||c.Merchant!=parent.Merchant||c.FinancialUnits!=null&&c.FinancialUnits!=c.Units||c.PaymentMethod is not ("" or "Bank")||c.HasSourceConflict||c.TransactionId!=null)||
            charges.Select(c=>c.SourceKey).Distinct().Count()!=charges.Count||charges.Select(PurchaseIdentity).Any(i=>i is null)||charges.Select(PurchaseIdentity).Distinct().Count()!=charges.Count||
            charges.Aggregate(0m,(sum,c)=>sum+c.Units!.Value)!=parent.Units)
            throw new FinanceRuleException("As cobranças documentadas devem ter referências próprias, categorias e somar exatamente o total da encomenda.");
        foreach(var charge in charges)
        {
            if(charge.Date<parent.Date||charge.Date!.Value.DayNumber-parent.Date.Value.DayNumber>90)
                throw new FinanceRuleException("Confere as datas dos envios desta encomenda.");
            charge.HouseholdId=parent.HouseholdId;charge.AccountId=parent.AccountId;
            if(!await db.Categories.AnyAsync(c=>c.Id==charge.CategoryId&&c.HouseholdId==parent.HouseholdId&&c.Active&&c.Kind==MovementKind.Expense))throw new FinanceRuleException("Escolhe categorias de despesa ativas para cada cobrança.");
            await ValidateAllocationsAsync(db,charge);
        }
        var existing=await db.PurchaseEvidence.Where(e=>e.ParentEvidenceId==id).OrderBy(e=>e.Id).ToListAsync();
        if(parent.PaymentMethod=="Split")
        {
            if(existing.Count!=charges.Count||charges.Any(c=>!existing.Any(e=>e.SourceKey==c.SourceKey&&e.OrderIdentity==PurchaseIdentity(c)&&e.Units==c.Units&&e.Date==c.Date&&e.ExpectedPaymentDate==c.ExpectedPaymentDate&&e.CategoryId==c.CategoryId&&e.Description==c.Description&&e.Details==c.Details&&e.CategoryAllocationsJson==c.CategoryAllocationsJson)))
                throw new FinanceRuleException("A repartição existente é diferente. Confere as cobranças guardadas.");
            return existing.Select(e=>e.Id).ToList();
        }
        if(parent.Version!=version||existing.Count>0||parent.BankReferenceId!=null||parent.FinancialUnits is not null)
            throw new FinanceRuleException("A compra foi alterada ou já tem uma referência aprovada. Atualiza a encomenda.");
        var keys=charges.Select(c=>c.SourceKey).ToArray();var identities=charges.Select(PurchaseIdentity).ToArray();
        if(await db.PurchaseEvidenceSources.AnyAsync(s=>s.HouseholdId==parent.HouseholdId&&keys.Contains(s.SourceKey))||await db.PurchaseEvidence.AnyAsync(e=>e.HouseholdId==parent.HouseholdId&&identities.Contains(e.OrderIdentity)))
            throw new FinanceRuleException("Um dos comprovativos já está guardado noutra compra. Associa-o antes de repartir.");
        foreach(var charge in charges)
        {
            charge.Id=0;charge.Version=1;charge.ParentEvidenceId=id;charge.OrderIdentity=PurchaseIdentity(charge);
            db.Add(charge);await db.SaveChangesAsync();
            db.Add(new PurchaseEvidenceSource{HouseholdId=parent.HouseholdId,EvidenceId=charge.Id,SourceKey=charge.SourceKey,Source=charge.Source,PayloadJson=JsonSerializer.Serialize(charge)});
        }
        parent.PaymentMethod="Split";parent.FinancialUnits=0;parent.Version++;
        parent.CategoryAllocationsJson=JsonSerializer.Serialize(charges.GroupBy(c=>c.CategoryId!.Value).Select(g=>new PurchaseCategoryAllocation(g.Key,g.Sum(c=>c.Units!.Value),string.Join("; ",g.Select(c=>c.Description)))));
        parent.ReviewNotes="Encomenda repartida por cobranças documentadas. O total da encomenda não cria outra despesa; os movimentos estão nos respetivos envios.";
        db.Add(new AuditChange{Entity="PurchaseEvidence",EntityId=id,Action="SplitDocumentedCharges",Actor="Owner",CreatedUtc=DateTime.UtcNow,DetailJson=JsonSerializer.Serialize(new{Charges=charges.Select(c=>new{c.Id,c.Units,c.CategoryId,c.SourceKey}),parent.Units})});
        await db.SaveChangesAsync();await tx.CommitAsync();return charges.Select(c=>c.Id).ToList();
    }
}
