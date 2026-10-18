using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class ReimbursementTests : IAsyncLifetime
{
    private readonly string folder=Path.Combine(Path.GetTempPath(),"ffl-reimburse-"+Guid.NewGuid().ToString("N"));
    private FinanceDb Open()=>FinanceDb.Open(Path.Combine(folder,"test.sqlite"));
    private ReimbursementService Service=>new(Open);
    private long household,account,otherAccount,category;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(folder);await using var db=Open();await db.Database.MigrateAsync();
        var h=new Household();db.Add(h);await db.SaveChangesAsync();household=h.Id;
        var a=new Account{HouseholdId=household,Name="António"};var b=new Account{HouseholdId=household,Name="Suzana"};var c=new Category{HouseholdId=household,Name="Equipamento",Kind=MovementKind.Expense};
        db.AddRange(a,b,c);await db.SaveChangesAsync();account=a.Id;otherAccount=b.Id;category=c.Id;
    }
    public Task DisposeAsync(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(folder,true);return Task.CompletedTask;}
    private async Task<long> Movement(long units,DateOnly? date=null,long? accountId=null,string description="Associação Cultural e Desportiva da Mealhada")
    {
        await using var db=Open();var t=new FinancialTransaction{HouseholdId=household,Date=date??new(2025,1,1),Kind=MovementKind.Expense,Description=description,Origin="Manual"};
        db.Add(t);await db.SaveChangesAsync();db.Add(new Posting{TransactionId=t.Id,AccountId=accountId??account,CategoryId=category,Date=t.Date,AmountUnits=units});await db.SaveChangesAsync();return t.Id;
    }
    [Fact] public async Task TwoPurchasesRemainIndependentAndPartialReceiptsCannotBeUsedTwice()
    {
        var first=await Movement(-315000);var second=await Movement(-315000,new(2025,9,30));
        var one=await Service.RegisterAsync(first,"Associação Cultural e Desportiva da Mealhada");var two=await Service.RegisterAsync(second,"Associação Cultural e Desportiva da Mealhada");
        Assert.Equal(one,await Service.RegisterAsync(first,"Associação Cultural e Desportiva da Mealhada"));
        var receipt=await Movement(200000,new(2025,4,30));await Service.LinkAsync(one,1,receipt,200000);await Service.LinkAsync(one,1,receipt,200000);
        var rows=await Service.ListAsync();Assert.Equal(115000,rows.Single(r=>r.Claim.Id==one).RemainingUnits);Assert.Equal(315000,rows.Single(r=>r.Claim.Id==two).RemainingUnits);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.LinkAsync(two,1,receipt,200000));
        await using var db=Open();Assert.Equal(3,await db.Transactions.CountAsync());Assert.Single(await db.ReimbursementAllocations.ToListAsync());
    }
    [Fact] public async Task OtherPersonsAccountIsNotAReimbursementCandidateAndCannotBeLinked()
    {
        var expense=await Movement(-315000);var claim=await Service.RegisterAsync(expense,"Associação Cultural e Desportiva da Mealhada");
        var receipt=await Movement(315000,new(2025,2,1),otherAccount);
        Assert.Empty(await Service.CandidatesAsync(claim));await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.LinkAsync(claim,1,receipt,315000));
        Assert.Equal(315000,Assert.Single(await Service.ListAsync()).RemainingUnits);
    }
    [Fact] public async Task SplittingOneBankReceiptPreservesOneMovementAndLaterStatementLinksIt()
    {
        var imports=new BankImportService(Open,Path.Combine(folder,"pdfs"));
        var id=Assert.Single(await imports.StageAsync("same-period.pdf",BankImportTests.SyntheticPdf(secondDescription:"Trf De Associacao C R Mealhada")));
        var bankAccount=(await imports.PreviewAsync(id)).Import.AccountId!.Value;
        var one=await Service.RegisterAsync(await Movement(-315000,accountId:bankAccount),"Associação Cultural e Desportiva da Mealhada");
        var two=await Service.RegisterAsync(await Movement(-185000,accountId:bankAccount),"Associação Cultural e Desportiva da Mealhada");
        var candidate=Assert.Single(await Service.CandidatesAsync(one));Assert.Equal(id,candidate.ImportId);Assert.Equal(500000,candidate.Units);
        var transaction=await Service.LinkStatementAsync(one,1,id,2,315000);
        Assert.Equal(transaction,await Service.LinkStatementAsync(one,1,id,2,315000));
        Assert.Equal(transaction,await Service.LinkStatementAsync(two,1,id,2,185000));
        Assert.All(await Service.ListAsync(),r=>Assert.Equal(0,r.RemainingUnits));Assert.Empty(await Service.CandidatesAsync(one));
        var review=await imports.PrepareReviewAsync(id);Assert.Equal("Link",review.Rows[1].Decision);Assert.Equal(transaction,review.Rows[1].ExistingTransactionId);
        review.Rows[0].Decision="Exclude";var result=await imports.ConfirmAsync(review);Assert.Equal(0,result.Created);Assert.Equal(1,result.Linked);
        await using var db=Open();Assert.Single(await db.Transactions.Where(t=>t.Origin=="Reimbursement").ToListAsync());
        Assert.Equal(2,await db.ReimbursementAllocations.CountAsync());
    }
    [Fact] public async Task VoidedReceiptReopensTheClaimAndUnlinkKeepsTheFinancialRecord()
    {
        var claim=await Service.RegisterAsync(await Movement(-315000),"Mealhada");var receipt=await Movement(315000,new(2025,2,1));
        await Service.LinkAsync(claim,1,receipt,315000);
        await using(var db=Open()){(await db.Transactions.SingleAsync(t=>t.Id==receipt)).Status="Voided";await db.SaveChangesAsync();}
        Assert.Equal(315000,Assert.Single(await Service.ListAsync()).RemainingUnits);
        await using(var db=Open()){var allocation=await db.ReimbursementAllocations.SingleAsync();await Service.UnlinkAsync(claim,2,allocation.Id);}
        await using(var db=Open()){Assert.Empty(await db.ReimbursementAllocations.ToListAsync());Assert.NotNull(await db.Transactions.FindAsync(receipt));}
    }
    [Fact] public async Task OverpaymentAndChangedExpenseAreRejectedAtomically()
    {
        var expense=await Movement(-315000);var claim=await Service.RegisterAsync(expense,"Mealhada");var receipt=await Movement(630000,new(2025,2,1));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.LinkAsync(claim,1,receipt,630000));
        await using(var db=Open()){(await db.Postings.SingleAsync(p=>p.TransactionId==expense)).AmountUnits=-300000;await db.SaveChangesAsync();}
        Assert.True(Assert.Single(await Service.ListAsync()).NeedsReview);await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.LinkAsync(claim,1,receipt,300000));
        await using(var db=Open())Assert.Empty(await db.ReimbursementAllocations.ToListAsync());
    }
    [Fact] public async Task ChangingReceiptToAnotherPersonsAccountReopensAndFlagsTheClaim()
    {
        var claim=await Service.RegisterAsync(await Movement(-315000),"Mealhada");var receipt=await Movement(315000,new(2025,2,1));
        await Service.LinkAsync(claim,1,receipt,315000);
        await using(var db=Open()){(await db.Postings.SingleAsync(p=>p.TransactionId==receipt)).AccountId=otherAccount;await db.SaveChangesAsync();}
        var row=Assert.Single(await Service.ListAsync());Assert.True(row.NeedsReview);Assert.Equal(315000,row.RemainingUnits);Assert.False(Assert.Single(row.Receipts).Valid);
    }
    [Fact] public async Task RejectedStatementAllocationRollsBackItsNewFinancialReceipt()
    {
        var imports=new BankImportService(Open,Path.Combine(folder,"pdfs"));
        var id=Assert.Single(await imports.StageAsync("refund.pdf",BankImportTests.SyntheticPdf(secondDescription:"Trf De Associacao C R Mealhada")));
        var bankAccount=(await imports.PreviewAsync(id)).Import.AccountId!.Value;
        var claim=await Service.RegisterAsync(await Movement(-315000,accountId:bankAccount),"Mealhada");
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.LinkStatementAsync(claim,1,id,2,630000));
        await using var db=Open();Assert.Empty(await db.Transactions.Where(t=>t.Origin=="Reimbursement").ToListAsync());Assert.Empty(await db.ReimbursementAllocations.ToListAsync());Assert.Empty(await db.Commands.ToListAsync());
    }
}
