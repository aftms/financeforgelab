using System.Text.Json;
using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using Xunit;
namespace FinanceForgeLab.Tests;

public sealed partial class PurchaseAllocationTests:IAsyncLifetime
{
    readonly string folder=Path.Combine(Path.GetTempPath(),"ffl-invoice-"+Guid.NewGuid().ToString("N"));
    FinanceDb Open()=>FinanceDb.Open(Path.Combine(folder,"test.sqlite"));
    BankImportService Imports=>new(Open,Path.Combine(folder,"pdfs"));
    FinanceService Finance=>new(Open);
    long food,care,careRoot;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(folder);await using var db=Open();await db.Database.MigrateAsync();var household=new Household();db.Add(household);await db.SaveChangesAsync();
        var groceries=new Category{HouseholdId=household.Id,Name="Alimentação",Kind=MovementKind.Expense};
        var hygiene=new Category{HouseholdId=household.Id,Name="Cuidados pessoais",Kind=MovementKind.Expense};db.AddRange(groceries,hygiene);await db.SaveChangesAsync();food=groceries.Id;careRoot=hygiene.Id;
        var leaf=new Category{HouseholdId=household.Id,Name="Higiene",Kind=MovementKind.Expense,ParentId=careRoot};db.Add(leaf);await db.SaveChangesAsync();care=leaf.Id;
    }
    public Task DisposeAsync(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(folder,true);return Task.CompletedTask;}
    PurchaseEvidence Example(string key="pdf:receipt",long bank=100000)=>new(){SourceKey=key,Source="Fatura Continente de teste",Channel="Continente",Merchant="Continente",OrderReference=key,Date=new(2025,1,1),Units=200000,FinancialUnits=bank,PaymentMethod=bank==0?"Benefits":"Mixed",CategoryId=food,Description="Compra mista",CategoryAllocationsJson=JsonSerializer.Serialize(new[]{new PurchaseCategoryAllocation(food,150000),new PurchaseCategoryAllocation(care,50000)})};
    PurchaseEvidence Order()=>new(){SourceKey="email:order",Source="Email Amazon: confirmação",Channel="Amazon",Merchant="Amazon",OrderReference="order-123",Date=new(2025,1,1),Units=200000,Description="Encomenda com dois envios"};
    List<PurchaseEvidence> Charges()=>[new(){SourceKey="email:shipment-one",Source="Email Amazon: envio um",Channel="Amazon",Merchant="Amazon",OrderReference="order-123/envio-1",Date=new(2025,1,2),Units=150000,CategoryId=food,Description="Alimentos"},new(){SourceKey="email:shipment-two",Source="Email Amazon: envio dois",Channel="Amazon",Merchant="Amazon",OrderReference="order-123/envio-2",Date=new(2025,1,3),Units=50000,CategoryId=care,Description="Cuidados pessoais"}];
    [Fact] public async Task OrderWithSeparateShipmentsBooksOnlyItsDocumentedChargesAndRetriesDoNotDuplicate()
    {
        var order=Order();var id=Assert.Single(await Imports.StageEvidenceAsync([order]));var parent=Assert.Single(await Imports.EvidenceAsync());
        var ids=await Imports.SplitEvidenceAsync(id,parent.Version,Charges());Assert.Equal(2,ids.Count);
        foreach(var child in (await Imports.EvidenceAsync()).Where(e=>e.ParentEvidenceId==id))await Imports.ConfirmEvidenceMovementAsync(child.Id,child.Version,null,child.CategoryId!.Value,true);
        Assert.Equal(ids,await Imports.SplitEvidenceAsync(id,parent.Version,Charges()));
        Assert.Equal(id,Assert.Single(await Imports.StageEvidenceAsync([Order()])));
        parent=(await Imports.EvidenceAsync()).Single(e=>e.Id==id);Assert.Equal("Split",parent.PaymentMethod);Assert.Equal(0,parent.FinancialUnits);Assert.Null(parent.TransactionId);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmEvidenceMovementAsync(id,parent.Version,null,food,true));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ApproveEvidenceAsync(id,parent.Version,1,food,true));
        Assert.Empty((await Imports.EvidenceMatchesAsync())[id]);
        await using var db=Open();Assert.Equal(2,await db.Transactions.CountAsync());Assert.Equal(-200000,await db.Postings.SumAsync(p=>p.AmountUnits));
        var dashboard=await Finance.DashboardAsync(2025);Assert.Equal(200000,dashboard.ExpenseUnits);Assert.Equal(150000,dashboard.Expenses.Single(e=>e.Name=="Alimentação").Units);
        Assert.Equal(50000,dashboard.Expenses.Single(e=>e.Name=="Cuidados pessoais").Units);
        var changed=Charges();changed[0].Description="Different item";await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.SplitEvidenceAsync(id,parent.Version,changed));
        var original=Assert.Single((await Imports.EvidenceSourcesAsync([id])));await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ResolveEvidenceSourceAsync(id,parent.Version,original.Id));
    }
    [Fact] public async Task IncompleteOrStaleShipmentSplitLeavesTheOriginalOrderUnchanged()
    {
        var id=Assert.Single(await Imports.StageEvidenceAsync([Order()]));var parent=Assert.Single(await Imports.EvidenceAsync());
        var incomplete=Charges();incomplete[1].Units=40000;
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.SplitEvidenceAsync(id,parent.Version,incomplete));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.SplitEvidenceAsync(id,parent.Version-1,Charges()));
        var duplicate=Charges();duplicate[1].SourceKey=duplicate[0].SourceKey;await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.SplitEvidenceAsync(id,parent.Version,duplicate));
        await using var db=Open();Assert.Single(await db.PurchaseEvidence.ToListAsync());Assert.Single(await db.PurchaseEvidenceSources.ToListAsync());Assert.Empty(await db.Transactions.ToListAsync());
        Assert.Null((await db.PurchaseEvidence.SingleAsync()).FinancialUnits);
    }
    [Fact] public async Task RecordedOrderCannotAlsoCreateShipmentExpenses()
    {
        var id=Assert.Single(await Imports.StageEvidenceAsync([Order()]));var parent=Assert.Single(await Imports.EvidenceAsync());
        await Imports.ConfirmEvidenceMovementAsync(id,parent.Version,null,food,true);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.SplitEvidenceAsync(id,parent.Version,Charges()));
        await using var db=Open();Assert.Single(await db.Transactions.ToListAsync());Assert.Single(await db.PurchaseEvidence.ToListAsync());
    }
    [Fact] public async Task MixedPaymentHasOnePostingAndCategoryReportsPreserveTheBankTotal()
    {
        var id=Assert.Single(await Imports.StageEvidenceAsync([Example()]));var evidence=Assert.Single(await Imports.EvidenceAsync());
        var transaction=await Imports.ConfirmEvidenceMovementAsync(id,evidence.Version,null,food,true);
        Assert.Equal(transaction,await Imports.ConfirmEvidenceMovementAsync(id,evidence.Version,null,food,true));
        await using(var db=Open()){Assert.Single(await db.Transactions.ToListAsync());Assert.Equal(-100000,Assert.Single(await db.Postings.ToListAsync()).AmountUnits);}
        var dashboard=await Finance.DashboardAsync(2025);Assert.Equal(100000,dashboard.ExpenseUnits);Assert.Equal(75000,dashboard.Expenses.Single(e=>e.Name=="Alimentação").Units);Assert.Equal(25000,dashboard.Expenses.Single(e=>e.Name=="Cuidados pessoais").Units);
        var row=Assert.Single((await Finance.MovementsAsync(new(){CategoryId=careRoot})).Rows);Assert.Equal(transaction,row.Id);Assert.Empty(row.CategoryAllocations);Assert.Equal("Higiene",row.Category);Assert.Equal("Cuidados pessoais",row.ParentCategory);Assert.Equal(-25000,row.Units);Assert.True(row.IsCategoryEntry);
        Assert.Equal(-25000,Assert.Single((await Finance.MovementsAsync(new(){CategoryId=care})).Rows).Units);
        var entries=await Finance.MovementsAsync(new(){TransactionId=transaction});Assert.Equal(2,entries.Total);Assert.Equal(-100000,entries.Rows.Sum(p=>p.Units));Assert.Equal(2,entries.Rows.Select(p=>p.EntryKey).Distinct().Count());Assert.Equal(2,dashboard.Count);
        var csv=await Finance.ExportCsvAsync(new(){CategoryId=care,Sort=MovementSort.Category});Assert.Contains("Higiene",csv);Assert.Contains("-2.5000",csv);Assert.DoesNotContain("Várias categorias",csv);Assert.Equal(2,csv.Split('\n',StringSplitOptions.RemoveEmptyEntries).Length);
        var statement=Assert.Single(await Imports.StageAsync("mixed-match.pdf",BankImportTests.SyntheticPdf(firstDescription:"Continente Loures",continuation:"")));
        var match=Assert.Single((await Imports.PreviewAsync(statement)).Duplicates[1]);
        Assert.Equal(transaction,match.TransactionId);
        Assert.Equal(new[]{"Alimentação","Cuidados pessoais / Higiene"},match.Classifications);
        var review=await Imports.PrepareReviewAsync(statement);Assert.Equal("Link",review.Rows[0].Decision);
        await Imports.ConfirmRowsAsync(review,[1]);
        var email=Example();email.SourceKey="email:receipt";email.Source="Email Continente: confirmação da compra";
        Assert.Equal(id,Assert.Single(await Imports.StageEvidenceAsync([email])));
        var after=await Finance.MovementsAsync(new(){TransactionId=transaction});
        Assert.Equal(entries.Rows.Select(r=>r.EntryKey),after.Rows.Select(r=>r.EntryKey));Assert.Equal(-100000,after.Rows.Sum(r=>r.Units));
        Assert.All(after.Rows,r=>Assert.Equal(new DateOnly(2025,1,1),r.Date));
        Assert.All(after.Rows,r=>
        {
            Assert.Equal(3,r.Sources.Count);
            Assert.Contains(r.Sources,s=>s.Kind=="Email"&&s.Url.StartsWith($"/compras?compra={id}#fonte-"));
            Assert.Contains(r.Sources,s=>s.Kind=="Fatura / documento");
            Assert.Contains(r.Sources,s=>s.Kind=="Extrato"&&s.Download&&s.Url==$"/api/v1/statements/{statement}/original");
        });
        Assert.Equal(100000,(await Finance.DashboardAsync(2025)).ExpenseUnits);
        await using(var db=Open()){Assert.Single(await db.Transactions.Where(t=>t.Kind==MovementKind.Expense).ToListAsync());Assert.Single(await db.BankImportEntries.ToListAsync());}
    }
    [Fact] public async Task BankPaymentThenInvoiceCreatesCategoryRecordsWithoutAnotherExpense()
    {
        var account=await Finance.SaveAccountAsync(new(){Name="Conta teste",BalanceStartDate=new(2025,1,1)});
        var transaction=await Finance.SaveMovementAsync(new(){Date=new(2025,1,1),Kind=MovementKind.Expense,AccountId=account,CategoryId=food,Amount=10,Description="Continente"});
        var id=Assert.Single(await Imports.StageEvidenceAsync([Example()]));var evidence=Assert.Single(await Imports.EvidenceAsync());
        await Imports.LinkEvidenceAsync(id,evidence.Version,transaction);
        var page=await Finance.MovementsAsync(new(){Kind=MovementKind.Expense});Assert.Equal(2,page.Total);Assert.Equal(-100000,page.Rows.Sum(r=>r.Units));Assert.All(page.Rows,r=>Assert.Equal(transaction,r.Id));
        Assert.Equal(100000,(await Finance.DashboardAsync(2025)).ExpenseUnits);
        await using var db=Open();Assert.Single(await db.Transactions.Where(t=>t.Kind==MovementKind.Expense).ToListAsync());
    }
    [Fact] public async Task CategoryRecordDisplaysLinkedSourcesWithoutExpandingRawInvoiceText()
    {
        await using var services=new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        var row=new MovementRow{Description="Continente",EvidenceId=7,Origin="Purchase",Notes="ARTIGO E MORADA DO EMAIL",Sources=[new("Email","Confirmação Continente","/compras?compra=7#fonte-9"),new("Extrato","extrato.pdf","/api/v1/statements/3/original",true)]};
        var html=await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<FinanceForgeLab.Web.Components.MovementDetail>(Microsoft.AspNetCore.Components.ParameterView.FromDictionary(new Dictionary<string,object?>{{"Row",row}}))).ToHtmlString()));
        Assert.Contains("Origem e documentos (2)",html);Assert.Contains("Compra documentada",html);
        Assert.Contains("/compras?compra=7#fonte-9",html);Assert.Contains("/api/v1/statements/3/original",html);
        Assert.DoesNotContain("ARTIGO E MORADA DO EMAIL",html);Assert.DoesNotContain("<details open",html);
    }
    [Fact] public async Task CategoryRecordsAreSortedFilteredAndPagedBeforeCsvExport()
    {
        for(var i=0;i<6;i++)
        {
            var evidence=Example($"invoice-{i}",100000+i*1000);evidence.Date=new DateOnly(2025,1,1).AddDays(i*15);
            var id=Assert.Single(await Imports.StageEvidenceAsync([evidence]));
            await Imports.ConfirmEvidenceMovementAsync(id,1,null,food,true);
        }
        var first=await Finance.MovementsAsync(new(){PageSize=10,Sort=MovementSort.Amount,Descending=false});
        var last=await Finance.MovementsAsync(new(){PageSize=10,Page=2,Sort=MovementSort.Amount,Descending=false});
        Assert.Equal(12,first.Total);Assert.Equal(10,first.Rows.Count);Assert.Equal(2,last.Rows.Count);
        var all=first.Rows.Concat(last.Rows).ToList();Assert.Equal(12,all.Select(r=>r.EntryKey).Distinct().Count());Assert.Equal(all.Select(r=>r.Units).Order(),all.Select(r=>r.Units));
        Assert.Equal(6,(await Finance.MovementsAsync(new(){CategoryId=careRoot})).Total);
        var csv=await Finance.ExportCsvAsync(new(){Sort=MovementSort.Amount,Descending=false});Assert.Equal(13,csv.Split('\n',StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(-all.Sum(r=>r.Units),(await Finance.DashboardAsync(2025)).ExpenseUnits);
    }
    [Fact] public async Task BenefitsAreSavedWithoutBankMatchesOrAdditionalExpenses()
    {
        var id=Assert.Single(await Imports.StageEvidenceAsync([Example(bank:0)]));var evidence=Assert.Single(await Imports.EvidenceAsync());
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmEvidenceMovementAsync(id,evidence.Version,null,food,true));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ApproveEvidenceAsync(id,evidence.Version,1,food,true));
        Assert.Empty((await Imports.EvidenceMatchesAsync())[id]);await using var db=Open();Assert.Empty(await db.Transactions.ToListAsync());
    }
    [Fact] public async Task CardTopUpIsATransferAndItsReceiptsCannotCreateAnotherExpense()
    {
        var bank=await Finance.SaveAccountAsync(new(){Name="Banco",BalanceStartDate=new(2025,1,1)});
        var wallet=await Finance.SaveAccountAsync(new(){Name="Cartão Continente",Type="Other",BalanceStartDate=new(2025,1,1)});
        var top=Example();top.Units=top.FinancialUnits=1000000;top.PaymentMethod="TopUp";top.CategoryId=null;top.CategoryAllocationsJson="[]";
        var id=Assert.Single(await Imports.StageEvidenceAsync([top]));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmEvidenceMovementAsync(id,1,bank,food,true));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ApproveEvidenceAsync(id,1,bank,food,true));
        Assert.Empty((await Imports.EvidenceMatchesAsync([id]))[id]);
        var transfer=await Finance.SaveMovementAsync(new(){Date=new(2025,1,1),Kind=MovementKind.Transfer,AccountId=bank,DestinationAccountId=wallet,Amount=100,Description="Carregamento Continente"});
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.LinkTopUpAsync(id,1,transfer,bank));
        await Imports.LinkTopUpAsync(id,1,transfer,wallet);await Imports.LinkTopUpAsync(id,1,transfer,wallet);
        Assert.Equal(transfer,Assert.Single((await Imports.EvidenceMatchesAsync([id]))[id]).TransactionId);
        var expense=Example("receipt:card");expense.Units=expense.FinancialUnits=100000;expense.PaymentMethod="StoredValue";expense.CategoryAllocationsJson=JsonSerializer.Serialize(new[]{new PurchaseCategoryAllocation(food,75000),new PurchaseCategoryAllocation(care,25000)});
        var purchase=Assert.Single(await Imports.StageEvidenceAsync([expense]));await Imports.ConfirmEvidenceMovementAsync(purchase,1,wallet,food,true);
        Assert.Equal(100000,(await Finance.DashboardAsync(2025)).ExpenseUnits);
        Assert.Equal(900000,(await Finance.AccountsAsync()).Single(a=>a.Account.Id==wallet).BalanceUnits);
        Assert.Single(Assert.Single((await Finance.MovementsAsync(new(){TransactionId=transfer})).Rows).Sources);
        await using var db=Open();Assert.Equal(2,await db.Postings.CountAsync(p=>p.TransactionId==transfer));
    }
    [Fact] public async Task DashboardCategoriesIdentifyRootFiltersAndUnclassifiedExcludesAllocatedPurchases()
    {
        var id=Assert.Single(await Imports.StageEvidenceAsync([Example()]));var e=Assert.Single(await Imports.EvidenceAsync());
        var mixed=await Imports.ConfirmEvidenceMovementAsync(id,e.Version,null,food,true);
        long unclassified;
        await using(var db=Open())
        {
            (await db.Postings.SingleAsync(p=>p.TransactionId==mixed)).CategoryId=null;
            var t=new FinancialTransaction{HouseholdId=1,Date=new(2025,1,2),Kind=MovementKind.Expense,Description="Despesa sem categoria",Origin="Legacy"};db.Add(t);await db.SaveChangesAsync();unclassified=t.Id;
            db.Add(new Posting{TransactionId=t.Id,Date=t.Date,AmountUnits=-10000});await db.SaveChangesAsync();
        }
        var dashboard=await Finance.DashboardAsync(2025);
        Assert.Equal(food,dashboard.Expenses.Single(c=>c.Name=="Alimentação").CategoryId);
        Assert.Equal(careRoot,dashboard.Expenses.Single(c=>c.Name=="Cuidados pessoais").CategoryId);
        Assert.Null(dashboard.Expenses.Single(c=>c.Name=="Por classificar").CategoryId);
        Assert.Equal(unclassified,Assert.Single((await Finance.MovementsAsync(new(){CategoryId=0,Kind=MovementKind.Expense,From=new(2025,1,1),To=new(2025,12,31)})).Rows).Id);
        foreach(var category in dashboard.Expenses)
        {
            var rows=(await Finance.MovementsAsync(new(){CategoryId=category.CategoryId??0,Kind=MovementKind.Expense,From=new(2025,1,1),To=new(2025,12,31)})).Rows;
            Assert.Single(rows);Assert.Equal(category.CategoryId is null?unclassified:mixed,rows[0].Id);
        }
    }
    [Fact] public async Task InvalidOrStaleAllocationsDoNotChangeFinancialAmounts()
    {
        var bad=Example();bad.CategoryAllocationsJson=JsonSerializer.Serialize(new[]{new PurchaseCategoryAllocation(food,1)});
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.StageEvidenceAsync([bad]));
        var id=Assert.Single(await Imports.StageEvidenceAsync([Example()]));var evidence=Assert.Single(await Imports.EvidenceAsync());
        await Imports.ConfirmEvidenceMovementAsync(id,evidence.Version,null,food,true);evidence=Assert.Single(await Imports.EvidenceAsync());
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.SaveEvidenceAllocationsAsync(id,evidence.Version-1,[new(food,200000)]));
        await Imports.SaveEvidenceAllocationsAsync(id,evidence.Version,[new(care,200000)]);
        Assert.Equal(100000,(await Finance.DashboardAsync(2025)).ExpenseUnits);Assert.Empty((await Finance.MovementsAsync(new(){CategoryId=food})).Rows);
        await using var db=Open();Assert.Equal(-100000,Assert.Single(await db.Postings.ToListAsync()).AmountUnits);
    }
    [Fact] public async Task ArchivedPdfIsImmutableAndSurvivesDatabaseBackup()
    {
        var id=Assert.Single(await Imports.StageEvidenceAsync([Example()]));var content=BankImportTests.SyntheticPdf();
        await Imports.AttachEvidencePdfAsync("pdf:receipt","invoice.pdf",content);await Imports.AttachEvidencePdfAsync("pdf:receipt","renamed.pdf",content);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.AttachEvidencePdfAsync("pdf:receipt","other.pdf",BankImportTests.SyntheticPdf("changed")));
        var source=Assert.Single(await Imports.EvidenceSourcesAsync());Assert.Empty(source.DocumentContent);Assert.NotEmpty(source.DocumentSha256);
        await using(var services=new ServiceCollection().AddLogging().AddSingleton(Imports).AddSingleton(Finance).BuildServiceProvider())
        {
            await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
            var html=await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<FinanceForgeLab.Web.Components.Pages.Purchases>()).ToHtmlString()));
            Assert.Contains("Repartição por categoria",html);Assert.Contains("Fatura PDF original",html);Assert.Contains("Guardar repartição",html);
        }
        var backup=Path.Combine(folder,"backup.sqlite");DatabaseFiles.Backup(Path.Combine(folder,"test.sqlite"),backup);
        var restored=new BankImportService(()=>FinanceDb.Open(backup),folder);Assert.Equal(content,(await restored.EvidenceOriginalAsync(source.Id)).Content);
    }
    [Fact] public void ProportionalAllocationConservesCentsAndRejectsMalformedInput()
    {
        var json=JsonSerializer.Serialize(new[]{new PurchaseCategoryAllocation(1,100),new PurchaseCategoryAllocation(2,100),new PurchaseCategoryAllocation(3,100)});
        var rows=PurchaseAllocationMath.Read(json,300,200);Assert.Equal(200,rows.Sum(r=>r.Units));Assert.Equal(new long[]{1,2},rows.Select(r=>r.CategoryId));
        Assert.False(PurchaseAllocationMath.ContainsCategory("{broken",1,300,200));Assert.Throws<FinanceRuleException>(()=>PurchaseAllocationMath.Read(json,299,200));
    }
    [Fact] public async Task InvoiceCoverageUsesTheActualAccountAndNeverTreatsBenefitsAsBankPayments()
    {
        var first=Assert.Single(await Imports.StageAsync("one.pdf",BankImportTests.SyntheticPdf(firstDescription:"Continente Loures",continuation:"")));
        var account=(await Imports.PreviewAsync(first)).Import.AccountId!.Value;
        var id=Assert.Single(await Imports.StageEvidenceAsync([Example()]));var evidence=Assert.Single(await Imports.EvidenceAsync());
        await Imports.ConfirmEvidenceMovementAsync(id,evidence.Version,account,food,true);
        Assert.Empty((await Imports.InvoiceCoverageAsync("Continente")).Gaps);
        await Imports.StageAsync("other-account.pdf",BankImportTests.SyntheticPdf("other-account",reference:"7777 6666 3333",firstDescription:"Continente Portugal",continuation:""));
        await Imports.StageEvidenceAsync([Example("benefits:other",0)]);
        var coverage=await Imports.InvoiceCoverageAsync("Continente");var gap=Assert.Single(coverage.Gaps);Assert.True(gap.PossibleTopUp);Assert.Equal(100000,gap.Units);Assert.Equal(new DateOnly(2025,1,1),coverage.From);
    }
}
