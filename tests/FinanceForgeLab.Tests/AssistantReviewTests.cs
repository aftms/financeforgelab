using System.Text.Json;
using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed partial class AssistantTests
{
    [Fact]public async Task LargeReviewContinuesBeyondEightRoundsAndKeepsOnlyBaselineAndLatestSnapshot()
    {
        var id=await Service.StartScanAsync("review");var worker=Worker(new FakeGateway{Response=Result("continue")});
        for(var i=0;i<9;i++)await worker.TickAsync();
        var detail=await Service.DetailAsync(id);Assert.Equal("Queued",detail.Request.Status);Assert.Equal(10,detail.Request.Round);
        Assert.Equal(2,detail.Operations.Count(o=>o.Type=="review_snapshot"));
        Assert.Equal(id,await Service.StartScanAsync("review"));
    }
    private async Task<long> UnclassifiedAsync()
    {
        var id=await new FinanceService(Open).SaveMovementAsync(new(){Date=new(2020,1,2),Kind=MovementKind.Expense,Amount=12,AccountId=1,CategoryId=1,Description="Café teste"});
        await using var db=Open();var p=await db.Postings.SingleAsync(p=>p.TransactionId==id);p.CategoryId=null;await db.SaveChangesAsync();return id;
    }

    [Fact]public async Task ReviewShortcutResumesAndEmptyReviewCompletesWithoutSourceCursor()
    {
        var id=await Service.StartScanAsync("review");Assert.Equal(id,await Service.StartScanAsync("review"));
        await Worker(new FakeGateway()).TickAsync();var detail=await Service.DetailAsync(id);
        Assert.Equal("Completed",detail.Request.Status);Assert.Empty(AssistantService.LatestReview(detail.Operations)!.Issues);
        Assert.DoesNotContain(detail.Operations,o=>o.Type=="source_cursor");
        Assert.NotEqual(id,await Service.StartScanAsync("review"));
        Assert.Contains(await Service.SyncSummaryAsync(),s=>s.Kind=="review"&&s.LastCompleted!=null);
    }

    [Fact]public async Task ReviewCannotClaimCompletionWhileItemsHaveNotBeenAnalysed()
    {
        var movement=await UnclassifiedAsync();var id=await Service.StartScanAsync("review");var fake=new FakeGateway();
        await Worker(fake).TickAsync();var detail=await Service.DetailAsync(id);
        Assert.Equal("Partial",detail.Request.Status);Assert.Contains(JsonSerializer.Deserialize<List<AssistantQuestion>>(detail.Turns.Single().QuestionsJson,AssistantService.Json)!,q=>q.Text.Contains("sem análise documentada"));
        var snapshot=AssistantService.LatestReview(detail.Operations)!;Assert.Contains(snapshot.Issues,i=>i.Key==$"movement:{movement}:category");
        Assert.Contains("review-check.json",Assert.Single(fake.Prompts));
        Assert.True(File.Exists(Path.Combine(folder,"exchange","exchange",id,"1","bank-entries.jsonl")));
        Assert.Equal(2,detail.Operations.Count(o=>o.Type=="review_snapshot"));
    }

    [Fact]public async Task ReviewReportsPersistedCorrectionAndRechecksWithoutDuplicatingMovement()
    {
        var movement=await UnclassifiedAsync();var id=await Service.StartScanAsync("review");
        var form=await new FinanceService(Open).MovementAsync(movement);
        var action=Action("fix-category","classify",new AssistantActions.ClassificationAction(movement,form.Version,1));
        await Worker(new FakeGateway{Response=Result(actions:[action])}).TickAsync();
        var detail=await Service.DetailAsync(id);Assert.Equal("Completed",detail.Request.Status);
        Assert.Empty(AssistantService.LatestReview(detail.Operations)!.Issues);
        var baseline=JsonSerializer.Deserialize<AssistantActionReceipt>(detail.Operations.Single(o=>o.Key=="system:review:baseline").ResultJson,AssistantService.Json)!.Review!;
        Assert.Contains(baseline.Issues,i=>i.Key==$"movement:{movement}:category");
        Assert.Single(detail.Operations,o=>o.Type=="classify"&&o.Status=="Completed");
        await Actions.ExecuteAsync(id,1,action);
        await using var db=Open();Assert.Single(await db.Transactions.ToListAsync());Assert.Equal(1,(await db.Postings.SingleAsync()).CategoryId);
    }

    [Fact]public async Task ReviewNotesPreserveUnresolvedProblemsAndRejectInventedKeys()
    {
        var movement=await UnclassifiedAsync();var id=await Service.StartScanAsync("review");
        var note=Action("unresolved","review_note",new AssistantActions.ReviewNotesAction([new($"movement:{movement}:category","Descrição histórica insuficiente; não há comprovativo. Pedir identificação ao titular.")]));
        await Worker(new FakeGateway{Response=Result(actions:[note])}).TickAsync();var detail=await Service.DetailAsync(id);
        Assert.Equal("Completed",detail.Request.Status);Assert.Single(AssistantService.LatestReview(detail.Operations)!.Issues);
        Assert.Single(AssistantService.ReviewNotes(detail.Operations));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Actions.ExecuteAsync(id,1,Action("fake","review_note",new AssistantActions.ReviewNotesAction([new("not-real","Conferido")]))));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Actions.ExecuteAsync(id,1,Action("system:review:current","classify",new{})));
        await using var db=Open();Assert.Null((await db.Postings.SingleAsync()).CategoryId);
    }

    [Fact]public async Task ReviewInventorySeparatesAccountsAndExemptsBenefitsSplitAndWalletTransfers()
    {
        var finance=new FinanceService(Open);
        foreach(var account in new long[]{1,2})await finance.SaveMovementAsync(new(){Date=new(2026,10,1),Kind=MovementKind.Expense,Amount=12,AccountId=account,CategoryId=1,Description="Mesmo comerciante"});
        var transfer=await finance.SaveMovementAsync(new(){Date=new(2026,10,2),Kind=MovementKind.Transfer,Amount=50,AccountId=1,DestinationAccountId=2,Description="Carregamento"});
        await using(var db=Open())
        {
            foreach(var method in new[]{"Benefits","Split","TopUp"})db.Add(new PurchaseEvidence{HouseholdId=1,SourceKey=method,PaymentMethod=method,Date=new(2026,10,2),Units=500000,FinancialUnits=method=="TopUp"?500000:0,TransactionId=method=="TopUp"?transfer:null,AccountId=method=="TopUp"?2:null});
            await db.SaveChangesAsync();
        }
        Assert.Empty((await Service.InspectReviewAsync()).Issues);
        await finance.SaveMovementAsync(new(){Date=new(2026,10,1),Kind=MovementKind.Expense,Amount=12,AccountId=1,CategoryId=1,Description="Mesmo comerciante"});
        var snapshot=await Service.InspectReviewAsync();Assert.Single(snapshot.Issues);Assert.Equal("Possível duplicado",snapshot.Issues.Single().Kind);
    }

    [Fact]public async Task ReviewFindsInvalidAllocationsAndBrokenBankLinkWithoutChangingData()
    {
        var ids=await Imports.StageAsync("statement.pdf",BankImportTests.SyntheticPdf());
        await using(var db=Open())
        {
            db.Add(new PurchaseEvidence{HouseholdId=1,SourceKey="bad",Merchant="Teste",Date=new(2026,1,1),Units=120000,CategoryAllocationsJson="[{\"CategoryId\":1,\"Units\":100000}]"});await db.SaveChangesAsync();
        }
        var snapshot=await Service.InspectReviewAsync();
        Assert.Contains(snapshot.Issues,i=>i.Kind=="Repartição incoerente");Assert.Contains(snapshot.Issues,i=>i.Key.StartsWith($"bank:{ids[0]}:row:"));
        await using var check=Open();Assert.Empty(await check.BankImportEntries.ToListAsync());
    }

    [Fact]public async Task ExistingEvidenceCanBeConfirmedAndReplayedWithoutAddingASourceOrExpenseTwice()
    {
        var e=(await Imports.StageEvidenceAsync([new(){SourceKey="saved-email",Source="Email",Channel="Teste",Merchant="Teste",OrderReference="XYZ",Date=new(2026,9,1),Units=120000,CategoryId=1,Description="Alimentos"}])).Single();
        var id=await Service.StartScanAsync("review");
        await using var db=Open();var evidence=await db.PurchaseEvidence.AsNoTracking().SingleAsync();
        var action=Action("confirm-evidence","evidence",new AssistantActions.EvidenceAction(e,evidence.Version,1,1,null));
        var first=await Actions.ExecuteAsync(id,1,action);var again=await Actions.ExecuteAsync(id,1,action);
        Assert.Equal(first.TransactionIds,again.TransactionIds);Assert.Single(await db.Transactions.ToListAsync());Assert.Single(await db.PurchaseEvidenceSources.ToListAsync());
    }
}
