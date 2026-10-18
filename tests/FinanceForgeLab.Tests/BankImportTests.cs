using System.Globalization;
using System.Text;
using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class BankImportTests : IAsyncLifetime
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ffl-bank-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(folder, "test.sqlite");
    private FinanceDb Open() => FinanceDb.Open(DatabasePath);
    private BankImportService Imports => new(Open, Path.Combine(folder, "pdfs"));
    private FinanceService Finance => new(Open);
    private long account, savings, expense, income;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(folder); await using var db = Open(); await db.Database.MigrateAsync();
        var household = new Household(); db.Add(household); await db.SaveChangesAsync();
        var e = new Category { HouseholdId = household.Id, Name = "Casa", Kind = MovementKind.Expense };
        var i = new Category { HouseholdId = household.Id, Name = "Trabalho", Kind = MovementKind.Income };
        db.AddRange(e, i); await db.SaveChangesAsync(); expense = e.Id; income = i.Id;
        var initial = Assert.Single(await Imports.StageAsync("synthetic.pdf", SyntheticPdf()));
        account = (await Imports.PreviewAsync(initial)).Import.AccountId!.Value;
        savings = await Finance.SaveAccountAsync(new() { Name = "Poupanca", OpeningBalance = 200, BalanceStartDate = new(2025, 1, 1) });
    }
    public Task DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); return Task.CompletedTask; }
    private async Task<BankImportReview> Review(byte[]? pdf = null)
    {
        var id = (await Imports.StageAsync("synthetic.pdf", pdf ?? SyntheticPdf()))[0];
        var p = await Imports.PreviewAsync(id, account);
        return new() { Id = id, Version = p.Import.Version, AccountId = account, Rows = p.Statement.Rows.Select(r => new BankRowDecision { Ordinal = r.Ordinal, Kind = r.Units > 0 ? MovementKind.Income : MovementKind.Expense, CategoryId = r.Units > 0 ? income : expense }).ToList() };
    }
    private async Task<long> SmallSummaryDiscrepancy(string scenario = "small")
    {
        await using var db = Open();
        var reference = await db.BankImports.Select(b => b.SourceReference).FirstAsync();
        var section = new StatementAccount { Reference=reference, From=new(2026,7,1), To=new(2026,7,31),
            OpeningUnits=1_000_000, ClosingUnits=scenario=="large"?900300:900000, DebitTotalUnits=scenario=="large"?99700:100000, CreditTotalUnits=0,
            Errors=["Os movimentos Revolut não conferem com os totais e o saldo final."], Rows=[
                new(){Ordinal=1,Date=new(2026,7,5),ValueDate=new(2026,7,5),Description="Render verificação cartão",Units=-8900,BalanceUnits=991100},
                new(){Ordinal=2,Date=new(2026,7,5),ValueDate=new(2026,7,6),Description="Render mensalidade",Units=-100000,BalanceUnits=891100},
                new(){Ordinal=3,Date=new(2026,7,7),ValueDate=new(2026,7,7),Description="Render devolução",Units=8700,BalanceUnits=899800}] };
        if(scenario=="balance")section.Rows[1].BalanceUnits++;
        if(scenario=="reading")section.Errors.Add("Data ambígua.");
        if(scenario=="missing-total")section.CreditTotalUnits=null;
        if(scenario=="summary-equation")section.ClosingUnits++;
        if(scenario=="date")section.Rows[0].Date=section.From.AddDays(-1);
        var statement=new BankStatement{BankName="Revolut",Accounts=[section],Errors=scenario=="document"?["Documento incompleto."]:[]};
        var bytes=Encoding.UTF8.GetBytes("%PDF-summary-discrepancy-"+scenario);
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        db.Add(new BankDocument{Id=hash,Name="summary-discrepancy.pdf",Content=bytes,StatementJson=System.Text.Json.JsonSerializer.Serialize(statement)});
        var batch=new BankImport{DocumentId=hash,SourceReference=reference,AccountId=account};db.Add(batch);await db.SaveChangesAsync();
        var review=new BankImportReview{Id=batch.Id,Version=batch.Version,AccountId=account,Rows=section.Rows.Select(r=>new BankRowDecision{Ordinal=r.Ordinal,Kind=MovementKind.Expense,CategoryId=expense}).ToList()};
        batch.ReviewJson=System.Text.Json.JsonSerializer.Serialize(review);await db.SaveChangesAsync();return batch.Id;
    }
    [Theory]
    [InlineData("large")][InlineData("balance")][InlineData("reading")][InlineData("document")]
    [InlineData("missing-total")][InlineData("summary-equation")][InlineData("date")]
    public async Task SmallSummaryPolicyDoesNotBypassOtherConformanceGuards(string scenario)
    {
        var id=await SmallSummaryDiscrepancy(scenario);var preview=await Imports.PreviewAsync(id);
        Assert.NotEmpty(preview.BlockingErrors);
        await Assert.ThrowsAsync<FinanceRuleException>(async()=>await Imports.ConfirmRowsAsync(await Imports.PrepareReviewAsync(id),[2]));
        await using var db=Open();Assert.False(await db.BankImportEntries.AnyAsync(e=>e.ImportId==id));
    }
    [Fact] public async Task SmallSummaryDifferenceAllowsImportAndRemainsPendingAfterCompletionWithoutAdjustment()
    {
        var id=await SmallSummaryDiscrepancy();var before=await Finance.DashboardAsync(2026);
        var issue=Assert.Single(before.StatementInconsistencies);Assert.Equal(id,issue.ImportId);Assert.Equal(200,issue.Detail.DifferenceUnits);
        Assert.True(issue.Detail.AllowsImport);Assert.Equal(108900,issue.Detail.CalculatedDebitUnits);Assert.Equal(8700,issue.Detail.CalculatedCreditUnits);
        var preview=await Imports.PreviewAsync(id);Assert.Empty(preview.BlockingErrors);Assert.NotEmpty(preview.Statement.Errors);
        var summary=(await Imports.ListAsync()).Single(s=>s.Id==id);Assert.False(summary.HasErrors);Assert.True(summary.HasInconsistency);
        var original=(await Imports.OriginalAsync(id)).Content;
        var review=await Imports.PrepareReviewAsync(id);var result=await Imports.ConfirmAsync(review);
        Assert.Equal(3,result.Created);Assert.Equal(result,await Imports.ConfirmAsync(review));
        var after=await Finance.DashboardAsync(2026);Assert.Equal(0,after.PendingBankRows);Assert.Single(after.StatementInconsistencies);
        Assert.True(after.HasReviewPending);Assert.Equal(100200,after.ExpenseUnits);Assert.Equal(0,after.IncomeUnits);
        Assert.Empty((await Finance.DashboardAsync(2025)).StatementInconsistencies);
        Assert.Equal(original,(await Imports.OriginalAsync(id)).Content);
        await using var db=Open();Assert.Equal(3,await db.BankImportEntries.CountAsync(e=>e.ImportId==id));
        var audit=await db.AuditChanges.SingleAsync(a=>a.Entity=="BankImport"&&a.EntityId==id&&a.Action=="Confirm");
        Assert.Contains("\"DifferenceUnits\":200",audit.DetailJson);Assert.Contains("PreservedSectionErrors",audit.DetailJson);
        await Imports.VoidAsync(id,(await Imports.PreviewAsync(id)).Import.Version);
        Assert.Empty((await Finance.DashboardAsync(2026)).StatementInconsistencies);
    }
    [Fact] public async Task SmallSummaryDifferenceShowsPendingEntryAndAllowsIndividualApprovalInThePage()
    {
        var id=await SmallSummaryDiscrepancy();
        var html=await RenderReview(id,interact:async page=>await page.Call("ApproveRow",2));
        Assert.Contains("Aprovação concluída: 1 movimentos criados",html);
        Assert.Contains("Incongruência no resumo do extrato",html);Assert.Contains("Podes aprovar as linhas válidas",html);
        Assert.DoesNotContain("Este lote não pode ser confirmado",html);Assert.DoesNotContain("✓ Saldos e totais conferidos",html);
        await using var services=new ServiceCollection().AddLogging().AddSingleton(Finance).BuildServiceProvider();
        await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        var pending=await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<FinanceForgeLab.Web.Components.Pages.Pending>()).ToHtmlString()));
        Assert.Contains("Incongruências dos extratos",pending);Assert.Contains("summary-discrepancy.pdf",pending);
        Assert.Contains($"/extratos?rever={id}",pending);Assert.Contains($"/api/v1/statements/{id}/original",pending);
        Assert.Contains("continuam visíveis depois de tratar as linhas",pending);
    }
    [Theory]
    [InlineData("valid",true)]
    [InlineData("unchecked",false)]
    [InlineData("balance",false)]
    [InlineData("other-error",false)]
    [InlineData("document-error",false)]
    [InlineData("credit",false)]
    [InlineData("short-reason",false)]
    [InlineData("duplicate",false)]
    public async Task VerifiedDebitRetainsSummaryWarningAndRequiresIndividualProof(string scenario,bool expected)
    {
        await using var db=Open();
        var reference=await db.BankImports.Select(b=>b.SourceReference).FirstAsync();
        const string warning="Os movimentos Revolut não conferem com os totais e o saldo final.";
        var statement=new BankStatement{BankName="Revolut",Errors=scenario=="document-error"?["Documento incompleto."]:[],Accounts=[new(){
            Reference=reference,From=new(2026,7,1),To=new(2026,7,31),OpeningUnits=1000000,ClosingUnits=850200,
            Errors=scenario=="other-error"?["Data ambígua."]:[warning],Rows=[
                new(){Ordinal=1,Date=new(2026,7,5),ValueDate=new(2026,7,6),Description="Render.com",Units=scenario=="credit"?100000:-100000,BalanceUnits=scenario=="balance"?899900:900000},
                new(){Ordinal=2,Date=new(2026,7,8),Description="Outra compra por conferir",Units=-50000,BalanceUnits=850000}]}]};
        var bytes=Encoding.UTF8.GetBytes("%PDF-verified-debit-"+scenario);
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var json=System.Text.Json.JsonSerializer.Serialize(statement);
        db.Add(new BankDocument{Id=hash,Name="verified.pdf",Content=bytes,StatementJson=json});await db.SaveChangesAsync();
        var batch=new BankImport{DocumentId=hash,SourceReference=reference,AccountId=account};db.Add(batch);await db.SaveChangesAsync();
        var review=new BankImportReview{Id=batch.Id,Version=batch.Version,AccountId=account,Rows=[
            new(){Ordinal=1,Decision="Import",Kind=MovementKind.Expense,CategoryId=expense,ClassificationEdited=scenario!="unchecked"},
            new(){Ordinal=2,Decision="Pending",Kind=MovementKind.Expense,CategoryId=expense,Detail="Manter por conferir."}]};
        batch.ReviewJson=System.Text.Json.JsonSerializer.Serialize(review);await db.SaveChangesAsync();
        if(scenario=="duplicate")await Finance.SaveMovementAsync(new(){Date=new(2026,7,5),Kind=MovementKind.Expense,AccountId=account,CategoryId=expense,Amount=10,Description="Render.com"});
        var before=await db.Transactions.CountAsync();
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmRowsAsync(review,[1]));
        var reason=scenario=="short-reason"?"ok":"Recibo do fornecedor e débito individual conferidos por entidade, montante, data e saldo sucessivo; aviso global permanece.";
        if(!expected)
        {
            await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmVerifiedDebitRowsAsync(review,[1],reason));
            Assert.Equal(before,await db.Transactions.CountAsync());Assert.False(await db.BankImportEntries.AnyAsync(e=>e.ImportId==batch.Id));return;
        }
        await Imports.ConfirmVerifiedDebitRowsAsync(review,[1],reason);
        var reopened=await Imports.PrepareReviewAsync(batch.Id);
        await Imports.ConfirmVerifiedDebitRowsAsync(reopened,[1],reason);
        Assert.Equal(before+1,await db.Transactions.CountAsync());
        var entry=Assert.Single(await db.BankImportEntries.Where(e=>e.ImportId==batch.Id).ToListAsync());
        Assert.Equal(1,entry.Ordinal);Assert.True(entry.Created);
        var post=await db.Postings.SingleAsync(p=>p.TransactionId==entry.TransactionId);
        Assert.Equal(-100000,post.AmountUnits);Assert.Equal(new DateOnly(2026,7,6),post.ValueDate);
        var saved=await db.BankImports.AsNoTracking().SingleAsync(b=>b.Id==batch.Id);
        Assert.Equal("Draft",saved.Status);Assert.Equal(json,(await db.BankDocuments.AsNoTracking().SingleAsync(d=>d.Id==hash)).StatementJson);
        Assert.Equal("Pending",System.Text.Json.JsonSerializer.Deserialize<BankImportReview>(saved.ReviewJson)!.Rows[1].Decision);
        var audits=await db.AuditChanges.Where(a=>a.Entity=="BankImport"&&a.EntityId==batch.Id&&a.Action=="ConfirmVerifiedDebits").ToListAsync();
        Assert.Equal(2,audits.Count);
        Assert.All(audits,a=>{using var audit=System.Text.Json.JsonDocument.Parse(a.DetailJson);Assert.Equal(reason,audit.RootElement.GetProperty("VerifiedDebitReason").GetString());Assert.Equal(warning,Assert.Single(audit.RootElement.GetProperty("PreservedSectionErrors").EnumerateArray()).GetString());});
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmAsync(reopened));
    }
    [Theory]
    [InlineData(false,false,2,100000L,false,true)]
    [InlineData(true,false,2,100000L,false,false)]
    [InlineData(true,true,2,100000L,false,true)]
    [InlineData(false,false,5,100000L,false,false)]
    [InlineData(false,false,2,99900L,false,false)]
    [InlineData(false,false,2,100000L,true,false)]
    public async Task InternalTransferCorrectionPreservesSettlementDatesAndGuardsAgainstDuplicateCredits(bool warning,bool acknowledge,int elapsed,long creditUnits,bool duplicate,bool expected)
    {
        var source=await Review(SyntheticPdf("internal-transfer",firstDescription:"Trf Para Antonio",continuation:""));
        await Imports.ConfirmRowsAsync(source,[1]);
        await using var db=Open();var sourceEntry=await db.BankImportEntries.AsNoTracking().SingleAsync(e=>e.ImportId==source.Id);
        var movement=await db.Transactions.AsNoTracking().SingleAsync(t=>t.Id==sourceEntry.TransactionId);
        var destinationDate=movement.Date.AddDays(elapsed);
        var statement=new BankStatement{BankName="Revolut",Accounts=[new(){Reference="destination-proof",From=movement.Date,To=destinationDate,OpeningUnits=0,ClosingUnits=creditUnits,
            Errors=warning?["Os movimentos Revolut não conferem com os totais e o saldo final."]:[],
            Rows=[new(){Ordinal=1,Date=destinationDate,ValueDate=destinationDate,Description="Carregamento de Antonio",Units=creditUnits,BalanceUnits=creditUnits}]}]};
        var bytes=Encoding.UTF8.GetBytes("%PDF-internal-transfer-credit");var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var json=System.Text.Json.JsonSerializer.Serialize(statement);
        db.Add(new BankDocument{Id=hash,Name="destination.pdf",Content=bytes,StatementJson=json});await db.SaveChangesAsync();
        var incoming=new BankImport{DocumentId=hash,SourceReference="destination-proof",AccountId=savings,ReviewJson=""};db.Add(incoming);await db.SaveChangesAsync();
        incoming.ReviewJson=System.Text.Json.JsonSerializer.Serialize(new BankImportReview{Id=incoming.Id,Version=incoming.Version,AccountId=savings,Rows=[new(){Ordinal=1,Decision="Pending",Kind=MovementKind.Transfer}]});await db.SaveChangesAsync();
        if(duplicate)await Finance.SaveMovementAsync(new(){Date=destinationDate,Kind=MovementKind.Income,AccountId=savings,CategoryId=income,Amount=10,Description="Already recorded credit"});
        var before=await Finance.DashboardAsync(2025);var postsBefore=await db.Postings.CountAsync();
        Task Correct()=>Imports.ReclassifyBankTransferAsync(movement.Id,movement.Version,source.Id,1,incoming.Id,1,savings,"Titular e dois extratos confirmam transferência interna.",acknowledge);
        if(!expected)
        {
            await Assert.ThrowsAsync<FinanceRuleException>(Correct);
            Assert.Equal(MovementKind.Expense,(await db.Transactions.AsNoTracking().SingleAsync(t=>t.Id==movement.Id)).Kind);
            Assert.Equal(postsBefore,await db.Postings.CountAsync());Assert.False(await db.BankImportEntries.AnyAsync(e=>e.ImportId==incoming.Id));return;
        }
        await Correct();await Correct();
        var saved=await db.Transactions.AsNoTracking().SingleAsync(t=>t.Id==movement.Id);
        var posts=await db.Postings.AsNoTracking().Where(p=>p.TransactionId==movement.Id).ToListAsync();
        Assert.Equal(MovementKind.Transfer,saved.Kind);Assert.Equal(movement.Date,saved.Date);Assert.Equal(2,posts.Count);Assert.Equal(0,posts.Sum(p=>p.AmountUnits));Assert.All(posts,p=>Assert.Null(p.CategoryId));
        Assert.Equal(movement.Date,posts.Single(p=>p.AccountId==account).Date);Assert.Equal(destinationDate,posts.Single(p=>p.AccountId==savings).Date);
        var entry=await db.BankImportEntries.AsNoTracking().SingleAsync(e=>e.ImportId==incoming.Id);Assert.Equal(movement.Id,entry.TransactionId);Assert.False(entry.Created);
        Assert.Equal(json,(await db.BankDocuments.AsNoTracking().SingleAsync(d=>d.Id==hash)).StatementJson);
        var batch=await db.BankImports.AsNoTracking().SingleAsync(b=>b.Id==incoming.Id);Assert.Equal("Draft",batch.Status);
        var choice=System.Text.Json.JsonSerializer.Deserialize<BankImportReview>(batch.ReviewJson)!.Rows.Single();Assert.Equal("Link",choice.Decision);Assert.Equal(MovementKind.Transfer,choice.Kind);Assert.Equal(account,choice.CounterpartyAccountId);
        var after=await Finance.DashboardAsync(2025);Assert.Equal(before.ExpenseUnits-100000,after.ExpenseUnits);Assert.Equal(before.IncomeUnits,after.IncomeUnits);
        Assert.Single(await db.AuditChanges.Where(a=>a.Action=="ReclassifyInternalTransfer").ToListAsync());
        var edit=await Finance.MovementAsync(movement.Id);edit.Notes="Detalhe atualizado pelo titular.";await Finance.SaveMovementAsync(edit);
        var editedPosts=await db.Postings.AsNoTracking().Where(p=>p.TransactionId==movement.Id).ToListAsync();
        Assert.Equal(movement.Date,editedPosts.Single(p=>p.AccountId==account).Date);Assert.Equal(destinationDate,editedPosts.Single(p=>p.AccountId==savings).Date);
        Assert.Equal(saved.Version+1,(await Imports.PrepareReviewAsync(incoming.Id)).Rows.Single().ExistingVersion);
    }
    [Fact] public async Task DocumentListingIncludesGroupingIdentityAndReadErrors()
    {
        var initial=Assert.Single(await Imports.ListAsync());
        Assert.NotEmpty(initial.DocumentId);Assert.Equal(0,initial.SectionIndex);Assert.Equal(2,initial.Rows);Assert.False(initial.HasErrors);
        await Imports.StageAsync("synthetic.pdf",SyntheticPdf("bad-listing",closing:"1.040,01"));
        var documents=BankDocumentSummary.Group(await Imports.ListAsync());
        Assert.Equal(2,documents.Count);Assert.Single(documents,d=>d.HasErrors);
    }
    [Fact] public async Task UploadAutomaticallyIdentifiesExistingPurchaseAccountAndRetryKeepsOneExpense()
    {
        var e=PurchaseDocument("auto:account");var id=Assert.Single(await Imports.StageEvidenceAsync([e]));
        var movement=await Imports.ConfirmEvidenceMovementAsync(id,1,null,expense,true);
        var pdf=SyntheticPdf("auto-account",firstDescription:"AMAZON",continuation:"");
        var statement=Assert.Single(await Imports.StageAsync("bank.pdf",pdf));
        await using var db=Open();
        Assert.Equal(account,(await db.Postings.SingleAsync(p=>p.TransactionId==movement)).AccountId);
        Assert.Equal(account,(await db.PurchaseEvidence.SingleAsync(p=>p.Id==id)).AccountId);
        Assert.Equal(movement,(await db.BankImportEntries.SingleAsync(p=>p.ImportId==statement)).TransactionId);
        Assert.Contains(await db.AuditChanges.ToListAsync(),a=>a.Action=="AutomaticPurchaseReconciliation");
        await Imports.StageAsync("again.pdf",pdf);
        Assert.Single(await db.Transactions.Where(t=>t.Kind==MovementKind.Expense).ToListAsync());
        Assert.Single(await db.BankImportEntries.Where(p=>p.ImportId==statement).ToListAsync());
        Assert.Equal(-100000,(await db.Postings.SingleAsync(p=>p.TransactionId==movement)).AmountUnits);
    }
    [Fact] public async Task AutomaticReconciliationDoesNotChooseBetweenTwoPeopleAccounts()
    {
        var a=Assert.Single(await Imports.StageAsync("antonio.pdf",SyntheticPdf("account-a",firstDescription:"AMAZON",continuation:"")));
        var b=Assert.Single(await Imports.StageAsync("suzana.pdf",SyntheticPdf("account-b",reference:"9999 8888 7777",firstDescription:"AMAZON",continuation:"")));
        var id=Assert.Single(await Imports.StageEvidenceAsync([PurchaseDocument("auto:ambiguous-accounts")]));
        var movement=await Imports.ConfirmEvidenceMovementAsync(id,1,null,expense,true);
        Assert.Equal(0,await Imports.ReconcileExistingPurchasesAsync([a,b]));
        await using var db=Open();Assert.Null((await db.Postings.SingleAsync(p=>p.TransactionId==movement)).AccountId);Assert.Empty(await db.BankImportEntries.ToListAsync());
    }
    [Theory]
    [InlineData("4704", 2, true, "704")]
    [InlineData("4868", 2, false, "704")]
    [InlineData("4704", 3, false, "704")]
    [InlineData("4704", 2, true, "8644704")]
    [InlineData("3704", 2, false, "8644704")]
    public async Task ViaVerdeLowValueDebitRequiresDocumentedCardAndExactDay(string card, int day, bool matched, string bankCard)
    {
        var e=PurchaseDocument("via-verde:card:"+card+":"+day);e.Channel="Via Verde";e.Merchant="Via Verde";
        e.Description="Via Verde — portagens e estacionamento";e.Date=new(2025,1,day);e.Details=$"Débitos agrupados por data e cartão. Cartão terminado em {card}.";
        var id=Assert.Single(await Imports.StageEvidenceAsync([e]));var movement=await Imports.ConfirmEvidenceMovementAsync(id,1,null,expense,true);
        var pdf=SyntheticPdf("via-verde:"+card+":"+day,firstDescription:$"PAGAMENTO BAIXO VALOR CARTAO ****{bankCard}*",continuation:"");
        var batch=Assert.Single(await Imports.StageAsync("bank.pdf",pdf));
        await Imports.StageAsync("retry.pdf",pdf);
        await using var db=Open();Assert.Single(await db.Transactions.Where(t=>t.Kind==MovementKind.Expense).ToListAsync());
        Assert.Equal(matched?account:(long?)null,(await db.Postings.SingleAsync(p=>p.TransactionId==movement)).AccountId);
        Assert.Equal(matched?1:0,await db.BankImportEntries.CountAsync(x=>x.ImportId==batch));
    }
    [Fact] public async Task AutomaticReconciliationPreservesExcludedDecisionAndInvalidReading()
    {
        var pdf=SyntheticPdf("explicit-decision",firstDescription:"AMAZON",continuation:"");
        var statement=Assert.Single(await Imports.StageAsync("bank.pdf",pdf));
        var review=await Imports.PrepareReviewAsync(statement);review.Rows[0].Decision="Exclude";await Imports.SaveReviewAsync(review);
        var id=Assert.Single(await Imports.StageEvidenceAsync([PurchaseDocument("auto:excluded")]));
        var movement=await Imports.ConfirmEvidenceMovementAsync(id,1,null,expense,true);
        await Imports.StageAsync("again.pdf",pdf);
        await Imports.StageAsync("invalid.pdf",SyntheticPdf("invalid-auto",closing:"1.040,01",firstDescription:"AMAZON",continuation:""));
        await using var db=Open();Assert.Null((await db.Postings.SingleAsync(p=>p.TransactionId==movement)).AccountId);Assert.Empty(await db.BankImportEntries.ToListAsync());
        Assert.Equal("Exclude",(await Imports.PrepareReviewAsync(statement)).Rows[0].Decision);
    }
    [Fact] public async Task ConfirmingPurchaseAfterStatementImmediatelyIdentifiesItsAccount()
    {
        var statement=Assert.Single(await Imports.StageAsync("already-loaded.pdf",SyntheticPdf("already-loaded",firstDescription:"AMAZON",continuation:"")));
        var e=PurchaseDocument("invoice:after-statement");e.Date=new(2025,1,5);
        var id=Assert.Single(await Imports.StageEvidenceAsync([e]));
        var movement=await Imports.ConfirmEvidenceMovementAsync(id,1,null,expense,true);
        Assert.Equal(movement,await Imports.ConfirmEvidenceMovementAsync(id,1,null,expense,true));
        await using var db=Open();
        Assert.Equal(account,(await db.Postings.SingleAsync(p=>p.TransactionId==movement)).AccountId);
        Assert.Equal(account,(await db.PurchaseEvidence.SingleAsync(p=>p.Id==id)).AccountId);
        Assert.Equal(movement,(await db.BankImportEntries.SingleAsync(e=>e.ImportId==statement)).TransactionId);
        Assert.Single(await db.Transactions.Where(t=>t.Kind==MovementKind.Expense).ToListAsync());
    }

    [Fact] public async Task PaymentBeforeInvoiceIsMatchedWithoutChangingEitherDate()
    {
        var evidence=PurchaseDocument("invoice:after-payment");evidence.Date=new(2025,1,5);
        var id=Assert.Single(await Imports.StageEvidenceAsync([evidence]));
        var movement=await Imports.ConfirmEvidenceMovementAsync(id,1,null,expense,true);
        var statement=Assert.Single(await Imports.StageAsync("earlier-payment.pdf",SyntheticPdf("earlier-payment",firstDescription:"AMAZON",continuation:"")));
        await using var db=Open();
        Assert.Equal(account,(await db.Postings.SingleAsync(p=>p.TransactionId==movement)).AccountId);
        Assert.Equal(new DateOnly(2025,1,5),(await db.Transactions.SingleAsync(t=>t.Id==movement)).Date);
        Assert.Equal(new DateOnly(2025,1,2),(await Imports.PreviewAsync(statement)).Statement.Rows[0].Date);
        Assert.Equal(movement,(await db.BankImportEntries.SingleAsync(e=>e.ImportId==statement)).TransactionId);
    }

    [Fact] public async Task UnchargedCancellationKeepsDocumentsAndDoesNotReopenAsPending()
    {
        var proof=PurchaseDocument("booking:cancel-receipt");proof.Channel="Booking.com";proof.Date=new(2025,1,5);
        var id=Assert.Single(await Imports.StageEvidenceAsync([proof]));var movement=await Imports.ConfirmEvidenceMovementAsync(id,1,null,expense,true);
        var cancellation=PurchaseDocument("booking:cancel-confirmed");cancellation.Channel=proof.Channel;cancellation.OrderReference=proof.OrderReference;cancellation.Date=proof.Date;cancellation.Units=null;cancellation.Description="Reserva cancelada sem custos";
        Assert.Equal(id,Assert.Single(await Imports.StageEvidenceAsync([cancellation])));
        await using var db=Open();var e=await db.PurchaseEvidence.AsNoTracking().SingleAsync(e=>e.Id==id);var source=await db.PurchaseEvidenceSources.SingleAsync(s=>s.SourceKey==cancellation.SourceKey);
        await Imports.CancelUnchargedEvidenceAsync(id,e.Version,source.Id,"Autorização cancelada; sem cobrança bancária.");
        await Imports.CancelUnchargedEvidenceAsync(id,e.Version,source.Id,"Autorização cancelada; sem cobrança bancária.");
        Assert.Equal("Voided",(await db.Transactions.AsNoTracking().SingleAsync(t=>t.Id==movement)).Status);
        Assert.Equal("Cancelled",(await db.PurchaseEvidence.AsNoTracking().SingleAsync(e=>e.Id==id)).PaymentMethod);
        Assert.DoesNotContain(id,await Finance.PendingPurchaseIdsAsync());
        Assert.Equal(2,await db.PurchaseEvidenceSources.CountAsync(s=>s.EvidenceId==id));
        Assert.Single(await db.AuditChanges.Where(a=>a.EntityId==movement&&a.Action=="VoidCancelledPurchase").ToListAsync());
    }

    [Theory]
    [InlineData(100000,0,true,false)]
    [InlineData(90000,0,false,false)]
    [InlineData(100000,30,false,false)]
    [InlineData(0,0,false,false)]
    [InlineData(100000,0,false,true)]
    public async Task MerchantReceiptUsesItsAssociatedPayPalProofOnlyForTheSamePayment(long proofUnits,int dateOffset,bool expected,bool authorization)
    {
        var review=await Review(SyntheticPdf("booking-paypal",firstDescription:"COBRANCA SDD PAYPAL",continuation:""));
        await Imports.ConfirmRowsAsync(review,[review.Rows[0].Ordinal]);
        await using var db=Open();var bank=(await db.BankImportEntries.SingleAsync(e=>e.ImportId==review.Id)).TransactionId;
        var evidence=PurchaseDocument("booking:receipt");evidence.Channel="Booking.com";evidence.Merchant="Booking.com";evidence.Date=new(2025,1,5);
        var id=Assert.Single(await Imports.StageEvidenceAsync([evidence]));
        var purchase=await Imports.ConfirmEvidenceMovementAsync(id,1,null,expense,true,true);
        if(proofUnits>0){db.Add(new PurchaseEvidenceSource{HouseholdId=evidence.HouseholdId,EvidenceId=id,SourceKey="paypal:receipt",Source="PayPal receipt",PayloadJson=System.Text.Json.JsonSerializer.Serialize(new PurchaseEvidence{Channel="PayPal",Date=evidence.Date.Value.AddDays(dateOffset),Units=proofUnits,Merchant="Booking.com",PaymentMethod=authorization?"Authorization":""})});await db.SaveChangesAsync();}
        var matches=await Imports.EvidenceMatchesAsync([id]);
        Assert.Equal(expected,matches[id].Any(m=>m.TransactionId==bank));
        if(!expected){await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConsolidateBankPurchaseAsync(review.Id,review.Rows[0].Ordinal,purchase,1,bank,1));return;}
        await Imports.ConsolidateBankPurchaseAsync(review.Id,review.Rows[0].Ordinal,purchase,1,bank,1);
        db.ChangeTracker.Clear();Assert.Equal("Voided",(await db.Transactions.SingleAsync(t=>t.Id==bank)).Status);
        Assert.Equal(account,(await db.Postings.SingleAsync(p=>p.TransactionId==purchase)).AccountId);
        Assert.Equal(purchase,(await db.BankImportEntries.SingleAsync(e=>e.ImportId==review.Id)).TransactionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsolidatingVerifiedBankDuplicateKeepsPurchaseAndSourcesAndRejectsEditedBank(bool editBank)
    {
        var review=await Review(SyntheticPdf("duplicate-repair",firstDescription:"AMAZON",continuation:""));
        await Imports.ConfirmRowsAsync(review,[review.Rows[0].Ordinal]);
        await using var db=Open();
        var bank=(await db.BankImportEntries.SingleAsync(e=>e.ImportId==review.Id)).TransactionId;
        var evidence=PurchaseDocument("invoice:duplicate-repair");evidence.Date=new(2025,1,5);
        var id=Assert.Single(await Imports.StageEvidenceAsync([evidence]));
        var movement=await Imports.ConfirmEvidenceMovementAsync(id,1,null,expense,true,true);
        if(editBank)
        {
            var t=await db.Transactions.SingleAsync(t=>t.Id==bank);t.Version++;t.Description="Owner correction";await db.SaveChangesAsync();
            await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConsolidateBankPurchaseAsync(review.Id,review.Rows[0].Ordinal,movement,1,bank,1));
            Assert.Null((await db.Postings.SingleAsync(p=>p.TransactionId==movement)).AccountId);return;
        }
        await Imports.ConsolidateBankPurchaseAsync(review.Id,review.Rows[0].Ordinal,movement,1,bank,1);
        await Imports.ConsolidateBankPurchaseAsync(review.Id,review.Rows[0].Ordinal,movement,1,bank,1);
        db.ChangeTracker.Clear();
        Assert.Equal("Voided",(await db.Transactions.SingleAsync(t=>t.Id==bank)).Status);
        Assert.Equal(movement,(await db.BankImportEntries.SingleAsync(e=>e.ImportId==review.Id)).TransactionId);
        Assert.False((await db.BankImportEntries.SingleAsync(e=>e.ImportId==review.Id)).Created);
        var post=await db.Postings.SingleAsync(p=>p.TransactionId==movement);
        Assert.Equal(account,post.AccountId);Assert.Equal(expense,post.CategoryId);Assert.Equal(-100000,post.AmountUnits);
        Assert.Equal(movement,(await db.PurchaseEvidence.SingleAsync(e=>e.Id==id)).TransactionId);
        Assert.Single(await db.PurchaseEvidenceSources.ToListAsync());
        Assert.Single(await db.Transactions.Where(t=>t.Status=="Confirmed"&&t.Kind==MovementKind.Expense).ToListAsync());
    }

    [Fact] public async Task NativeReaderReconcilesPdfAndJoinsWrappedDescriptions()
    {
        var review = await Review(); var preview = await Imports.PreviewAsync(review.Id);
        Assert.Empty(preview.Statement.Errors); Assert.Equal(2, preview.Statement.Rows.Count);
        Assert.Equal("Compra Loja de teste", preview.Statement.Rows[0].Description);
        Assert.Equal(-100_000, preview.Statement.Rows[0].Units); Assert.Equal(100_000, preview.Statement.DebitTotalUnits);
        Assert.Equal(10_400_000, preview.Statement.ClosingUnits);
        await using var db = Open(); Assert.Equal(2, await db.Transactions.CountAsync()); // Opening balances only.
    }
    [Fact] public async Task PartialConfirmationPreservesUnresolvedRowsAndFullConfirmationDoesNotDuplicate()
    {
        var review = await Review(); var ordinal = review.Rows[1].Ordinal;
        review.Rows[0].Decision = "Pending"; review.Rows[0].Detail = "Precisa de identificação";
        await Imports.ConfirmRowsAsync(review, [ordinal]);
        await using (var db = Open())
        {
            Assert.Equal("Draft", (await db.BankImports.SingleAsync()).Status);
            Assert.Single(await db.BankImportEntries.ToListAsync());
        }
        var partial = await Imports.PrepareReviewAsync(review.Id);
        var html=await RenderReview(review.Id);
        Assert.Contains("✓ Registado",html);Assert.Contains("Classificado: Trabalho",html);
        Assert.Contains("Já incluído nos registos financeiros",html);
        Assert.Equal(1,Assert.Single(await Imports.ListAsync()).PendingRows);
        Assert.Contains("1 registados",html);Assert.Contains("1 com categoria · por validar",html);
        Assert.DoesNotContain("1 por classificar",html);
        var dashboard=await Finance.DashboardAsync(2025);
        Assert.Equal(500_000,dashboard.IncomeUnits);Assert.Equal(1,dashboard.PendingBankRows);
        Assert.Equal("Pending", partial.Rows[0].Decision); Assert.Equal("Precisa de identificação", partial.Rows[0].Detail);
        Assert.Equal("Link", partial.Rows[1].Decision);
        await Imports.ConfirmRowsAsync(partial, [ordinal]); // Repeating a completed source line creates nothing.
        partial = await Imports.PrepareReviewAsync(review.Id);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(partial));
        partial.Rows[0].Decision = "Import";
        var completed = await Imports.ConfirmAsync(partial);
        Assert.Equal(2, completed.Created);
        Assert.Equal(0,(await Finance.DashboardAsync(2025)).PendingBankRows);
        await using var check = Open(); Assert.Equal(2, await check.BankImportEntries.CountAsync());
        Assert.Equal(2, await check.Transactions.CountAsync(t=>t.Origin=="BankImport"));
        await Imports.VoidAsync(partial.Id, (await Imports.PreviewAsync(partial.Id)).Import.Version);
        Assert.Empty(await check.Transactions.AsNoTracking().Where(t=>t.Origin=="BankImport"&&t.Status=="Confirmed").ToListAsync());
    }
    [Fact] public async Task PartialConfirmationStillChecksDuplicatesAndConflictingSourceDecisions()
    {
        var review=await Review();
        await Finance.SaveMovementAsync(new(){AccountId=account,CategoryId=expense,Kind=MovementKind.Expense,Date=(await Imports.PreviewAsync(review.Id)).Statement.Rows[0].Date,Description="Compra Loja de teste",Amount=10});
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmRowsAsync(review,[review.Rows[0].Ordinal]));
        await Imports.ConfirmRowsAsync(review,[review.Rows[1].Ordinal]);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmRowsAsync(review,[review.Rows[1].Ordinal])); // stale review
        review=await Imports.PrepareReviewAsync(review.Id);review.Rows[1].Decision="Exclude";
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmRowsAsync(review,[review.Rows[1].Ordinal]));
    }
    [Fact] public async Task DocumentListingKeepsMoreThanOneHundredDocumentsAccessible()
    {
        await using (var db = Open())
        {
            var original = await db.BankDocuments.SingleAsync();
            for (var n = 1; n <= 105; n++)
            {
                var id = "listing-" + n;
                db.Add(new BankDocument { Id = id, Name = n + ".pdf", Content = original.Content, StatementJson = original.StatementJson, ReaderVersion = original.ReaderVersion });
                db.Add(new BankImport { DocumentId = id, SectionIndex = 0, SourceReference = "111122223333" });
            }
            await db.SaveChangesAsync();
        }
        Assert.Equal(106, BankDocumentSummary.Group(await Imports.ListAsync()).Count);
    }
    [Fact] public async Task OcrKeepsOriginalPdfAndStillBlocksUnbalancedExtraction()
    {
        var content = SyntheticPdf("ocr-original"); var path = Path.Combine(folder, "ocr-input.pdf"); await File.WriteAllBytesAsync(path, content);
        var lines = BankPdfContent.Read(path).Lines; var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.StageOcrAsync("scan.pdf", content, "wrong-hash", lines));
        var id = Assert.Single(await Imports.StageOcrAsync("scan.pdf", content, sha, lines));
        Assert.Equal(content, (await Imports.OriginalAsync(id)).Content);
        Assert.Empty((await Imports.PreviewAsync(id)).Statement.Errors);
        content = SyntheticPdf("ocr-unbalanced"); sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        lines = lines.Select(l => l.Text.Contains("SALDO CONTABILISTICO") ? l with { Cells = [l.Cells[0], l.Cells[1], l.Cells[2], l.Cells[3], l.Cells[4], "1.040,01"] } : l).ToList();
        id = Assert.Single(await Imports.StageOcrAsync("scan-bad.pdf", content, sha, lines));
        Assert.NotEmpty((await Imports.PreviewAsync(id)).Statement.Errors);
        Assert.Equal(content, (await Imports.OriginalAsync(id)).Content);
        await Assert.ThrowsAsync<FinanceRuleException>(async () => await Imports.ConfirmAsync(await Imports.PrepareReviewAsync(id)));
    }
    [Fact] public async Task ExplicitAccountIdentifierMigrationReusesTheExistingAccount()
    {
        await using (var db = Open())
        {
            var document = await db.BankDocuments.SingleAsync();
            var statement = System.Text.Json.JsonSerializer.Deserialize<BankStatement>(document.StatementJson)!;
            statement.Accounts[0].ReferenceAliases = ["111122223333", "222233334444"];
            document.StatementJson = System.Text.Json.JsonSerializer.Serialize(statement);
            await db.SaveChangesAsync();
        }
        var id = Assert.Single(await Imports.StageAsync("migrated.pdf", SyntheticPdf("migrated-id", reference:"2222 3333 4444")));
        var preview = await Imports.PreviewAsync(id);
        Assert.Equal(account, preview.Import.AccountId); Assert.Equal("111122223333", preview.Import.SourceReference);
        var review = await Imports.PrepareReviewAsync(id);
        foreach (var row in review.Rows) row.CategoryId = row.Kind == MovementKind.Income ? income : expense;
        Assert.Equal(2,(await Imports.ConfirmAsync(review)).Created);
        await using var check = Open(); Assert.Equal(2,await check.Accounts.CountAsync());
    }
    [Fact] public async Task SamePeriodAndFilenameForDifferentPeopleKeepAccountsAndMovementsSeparate()
    {
        var firstId=Assert.Single(await Imports.StageAsync("same-name.pdf",SyntheticPdf("person-one")));
        var secondId=Assert.Single(await Imports.StageAsync("same-name.pdf",SyntheticPdf("person-two",reference:"7777 6666 3333")));
        var secondAccount=(await Imports.PreviewAsync(secondId)).Import.AccountId!.Value;
        Assert.NotEqual(account,secondAccount);
        await using(var db=Open())
        {
            var household=await db.Households.SingleAsync();
            var antonio=new Person {HouseholdId=household.Id,Name="António"};
            var susana=new Person {HouseholdId=household.Id,Name="Susana"};
            db.AddRange(antonio,susana);await db.SaveChangesAsync();
            (await db.Accounts.SingleAsync(a=>a.Id==account)).Name="novobanco · António · 3333";
            (await db.Accounts.SingleAsync(a=>a.Id==secondAccount)).Name="novobanco · Susana · 3333";
            (await db.BankImports.SingleAsync(b=>b.Id==firstId)).PersonId=antonio.Id;
            (await db.BankImports.SingleAsync(b=>b.Id==secondId)).PersonId=susana.Id;
            await db.SaveChangesAsync();
        }
        var first=await Imports.PrepareReviewAsync(firstId);
        foreach(var row in first.Rows)row.CategoryId=row.Kind==MovementKind.Income?income:expense;
        Assert.Equal(2,(await Imports.ConfirmAsync(first)).Created);
        var second=await Imports.PrepareReviewAsync(secondId);
        Assert.Equal(secondAccount,second.AccountId);
        Assert.Empty((await Imports.PreviewAsync(secondId)).Duplicates);
        Assert.All(second.Rows,row=>Assert.Equal("Import",row.Decision));
        foreach(var row in second.Rows)row.CategoryId=row.Kind==MovementKind.Income?income:expense;
        var documents=BankDocumentSummary.Group(await Imports.ListAsync());
        Assert.Equal(2,documents.Count(d=>d.Name=="same-name.pdf"));
        var one=Assert.Single(documents.Single(d=>d.Parts.Any(p=>p.Id==firstId)).Parts);
        var two=Assert.Single(documents.Single(d=>d.Parts.Any(p=>p.Id==secondId)).Parts);
        Assert.Equal(one.From,two.From);Assert.Equal(one.To,two.To);Assert.Equal(one.AccountReference,two.AccountReference);
        Assert.Equal("António",one.PersonName);Assert.Equal("Susana",two.PersonName);
        var html=await RenderReview(secondId);
        Assert.Contains("novobanco · António · 3333",html);Assert.Contains("novobanco · Susana · 3333",html);
        Assert.Contains("Pessoa: Susana",html);Assert.Contains("Conta: novobanco · Susana · 3333",html);
        Assert.Equal(2,(await Imports.ConfirmAsync(second)).Created);
    }
    [Fact] public async Task StatementListShowsSavedPersonWithoutChangingTheBankAccount()
    {
        var review=await Imports.PrepareReviewAsync(Assert.Single(await Imports.ListAsync()).Id);
        await using(var db=Open())
        {
            var person=new Person{HouseholdId=await db.Households.Select(h=>h.Id).SingleAsync(),Name="Pessoa da revisão"};
            db.Add(person);await db.SaveChangesAsync();review.PersonId=person.Id;
        }
        await Imports.SaveReviewAsync(review);
        var summary=Assert.Single(await Imports.ListAsync());
        Assert.Equal(account,summary.AccountId);Assert.Equal("Pessoa da revisão",summary.PersonName);
    }
    [Fact] public async Task ConfirmationIsAtomicRepeatableAndPreservesPdfAndValueDate()
    {
        var review = await Review(); var first = await Imports.ConfirmAsync(review); Assert.Equal(first, await Imports.ConfirmAsync(review));
        Assert.Equal(2, first.Created); Assert.Equal(0, first.Linked);
        await using var db = Open(); Assert.Equal(4, await db.Transactions.CountAsync());
        Assert.All(await db.Postings.Where(p => p.CategoryId != null).ToListAsync(), p => Assert.NotNull(p.ValueDate));
        Assert.Equal(SyntheticPdf(), (await db.BankDocuments.SingleAsync()).Content);
        var backup = Path.Combine(folder, "backup.sqlite"); DatabaseFiles.Backup(DatabasePath, backup);
        await using var restored = FinanceDb.Open(backup); Assert.Equal(SyntheticPdf(), (await restored.BankDocuments.SingleAsync()).Content); Assert.True(await DatabaseFiles.CheckIntegrityAsync(restored));
    }
    [Fact] public async Task ReuploadingSamePdfReturnsExistingReviewEvenAfterImport()
    {
        var review = await Review(); await Imports.ConfirmAsync(review);
        Assert.Equal(review.Id, Assert.Single(await Imports.StageAsync("renamed.pdf", SyntheticPdf())));
        await using var db = Open(); Assert.Single(await db.BankDocuments.ToListAsync()); Assert.Equal(2, await db.BankImportEntries.CountAsync());
    }
    [Fact] public async Task OverlappingStatementsRequireExplicitDuplicateDecision()
    {
        var first = await Review(); await Imports.ConfirmAsync(first);
        var second = await Review(SyntheticPdf("another-document"));
        Assert.Equal(2, (await Imports.PreviewAsync(second.Id, account)).Duplicates.Count);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(second));
        foreach (var row in second.Rows) row.AcknowledgeDuplicate = true;
        Assert.Equal(2, (await Imports.ConfirmAsync(second)).Created); // Equal legitimate movements can be kept explicitly.
    }
    [Fact] public async Task LinkingExistingMovementsDoesNotDuplicateBalancesAndProtectsTheOriginalBatch()
    {
        var first = await Review(); await Imports.ConfirmAsync(first);
        var second = await Review(SyntheticPdf("overlap")); var preview = await Imports.PreviewAsync(second.Id, account);
        foreach (var row in second.Rows) { var match = Assert.Single(preview.Duplicates[row.Ordinal]); row.Decision = "Link"; row.ExistingTransactionId = match.TransactionId; row.ExistingVersion = match.Version; }
        var result = await Imports.ConfirmAsync(second); Assert.Equal(0, result.Created); Assert.Equal(2, result.Linked);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.VoidAsync(first.Id, first.Version + 1));
        await Imports.VoidAsync(second.Id, second.Version + 1); // Unlinks this batch without voiding someone else's movements.
        Assert.Equal(10_400_000, (await Finance.AccountsAsync()).Single(a => a.Account.Id == account).BalanceUnits);
        await Imports.VoidAsync(first.Id, first.Version + 1);
        Assert.Equal(10_000_000, (await Finance.AccountsAsync()).Single(a => a.Account.Id == account).BalanceUnits);
    }
    [Fact] public async Task FailedSecondRowRollsBackTheFirstRowAndAllLinks()
    {
        var review = await Review(); review.Rows[1].CategoryId = expense;
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(review));
        await using var db = Open(); Assert.Equal(2, await db.Transactions.CountAsync()); Assert.Empty(await db.BankImportEntries.ToListAsync()); Assert.Equal("Draft", (await db.BankImports.SingleAsync()).Status);
    }
    [Fact] public async Task ImportedTransferHasTwoPostingsAndIsExcludedFromExpenses()
    {
        var review = await Review(); review.Rows[0].Kind = MovementKind.Transfer; review.Rows[0].CounterpartyAccountId = savings;
        await Imports.ConfirmAsync(review); var dashboard = await Finance.DashboardAsync(2025);
        Assert.Equal(0, dashboard.ExpenseUnits); Assert.Equal(500_000, dashboard.IncomeUnits);
        Assert.Equal(2_100_000, (await Finance.AccountsAsync()).Single(a => a.Account.Id == savings).BalanceUnits);
        await Imports.VoidAsync(review.Id, review.Version + 1);
        Assert.Equal(2_000_000, (await Finance.AccountsAsync()).Single(a => a.Account.Id == savings).BalanceUnits);
    }
    [Fact] public async Task EditsAfterImportBlockBatchVoidWithoutPartialChanges()
    {
        var review = await Review(); await Imports.ConfirmAsync(review);
        await using var db = Open(); var id = await db.BankImportEntries.Where(x => x.ImportId == review.Id && x.Ordinal == 2).Select(x => x.TransactionId).SingleAsync();
        var edit = await Finance.MovementAsync(id); edit.Description = "Alterado"; await Finance.SaveMovementAsync(edit);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.VoidAsync(review.Id, review.Version + 1));
        Assert.Equal(4, await db.Transactions.CountAsync(t => t.Status == "Confirmed"));
    }
    [Fact] public async Task OneBankReferenceCannotBeMappedToDifferentApplicationAccounts()
    {
        var first = await Review(); await Imports.ConfirmAsync(first); var second = await Review(SyntheticPdf("new-statement")); second.AccountId = savings;
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(second));
    }
    [Fact] public async Task InvalidTotalsAndMissingDecisionsCannotCreateMovements()
    {
        var review = await Review(SyntheticPdf("bad", "1.040,01"));
        Assert.NotEmpty((await Imports.PreviewAsync(review.Id)).Statement.Errors);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(review));
        await using var db = Open(); Assert.Equal(2, await db.Transactions.CountAsync());
    }
    [Fact] public async Task MissingRowsAndStaleExistingMovementAreRejected()
    {
        var first = await Review(); var incomplete = await Review(); incomplete.Rows.RemoveAt(0);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(incomplete));
        await Imports.ConfirmAsync(first); var second = await Review(SyntheticPdf("stale")); var preview = await Imports.PreviewAsync(second.Id, account);
        foreach (var row in second.Rows) { var existing = preview.Duplicates[row.Ordinal][0]; row.Decision = "Link"; row.ExistingTransactionId = existing.TransactionId; row.ExistingVersion = existing.Version; }
        var edited = await Finance.MovementAsync(second.Rows[1].ExistingTransactionId!.Value); edited.Notes = "changed"; await Finance.SaveMovementAsync(edited);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(second));
        await using var db = Open(); Assert.Empty(await db.BankImportEntries.Where(x => x.ImportId == second.Id).ToListAsync());
    }
    [Fact] public async Task TamperedStoredPdfCannotBeImported()
    {
        var review = await Review(); await using (var db = Open()) { var document = await db.BankDocuments.SingleAsync(); document.Content = "%PDF-tampered"u8.ToArray(); await db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(review));
    }
    [Fact] public async Task DraftReviewPersistsWithoutFinancialEffectsAndRejectsStaleSave()
    {
        var review = await Review(); var stale = await Review(); review.Rows[0].Decision = "Exclude";
        await Imports.SaveReviewAsync(review); Assert.Equal(3, review.Version);
        var preview = await Imports.PreviewAsync(review.Id); Assert.Contains("Exclude", preview.Import.ReviewJson);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.SaveReviewAsync(stale));
        await using var db = Open(); Assert.Equal(2, await db.Transactions.CountAsync());
        Assert.Equal(1, (await Imports.ConfirmAsync(review)).Created);
    }
    [Fact] public async Task ReuploadAfterVoidCreatesNewReviewAndPreservesPreviousBatch()
    {
        var first = await Review(); await Imports.ConfirmAsync(first); await Imports.VoidAsync(first.Id, first.Version + 1);
        var retry = await Review(); Assert.NotEqual(first.Id, retry.Id);
        Assert.Equal(account, (await Imports.PreviewAsync(retry.Id)).Import.AccountId);
        retry.AccountId = savings;
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(retry));
        retry.AccountId = account; await Imports.ConfirmAsync(retry);
        await using var db = Open(); Assert.Equal("Voided", (await db.BankImports.FindAsync(first.Id))!.Status);
        Assert.Equal(4, await db.BankImportEntries.CountAsync()); Assert.Equal(10_400_000, (await Finance.AccountsAsync()).Single(x => x.Account.Id == account).BalanceUnits);
    }
    [Fact] public async Task NonEuroTableIsBlockedEvenWhenAllNumbersReconcile()
    {
        var review = await Review(SyntheticPdf(currency: "USD"));
        Assert.Contains((await Imports.PreviewAsync(review.Id)).Statement.Errors, e => e.Contains("EUR"));
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(review));
    }
    [Fact] public async Task NewReferenceCreatesOneAccountWithAuditedOpeningAndNoStatementMovements()
    {
        var ids = await Imports.StageAsync("new.pdf", SyntheticPdf(reference: "9999 8888 7777", product: "CONTA POUPANCA"));
        var preview = await Imports.PreviewAsync(Assert.Single(ids));
        await using var db = Open();
        var created = await db.Accounts.SingleAsync(a => a.Id == preview.Import.AccountId);
        Assert.Equal("Savings", created.Type); Assert.Equal("EUR", created.Currency);
        Assert.Contains("7777", created.Name); Assert.Equal(new DateOnly(2025, 1, 1), created.BalanceStartDate);
        var opening = await db.Postings.SingleAsync(p => p.AccountId == created.Id);
        Assert.Equal(10_000_000, opening.AmountUnits);
        Assert.Equal(MovementKind.Opening, (await db.Transactions.FindAsync(opening.TransactionId))!.Kind);
        Assert.Equal("Draft", preview.Import.Status); Assert.Null(preview.Import.PersonId);
        Assert.Empty(await db.BankImportEntries.ToListAsync());
        Assert.Single(await db.AuditChanges.Where(a => a.EntityId == created.Id && a.Action == "AutoCreateFromStatement").ToListAsync());
    }
    [Fact] public async Task ParallelUploadsOfSameReferenceReuseAccountAndPreserveOpening()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Imports.StageAsync($"new-{i}.pdf", SyntheticPdf($"parallel-{i}", reference: "5555 4444 3333"))));
        var ids = new List<long?>();
        foreach (var result in results) ids.Add((await Imports.PreviewAsync(result[0])).Import.AccountId);
        Assert.Single(ids.Distinct()); Assert.NotNull(ids[0]);
        var older = Assert.Single(await Imports.StageAsync("older.pdf", SyntheticPdf(reference: "5555 4444 3333", previousYear: true)));
        Assert.Equal(ids[0], (await Imports.PreviewAsync(older)).Import.AccountId);
        await using var db = Open(); Assert.Equal(3, await db.Accounts.CountAsync());
        Assert.Single(await db.Postings.Where(p => p.AccountId == ids[0]).ToListAsync());
        Assert.Equal(new DateOnly(2025, 1, 1), (await db.Accounts.FindAsync(ids[0]))!.BalanceStartDate);
    }
    [Fact] public async Task LastFourDigitsDoNotIdentifyTheAccount()
    {
        var first = Assert.Single(await Imports.StageAsync("one.pdf", SyntheticPdf(reference: "5555 4444 3333")));
        var second = Assert.Single(await Imports.StageAsync("two.pdf", SyntheticPdf(reference: "7777 6666 3333")));
        var one = (await Imports.PreviewAsync(first)).Import.AccountId; var two = (await Imports.PreviewAsync(second)).Import.AccountId;
        Assert.NotEqual(one, two); Assert.NotEqual(account, one); Assert.NotEqual(account, two);
        await using var db = Open(); Assert.Equal(4, await db.Accounts.CountAsync());
        Assert.Equal(4, (await db.Accounts.Select(a => a.Name).ToListAsync()).Distinct().Count());
    }
    [Fact] public async Task InvalidUnknownReferenceCreatesNeitherAccountNorOpening()
    {
        var pdf = SyntheticPdf(closing: "1.040,01", reference: "5555 4444 3333");
        var id = Assert.Single(await Imports.StageAsync("bad.pdf", pdf));
        Assert.Null((await Imports.PreviewAsync(id)).Import.AccountId);
        Assert.Equal(id, Assert.Single(await Imports.StageAsync("bad-again.pdf", pdf)));
        await using var db = Open(); Assert.Equal(2, await db.Accounts.CountAsync()); Assert.Equal(2, await db.Transactions.CountAsync());
    }
    [Fact] public async Task PreviouslyStagedUnassignedDocumentGetsOneAccountOnReupload()
    {
        var pdf = SyntheticPdf(reference: "4444 5555 6666");
        var id = Assert.Single(await Imports.StageAsync("existing.pdf", pdf));
        // Represent a draft saved by the earlier application, before automatic accounts.
        await using (var db = Open())
        {
            var batch = await db.BankImports.SingleAsync(b => b.Id == id);
            var old = batch.AccountId!.Value; batch.AccountId = null;
            var posts = await db.Postings.Where(p => p.AccountId == old).ToListAsync();
            var movements = await db.Transactions.Where(t => posts.Select(p => p.TransactionId).Contains(t.Id)).ToListAsync();
            db.RemoveRange(posts); db.RemoveRange(movements);
            db.Remove((await db.Accounts.FindAsync(old))!); await db.SaveChangesAsync();
        }
        Assert.Equal(id, Assert.Single(await Imports.StageAsync("again.pdf", pdf)));
        var assigned = (await Imports.PreviewAsync(id)).Import.AccountId; Assert.NotNull(assigned);
        await Imports.StageAsync("again-twice.pdf", pdf);
        Assert.Equal(assigned, (await Imports.PreviewAsync(id)).Import.AccountId);
        await using var check = Open(); Assert.Equal(3, await check.Accounts.CountAsync());
        Assert.Single(await check.Postings.Where(p => p.AccountId == assigned).ToListAsync());
    }
    [Fact] public async Task ReuploadPreservesSavedReviewAndKnownPerson()
    {
        await using (var db = Open())
        {
            var person = new Person { HouseholdId = (await db.Households.SingleAsync()).Id, Name = "Pessoa de teste" };
            db.Add(person); await db.SaveChangesAsync();
            (await db.BankImports.SingleAsync()).PersonId = person.Id; await db.SaveChangesAsync();
        }
        var review = await Review(); review.Rows[0].Decision = "Exclude"; await Imports.SaveReviewAsync(review);
        var before = (await Imports.PreviewAsync(review.Id)).Import;
        await Imports.StageAsync("again.pdf", SyntheticPdf());
        var after = (await Imports.PreviewAsync(review.Id)).Import;
        Assert.Equal(before.ReviewJson, after.ReviewJson); Assert.Equal(before.Version, after.Version);
        var next = Assert.Single(await Imports.StageAsync("next.pdf", SyntheticPdf("next")));
        var batch = (await Imports.PreviewAsync(next)).Import;
        Assert.Equal(account, batch.AccountId); Assert.Equal(before.PersonId, batch.PersonId);
        await using var check = Open(); Assert.Equal(2, await check.Accounts.CountAsync()); Assert.Equal(2, await check.Transactions.CountAsync());
    }
    [Theory]
    [InlineData("CUF", "Saúde")]
    [InlineData("LUZ SAUDE", "Saúde")]
    [InlineData("A Tasca Do Kim Frielas", "Refeições fora e entregas")]
    [InlineData("Compra Cartão **** 868* A Cafeteira Cartaxo", "Refeições fora e entregas")]
    [InlineData("Compra Cartao Cafe Central", "Refeições fora e entregas")]
    [InlineData("Compra Cartao Cafeteria Peaberry", "Refeições fora e entregas")]
    [InlineData("Compra Cartao Cafet Jardim Santos Lisboa", "Refeições fora e entregas")]
    [InlineData("Compra Mbway Cartao Cafecop Lda Cartaxo", "Refeições fora e entregas")]
    [InlineData("Compra Cartao Rest Bem Estar Loures", "Refeições fora e entregas")]
    [InlineData("Compra Cartao Ponto Da Fruta Infantado", "Supermercado e mercearia")]
    [InlineData("Compra Cartao Clin Medi Dent Pra Lisboa", "Saúde")]
    [InlineData("Compra Cartao Sao Roque Vetclinic Loures", "Veterinário e medicamentos")]
    [InlineData("Compra Cartao Deichmann Loures", "Calçado")]
    [InlineData("Compra Cartao Cortefiel Ubbo", "Roupa")]
    [InlineData("Telpark 1,50 Eur Cartao", "Estacionamento")]
    [InlineData("Render - Para: Render.com, San Francisco, CA", "Software e serviços digitais")]
    [InlineData("Render.Com 17,50 Usd Cartao 431 Cambio 0,8628571", "Software e serviços digitais")]
    [InlineData("Pag. Prestacao Crd Pessoal N 1571003209 20260702", "Outras prestações e dívidas")]
    public async Task RecognizesHealthcareRestaurantsAndPersonalLoanInstallments(string description, string categoryName)
    {
        await using var db = Open(); var household = (await db.Households.SingleAsync()).Id;
        var category = new Category { HouseholdId = household, Name = categoryName, Kind = MovementKind.Expense };
        db.Add(category); await db.SaveChangesAsync();
        // Health insurance has the same name but must not capture hospital payments.
        if (categoryName == "Saúde") { db.Add(new Category { HouseholdId = household, Name = "Saúde", Kind = MovementKind.Expense, ParentId = expense }); await db.SaveChangesAsync(); }
        var id = Assert.Single(await Imports.StageAsync("recognized.pdf", SyntheticPdf(firstDescription: description, continuation: "")));
        var review = await Imports.PrepareReviewAsync(id);
        Assert.Equal(category.Id, review.Rows[0].CategoryId); Assert.Null(review.Rows[0].Detail);
        Assert.Equal(2, await db.Transactions.CountAsync());
    }

    [Theory]
    [InlineData("Pag. Prestacao Crd Pessoal N 1571003209 20261002","Crédito pessoal")]
    [InlineData("Pagamento Prestacao Ch N 221 Processo 0910000070","Crédito à habitação")]
    public async Task LoanInstallmentsPreferTheirSpecificCategory(string description,string name)
    {
        await using var db=Open();var household=(await db.Households.SingleAsync()).Id;
        var fallback=new Category{HouseholdId=household,Name="Outras prestações e dívidas",Kind=MovementKind.Expense};
        var personal=new Category{HouseholdId=household,Name="Crédito pessoal",Kind=MovementKind.Expense};
        var housing=new Category{HouseholdId=household,Name="Crédito à habitação",Kind=MovementKind.Expense};
        db.AddRange(fallback,personal,housing);await db.SaveChangesAsync();
        var id=Assert.Single(await Imports.StageAsync("loan.pdf",SyntheticPdf(firstDescription:description,continuation:"")));
        var preview=await Imports.PreviewAsync(id);var categoryId=preview.Suggestions[preview.Statement.Rows[0].Ordinal].CategoryId;
        Assert.Equal(name,(await db.Categories.SingleAsync(c=>c.Id==categoryId)).Name);
    }
    [Fact] public async Task SavedCodesKeepTheirMeaningAndDoNotConflateOtherReferences()
    {
        var reference = await Imports.SaveReferenceAsync(new() { AccountId = account, CategoryId = expense, Merchant = "PayPal", Code = "00123456", Description = "Prestação do computador" });
        await AddClassifiedHistory("PAYPAL 99887766", income, -100_000);
        var id = Assert.Single(await Imports.StageAsync("code.pdf", SyntheticPdf(firstDescription: "PAYPAL 00123456", continuation: "")));
        var review = await Imports.PrepareReviewAsync(id);
        Assert.Equal(expense, review.Rows[0].CategoryId); Assert.Equal("Prestação do computador", review.Rows[0].Detail);
        var other = Assert.Single(await Imports.StageAsync("other-code.pdf", SyntheticPdf(firstDescription: "PAYPAL 100123456", continuation: "")));
        Assert.Null((await Imports.PrepareReviewAsync(other)).Rows[0].CategoryId);
        await Imports.ConfirmAsync(review);
        await using var db = Open();
        Assert.Equal("Prestação do computador", (await db.Transactions.SingleAsync(t => t.Description == "PAYPAL 00123456")).Notes);
        Assert.Single(await Imports.ReferencesAsync(account)); Assert.Empty(await Imports.ReferencesAsync(savings));
        var saved = (await Imports.ReferencesAsync(account)).Single();
        await Imports.ArchiveReferenceAsync(reference, saved.Version);
        Assert.Empty(await Imports.ReferencesAsync(account));
    }

    [Fact] public async Task LoanHistoryPreservesContractNumbersButIgnoresTrailingBankDates()
    {
        await AddClassifiedHistory("Pag Prestacao Crd Pessoal N 1571003209 20241202", expense, -100_000);
        var same = Assert.Single(await Imports.StageAsync("same-contract.pdf", SyntheticPdf(firstDescription: "Pag Prestacao Crd Pessoal N 1571003209 20250102", continuation: "")));
        Assert.Equal(expense, (await Imports.PrepareReviewAsync(same)).Rows[0].CategoryId);
        var other = Assert.Single(await Imports.StageAsync("other-contract.pdf", SyntheticPdf(firstDescription: "Pag Prestacao Crd Pessoal N 9991003209 20250102", continuation: "")));
        Assert.Null((await Imports.PrepareReviewAsync(other)).Rows[0].CategoryId);
    }

    [Fact] public async Task GenericPaymentPlatformHistoryNeverDecidesWhatWasBought()
    {
        await AddClassifiedHistory("AMAZON", expense, -100_000);
        var id = Assert.Single(await Imports.StageAsync("amazon.pdf", SyntheticPdf(firstDescription: "AMAZON", continuation: "")));
        Assert.Null((await Imports.PrepareReviewAsync(id)).Rows[0].CategoryId);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.SaveReferenceAsync(new() { AccountId = account, CategoryId = expense, Merchant = "Amazon", Description = "Tudo é casa" }));
    }

    [Fact] public async Task PurchaseEvidenceUsesMerchantDateAndExactAmountAndConflictsRemainUnresolved()
    {
        await Imports.SaveReferenceAsync(new() { IsPurchase = true, AccountId = account, CategoryId = expense, Merchant = "Amazon", Description = "Livro", PurchaseDate = new(2025, 1, 1), Amount = 10 });
        var id = Assert.Single(await Imports.StageAsync("purchase.pdf", SyntheticPdf(firstDescription: "AMZNMktpES", continuation: "")));
        Assert.Equal("Livro", (await Imports.PrepareReviewAsync(id)).Rows[0].Detail);
        var unrelated = Assert.Single(await Imports.StageAsync("unrelated.pdf", SyntheticPdf(firstDescription: "Outra loja", continuation: "")));
        Assert.Null((await Imports.PrepareReviewAsync(unrelated)).Rows[0].CategoryId);
        var past = Assert.Single(await Imports.StageAsync("past.pdf", SyntheticPdf(firstDescription: "AMAZON", continuation: "", previousYear: true)));
        Assert.Null((await Imports.PrepareReviewAsync(past)).Rows[0].CategoryId);
        var repeated = Assert.Single(await Imports.StageAsync("repeated-purchase.pdf", SyntheticPdf(firstDescription: "AMAZON", continuation: "", secondDescription: "AMAZON", repeatedPurchase: true)));
        Assert.All((await Imports.PrepareReviewAsync(repeated)).Rows, r => Assert.Null(r.CategoryId));
        await Imports.SaveReferenceAsync(new() { AccountId = account, CategoryId = expense, Merchant = "AMAZON", Description = "Outra compra", PurchaseDate = new(2025, 1, 1), Amount = 10 });
        Assert.Null((await Imports.PrepareReviewAsync(id)).Rows[0].CategoryId);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.SaveReferenceAsync(new() { IsPurchase = true, AccountId = account, CategoryId = expense, Merchant = "Outra loja", Description = "Imagem incompleta" }));
    }

    [Theory]
    [InlineData(11, 1, false)]
    [InlineData(10, 3, false)]
    [InlineData(10, 1, true)]
    public async Task PurchaseDoesNotMatchDifferentAmountsFutureDatesOrOtherAccounts(int amount, int day, bool otherAccount)
    {
        await Imports.SaveReferenceAsync(new() { IsPurchase = true, AccountId = otherAccount ? savings : account, CategoryId = expense, Merchant = "Amazon", Description = "Compra identificada", PurchaseDate = new(2025, 1, day), Amount = amount });
        var id = Assert.Single(await Imports.StageAsync("nonmatch.pdf", SyntheticPdf(firstDescription: "AMAZON", continuation: "")));
        Assert.Null((await Imports.PrepareReviewAsync(id)).Rows[0].CategoryId);
    }

    [Fact] public async Task ReferenceUpdatesAreVersionedAndBackupPreservesExplanations()
    {
        var id = await Imports.SaveReferenceAsync(new() { AccountId = account, CategoryId = expense, Code = "1571003209", Description = "Finalidade indicada pelo proprietário" });
        var form = new BankReferenceForm { Id = id, Version = 1, AccountId = account, CategoryId = expense, Code = "1571003209", Description = "Descrição corrigida" };
        await Imports.SaveReferenceAsync(form);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.SaveReferenceAsync(form));
        var backup = Path.Combine(folder, "references-backup.sqlite"); DatabaseFiles.Backup(DatabasePath, backup);
        await using var db = FinanceDb.Open(backup); Assert.Equal("Descrição corrigida", (await db.BankReferences.SingleAsync()).Description);
        Assert.True(await DatabaseFiles.CheckIntegrityAsync(db));
        var html = await RenderReview(Assert.Single(await Imports.ListAsync()).Id);
        Assert.Contains("Referências e compras", html); Assert.Contains("Descrição corrigida", html);
    }

    [Fact] public async Task FreshReviewSuggestsRecognizedCategoriesWithoutCreatingMovements()
    {
        await using var db = Open();
        var supermarket = new Category { HouseholdId = (await db.Households.SingleAsync()).Id, Name = "Supermercados", Kind = MovementKind.Expense };
        db.Add(supermarket); await db.SaveChangesAsync();
        var id = Assert.Single(await Imports.StageAsync("categories.pdf", SyntheticPdf(firstDescription: "Compra CONTINENTE")));
        var review = await Imports.PrepareReviewAsync(id);
        Assert.Equal(supermarket.Id, review.Rows[0].CategoryId);
        Assert.Equal(income, review.Rows[1].CategoryId); // Salary maps to the existing Trabalho category.
        Assert.All(review.Rows, r => Assert.Equal("Import", r.Decision));
        Assert.Equal(2, (await Imports.PreviewAsync(id)).Suggestions.Count);
        Assert.Equal(2, await db.Transactions.CountAsync());
        Assert.Empty(await db.BankImportEntries.ToListAsync());
        Assert.Equal(2, (await Imports.ConfirmAsync(review)).Created); // Global approval imports the suggestions.
    }
    [Fact] public async Task PossibleContinenteFundingIsNotAutomaticallyBookedFromAnOldSupermarketClassification()
    {
        var review=await Review(SyntheticPdf("wallet-load",firstDescription:"Compra CONTINENTE PORTUGAL",continuation:""));
        await Imports.SaveReviewAsync(review);
        var preview=await Imports.PreviewAsync(review.Id);
        Assert.Contains("Possível carregamento",preview.ReviewWarnings[1]);Assert.False(preview.Suggestions.ContainsKey(1));
        var html=await RenderReview(review.Id,interact:async page=>{await page.Call("SelectReady");Assert.True(page.Review.Rows[0].CategoryId!=null);});
        Assert.Contains("Possível carregamento",html);Assert.Contains("Selecionar prontos (1)",html);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmRowsAsync(review,[1]));
        await using(var db=Open()){Assert.Empty(await db.BankImportEntries.ToListAsync());Assert.Equal(2,await db.Transactions.CountAsync());}
        review.Rows[0].ClassificationEdited=true; // The owner verified the purpose and chose this classification.
        await Imports.ConfirmRowsAsync(review,[1]);
        Assert.Equal(expense,(await Imports.PrepareReviewAsync(review.Id)).Rows[0].CategoryId);
    }
    [Fact] public async Task MerchantHistoryDoesNotResolvePossibleContinenteWalletFunding()
    {
        await AddClassifiedHistory("Compra CONTINENTE PORTUGAL",expense,-100000);
        var id=Assert.Single(await Imports.StageAsync("wallet-history.pdf",SyntheticPdf("wallet-history",firstDescription:"Compra CONTINENTE PORTUGAL",continuation:"")));
        var review=await Imports.PrepareReviewAsync(id);Assert.Null(review.Rows[0].CategoryId);
        Assert.False((await Imports.PreviewAsync(id)).Suggestions.ContainsKey(1));
    }
    [Fact] public async Task ExactDocumentedContinentePurchaseCanStillBeReconciledWithoutAnotherExpense()
    {
        var e=new PurchaseEvidence{SourceKey="receipt:continente-direct",Source="Fatura documentada",Channel="Continente",Merchant="Continente",OrderReference="purchase-direct-1",Date=new(2025,1,1),Units=100000,FinancialUnits=100000,PaymentMethod="Bank",CategoryId=expense,Description="Compra direta documentada"};
        var evidenceId=Assert.Single(await Imports.StageEvidenceAsync([e]));var evidence=(await Imports.EvidenceAsync()).Single(x=>x.Id==evidenceId);
        var tx=await Imports.ConfirmEvidenceMovementAsync(evidenceId,evidence.Version,account,expense,true);
        var id=Assert.Single(await Imports.StageAsync("wallet-direct.pdf",SyntheticPdf("wallet-direct",firstDescription:"Compra CONTINENTE PORTUGAL",continuation:"")));
        var review=await Imports.PrepareReviewAsync(id);Assert.Equal("Link",review.Rows[0].Decision);Assert.Equal(tx,review.Rows[0].ExistingTransactionId);
        await Imports.ConfirmRowsAsync(review,[1]);await using var db=Open();Assert.Equal(3,await db.Transactions.CountAsync());Assert.Single(await db.BankImportEntries.ToListAsync());
    }
    [Fact] public async Task HistoryRecognizesMerchantWithDifferentReferencesAndAccents()
    {
        await AddClassifiedHistory("Pagamento Farmácia 998877 Lisboa", expense, -100_000);
        await AddClassifiedHistory("Compra FARMACIA 112233 LISBOA", expense, -200_000);
        var id = Assert.Single(await Imports.StageAsync("history.pdf", SyntheticPdf(firstDescription: "Compra FARMACIA 445566", continuation: "Lisboa")));
        var review = await Imports.PrepareReviewAsync(id);
        Assert.Equal(expense, review.Rows[0].CategoryId);
        var suggestion = (await Imports.PreviewAsync(id)).Suggestions[1];
        Assert.Equal("Alta", suggestion.Confidence); Assert.StartsWith("Histórico", suggestion.Reason);
    }
    [Fact] public async Task ConflictingHistoryDoesNotGuessEvenWhenMerchantHasARule()
    {
        await using var db = Open();
        var other = new Category { HouseholdId = (await db.Households.SingleAsync()).Id, Name = "Supermercados", Kind = MovementKind.Expense };
        db.Add(other); await db.SaveChangesAsync();
        await AddClassifiedHistory("Compra CONTINENTE Loja de teste", expense, -100_000);
        await AddClassifiedHistory("Compra CONTINENTE Loja de teste", other.Id, -100_000);
        var id = Assert.Single(await Imports.StageAsync("conflict.pdf", SyntheticPdf(firstDescription: "Compra CONTINENTE")));
        Assert.Null((await Imports.PrepareReviewAsync(id)).Rows[0].CategoryId);
        Assert.False((await Imports.PreviewAsync(id)).Suggestions.ContainsKey(1));
    }
    [Fact] public async Task SuggestionsIgnoreInactiveParentsVoidedHistoryAndOtherHouseholds()
    {
        await using var db = Open(); var household = (await db.Households.SingleAsync()).Id;
        var parent = new Category { HouseholdId = household, Name = "Saúde", Kind = MovementKind.Expense, Active = false };
        var foreign = new Household { Name = "Outro agregado" }; db.AddRange(parent, foreign); await db.SaveChangesAsync();
        var child = new Category { HouseholdId = household, ParentId = parent.Id, Name = "Farmácia", Kind = MovementKind.Expense };
        var foreignCategory = new Category { HouseholdId = foreign.Id, Name = "Farmácia", Kind = MovementKind.Expense };
        db.AddRange(child, foreignCategory); await db.SaveChangesAsync();
        await AddClassifiedHistory("Compra FARMACIA Loja de teste", child.Id, -100_000);
        await AddClassifiedHistory("Compra FARMACIA Loja de teste", expense, -100_000, status: "Voided");
        await AddClassifiedHistory("Compra FARMACIA Loja de teste", foreignCategory.Id, -100_000, householdId: foreign.Id);
        var id = Assert.Single(await Imports.StageAsync("inactive.pdf", SyntheticPdf(firstDescription: "Compra FARMACIA")));
        Assert.Null((await Imports.PrepareReviewAsync(id)).Rows[0].CategoryId);
    }
    [Fact] public async Task SavedReviewKeepsCorrectionsExclusionsAndDeliberatelyEmptyChoices()
    {
        var id = Assert.Single(await Imports.StageAsync("saved.pdf", SyntheticPdf("saved")));
        var review = await Imports.PrepareReviewAsync(id);
        Assert.Equal(income, review.Rows[1].CategoryId);
        review.Rows[0].CategoryId = expense; review.Rows[0].Decision = "Exclude";
        review.Rows[1].CategoryId = null;
        await Imports.SaveReviewAsync(review);
        var reopened = await Imports.PrepareReviewAsync(id);
        Assert.Equal(expense, reopened.Rows[0].CategoryId); Assert.Equal("Exclude", reopened.Rows[0].Decision);
        Assert.Null(reopened.Rows[1].CategoryId); Assert.Equal(review.Version, reopened.Version);
    }
    [Fact] public async Task ClearDuplicatesAreSuggestedAsLinksAndOnlyAppliedOnApproval()
    {
        await Imports.ConfirmAsync(await Review());
        var id = Assert.Single(await Imports.StageAsync("duplicates.pdf", SyntheticPdf("duplicates")));
        var review = await Imports.PrepareReviewAsync(id);
        Assert.All(review.Rows, row => { Assert.NotNull(row.CategoryId); Assert.Equal("Link", row.Decision); Assert.NotNull(row.ExistingTransactionId); Assert.False(row.AcknowledgeDuplicate); });
        await using var db = Open(); Assert.Empty(await db.BankImportEntries.Where(e => e.ImportId == id).ToListAsync());
        var result = await Imports.ConfirmAsync(review); Assert.Equal(0, result.Created); Assert.Equal(2, result.Linked);
    }
    [Fact] public async Task ManuallyEnteredMovementIsLinkedWithoutChangingItsDataOrBalance()
    {
        var existingId = await ExistingMovement(new(2025, 1, 2), "Compra Loja de teste", -100_000);
        var before = (await Finance.AccountsAsync()).Single(a => a.Account.Id == account).BalanceUnits;
        var id = Assert.Single(await Imports.StageAsync("manual.pdf", SyntheticPdf("manual")));
        var review = await Imports.PrepareReviewAsync(id);
        Assert.Equal("Link", review.Rows[0].Decision); Assert.Equal(existingId, review.Rows[0].ExistingTransactionId);
        review.Rows[1].Decision = "Exclude";
        var result = await Imports.ConfirmAsync(review);
        Assert.Equal(0, result.Created); Assert.Equal(1, result.Linked);
        Assert.Equal(before, (await Finance.AccountsAsync()).Single(a => a.Account.Id == account).BalanceUnits);
        await using var db = Open(); var movement = await db.Transactions.SingleAsync(t => t.Id == existingId);
        Assert.Equal("Manual", movement.Origin); Assert.Equal(1, movement.Version); Assert.Equal("Compra Loja de teste", movement.Description);
        Assert.Equal(expense, (await db.Postings.SingleAsync(p => p.TransactionId == existingId)).CategoryId);
        await Imports.VoidAsync(id, review.Version + 1);
        Assert.Equal("Confirmed", (await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == existingId)).Status);
    }
    [Fact] public async Task NearbyDatesIncludingOutsidePdfPeriodNeedExplicitReview()
    {
        var existingId = await ExistingMovement(new(2024, 12, 31), "Pagamento Loja de teste", -100_000);
        var id = Assert.Single(await Imports.StageAsync("nearby.pdf", SyntheticPdf("nearby")));
        var preview = await Imports.PreviewAsync(id); var match = Assert.Single(preview.Duplicates[1]);
        Assert.Equal(existingId, match.TransactionId); Assert.False(match.StrongMatch); Assert.Contains("3 dias", match.Reason);
        var review = await Imports.PrepareReviewAsync(id); Assert.Equal("Pending", review.Rows[0].Decision);
        review.Rows[0].Decision = "Import"; review.Rows[0].CategoryId = expense; review.Rows[1].Decision = "Exclude";
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(review));
        review.Rows[0].Decision = "Link"; review.Rows[0].ExistingTransactionId = match.TransactionId; review.Rows[0].ExistingVersion = match.Version;
        Assert.Equal(1, (await Imports.ConfirmAsync(review)).Linked);
    }
    [Fact] public async Task ValueDateMatchIsFlaggedEvenWhenDescriptionsDiffer()
    {
        await ExistingMovement(new(2025, 1, 1), "Descrição manual", -100_000, valueDate: new(2025, 1, 2));
        var id = Assert.Single(await Imports.StageAsync("value-date.pdf", SyntheticPdf("value-date")));
        var match = Assert.Single((await Imports.PreviewAsync(id)).Duplicates[1]);
        Assert.Contains("data-valor", match.Reason); Assert.False(match.StrongMatch);
        Assert.Equal("Pending", (await Imports.PrepareReviewAsync(id)).Rows[0].Decision);
    }
    [Fact] public async Task LegacyMovementWithoutAccountRequiresConfirmationThenIdentifiesAccount()
    {
        var existingId = await ExistingMovement(new(2025, 1, 2), "Despesa antiga", -100_000, unassigned: true);
        var id = Assert.Single(await Imports.StageAsync("legacy.pdf", SyntheticPdf("legacy")));
        var match = Assert.Single((await Imports.PreviewAsync(id)).Duplicates[1]);
        Assert.Null(match.AccountId); Assert.Contains("sem conta", match.Reason); Assert.False(match.StrongMatch);
        var review = await Imports.PrepareReviewAsync(id); Assert.Equal("Pending", review.Rows[0].Decision);
        review.Rows[0].Decision = "Link"; review.Rows[0].ExistingTransactionId = existingId; review.Rows[0].ExistingVersion = match.Version;
        review.Rows[1].Decision = "Exclude"; Assert.Equal(1, (await Imports.ConfirmAsync(review)).Linked);
        await using var db = Open(); var posting=await db.Postings.SingleAsync(p => p.TransactionId == existingId);
        Assert.Equal(account,posting.AccountId);Assert.Equal(-100_000,posting.AmountUnits);Assert.Equal(expense,posting.CategoryId);
        Assert.Equal(new DateOnly(2025,1,2),(await db.Transactions.SingleAsync(t=>t.Id==existingId)).Date);
        Assert.Contains(await db.AuditChanges.ToListAsync(),a=>a.Action=="IdentifyLegacyAccount"&&a.EntityId==existingId);
        Assert.Equal(1,(await Imports.ConfirmAsync(review)).Linked);
        Assert.Single(await db.Transactions.Where(t=>t.Kind==MovementKind.Expense).ToListAsync());
    }
    [Theory]
    [InlineData("OtherAccount")]
    [InlineData("OtherHousehold")]
    [InlineData("Voided")]
    [InlineData("OppositeSign")]
    [InlineData("DifferentAmount")]
    [InlineData("FarDate")]
    [InlineData("OtherEntity")]
    public async Task UnrelatedMovementsAreNotDuplicateCandidates(string variation)
    {
        long? foreignHousehold = null;
        if (variation == "OtherHousehold")
        {
            await using var db = Open(); var household = new Household { Name = "Outro agregado" }; db.Add(household); await db.SaveChangesAsync(); foreignHousehold = household.Id;
        }
        await ExistingMovement(variation == "FarDate" ? new(2024, 12, 29) : variation == "OtherEntity" ? new(2025, 1, 1) : new(2025, 1, 2),
            variation == "OtherEntity" ? "Entidade diferente" : "Compra Loja de teste",
            variation == "OppositeSign" ? 100_000 : variation == "DifferentAmount" ? -200_000 : -100_000,
            postingAccountId: variation == "OtherAccount" ? savings : null,
            unassigned: variation == "OtherHousehold", householdId: foreignHousehold, status: variation == "Voided" ? "Voided" : "Confirmed");
        var id = Assert.Single(await Imports.StageAsync("unrelated.pdf", SyntheticPdf("unrelated")));
        Assert.False((await Imports.PreviewAsync(id)).Duplicates.ContainsKey(1));
    }
    [Fact] public async Task SeveralMatchingMovementsRemainPendingInsteadOfPickingOne()
    {
        await ExistingMovement(new(2025, 1, 2), "Compra Loja de teste", -100_000);
        await ExistingMovement(new(2025, 1, 2), "Compra Loja de teste", -100_000);
        var id = Assert.Single(await Imports.StageAsync("ambiguous.pdf", SyntheticPdf("ambiguous")));
        Assert.Equal(2, (await Imports.PreviewAsync(id)).Duplicates[1].Count);
        var row = (await Imports.PrepareReviewAsync(id)).Rows[0];
        Assert.Equal("Pending", row.Decision); Assert.Null(row.ExistingTransactionId);
    }
    [Fact] public async Task NewManualEntryAfterReviewIsDetectedAgainAtConfirmation()
    {
        var review = await Review();
        await ExistingMovement(new(2025, 1, 2), "Descrição diferente", -100_000);
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(review));
        await using var db = Open(); Assert.Empty(await db.BankImportEntries.ToListAsync());
    }
    [Fact] public async Task RepeatedPdfRowsCannotBothUseOneExistingMovement()
    {
        var existingId = await ExistingMovement(new(2025, 1, 2), "Compra Loja de teste", -100_000);
        var id = Assert.Single(await Imports.StageAsync("repeated.pdf", SyntheticPdf("repeated", secondDescription: "Pagamento Loja de teste", repeatedPurchase: true)));
        var preview = await Imports.PreviewAsync(id); Assert.Empty(preview.Statement.Errors);
        var review = await Imports.PrepareReviewAsync(id); Assert.All(review.Rows, row => Assert.Equal("Pending", row.Decision));
        foreach (var row in review.Rows) { row.Decision = "Link"; row.ExistingTransactionId = existingId; row.ExistingVersion = 1; }
        await Assert.ThrowsAsync<FinanceRuleException>(() => Imports.ConfirmAsync(review));
        await using var db = Open(); Assert.Empty(await db.BankImportEntries.ToListAsync());
    }
    [Fact] public async Task DuplicateReviewShowsTheOriginalAndTheSuggestedAssociation()
    {
        var transaction=await ExistingMovement(new(2025, 1, 2), "Compra Loja de teste", -100_000);
        await using(var db=Open())
        {
            var root=await db.Categories.SingleAsync(c=>c.Id==expense);
            var sub=new Category{HouseholdId=root.HouseholdId,ParentId=root.Id,Name="Eletricidade",Kind=MovementKind.Expense};
            db.Add(sub);await db.SaveChangesAsync();
            (await db.Postings.SingleAsync(p=>p.TransactionId==transaction)).CategoryId=sub.Id;await db.SaveChangesAsync();
        }
        var id = Assert.Single(await Imports.StageAsync("duplicate-ui.pdf", SyntheticPdf("duplicate-ui")));
        var html = await RenderReview(id);
        Assert.Contains("Introduzido manualmente", html); Assert.Contains("Mesma data e montante", html);
        Assert.Contains("Associação selecionada para aprovação", html);
        Assert.Contains("esta linha fica associada ao movimento existente", html);
        Assert.Contains("não é criada outra despesa", html);
        Assert.Contains("Casa / Eletricidade", html);
        Assert.Contains("Categoria / subcategoria do movimento existente", html);
        Assert.Contains("A associação mantém esta classificação", html);
        Assert.DoesNotMatch("<button[^>]*disabled[^>]*>Aprovar e importar</button>", html);
    }
    private async Task<long> ExistingMovement(DateOnly date, string description, long units, long? postingAccountId = null,
        bool unassigned = false, long? householdId = null, string status = "Confirmed", DateOnly? valueDate = null)
    {
        await using var db = Open();
        var movement = new FinancialTransaction { HouseholdId = householdId ?? (await db.Households.OrderBy(h => h.Id).FirstAsync()).Id,
            Kind = MovementKind.Expense, Date = date, Description = description, Status = status, Origin = unassigned ? "Legacy" : "Manual" };
        db.Add(movement); await db.SaveChangesAsync();
        db.Add(new Posting { TransactionId = movement.Id, AccountId = unassigned ? null : postingAccountId ?? account,
            CategoryId = householdId is null ? expense : null, Date = date, ValueDate = valueDate, AmountUnits = units }); await db.SaveChangesAsync();
        return movement.Id;
    }
    [Fact] public async Task HistoryCanSuggestAnExpenseRefundWithPositiveAmount()
    {
        await AddClassifiedHistory("Reembolso Loja", expense, 500_000);
        var id = Assert.Single(await Imports.StageAsync("refund.pdf", SyntheticPdf(secondDescription: "Reembolso Loja")));
        var row = (await Imports.PrepareReviewAsync(id)).Rows[1];
        Assert.Equal(MovementKind.Expense, row.Kind); Assert.Equal(expense, row.CategoryId);
    }
    [Fact] public async Task TransferSuggestionUsesTheKnownOtherAccountAndTheSameDirection()
    {
        await Finance.SaveMovementAsync(new() { Date = new(2024, 12, 1), Kind = MovementKind.Transfer, AccountId = account,
            DestinationAccountId = savings, Amount = 10, Description = "TRF POUPANCA" });
        var id = Assert.Single(await Imports.StageAsync("transfer.pdf", SyntheticPdf(firstDescription: "TRF POUPANCA", continuation: "")));
        var row = (await Imports.PrepareReviewAsync(id)).Rows[0];
        Assert.Equal(MovementKind.Transfer, row.Kind); Assert.Equal(savings, row.CounterpartyAccountId); Assert.Null(row.CategoryId);
        await using var db = Open(); (await db.Accounts.SingleAsync(a => a.Id == savings)).Active = false; await db.SaveChangesAsync();
        Assert.False((await Imports.PreviewAsync(id)).Suggestions.ContainsKey(1));
    }
    [Fact] public async Task GenericTransfersAndUnknownMerchantsRemainUnclassified()
    {
        await AddClassifiedHistory("TRF 123456", expense, -100_000);
        var id = Assert.Single(await Imports.StageAsync("unknown.pdf", SyntheticPdf(firstDescription: "TRF 987654", continuation: "")));
        Assert.Null((await Imports.PrepareReviewAsync(id)).Rows[0].CategoryId);
        var initial = Assert.Single(await Imports.ListAsync(), b => b.Name == "synthetic.pdf");
        Assert.Null((await Imports.PrepareReviewAsync(initial.Id)).Rows[0].CategoryId);
    }
    [Fact] public async Task AddingEmailReferencePreservesPurchaseAndIdempotenceAndReachesMovementSources()
    {
        var id=Assert.Single(await Imports.StageEvidenceAsync([PurchaseDocument("email:metadata")]));
        var transaction=await Imports.ConfirmEvidenceMovementAsync(id,1,account,expense,true);
        var source=Assert.Single(await Imports.EvidenceSourcesAsync([id]));
        var email=new EmailSourceReference("hotmail","Compras","mail-123","shop@example.com",DateTimeOffset.Parse("2026-09-03T12:57:37Z"),"Recibo da compra");
        var before=Assert.Single(await Imports.EvidenceAsync());
        await Imports.SaveEmailSourceReferencesAsync(new Dictionary<long,EmailSourceReference>{{source.Id,email}});
        await Imports.SaveEmailSourceReferencesAsync(new Dictionary<long,EmailSourceReference>{{source.Id,email}});
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before),System.Text.Json.JsonSerializer.Serialize(Assert.Single(await Imports.EvidenceAsync())));
        var movement=Assert.Single((await Finance.MovementsAsync(new(){TransactionId=transaction})).Rows);
        Assert.Equal(email,Assert.Single(movement.Sources).Email);
        await using var db=Open();Assert.Single(await db.AuditChanges.Where(a=>a.Action=="IdentifyEmailSource").ToListAsync());
        Assert.Null(db.Model.FindEntityType(typeof(PurchaseEvidence))!.FindProperty("Email"));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.SaveEmailSourceReferencesAsync(new Dictionary<long,EmailSourceReference>{{source.Id,email with{MessageId="different"}}}));
    }

    [Fact] public async Task PurchaseBankLinkOpensItsStatementAndHighlightsTheRequestedRow()
    {
        var id=Assert.Single(await Imports.StageAsync("linked-bank.pdf",SyntheticPdf("linked-bank",firstDescription:"AMAZON",continuation:"")));
        await Imports.StageEvidenceAsync([PurchaseDocument("email:deep-link")]);
        await using var services=new ServiceCollection().AddLogging().AddSingleton(Imports).AddSingleton(Finance).BuildServiceProvider();
        await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        var html=await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<FinanceForgeLab.Web.Components.Pages.Purchases>()).ToHtmlString()));
        Assert.Contains($"href=\"/extratos?rever={id}&linha=1\"",html);
        var review=await RenderReview(id,interact:async page=>
        {
            page.ReviewRequested=id;page.RowRequested=1;
            typeof(FinanceForgeLab.Web.Components.Pages.Statements).GetField("hideRegistered",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(page,true);
            await page.Call("OnParametersSetAsync");
            Assert.Equal(id,page.Review.Id);
            Assert.False((bool)typeof(FinanceForgeLab.Web.Components.Pages.Statements).GetField("hideRegistered",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(page)!);
        });
        Assert.Contains($"statement-row-focused\" id=\"statement-{id}-row-1\"",review);
        Assert.Contains("linked-bank.pdf",review);
    }

    [Fact] public async Task ReviewRendersSuggestionsAndFiltersUnresolvedRowsBeforeApproval()
    {
        var id = Assert.Single(await Imports.StageAsync("render.pdf", SyntheticPdf()));
        var html = await RenderReview(id);
        Assert.Contains("1 sugestões preenchidas · 1 por resolver", html);
        Assert.Contains("Sugestão · confiança média", html);
        Assert.Equal(2, Regex.Matches(html, "class=\"statement-description\"").Count);
        Assert.Matches("<button[^>]*disabled[^>]*>Aprovar e importar</button>", html);
        var filtered = await RenderReview(id, onlyAttention: true);
        Assert.Contains("Compra Loja de teste", filtered); Assert.DoesNotContain("Descrição no extrato</span><strong>Salario", filtered);
        Assert.Single(Regex.Matches(filtered, "class=\"statement-description\""));
    }
    [Fact] public async Task FullyClassifiedReviewCanBeApprovedWithoutEditingEveryRow()
    {
        await AddClassifiedHistory("Compra Loja de teste", expense, -100_000);
        var id = Assert.Single(await Imports.StageAsync("ready.pdf", SyntheticPdf()));
        var html = await RenderReview(id);
        Assert.Contains("2 sugestões preenchidas · 0 por resolver", html);
        Assert.DoesNotMatch("<button[^>]*disabled[^>]*>Aprovar e importar</button>", html);
        Assert.Contains("Aprovar e importar</button>", html);
    }
    [Fact] public async Task IndividualApprovalLeavesTheOtherRowsOpenAndRegisteredRowsHaveNoCategoryInputs()
    {
        var review=await Review();review.Rows[0].Decision="Pending";review.Rows[0].CategoryId=null;await Imports.SaveReviewAsync(review);
        var html=await RenderReview(review.Id,interact:async page=>
        {
            await page.Call("ApproveRow",review.Rows[1].Ordinal);
            Assert.Equal("Link",page.Review.Rows[1].Decision);Assert.Equal("Pending",page.Review.Rows[0].Decision);
        });
        Assert.Contains("Aprovação concluída: 1 movimentos criados",html);
        var registered=Regex.Match(html,"<tr class=\"statement-row-registered\".*?</tr>",RegexOptions.Singleline).Value;
        Assert.NotEmpty(registered);Assert.Contains("Trabalho",registered);Assert.DoesNotContain("<select",registered);Assert.DoesNotContain("statement-approve-row",registered);
        await using var db=Open();Assert.Single(await db.BankImportEntries.ToListAsync());Assert.Equal("Draft",(await db.BankImports.SingleAsync()).Status);
    }
    [Fact] public async Task RecordedTransferWithEditedDescriptionRemainsRegisteredWithoutAnotherApproval()
    {
        var review=await Review();var choice=review.Rows[0];choice.Kind=MovementKind.Transfer;choice.CategoryId=null;choice.CounterpartyAccountId=savings;
        await Imports.ConfirmRowsAsync(review,[choice.Ordinal]);
        var id=(await Imports.PreviewAsync(review.Id)).RecordedTransactions[choice.Ordinal];
        var form=await Finance.MovementAsync(id);form.Description="EMEL · Carregamento ePark";await Finance.SaveMovementAsync(form);
        var reopened=await Imports.PrepareReviewAsync(review.Id);
        Assert.Equal((await Finance.MovementAsync(id)).Version,reopened.Rows[0].ExistingVersion);
        var html=await RenderReview(review.Id);
        var registered=Regex.Match(html,"<tr class=\"statement-row-registered\".*?</tr>",RegexOptions.Singleline).Value;
        Assert.NotEmpty(registered);Assert.Contains("Transferência",registered);Assert.DoesNotContain("statement-approve-row",registered);
        await using var db=Open();Assert.Single(await db.BankImportEntries.ToListAsync());Assert.Single(await db.Transactions.Where(t=>t.Kind==MovementKind.Transfer).ToListAsync());
    }
    [Fact] public async Task RecordedPurchaseWithLaterBankDateAndEditedCategoryRemainsRegistered()
    {
        var document=PurchaseDocument("email:earlier-purchase");document.Date=new(2024,12,28);
        var evidenceId=Assert.Single(await Imports.StageEvidenceAsync([document]));
        var movement=await Imports.ConfirmEvidenceMovementAsync(evidenceId,1,account,expense,true);
        var batch=Assert.Single(await Imports.StageAsync("later-charge.pdf",SyntheticPdf(firstDescription:"AMZNMktpES",continuation:"")));
        var review=await Imports.PrepareReviewAsync(batch);
        // The association may already be recorded automatically when the statement arrives.
        if(!(await Imports.PreviewAsync(batch)).RecordedTransactions.ContainsKey(1))await Imports.ConfirmRowsAsync(review,[1]);
        var subcategory=await Finance.SaveCategoryAsync(new(){Name="Por classificar",Kind=MovementKind.Expense,ParentId=expense});
        var form=await Finance.MovementAsync(movement);form.CategoryId=subcategory;await Finance.SaveMovementAsync(form);
        var reopened=await Imports.PrepareReviewAsync(batch);
        Assert.Equal((await Finance.MovementAsync(movement)).Version,reopened.Rows[0].ExistingVersion);
        var html=await RenderReview(batch);
        var registered=Regex.Match(html,"<tr class=\"statement-row-registered\".*?</tr>",RegexOptions.Singleline).Value;
        Assert.NotEmpty(registered);Assert.Contains("Por classificar",registered);Assert.DoesNotContain("statement-approve-row",registered);
        await using var db=Open();Assert.Single(await db.Transactions.Where(t=>t.Kind==MovementKind.Expense).ToListAsync());
        // A changed transaction date no longer agrees with its original purchase document.
        form=await Finance.MovementAsync(movement);form.Date=form.Date.AddDays(1);await Finance.SaveMovementAsync(form);
        reopened=await Imports.PrepareReviewAsync(batch);
        Assert.NotEqual((await Finance.MovementAsync(movement)).Version,reopened.Rows[0].ExistingVersion);
    }
    [Fact] public async Task RecordedTransferWithChangedDestinationStillRequiresReview()
    {
        var review=await Review();var choice=review.Rows[0];choice.Kind=MovementKind.Transfer;choice.CategoryId=null;choice.CounterpartyAccountId=savings;
        await Imports.ConfirmRowsAsync(review,[choice.Ordinal]);
        var id=(await Imports.PreviewAsync(review.Id)).RecordedTransactions[choice.Ordinal];
        var previous=(await Imports.PrepareReviewAsync(review.Id)).Rows[0].ExistingVersion;
        var other=await Finance.SaveAccountAsync(new(){Name="Outra conta",BalanceStartDate=new(2025,1,1)});
        var form=await Finance.MovementAsync(id);form.DestinationAccountId=other;await Finance.SaveMovementAsync(form);
        var reopened=await Imports.PrepareReviewAsync(review.Id);
        Assert.Equal(previous,reopened.Rows[0].ExistingVersion);
        Assert.NotEqual((await Finance.MovementAsync(id)).Version,reopened.Rows[0].ExistingVersion);
    }
    [Fact] public async Task SelectionApprovalRejectsIncompleteRowsAndSelectReadyOnlyChoosesValidRows()
    {
        var review=await Review();review.Rows[0].CategoryId=null;review.Rows[0].Decision="Pending";await Imports.SaveReviewAsync(review);
        await RenderReview(review.Id,interact:async page=>
        {
            foreach(var row in page.Review.Rows)await page.Call("ToggleRow",row.Ordinal,new ChangeEventArgs{Value=true});
            await page.Call("ApproveSelection");
            await using(var db=Open())Assert.Empty(await db.BankImportEntries.ToListAsync());
            await page.Call("SelectReady");await page.Call("ApproveSelection");
            await using(var db=Open())Assert.Single(await db.BankImportEntries.ToListAsync());
            Assert.Null(page.Review.Rows[0].CategoryId);Assert.Equal("Pending",page.Review.Rows[0].Decision);
        });
    }
    [Fact] public async Task SelectionCanApproveSeveralRowsAtOnceWithoutRecreatingRecordedPayments()
    {
        var review=await Review();await Imports.SaveReviewAsync(review);
        await RenderReview(review.Id,interact:async page=>{await page.Call("SelectReady");await page.Call("ApproveSelection");});
        await using var db=Open();Assert.Equal(2,await db.BankImportEntries.CountAsync());Assert.Equal(2,await db.Transactions.CountAsync(t=>t.Origin=="BankImport"));
        await RenderReview(review.Id,interact:async page=>{await page.Call("SelectReady");await page.Call("ApproveSelection");});
        Assert.Equal(2,await db.Transactions.CountAsync(t=>t.Origin=="BankImport"));
    }
    [Fact] public async Task SimilarApprovalSpansMonthsButKeepsOtherAccountsAndUnrelatedReferencesOut()
    {
        const string description="Prestacao contrato 1571003209";
        await Imports.SaveReferenceAsync(new(){AccountId=account,CategoryId=expense,Code="1571003209",Description="Crédito pessoal"});
        var first=Assert.Single(await Imports.StageAsync("jan2025.pdf",SyntheticPdf("same-ref-new",firstDescription:description,continuation:"")));
        var older=Assert.Single(await Imports.StageAsync("jan2024.pdf",SyntheticPdf("same-ref-old",previousYear:true,firstDescription:description,continuation:"")));
        await Imports.StageAsync("other-person.pdf",SyntheticPdf("same-ref-other-account",reference:"9999 8888 7777",firstDescription:description,continuation:""));
        await Imports.StageAsync("unrelated.pdf",SyntheticPdf("other-ref",previousYear:true,firstDescription:"Prestacao contrato 9999999999",continuation:""));
        var plan=await Imports.SimilarApprovalAsync(first,1);
        Assert.Equal(2,plan.Rows.Count);Assert.Contains(plan.Rows,r=>r.ImportId==older);Assert.Contains(plan.Rows,r=>r.ImportId==first);
        var result=await Imports.ConfirmSimilarAsync(plan);Assert.Equal(2,result.Created);Assert.Equal(2,result.Statements);
        Assert.Empty((await Imports.SimilarApprovalAsync(first,1)).Rows);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmSimilarAsync(plan));
        await using var db=Open();Assert.Equal(2,await db.Transactions.CountAsync(t=>t.Origin=="BankImport"));
    }
    [Fact] public async Task SimilarGroupCanBeReviewedAndApprovedFromThePage()
    {
        await Imports.SaveReferenceAsync(new(){AccountId=account,CategoryId=expense,Code="1571003209",Description="Crédito pessoal"});
        var id=Assert.Single(await Imports.StageAsync("current.pdf",SyntheticPdf("ui-group-current",firstDescription:"Prestacao 1571003209",continuation:"")));
        await Imports.StageAsync("previous.pdf",SyntheticPdf("ui-group-previous",previousYear:true,firstDescription:"Prestacao 1571003209",continuation:""));
        var preview=await RenderReview(id,interact:async page=>await page.Call("PrepareSimilar",1));
        Assert.Contains("Movimentos nas mesmas condições",preview);Assert.Contains("Aprovar estes 2 movimentos",preview);
        Assert.Contains("current.pdf",preview);Assert.Contains("previous.pdf",preview);
        var after=await RenderReview(id,interact:async page=>{await page.Call("PrepareSimilar",1);await page.Call("ApproveSimilar");});
        Assert.Contains("Grupo aprovado: 2 movimentos registados em 2 extratos",after);
        await using var db=Open();Assert.Equal(2,await db.Transactions.CountAsync(t=>t.Origin=="BankImport"));
    }
    [Fact] public async Task SimilarGroupLeavesRepeatedSourceLinesForManualReview()
    {
        await Imports.SaveReferenceAsync(new(){AccountId=account,CategoryId=expense,Code="1571003209",Description="Crédito pessoal"});
        var id=Assert.Single(await Imports.StageAsync("one.pdf",SyntheticPdf("overlap-one",firstDescription:"Prestacao 1571003209",continuation:"")));
        await Imports.StageAsync("two.pdf",SyntheticPdf("overlap-two",firstDescription:"Prestacao 1571003209",continuation:""));
        Assert.Empty((await Imports.SimilarApprovalAsync(id,1)).Rows);
        await using var db=Open();Assert.Empty(await db.BankImportEntries.ToListAsync());
    }
    [Fact] public async Task SimilarApprovalRejectsStalePlansAndRollsBackEveryStatementIfAnyPdfFailsIntegrity()
    {
        const string description="Prestacao contrato 1571003209";
        await Imports.SaveReferenceAsync(new(){AccountId=account,CategoryId=expense,Code="1571003209",Description="Crédito pessoal"});
        var first=Assert.Single(await Imports.StageAsync("first.pdf",SyntheticPdf("group-one",firstDescription:description,continuation:"")));
        var second=Assert.Single(await Imports.StageAsync("second.pdf",SyntheticPdf("group-two",previousYear:true,firstDescription:description,continuation:"")));
        var plan=await Imports.SimilarApprovalAsync(first,1);var review=await Imports.PrepareReviewAsync(second);await Imports.SaveReviewAsync(review);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmSimilarAsync(plan));
        plan=await Imports.SimilarApprovalAsync(first,1);Assert.Equal(2,plan.Rows.Count);
        await using(var db=Open()){var document=await db.BankDocuments.SingleAsync(d=>d.Id==(db.BankImports.Single(b=>b.Id==second).DocumentId));document.Content=[..document.Content,32];await db.SaveChangesAsync();}
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmSimilarAsync(plan));
        await using(var db=Open()){Assert.Empty(await db.BankImportEntries.ToListAsync());Assert.Empty(await db.Transactions.Where(t=>t.Origin=="BankImport").ToListAsync());}
    }
    [Fact] public async Task SimilarApprovalDoesNotOfferPossibleDuplicatesOrPaymentPlatformsWithoutSpecificReferences()
    {
        await Imports.SaveReferenceAsync(new(){AccountId=account,CategoryId=expense,Code="1571003209",Description="Crédito pessoal"});
        var id=Assert.Single(await Imports.StageAsync("duplicate.pdf",SyntheticPdf("group-duplicate",firstDescription:"Prestacao contrato 1571003209",continuation:"")));
        await ExistingMovement(new(2025,1,2),"Outra compra",-100_000);
        Assert.Empty((await Imports.SimilarApprovalAsync(id,1)).Rows);
        var platform=Assert.Single(await Imports.StageAsync("platform.pdf",SyntheticPdf("group-platform",firstDescription:"PAYPAL",continuation:"")));
        var review=await Imports.PrepareReviewAsync(platform);review.Rows[0].CategoryId=expense;await Imports.SaveReviewAsync(review);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.SimilarApprovalAsync(platform,1));
    }
    [Fact] public async Task ReopeningReviewReloadsCategoriesAndClassificationsSavedElsewhere()
    {
        var review=await Review();await Imports.SaveReviewAsync(review);
        var html=await RenderReview(review.Id,interact:async page=>
        {
            var leaf=await Finance.SaveCategoryAsync(new(){Name="Crédito pessoal",ParentId=expense,Kind=MovementKind.Expense});
            var saved=await Imports.PrepareReviewAsync(review.Id);saved.Rows[0].CategoryId=leaf;await Imports.SaveReviewAsync(saved);
            await page.Call("Open",review.Id);
            Assert.Equal(leaf,page.Review.Rows[0].CategoryId);
        });
        Assert.Contains("Classificado: Casa / Crédito pessoal",html);
        Assert.Matches("<option value=\"[0-9]+\" selected>Crédito pessoal</option>",html);
    }
    [Fact] public async Task SavedDraftOffersOneClickToFillSuggestionAndPersistsAcceptance()
    {
        var review = await Review(); review.Rows[1].Kind = MovementKind.Expense; review.Rows[1].CategoryId = null;
        review.Rows[1].Decision = "Pending"; review.Rows[1].Detail = "Nota minha";
        await Imports.SaveReviewAsync(review);
        var initial = await RenderReview(review.Id);
        Assert.Contains("Aplicar sugestão", initial); Assert.Contains("Receita · Trabalho", initial);
        Assert.Contains("class=\"category-picker\"", initial); Assert.Contains("Subcategoria", initial);
        var html = await RenderReview(review.Id, interact: async page =>
        {
            var row = page.Review.Rows[1];
            await page.Call("AcceptSuggestion", row);
            Assert.Equal(MovementKind.Income, row.Kind); Assert.Equal(income, row.CategoryId); Assert.Equal("Nota minha", row.Detail);
            Assert.True(row.SuggestionAccepted); Assert.Equal("Import", row.Decision);
            await page.Call("SaveReview");
        });
        Assert.Contains("✓ Sugestão aceite", html);
        Assert.True((await Imports.PrepareReviewAsync(review.Id)).Rows[1].SuggestionAccepted);
    }

    [Fact] public async Task BatchAcceptancePreservesManualEditsAndExcludedRows()
    {
        await AddClassifiedHistory("Compra Loja de teste", expense, -100_000);
        var review = await Review();
        await RenderReview(review.Id, interact: async page =>
        {
            var manual = page.Review.Rows[0]; var suggested = page.Review.Rows[1];
            await page.Call("CategoryChanged", manual, expense);
            suggested.CategoryId = null;
            await page.Call("AcceptSuggestions");
            Assert.Equal(expense, manual.CategoryId); Assert.False(manual.SuggestionAccepted); Assert.True(manual.ClassificationEdited);
            Assert.Equal(income, suggested.CategoryId); Assert.True(suggested.SuggestionAccepted);
            suggested.Decision = "Exclude"; suggested.CategoryId = null; suggested.SuggestionAccepted = false;
            await page.Call("AcceptSuggestions");
            Assert.Equal("Exclude", suggested.Decision); Assert.Null(suggested.CategoryId);
            await page.Call("AcceptSuggestion", manual); Assert.True(manual.SuggestionAccepted);
            await page.Call("KindChanged", manual); Assert.Null(manual.CategoryId); Assert.False(manual.SuggestionAccepted);
        });
    }

    [Fact] public async Task AcceptingClassificationDoesNotApprovePotentialDuplicates()
    {
        await AddClassifiedHistory("Compra Loja de teste", expense, -100_000);
        var review = await Review(SyntheticPdf("duplicate-accept", secondDescription: "Compra Loja de teste", repeatedPurchase: true));
        await RenderReview(review.Id, interact: async page =>
        {
            var row = page.Review.Rows[0];
            Assert.Equal("Pending", row.Decision);
            await page.Call("AcceptSuggestion", row);
            Assert.Equal(expense, row.CategoryId); Assert.True(row.SuggestionAccepted);
            Assert.Equal("Pending", row.Decision); Assert.False(row.AcknowledgeDuplicate);
        });
    }

    [Fact] public async Task ProcessingFeedbackAppearsBeforeWorkRejectsRepeatedClicksAndClearsAfterFailure()
    {
        var id=Assert.Single(await Imports.ListAsync()).Id;
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls=0;
        var html=await RenderReview(id,interact:async page=>
        {
            var running=page.Call("Run",(Func<Task>)(async()=>{calls++;started.SetResult();await release.Task;throw new FinanceRuleException("Falha de teste recuperável.");}));
            await started.Task;
            Assert.Contains("operation-busy",page.Snapshot());
            Assert.Contains("A processar…",page.Snapshot());
            await page.Call("Run",(Func<Task>)(()=>{calls++;return Task.CompletedTask;}));
            Assert.Equal(1,calls);
            release.SetResult();await running;
            Assert.DoesNotContain("operation-busy",page.Snapshot());
        });
        Assert.Contains("Falha de teste recuperável.",html);
    }
    private async Task<string> RenderReview(long id, bool onlyAttention = false, Func<ReviewRenderPage, Task>? interact = null)
    {
        var services = new ServiceCollection().AddLogging().AddSingleton(Imports).AddSingleton(Finance)
            .AddSingleton(new FinanceForgeLab.Web.Services.BackupManager(DatabasePath, Path.Combine(folder, "backups")))
            .AddSingleton<IJSRuntime, StaticRenderJs>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            ReviewRenderPage? page = null;
            var component = await renderer.RenderComponentAsync<ReviewRenderPage>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(ReviewRenderPage.ImportId)] = id, [nameof(ReviewRenderPage.OnlyAttention)] = onlyAttention, [nameof(ReviewRenderPage.Capture)] = (Action<ReviewRenderPage>)(p => page = p) }));
            page!.Snapshot=()=>WebUtility.HtmlDecode(component.ToHtmlString());
            if (interact is not null) await interact(page!);
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }
    public sealed class ReviewRenderPage : FinanceForgeLab.Web.Components.Pages.Statements
    {
        [Parameter] public Action<ReviewRenderPage>? Capture { get; set; }
        public Func<string> Snapshot { get; set; } = () => "";
        public BankImportReview Review => (BankImportReview)typeof(FinanceForgeLab.Web.Components.Pages.Statements).GetField("review", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
        public async Task Call(string method, params object?[] args)
        {
            var result = typeof(FinanceForgeLab.Web.Components.Pages.Statements).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, args);
            if (result is Task task) await task;
            StateHasChanged();
        }
        [Parameter] public long ImportId { get; set; }
        [Parameter] public bool OnlyAttention { get; set; }
        protected override async Task OnInitializedAsync()
        {
            await base.OnInitializedAsync();
            await (Task)typeof(FinanceForgeLab.Web.Components.Pages.Statements).GetMethod("Open", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this, [ImportId])!;
            typeof(FinanceForgeLab.Web.Components.Pages.Statements).GetField("onlyAttention", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, OnlyAttention);
            Capture?.Invoke(this);
        }
    }
    private sealed class StaticRenderJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new InvalidOperationException("Static rendering must not invoke JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
    private async Task AddClassifiedHistory(string description, long categoryId, long units, string status = "Confirmed", long? householdId = null)
    {
        await using var db = Open();
        var movement = new FinancialTransaction { HouseholdId = householdId ?? (await db.Households.OrderBy(h => h.Id).FirstAsync()).Id,
            Kind = (await db.Categories.SingleAsync(c => c.Id == categoryId)).Kind, Date = new(2024, 12, 1), Description = description, Status = status };
        db.Add(movement); await db.SaveChangesAsync();
        db.Add(new Posting { TransactionId = movement.Id, AccountId = householdId is null ? account : null, CategoryId = categoryId, Date = movement.Date, AmountUnits = units }); await db.SaveChangesAsync();
    }
    [Fact] public async Task EvidenceStagingIsIdempotentAndConflictingBatchRollsBackWithoutLedgerChanges()
    {
        var item = new PurchaseEvidence { SourceKey = "amazon:order-1", Source = "Imagem de teste", Channel = "Amazon", Description = "Teclado", Date = new(2025,1,1), Units = Money.ToUnits(10), CategoryId = expense };
        var first = Assert.Single(await Imports.StageEvidenceAsync([item]));
        Assert.All(await Imports.StageEvidenceAsync([item,item]),id=>Assert.Equal(first,id));
        // A different amount for the same document must never silently overwrite the evidence.
        item.Units = Money.ToUnits(20);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.StageEvidenceAsync([
            new() { SourceKey="another-order", Source="Imagem de teste", Description="Outra compra", Units=1 },item]));
        await using var db=Open(); Assert.Single(await db.PurchaseEvidence.ToListAsync());
        Assert.Equal(Money.ToUnits(10),(await db.PurchaseEvidence.SingleAsync()).Units);
        Assert.Equal(2,await db.Transactions.CountAsync()); Assert.Empty(await db.BankReferences.ToListAsync());
    }
    [Fact] public async Task EvidenceMatchesDraftAndExistingPaymentAndApprovalOnlyCreatesAReference()
    {
        var draft=Assert.Single(await Imports.StageAsync("evidence.pdf",SyntheticPdf(firstDescription:"AMZNMktpES",continuation:"")));
        var id=Assert.Single(await Imports.StageEvidenceAsync([new() {SourceKey="amazon:order-2",Source="Imagem de teste",Channel="Amazon",Description="Teclado",Date=new(2025,1,1),Units=Money.ToUnits(10),CategoryId=expense}]));
        var match=Assert.Single((await Imports.EvidenceMatchesAsync())[id]);Assert.Equal(draft,match.ImportId);Assert.Equal(account,match.AccountId);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ApproveEvidenceAsync(id,1,account,expense,false));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ApproveEvidenceAsync(id,1,0,expense,true));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ApproveEvidenceAsync(id,1,account,income,true));
        await Imports.ApproveEvidenceAsync(id,1,account,expense,true);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ApproveEvidenceAsync(id,1,account,expense,true));
        var reference=Assert.Single(await Imports.ReferencesAsync(account));Assert.Equal(Money.ToUnits(10),reference.AmountUnits);
        Assert.Equal(expense,(await Imports.PreviewAsync(draft)).Suggestions[match.Ordinal!.Value].CategoryId);
        await Imports.ApproveEvidenceAsync(id,2,account,expense,true);Assert.Single(await Imports.ReferencesAsync(account));
        await using(var db=Open()){Assert.Equal(2,await db.Transactions.CountAsync());}
        var transaction=await Finance.SaveMovementAsync(new(){Date=new(2025,1,2),Kind=MovementKind.Expense,AccountId=account,CategoryId=expense,Amount=10,Description="Compra AMAZON"});
        Assert.Contains((await Imports.EvidenceMatchesAsync())[id],m=>m.TransactionId==transaction);
    }
    [Theory]
    [InlineData("Amazon",1,11)]
    [InlineData("Amazon",10,10)]
    [InlineData("PayPal",1,10)]
    public async Task EvidenceDoesNotMatchWrongAmountDistantDateOrWrongChannel(string channel,int day,int amount)
    {
        await Imports.StageAsync("nonmatching-evidence.pdf",SyntheticPdf(firstDescription:"AMAZON",continuation:""));
        var id=Assert.Single(await Imports.StageEvidenceAsync([new(){SourceKey="test-order",Source="Imagem de teste",Channel=channel,Description="Compra",Date=new(2025,1,day),Units=Money.ToUnits(amount)}]));
        Assert.Empty((await Imports.EvidenceMatchesAsync())[id]);
    }
    [Fact] public async Task PurchaseInboxRendersProposedCategoryProvenanceAndWarningsWithoutChoosingAnAccount()
    {
        await Imports.StageEvidenceAsync([new(){SourceKey="render:order",Source="Duas imagens do mesmo pedido",Channel="Amazon",Description="Teclado e adaptador",Date=new(2026,10,12),Units=Money.ToUnits(26.41m),CategoryId=expense,ReviewNotes="Diferença de 0,43 €",Details="Artigos: 6,99 € e 18,99 €"}]);
        await using var services=new ServiceCollection().AddLogging().AddSingleton(Imports).AddSingleton(Finance).BuildServiceProvider();
        await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        var html=await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<FinanceForgeLab.Web.Components.Pages.Purchases>()).ToHtmlString()));
        Assert.Contains("Teclado e adaptador",html);Assert.Contains("Diferença de 0,43 €",html);Assert.Contains("Duas imagens do mesmo pedido",html);
        Assert.Contains("Subcategoria",html);Assert.Contains("Por associar — confirmar depois",html);Assert.Contains("Sem correspondência",html);
        Assert.Matches("<option value=\""+expense+"\" selected>Casa</option>",html);
        Assert.Matches("<button[^>]+disabled[^>]*>Aprovar e criar movimento</button>",html);
    }
    [Fact] public async Task UndatedDocumentsAreSeparateFromTheSelectedYearAndCannotInviteApproval()
    {
        await Imports.StageEvidenceAsync([
            new(){SourceKey="test:dated",Source="Documento de teste",Channel="Continente",Description="Compra de 2017",Date=new(2017,1,2),Units=10000},
            new(){SourceKey="test:undated",Source="Pasta Continente",Channel="Continente",Description="Documento sem dados",OrderReference="ficheiro.pdf"}]);
        await using var services=new ServiceCollection().AddLogging().AddSingleton(Imports).AddSingleton(Finance).BuildServiceProvider();
        await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        async Task<string> Render(int? year)=>await renderer.Dispatcher.InvokeAsync(async()=>WebUtility.HtmlDecode((await renderer.RenderComponentAsync<PurchaseYearTestPage>(ParameterView.FromDictionary(new Dictionary<string,object?>{{"InitialYear",year}}))).ToHtmlString()));
        var annual=await Render(2017);
        Assert.Contains("<h3>Compra de 2017</h3>",annual);Assert.DoesNotContain("<h3>Documento sem dados</h3>",annual);Assert.Contains("Ver documentos sem data",annual);
        var undated=await Render(0);
        Assert.DoesNotContain("<h3>Compra de 2017</h3>",undated);Assert.Contains("<h3>Documento sem dados</h3>",undated);
        Assert.DoesNotContain("Podes criar já o movimento",undated);Assert.DoesNotContain("Aprovar e criar movimento",undated);Assert.Contains("não estão associados a nenhum ano",undated);
        var all=await Render(null);Assert.Contains("<h3>Compra de 2017</h3>",all);Assert.Contains("<h3>Documento sem dados</h3>",all);
    }
    public sealed class PurchaseYearTestPage:FinanceForgeLab.Web.Components.Pages.Purchases
    {
        [Parameter] public int? InitialYear {get;set;}
        protected override Task OnInitializedAsync(){YearRequested=InitialYear;ReviewOnly=true;return base.OnInitializedAsync();}
    }
    private PurchaseEvidence PurchaseDocument(string key,string order="404-1111111-2222222",decimal amount=10)=>new(){SourceKey=key,Source=key.StartsWith("email")?"Email de confirmação":"Imagem da encomenda",Channel="Amazon",OrderReference=order,Description="Adaptador USB",Date=new(2025,1,1),Units=Money.ToUnits(amount),CategoryId=expense};
    [Fact] public async Task InvoiceIssuedWeeksEarlierMatchesItsScheduledDebitWithoutChangingIssueDate()
    {
        var invoice=PurchaseDocument("email:scheduled-gas");invoice.Channel="Gascan";invoice.Date=new(2024,12,1);invoice.ExpectedPaymentDate=new(2025,1,2);
        var id=Assert.Single(await Imports.StageEvidenceAsync([invoice]));
        var movement=await Imports.ConfirmEvidenceMovementAsync(id,1,null,expense,true);
        var statement=Assert.Single(await Imports.StageAsync("scheduled-gas.pdf",SyntheticPdf(firstDescription:"COBRANCA GASCAN",continuation:"")));
        var review=await Imports.PrepareReviewAsync(statement);
        Assert.Equal("Link",review.Rows[0].Decision);Assert.Equal(movement,review.Rows[0].ExistingTransactionId);
        review.Rows[1].Decision="Exclude";
        var result=await Imports.ConfirmAsync(review);Assert.Equal(0,result.Created);Assert.Equal(1,result.Linked);
        Assert.Equal(invoice.Date,(await Finance.MovementAsync(movement)).Date);
        Assert.Equal(invoice.ExpectedPaymentDate,Assert.Single(await Imports.EvidenceAsync()).ExpectedPaymentDate);
    }
    [Fact] public async Task ScheduledInvoiceCannotRecreateAnAlreadyRecordedBankPayment()
    {
        var paid=await Finance.SaveMovementAsync(new(){Date=new(2025,1,2),Kind=MovementKind.Expense,AccountId=account,CategoryId=expense,Amount=10,Description="COBRANCA GASCAN"});
        var invoice=PurchaseDocument("email:gas-already-paid");invoice.Channel="Gascan";invoice.Date=new(2024,12,1);invoice.ExpectedPaymentDate=new(2025,1,2);
        var id=Assert.Single(await Imports.StageEvidenceAsync([invoice]));
        Assert.Contains((await Imports.EvidenceMatchesAsync())[id],m=>m.TransactionId==paid);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmEvidenceMovementAsync(id,1,null,expense,true));
        await Imports.LinkEvidenceAsync(id,1,paid);
        Assert.Equal(paid,Assert.Single(await Imports.EvidenceAsync()).TransactionId);
    }
    [Theory]
    [InlineData("Gascan",20)]
    [InlineData("OtherCompany",2)]
    public async Task ScheduledDebitStillRequiresCompatibleDateAndMerchant(string merchant,int paymentDay)
    {
        var invoice=PurchaseDocument("email:wrong-schedule");invoice.Channel=merchant;invoice.Date=new(2024,12,1);invoice.ExpectedPaymentDate=new(2025,1,paymentDay);
        var id=Assert.Single(await Imports.StageEvidenceAsync([invoice]));await Imports.ConfirmEvidenceMovementAsync(id,1,null,expense,true);
        var statement=Assert.Single(await Imports.StageAsync("wrong-schedule.pdf",SyntheticPdf(firstDescription:"COBRANCA GASCAN",continuation:"")));
        Assert.Empty((await Imports.PreviewAsync(statement)).Duplicates);
    }
    [Fact] public async Task PurchaseCreatesExpenseBeforeBankAndLaterEmailAndStatementDoNotDuplicateIt()
    {
        var image=PurchaseDocument("image:before-bank");
        var id=Assert.Single(await Imports.StageEvidenceAsync([image]));
        var movement=await Imports.ConfirmEvidenceMovementAsync(id,1,account,expense,true);
        Assert.Equal(movement,await Imports.ConfirmEvidenceMovementAsync(id,1,account,expense,true));
        Assert.Equal(id,Assert.Single(await Imports.StageEvidenceAsync([PurchaseDocument("email:later-confirmation")])));
        var statement=Assert.Single(await Imports.StageAsync("later-bank.pdf",SyntheticPdf(firstDescription:"AMZNMktpES",continuation:"")));
        var review=await Imports.PrepareReviewAsync(statement);
        var purchase=review.Rows.First();Assert.Equal("Link",purchase.Decision);Assert.Equal(movement,purchase.ExistingTransactionId);
        foreach(var row in review.Rows.Where(r=>r.Decision!="Link"))row.Decision="Exclude";
        var result=await Imports.ConfirmAsync(review);Assert.Equal(0,result.Created);Assert.Equal(1,result.Linked);
        await using var db=Open();Assert.Single(await db.Transactions.Where(t=>t.Kind==MovementKind.Expense).ToListAsync());
        Assert.Equal("Purchase",(await db.Transactions.SingleAsync(t=>t.Id==movement)).Origin);
        Assert.Equal(2,(await Imports.EvidenceSourcesAsync()).Count);
        Assert.Equal(9_900_000,(await Finance.AccountsAsync()).Single(a=>a.Account.Id==account).BalanceUnits);
    }
    [Fact] public async Task PurchaseCreationRechecksExistingBankMovementAndRequiresCompleteReviewedData()
    {
        var id=Assert.Single(await Imports.StageEvidenceAsync([PurchaseDocument("email:complete")]));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmEvidenceMovementAsync(id,1,account,expense,false));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmEvidenceMovementAsync(id,1,0,expense,true));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmEvidenceMovementAsync(id,1,account,income,true));
        var existing=await Finance.SaveMovementAsync(new(){Date=new(2025,1,2),Kind=MovementKind.Expense,AccountId=account,CategoryId=expense,Amount=10,Description="Compra AMAZON"});
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmEvidenceMovementAsync(id,1,account,expense,true));
        Assert.Null(Assert.Single(await Imports.EvidenceAsync()).TransactionId);
        await Imports.LinkEvidenceAsync(id,1,existing);
        await using var db=Open();Assert.Single(await db.Transactions.Where(t=>t.Kind==MovementKind.Expense).ToListAsync());
    }
    [Fact] public async Task PartialLinkShowsTheCategoryOfTheExistingPurchaseAndUpdatesDocumentCounts()
    {
        var id=Assert.Single(await Imports.StageEvidenceAsync([PurchaseDocument("email:partial-link")]));
        var movement=await Imports.ConfirmEvidenceMovementAsync(id,1,null,expense,true);
        var statement=Assert.Single(await Imports.StageAsync("partial-link.pdf",SyntheticPdf(firstDescription:"AMZNMktpES",continuation:"")));
        var review=await Imports.PrepareReviewAsync(statement);Assert.Equal("Link",review.Rows[0].Decision);
        review.Rows[0].CategoryId=null;
        await Imports.ConfirmRowsAsync(review,[review.Rows[0].Ordinal]);
        var updated=await Imports.PrepareReviewAsync(statement);
        Assert.Equal(expense,updated.Rows[0].CategoryId);Assert.Equal(movement,updated.Rows[0].ExistingTransactionId);
        var summary=(await Imports.ListAsync()).Single(s=>s.Id==statement);
        Assert.Equal(1,summary.RegisteredRows);Assert.Equal(1,summary.PendingRows);Assert.Equal(1,summary.ClassifiedRows);Assert.Equal(0,summary.UnclassifiedRows);
        await using var db=Open();Assert.Single(await db.Transactions.Where(t=>t.Kind==MovementKind.Expense).ToListAsync());
    }
    [Fact] public async Task EmailCanCreateExpenseWithoutAccountAndBankIdentifiesAccountSevenDaysLater()
    {
        var email=PurchaseDocument("email:before-account");email.Date=new(2024,12,26);
        var id=Assert.Single(await Imports.StageEvidenceAsync([email]));
        var movement=await Imports.ConfirmEvidenceMovementAsync(id,1,null,expense,true);
        var edit=await Finance.MovementAsync(movement);Assert.Null(edit.AccountId);
        edit.Description="Adaptador USB comprado por email";await Finance.SaveMovementAsync(edit);
        var statement=Assert.Single(await Imports.StageAsync("seven-days.pdf",SyntheticPdf(firstDescription:"AMZNMktpES",continuation:"")));
        var review=await Imports.PrepareReviewAsync(statement);
        Assert.Equal("Link",review.Rows[0].Decision);Assert.Equal(movement,review.Rows[0].ExistingTransactionId);
        review.Rows[1].Decision="Exclude";
        var result=await Imports.ConfirmAsync(review);Assert.Equal(0,result.Created);Assert.Equal(1,result.Linked);
        await using var db=Open();var posting=await db.Postings.SingleAsync(p=>p.TransactionId==movement);
        Assert.Equal(account,posting.AccountId);Assert.Equal(-Money.ToUnits(10),posting.AmountUnits);Assert.Equal(email.Date,posting.Date);
        Assert.Equal(account,Assert.Single(await Imports.EvidenceAsync()).AccountId);
        Assert.Single(await db.Transactions.Where(t=>t.Kind==MovementKind.Expense).ToListAsync());
        Assert.Contains(await db.AuditChanges.ToListAsync(),a=>a.EntityId==movement&&a.Action=="IdentifyPurchaseAccount");
    }
    [Fact] public async Task UnknownAccountDoesNotAllowRecreatingExistingPaymentAndVoidedPurchaseIsNotSilentlyRecreated()
    {
        var first=Assert.Single(await Imports.StageEvidenceAsync([PurchaseDocument("email:first")]));
        var transaction=await Imports.ConfirmEvidenceMovementAsync(first,1,account,expense,true);
        var second=Assert.Single(await Imports.StageEvidenceAsync([PurchaseDocument("image:different","different-order")]));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmEvidenceMovementAsync(second,1,null,expense,true));
        await Finance.VoidMovementAsync(transaction,1,Guid.NewGuid());
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmEvidenceMovementAsync(first,2,account,expense,true));
        await using var db=Open();Assert.Single(await db.Transactions.Where(t=>t.Kind==MovementKind.Expense).ToListAsync());
    }
    [Fact] public async Task MissingDetailsAndConflictingSourcesDoNotCreateAnExpense()
    {
        var document=PurchaseDocument("email:incomplete");document.Date=null;
        var id=Assert.Single(await Imports.StageEvidenceAsync([document]));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmEvidenceMovementAsync(id,1,account,expense,true));
        await Imports.StageEvidenceAsync([PurchaseDocument("image:complete")]);
        await Imports.StageEvidenceAsync([PurchaseDocument("email:conflicting-total",amount:11)]);
        var purchase=Assert.Single(await Imports.EvidenceAsync());
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ConfirmEvidenceMovementAsync(id,purchase.Version,account,expense,true));
        await using var db=Open();Assert.Empty(await db.Transactions.Where(t=>t.Kind==MovementKind.Expense).ToListAsync());
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImageEmailAndBankPaymentDescribeOnePurchaseRegardlessOfArrivalOrder(bool bankFirst)
    {
        long transaction=0;
        if(bankFirst)transaction=await Finance.SaveMovementAsync(new(){Date=new(2025,1,2),Kind=MovementKind.Expense,AccountId=account,CategoryId=expense,Amount=10,Description="Compra AMZNMktpES"});
        var image=PurchaseDocument("image:amazon:1");var email=PurchaseDocument("email:outlook:message-1");email.Details="Artigos e referência conservados do email.";
        var id=Assert.Single(await Imports.StageEvidenceAsync([bankFirst?email:image]));
        Assert.Equal(id,Assert.Single(await Imports.StageEvidenceAsync([bankFirst?image:email])));
        if(!bankFirst)transaction=await Finance.SaveMovementAsync(new(){Date=new(2025,1,2),Kind=MovementKind.Expense,AccountId=account,CategoryId=expense,Amount=10,Description="Compra AMZNMktpES"});
        Assert.Equal(transaction,Assert.Single((await Imports.EvidenceMatchesAsync())[id]).TransactionId);
        await Imports.LinkEvidenceAsync(id,(await Imports.EvidenceAsync()).Single().Version,transaction);
        Assert.Equal(id,Assert.Single(await Imports.StageEvidenceAsync([email])));
        var purchase=Assert.Single(await Imports.EvidenceAsync());Assert.Equal(transaction,purchase.TransactionId);
        Assert.Equal(2,(await Imports.EvidenceSourcesAsync()).Count);
        await using var db=Open();Assert.Equal(3,await db.Transactions.CountAsync()); // Two opening balances, one expense.
        Assert.Equal("Compra AMZNMktpES",(await db.Transactions.SingleAsync(t=>t.Id==transaction)).Description);
    }
    [Fact] public async Task DifferentOrdersWithSameAmountAndDateStaySeparateAndCannotReuseOnePayment()
    {
        var ids=await Imports.StageEvidenceAsync([PurchaseDocument("image:first","order-111"),PurchaseDocument("email:second","order-222")]);
        Assert.NotEqual(ids[0],ids[1]);
        var transaction=await Finance.SaveMovementAsync(new(){Date=new(2025,1,2),Kind=MovementKind.Expense,AccountId=account,CategoryId=expense,Amount=10,Description="AMAZON"});
        await Imports.LinkEvidenceAsync(ids[0],1,transaction);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.LinkEvidenceAsync(ids[1],1,transaction));
        await Imports.UnlinkEvidenceAsync(ids[0],2);
        await Imports.LinkEvidenceAsync(ids[1],1,transaction);
    }
    [Fact] public async Task ConflictingEmailStaysWithOrderAndBlocksApprovalUntilSourceIsChosen()
    {
        var id=Assert.Single(await Imports.StageEvidenceAsync([PurchaseDocument("image:original")]));
        await Imports.ApproveEvidenceAsync(id,1,account,expense,true);
        Assert.Equal(id,Assert.Single(await Imports.StageEvidenceAsync([PurchaseDocument("email:conflict",amount:11)])));
        var purchase=Assert.Single(await Imports.EvidenceAsync());Assert.True(purchase.HasSourceConflict);Assert.Equal(Money.ToUnits(10),purchase.Units);
        Assert.Empty(await Imports.ReferencesAsync(account));Assert.Empty((await Imports.EvidenceMatchesAsync())[id]);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.ApproveEvidenceAsync(id,purchase.Version,account,expense,true));
        var email=(await Imports.EvidenceSourcesAsync()).Single(s=>s.SourceKey=="email:conflict");
        await Imports.ResolveEvidenceSourceAsync(id,purchase.Version,email.Id);
        purchase=Assert.Single(await Imports.EvidenceAsync());Assert.False(purchase.HasSourceConflict);Assert.Equal(Money.ToUnits(11),purchase.Units);
        await Imports.ApproveEvidenceAsync(id,purchase.Version,account,expense,true);
        Assert.Equal(Money.ToUnits(11),Assert.Single(await Imports.ReferencesAsync(account)).AmountUnits);
        Assert.Equal(2,(await Imports.EvidenceSourcesAsync()).Count);
    }
    [Fact] public async Task LinkingRejectsWrongAmountAndVoidedPaymentsWithoutWritingLedger()
    {
        var id=Assert.Single(await Imports.StageEvidenceAsync([PurchaseDocument("image:validate")]));
        var transaction=await Finance.SaveMovementAsync(new(){Date=new(2025,1,2),Kind=MovementKind.Expense,AccountId=account,CategoryId=expense,Amount=11,Description="AMAZON"});
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.LinkEvidenceAsync(id,1,transaction));
        await using var db=Open();var t=await db.Transactions.SingleAsync(t=>t.Id==transaction);t.Status="Voided";await db.SaveChangesAsync();
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Imports.LinkEvidenceAsync(id,1,transaction));
        Assert.Null(Assert.Single(await Imports.EvidenceAsync()).TransactionId);
    }
    [Fact] public async Task GoldPetBankDescriptorMatchesGoldpetOrderAndSpacedEmailAliasKeepsOnePurchase()
    {
        var id=Assert.Single(await Imports.StageEvidenceAsync([new(){SourceKey="email:goldpet:1",Source="Email Goldpet",Channel="Goldpet",Merchant="Goldpet",OrderReference="order-123",Date=new(2025,1,2),Units=100000,Description="Ração e antiparasitário",CategoryId=expense}]));
        var tx=await Imports.ConfirmEvidenceMovementAsync(id,1,account,expense,true);
        var batch=Assert.Single(await Imports.StageAsync("goldpet.pdf",SyntheticPdf("gold-pet",firstDescription:"Compra GOLD PET",continuation:"Loja")));
        var match=Assert.Single((await Imports.PreviewAsync(batch)).Duplicates[1]);Assert.True(match.StrongMatch);Assert.Equal(tx,match.TransactionId);
        Assert.Equal(id,Assert.Single(await Imports.StageEvidenceAsync([new(){SourceKey="email:goldpet:paid",Source="Email Gold Pet pago",Channel="Gold Pet",Merchant="Gold Pet",OrderReference="order-123",Date=new(2025,1,2),Units=100000,Description="Pagamento confirmado"}])));
        Assert.Single(await Imports.EvidenceAsync());
    }
    internal static byte[] SyntheticPdf(string marker = "synthetic", string closing = "1.040,00", string currency = "Euros", string reference = "1111 2222 3333", string product = "CONTA TESTE", bool previousYear = false, string firstDescription = "Compra", string continuation = "Loja de teste", string secondDescription = "Salario", bool repeatedPurchase = false)
    {
        static string Text(int x, int y, string text) => $"BT /F1 8 Tf {x} {y} Td ({text}) Tj ET\n";
        var content = Text(30, 760, $"{product} no {reference} de 01.01.2025 a 31.01.2025") + Text(30, 730, "Data") + Text(160, 730, "Descritivo") + Text(345, 730, "Debito") + Text(415, 730, "Credito") + Text(485, 730, "Saldo (" + currency + ")") +
            Text(27, 700, "01.01.25") + Text(103, 700, "SALDO ANTERIOR") + Text(500, 700, "1.000,00") +
            Text(27, 680, "02.01.25") + Text(65, 680, "02.01.25") + Text(103, 680, firstDescription) + Text(350, 680, "10,00") + Text(500, 680, "990,00") + Text(103, 664, continuation) +
            Text(27, 645, "03.01.25") + Text(65, 645, "03.01.25") + Text(103, 645, secondDescription) + Text(420, 645, "50,00") + Text(500, 645, "1.040,00") +
            Text(103, 620, "TOTAL") + Text(350, 620, "10,00") + Text(420, 620, "50,00") + Text(27, 600, "31.01.25") + Text(103, 600, "SALDO CONTABILISTICO") + Text(500, 600, closing);
        if (repeatedPurchase) content = content.Replace(Text(27, 645, "03.01.25"), Text(27, 645, "02.01.25"))
            .Replace(Text(65, 645, "03.01.25"), Text(65, 645, "02.01.25"))
            .Replace(Text(420, 645, "50,00"), Text(350, 645, "10,00"))
            .Replace(Text(500, 645, "1.040,00"), Text(500, 645, "980,00"))
            .Replace(Text(350, 620, "10,00"), Text(350, 620, "20,00"))
            .Replace(Text(420, 620, "50,00"), Text(420, 620, "0,00"))
            .Replace(Text(500, 600, closing), Text(500, 600, "980,00"));
        if (previousYear) content = content.Replace("2025", "2024").Replace(".25", ".24");
        var objects = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream" };
        var pdf = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString())); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = Encoding.ASCII.GetByteCount(pdf.ToString()); pdf.Append($"xref\n0 6\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        pdf.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n%{marker}"); return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
