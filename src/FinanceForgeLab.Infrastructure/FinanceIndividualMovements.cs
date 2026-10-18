using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed partial class FinanceService
{
    public async Task<MovementForm> MovementForRowAsync(MovementRow selected)
    {
        if(!selected.IsCategoryEntry&&selected.IndividualId is null)return await MovementAsync(selected.Id);
        await using var db=open();
        var rows=await EntryRowsAsync(db,new(){TransactionId=selected.Id,IncludeVoided=true});
        var row=rows.SingleOrDefault(r=>r.EntryKey==selected.EntryKey)??throw new FinanceRuleException("O movimento foi atualizado. Volta a abrir a lista.");
        if(row.Status!="Confirmed")throw new FinanceRuleException("Este movimento está anulado.");
        var destination=row.IndividualId is {} id?(await db.IndividualMovements.SingleAsync(m=>m.Id==id)).DestinationAccountId:null;
        return new(){Id=row.Id,IndividualId=row.IndividualId,SourceCategoryId=row.SourceCategoryId??row.CategoryId,EvidenceVersion=row.EvidenceVersion,
            Version=row.Version,Date=row.Date,Kind=row.Kind,Amount=Money.FromUnits(row.Kind==MovementKind.Income?row.Units:-row.Units),
            AccountId=row.AccountId,DestinationAccountId=destination,CategoryId=row.CategoryId,PersonId=row.PersonId,Description=row.DisplayDescription,Notes=row.IndividualId!=null?row.Notes:null};
    }
    public async Task<string?> MovementOriginWarningAsync(long paymentId)
    {await using var db=open();return (await IndividualRowsAsync(db)).FirstOrDefault(r=>r.Id==paymentId)?.ReconciliationWarning;}

    private static async Task<IndividualMovement> ResolveIndividualAsync(FinanceDb db,MovementForm form)
    {
        var parent=await db.Transactions.SingleOrDefaultAsync(t=>t.Id==form.Id)??throw new FinanceRuleException("Movimento inexistente.");
        if(parent.Status!="Confirmed")throw new FinanceRuleException("A origem foi anulada. Confere os movimentos associados.");
        if(form.IndividualId is {} id)
        {
            var found=await db.IndividualMovements.SingleOrDefaultAsync(m=>m.Id==id&&m.PaymentTransactionId==form.Id)??throw new FinanceRuleException("Movimento inexistente.");
            if(found.Version!=form.Version||found.Status!="Confirmed")throw new FinanceRuleException("O movimento foi alterado. Volta a abri-lo.");
            return found;
        }
        if(await db.IndividualMovements.AnyAsync(m=>m.PaymentTransactionId==form.Id)||parent.Version!=form.Version)
            throw new FinanceRuleException("Os movimentos foram atualizados. Volta a abrir a lista.");
        var e=await db.PurchaseEvidence.SingleOrDefaultAsync(e=>e.TransactionId==form.Id)??throw new FinanceRuleException("Origem não encontrada.");
        if(e.Version!=form.EvidenceVersion||e.HasSourceConflict)throw new FinanceRuleException("O documento foi atualizado ou tem divergências. Volta a abrir a lista.");
        var rows=CategoryEntries(await WithPurchaseClassificationAsync(db,await Query(db,new(){TransactionId=form.Id}).ToListAsync()));
        if(rows.Count==0||rows.Any(r=>!r.IsCategoryEntry)||!rows.Any(r=>r.CategoryId==form.SourceCategoryId))throw new FinanceRuleException("Não foi possível identificar o movimento escolhido.");
        var created=rows.Select(r=>new IndividualMovement{PaymentTransactionId=form.Id,SourceCategoryId=r.CategoryId!.Value,
            Date=r.Date,Kind=r.Kind,AmountUnits=r.Units,AccountId=r.AccountId,CategoryId=r.CategoryId,PersonId=r.PersonId,Description=r.DisplayDescription,Notes=null}).ToList();
        db.AddRange(created);e.Version++;
        AddAudit(db,"Transaction",form.Id,"IdentifyIndividualMovements",null,created);
        await db.SaveChangesAsync();
        return created.Single(m=>m.SourceCategoryId==form.SourceCategoryId);
    }

    private async Task<long> SaveIndividualAsync(MovementForm form)
    {
        Validate(form);var amount=Units(form.Amount);
        if(amount==0||amount==long.MinValue||form.Kind is not (MovementKind.Expense or MovementKind.Income or MovementKind.Transfer)||form.Kind==MovementKind.Transfer&&amount<=0)
            throw new FinanceRuleException("Tipo ou montante inválido.");
        await using var db=open();await using var tx=await db.Database.BeginTransactionAsync();
        var hash=Payload(form);if(await Receipt(db,form.RequestId,"SaveIndividualMovement",hash) is {} saved)return saved;
        var movement=await ResolveIndividualAsync(db,form);var before=JsonSerializer.SerializeToElement(movement);
        var parent=await db.Transactions.SingleAsync(t=>t.Id==form.Id);
        async Task Account(long? id)
        {
            if(id==null)return;
            if(!await db.Accounts.AnyAsync(a=>a.Id==id&&a.HouseholdId==parent.HouseholdId&&a.Currency=="EUR"&&(a.Active||a.Id==movement.AccountId||a.Id==movement.DestinationAccountId)))throw new FinanceRuleException("Conta inválida ou inativa.");
        }
        await Account(form.AccountId);
        if(form.PersonId is {} person&&!await db.People.AnyAsync(p=>p.Id==person&&p.HouseholdId==parent.HouseholdId))throw new FinanceRuleException("Pessoa inválida.");
        if(form.Kind==MovementKind.Transfer)
        {
            if(form.AccountId==null||form.DestinationAccountId==null||form.AccountId==form.DestinationAccountId)throw new FinanceRuleException("Seleciona duas contas distintas.");
            await Account(form.DestinationAccountId);
        }
        else
        {
            var category=await db.Categories.SingleOrDefaultAsync(c=>c.Id==form.CategoryId&&c.HouseholdId==parent.HouseholdId&&c.Kind==form.Kind&&(c.Active||c.Id==movement.CategoryId));
            if(category is null||category.ParentId is {} root&&!await db.Categories.AnyAsync(c=>c.Id==root&&c.Active)&&category.Id!=movement.CategoryId)throw new FinanceRuleException("Seleciona uma categoria disponível para este tipo.");
        }
        // An identified bank account fills untouched movements. An owner's explicit
        // change, including clearing the account, must never be silently overwritten.
        var sourceAccount=await db.Postings.Where(p=>p.TransactionId==form.Id).Select(p=>p.AccountId).SingleAsync();
        var effectiveAccount=movement.AccountEdited?movement.AccountId:sourceAccount;
        if(form.AccountId!=effectiveAccount){movement.AccountEdited=true;movement.AccountId=form.AccountId;}
        movement.Date=form.Date;movement.Kind=form.Kind;movement.AmountUnits=form.Kind==MovementKind.Income?amount:-amount;
        movement.CategoryId=form.Kind==MovementKind.Transfer?null:form.CategoryId;movement.DestinationAccountId=form.Kind==MovementKind.Transfer?form.DestinationAccountId:null;
        movement.Description=form.Description.Trim();movement.Notes=form.Notes;movement.PersonId=form.PersonId;movement.Version++;
        AddAudit(db,"IndividualMovement",movement.Id,"SaveMovement",before,movement);AddReceipt(db,form.RequestId,"SaveIndividualMovement",hash,form.Id);
        await db.SaveChangesAsync();await tx.CommitAsync();return form.Id;
    }

    public async Task VoidRowAsync(MovementRow row,Guid requestId)
    {
        if(!row.IsCategoryEntry&&row.IndividualId is null){await VoidMovementAsync(row.Id,row.Version,requestId);return;}
        await using var db=open();await using var tx=await db.Database.BeginTransactionAsync();
        var hash=Payload(new{row.Id,row.IndividualId,row.SourceCategoryId,row.CategoryId,row.Version,row.EvidenceVersion});
        if(await Receipt(db,requestId,"VoidIndividualMovement",hash) is not null)return;
        var movement=await ResolveIndividualAsync(db,new(){Id=row.Id,IndividualId=row.IndividualId,SourceCategoryId=row.SourceCategoryId??row.CategoryId,Version=row.Version,EvidenceVersion=row.EvidenceVersion});
        var before=JsonSerializer.SerializeToElement(movement);movement.Status="Voided";movement.Version++;
        AddAudit(db,"IndividualMovement",movement.Id,"Void",before,movement);AddReceipt(db,requestId,"VoidIndividualMovement",hash,row.Id);
        await db.SaveChangesAsync();await tx.CommitAsync();
    }

    internal static async Task<List<MovementRow>> IndividualRowsAsync(FinanceDb db)
    {
        var raw=await (from m in db.IndividualMovements.AsNoTracking()
            join t in db.Transactions on m.PaymentTransactionId equals t.Id
            join p in db.Postings on t.Id equals p.TransactionId
            join e in db.PurchaseEvidence on t.Id equals e.TransactionId
            select new{M=m,T=t,P=p,E=e}).ToListAsync();
        var categories=await db.Categories.AsNoTracking().ToDictionaryAsync(c=>c.Id);
        var accounts=await db.Accounts.AsNoTracking().ToDictionaryAsync(a=>a.Id);
        var people=await db.People.AsNoTracking().ToDictionaryAsync(p=>p.Id);
        var warnings=new Dictionary<long,string>();
        foreach(var group in raw.GroupBy(r=>r.T.Id))
        {
            var first=group.First();var active=group.Where(r=>r.M.Status=="Confirmed").ToList();
            var sum=active.Sum(r=>r.M.Kind==MovementKind.Transfer?0:r.M.AmountUnits);
            var difference=sum-first.P.AmountUnits;
            var accountMismatch=active.Any(r=>r.M.AccountEdited&&r.M.AccountId!=first.P.AccountId);
            if(difference!=0||accountMismatch)warnings[group.Key]="Por conferir com a origem: "+(difference!=0?$"diferença de {Money.FromUnits(Math.Abs(difference)):0.00} € no total dos movimentos associados.":"a conta do movimento difere da conta do pagamento.");
        }
        return raw.Select(r=>
        {
            var m=r.M;var account=m.AccountEdited?m.AccountId:r.P.AccountId;
            var category=m.CategoryId is {} c?categories.GetValueOrDefault(c):null;
            return new MovementRow{Id=r.T.Id,IndividualId=m.Id,SourceCategoryId=m.SourceCategoryId,Version=m.Version,EvidenceVersion=r.E.Version,
                Date=m.Date,Kind=m.Kind,Description=m.Description,Notes=m.Notes,PersonId=m.PersonId,Person=m.PersonId is {} person?people.GetValueOrDefault(person)?.Name:null,
                AccountId=account,DestinationAccountId=m.DestinationAccountId,Account=account is {} a?accounts.GetValueOrDefault(a)?.Name:null,CategoryId=m.CategoryId,Category=category?.Name,
                ParentCategory=category?.ParentId is {} parent?categories.GetValueOrDefault(parent)?.Name:null,Units=m.AmountUnits,Status=r.T.Status=="Confirmed"?m.Status:"Voided",Origin=r.T.Origin,
                EvidenceId=r.E.Id,PaymentTotalUnits=r.P.AmountUnits,ReconciliationWarning=warnings.GetValueOrDefault(r.T.Id)};
        }).ToList();
    }
}
