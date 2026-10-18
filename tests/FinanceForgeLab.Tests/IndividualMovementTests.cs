using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace FinanceForgeLab.Tests;

public sealed partial class PurchaseAllocationTests
{
    async Task<(long Transaction,long Evidence,List<MovementRow> Rows)> IndividualExample(long? account=null)
    {
        var id=Assert.Single(await Imports.StageEvidenceAsync([Example()]));var evidence=Assert.Single(await Imports.EvidenceAsync());
        var transaction=await Imports.ConfirmEvidenceMovementAsync(id,evidence.Version,account,food,true);
        return(transaction,id,(await Finance.MovementsAsync(new(){TransactionId=transaction})).Rows);
    }
    [Fact]public async Task EditingOneMovementPreservesSiblingOriginalPaymentAndSourcesAcrossFilters()
    {
        var x=await IndividualExample();var original=x.Rows.Single(r=>r.CategoryId==food);
        var account=await Finance.SaveAccountAsync(new(){Name="Conta individual",BalanceStartDate=new(2025,1,1)});
        var form=await Finance.MovementForRowAsync(original);
        Assert.Equal(7.5m,form.Amount);Assert.Null(form.Notes);
        form.Amount=8;form.Date=new(2026,2,1);form.CategoryId=care;form.Description="Compra corrigida";form.Notes="Nota deste movimento";form.AccountId=account;
        await Finance.SaveMovementAsync(form);await Finance.SaveMovementAsync(form);
        var rows=(await Finance.MovementsAsync(new(){TransactionId=x.Transaction})).Rows;
        Assert.Equal(2,rows.Count);Assert.All(rows,r=>Assert.Equal(care,r.CategoryId));Assert.Equal(2,rows.Select(r=>r.EntryKey).Distinct().Count());
        var edited=rows.Single(r=>r.SourceCategoryId==food);var sibling=rows.Single(r=>r.SourceCategoryId==care);
        Assert.Equal(-80000,edited.Units);Assert.Equal(form.Date,edited.Date);Assert.Equal(account,edited.AccountId);Assert.NotNull(edited.ReconciliationWarning);
        Assert.Equal(-25000,sibling.Units);Assert.Equal(new DateOnly(2025,1,1),sibling.Date);Assert.Null(sibling.AccountId);
        Assert.Equal(80000,(await Finance.DashboardAsync(2026)).ExpenseUnits);Assert.Equal(25000,(await Finance.DashboardAsync(2025)).ExpenseUnits);
        Assert.Equal(-80000,(await Finance.AccountsAsync()).Single(a=>a.Account.Id==account).BalanceUnits);
        Assert.Single((await Finance.MovementsAsync(new(){From=new(2026,1,1),To=new(2026,12,31),AccountId=account,CategoryId=care,Search="corrigida"})).Rows);
        var csv=await Finance.ExportCsvAsync(new(){From=new(2026,1,1),To=new(2026,12,31)});Assert.Contains("-8.0000",csv);Assert.DoesNotContain("-10.0000",csv);
        Assert.All(rows,r=>Assert.Contains(r.Sources,s=>s.Kind=="Fatura / documento"));
        await using var db=Open();Assert.Equal(-100000,(await db.Postings.SingleAsync(p=>p.TransactionId==x.Transaction)).AmountUnits);Assert.Equal(200000,(await db.PurchaseEvidence.SingleAsync()).Units);Assert.Equal(2,await db.IndividualMovements.CountAsync());
    }
    [Fact]public async Task VoidingOneMovementChangesOnlyItsTotalsAndPreservesHistoryAndDuplicateIdentity()
    {
        var account=await Finance.SaveAccountAsync(new(){Name="Conta",BalanceStartDate=new(2025,1,1)});
        var x=await IndividualExample(account);var selected=x.Rows.Single(r=>r.CategoryId==food);var command=Guid.NewGuid();
        await Finance.VoidRowAsync(selected,command);await Finance.VoidRowAsync(selected,command);
        var active=Assert.Single((await Finance.MovementsAsync(new(){TransactionId=x.Transaction})).Rows);Assert.Equal(care,active.CategoryId);Assert.NotNull(active.ReconciliationWarning);
        var history=(await Finance.MovementsAsync(new(){TransactionId=x.Transaction,IncludeVoided=true})).Rows;Assert.Equal(2,history.Count);Assert.Single(history,r=>r.Status=="Voided");
        Assert.Equal(25000,(await Finance.DashboardAsync(2025)).ExpenseUnits);Assert.Equal(-25000,(await Finance.AccountsAsync()).Single(a=>a.Account.Id==account).BalanceUnits);
        Assert.Equal(x.Evidence,Assert.Single(await Imports.StageEvidenceAsync([Example()])));
        Assert.Single((await Finance.MovementsAsync(new(){TransactionId=x.Transaction})).Rows);
        var issues=(await new AssistantService(Open).InspectReviewAsync()).Issues;Assert.Contains(issues,i=>i.Key==$"payment:{x.Transaction}:reconciliation");
        await using var db=Open();Assert.Equal("Confirmed",(await db.Transactions.SingleAsync(t=>t.Id==x.Transaction)).Status);Assert.Single(await db.PurchaseEvidenceSources.ToListAsync());
    }
    [Fact]public async Task StaleEditsAndWholePaymentActionsCannotOverwriteIndividualMovements()
    {
        var x=await IndividualExample();var a=await Finance.MovementForRowAsync(x.Rows[0]);var stale=await Finance.MovementForRowAsync(x.Rows[1]);
        a.Description="Alterado";await Finance.SaveMovementAsync(a);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Finance.SaveMovementAsync(stale));
        var fresh=(await Finance.MovementsAsync(new(){TransactionId=x.Transaction})).Rows;
        var b=await Finance.MovementForRowAsync(fresh.Single(r=>r.SourceCategoryId==x.Rows[1].CategoryId));b.Description="Outro movimento";await Finance.SaveMovementAsync(b);
        var old=await Finance.MovementAsync(x.Transaction);await Assert.ThrowsAsync<FinanceRuleException>(()=>Finance.SaveMovementAsync(old));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Finance.VoidMovementAsync(x.Transaction,old.Version,Guid.NewGuid()));
        var evidence=Assert.Single(await Imports.EvidenceAsync());await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.SaveEvidenceAllocationsAsync(evidence.Id,evidence.Version,[new(food,200000)]));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.UnlinkEvidenceAsync(evidence.Id,evidence.Version));
    }
    [Fact]public async Task InvalidIndividualEditRollsBackIdentificationAndLeavesTotalsUnchanged()
    {
        var x=await IndividualExample();var form=await Finance.MovementForRowAsync(x.Rows[0]);form.CategoryId=long.MaxValue;
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Finance.SaveMovementAsync(form));
        await using var db=Open();Assert.Empty(await db.IndividualMovements.ToListAsync());Assert.Equal(x.Rows[0].EvidenceVersion,(await db.PurchaseEvidence.SingleAsync()).Version);
        Assert.Equal(100000,(await Finance.DashboardAsync(2025)).ExpenseUnits);
    }
    [Fact]public async Task LaterBankIdentificationUpdatesUntouchedAccountsButPreservesExplicitAccountChange()
    {
        var x=await IndividualExample();var ownerAccount=await Finance.SaveAccountAsync(new(){Name="Outra conta",BalanceStartDate=new(2025,1,1)});
        var form=await Finance.MovementForRowAsync(x.Rows.Single(r=>r.CategoryId==food));form.AccountId=ownerAccount;await Finance.SaveMovementAsync(form);
        var batch=Assert.Single(await Imports.StageAsync("statement.pdf",BankImportTests.SyntheticPdf(firstDescription:"Continente Loures",continuation:"")));
        var review=await Imports.PrepareReviewAsync(batch);await Imports.ConfirmRowsAsync(review,[1]);
        var rows=(await Finance.MovementsAsync(new(){TransactionId=x.Transaction})).Rows;
        Assert.Equal(ownerAccount,rows.Single(r=>r.SourceCategoryId==food).AccountId);
        Assert.Equal(review.AccountId,rows.Single(r=>r.SourceCategoryId==care).AccountId);
        Assert.All(rows,r=>Assert.Contains(r.Sources,s=>s.Kind=="Extrato"));Assert.NotEqual(ownerAccount,review.AccountId);
        Assert.Equal(100000,(await Finance.DashboardAsync(2025)).ExpenseUnits);
    }
    [Fact]public async Task IndividualMovementCanBecomeTransferWithoutChangingItsSibling()
    {
        var source=await Finance.SaveAccountAsync(new(){Name="Origem",BalanceStartDate=new(2025,1,1)});
        var destination=await Finance.SaveAccountAsync(new(){Name="Destino",BalanceStartDate=new(2025,1,1)});
        var x=await IndividualExample(source);var form=await Finance.MovementForRowAsync(x.Rows.Single(r=>r.CategoryId==food));
        form.Kind=MovementKind.Transfer;form.DestinationAccountId=destination;await Finance.SaveMovementAsync(form);
        Assert.Equal(25000,(await Finance.DashboardAsync(2025)).ExpenseUnits);
        var balances=await Finance.AccountsAsync();Assert.Equal(-100000,balances.Single(a=>a.Account.Id==source).BalanceUnits);Assert.Equal(75000,balances.Single(a=>a.Account.Id==destination).BalanceUnits);
        Assert.Single((await Finance.MovementsAsync(new(){AccountId=destination})).Rows);
    }
}
