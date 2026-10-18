using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class RecurringExpenseTests:IAsyncLifetime
{
    readonly string folder=Path.Combine(Path.GetTempPath(),"ffl-recurring-"+Guid.NewGuid().ToString("N"));
    FinanceDb Open()=>FinanceDb.Open(Path.Combine(folder,"test.sqlite"));
    FinanceService Service=>new(Open);
    long home,water,food,account;
    public async Task InitializeAsync(){Directory.CreateDirectory(folder);await using var db=Open();await db.Database.MigrateAsync();Assert.False(db.Database.HasPendingModelChanges());db.Add(new Household());await db.SaveChangesAsync();home=await Service.SaveCategoryAsync(new(){Name="Casa",Kind=MovementKind.Expense});water=await Service.SaveCategoryAsync(new(){Name="Água",ParentId=home,Kind=MovementKind.Expense});food=await Service.SaveCategoryAsync(new(){Name="Supermercado",ParentId=home,Kind=MovementKind.Expense});account=await Service.SaveAccountAsync(new(){Name="Banco",BalanceStartDate=new(2024,1,1)});}
    public Task DisposeAsync(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(folder,true);return Task.CompletedTask;}
    Task<long> Expense(int month,decimal amount,long category,string description="Serviço de água",int year=2026)=>Service.SaveMovementAsync(new(){Kind=MovementKind.Expense,AccountId=account,CategoryId=category,Date=new(year,month,10),Amount=amount,Description=description});

    [Fact] public async Task VariableBillsAndAnnualPaymentsUseActualMonthAndOnlyConfirmedExpenses(){
        await Expense(1,30,water);await Expense(2,45,water);var annual=await Service.SaveCategoryAsync(new(){Name="IUC",ParentId=home,Kind=MovementKind.Expense});await Expense(4,120,annual,"IUC anual");await Expense(3,100,food);
        var voidId=await Expense(2,999,water);await Service.VoidMovementAsync(voidId,1,Guid.NewGuid());
        var cash=await Service.SaveAccountAsync(new(){Name="Numerário",Type="Cash",BalanceStartDate=new(2024,1,1)});
        await Service.SaveMovementAsync(new(){Kind=MovementKind.Transfer,AccountId=account,DestinationAccountId=cash,Amount=200,Date=new(2026,2,1),Description="Transferência"});
        Assert.Equal(2,await Service.IdentifyRecurringExpensesAsync());Assert.Equal(0,await Service.IdentifyRecurringExpensesAsync());
        var report=await Service.RecurringExpensesAsync(2026);Assert.Equal(1950000,report.TotalUnits);Assert.Equal(300000,report.MonthUnits(1));Assert.Equal(450000,report.MonthUnits(2));Assert.Equal(1200000,report.MonthUnits(4));Assert.Equal(0,report.MonthCount(3));
        Assert.Equal("Anual",report.Lines.Single(l=>l.Rule.CategoryId==annual).Frequency);Assert.Equal("Mensal · estimada",report.Lines.Single(l=>l.Rule.CategoryId==water).Frequency);
    }
    [Fact] public async Task SpecificExclusionOverridesBroadCategoryAndSettingsAreAuditedAndVersioned(){
        await Expense(1,30,water,"Água municipal");await Expense(1,20,water,"Aquaservice");
        await Service.SaveRecurringExpenseAsync(new(){Name="Casa",CategoryId=home});
        var form=new RecurringExpenseForm{Name="Entrega de água",CategoryId=water,ContainsText="aquaservice",Included=false};var id=await Service.SaveRecurringExpenseAsync(form);Assert.Equal(id,await Service.SaveRecurringExpenseAsync(form));
        Assert.Equal(300000,(await Service.RecurringExpensesAsync(2026)).TotalUnits);
        await Service.SaveRecurringExpenseAsync(new(){Id=id,Version=1,Name="Entrega de água",CategoryId=water,ContainsText="aquaservice",Included=true});
        var report=await Service.RecurringExpensesAsync(2026);Assert.Equal(500000,report.TotalUnits);Assert.Equal(2,report.Included.SelectMany(l=>l.Movements).Count());
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.SaveRecurringExpenseAsync(new(){Id=id,Version=1,Name="Antigo",CategoryId=water,ContainsText="aquaservice"}));
        await using var db=Open();Assert.Equal(2,await db.AuditChanges.CountAsync(a=>a.Entity=="RecurringExpense"&&a.EntityId==id));Assert.Equal(2,await db.Transactions.CountAsync());
    }
    [Fact] public async Task InvoiceCategoriesAndIndividualEditsCountOnceAndAnnualInferenceUsesEarlierYears(){
        await Service.SaveRecurringExpenseAsync(new(){Name="Água",CategoryId=water});
        var imports=new BankImportService(Open,Path.Combine(folder,"archive"));var evidence=Assert.Single(await imports.StageEvidenceAsync([new(){SourceKey="mixed-water",Source="Fatura de teste",Channel="Serviços",OrderReference="mixed-123",Date=new(2026,2,10),Units=1000000,Description="Serviços e compras"}]));
        await imports.SaveEvidenceAllocationsAsync(evidence,1,[new(water,300000),new(food,700000)]);var e=Assert.Single(await imports.EvidenceAsync());await imports.ConfirmEvidenceMovementAsync(evidence,e.Version,account,water,true);
        var before=await Service.RecurringExpensesAsync(2026);Assert.Equal(300000,before.TotalUnits);Assert.Single(before.Included.SelectMany(l=>l.Movements));
        var row=Assert.Single(before.Included.SelectMany(l=>l.Movements));await Service.VoidRowAsync(row,Guid.NewGuid());Assert.Equal(0,(await Service.RecurringExpensesAsync(2026)).TotalUnits);
        await Expense(6,30,water,year:2024);await Expense(6,35,water,year:2025);await Expense(6,40,water,year:2026);
        var after=await Service.RecurringExpensesAsync(2026);Assert.Equal(400000,after.TotalUnits);Assert.Equal("Anual · estimada",Assert.Single(after.Lines).Frequency);
    }
}
