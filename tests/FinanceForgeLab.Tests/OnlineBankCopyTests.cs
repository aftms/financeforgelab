using System.Text;
using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class OnlineBankCopyTests
{
    static byte[] Copy(string reference="1111 2222 3333",string amount="50,00",string description="SALARIO") => Encoding.UTF8.GetBytes($"Conta {reference} (16-01-2025 21:50:39)\n03-01-2025\t03-01-2025\t{description}\t+ {amount} €\t+ 1.040,00 €\nVer Detalhes\n02-01-2025\t02-01-2025\tCOMPRA Loja de teste\t- 10,00 €\t+ 990,00 €\n");
    [Fact] public void ReaderKeepsSignsDatesSourceLinesAndConsecutiveBalances()
    {
        var read=OnlineBankStatementReader.Read(Copy());Assert.True(read.Valid);var section=Assert.Single(read.Accounts);
        Assert.Equal("111122223333",section.Reference);Assert.Equal(10_000_000,section.OpeningUnits);Assert.Equal(10_400_000,section.ClosingUnits);
        Assert.Equal(500_000,section.Rows[0].Units);Assert.Equal(-100_000,section.Rows[1].Units);Assert.Equal(4,section.Rows[1].Line);
        Assert.Equal(OnlineBankStatementReader.Version,read.ReaderVersion);Assert.Equal(0,section.Rows[0].Page);
        Assert.False(OnlineBankStatementReader.Read(Copy(amount:"50,01")).Valid);
    }
    [Fact] public void ReaderRejectsSkippedMalformedRowsAndWrongDateOrder()
    {
        Assert.Throws<FinanceRuleException>(()=>OnlineBankStatementReader.Read(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Copy())+"unread row\n")));
        Assert.Throws<FinanceRuleException>(()=>OnlineBankStatementReader.Read(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Copy()).Replace("03-01-2025\t","17-01-2025\t"))));
    }
    [Fact] public async Task CopyReusesKnownAccountAndFinancialRecordsAndRetainsTextWithoutAnOpeningBalance()
    {
        var folder=Path.Combine(Path.GetTempPath(),"ffl-online-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        FinanceDb Open()=>FinanceDb.Open(Path.Combine(folder,"test.sqlite"));
        try
        {
            await using(var db=Open()){await db.Database.MigrateAsync();db.Add(new Household());await db.SaveChangesAsync();}
            var service=new BankImportService(Open,Path.Combine(folder,"archive"));
            var pdfId=Assert.Single(await service.StageAsync("original.pdf",BankImportTests.SyntheticPdf()));var pdf=await service.PreviewAsync(pdfId);var account=pdf.Import.AccountId!.Value;
            long category;
            await using(var db=Open()){var c=new Category{HouseholdId=1,Kind=MovementKind.Expense,Name="Compras"};db.Add(c);await db.SaveChangesAsync();category=c.Id;}
            var review=await service.PrepareReviewAsync(pdfId);var purchase=review.Rows.Single(r=>r.Ordinal==1);purchase.CategoryId=category;
            await service.ConfirmRowsAsync(review,[1]);
            var copyId=Assert.Single(await service.StageOnlineCopyAsync("Cópia.txt",Copy()));var copy=await service.PreviewAsync(copyId);
            Assert.Equal(account,copy.Import.AccountId);Assert.True(Assert.Single(BankDocumentSummary.Group(await service.ListAsync()),d=>d.IsOnlineCopy).IsOnlineCopy);
            var duplicate=Assert.Single(copy.Duplicates[2]);Assert.True(duplicate.StrongMatch);
            var copyReview=await service.PrepareReviewAsync(copyId);Assert.Equal("Link",copyReview.Rows[1].Decision);await service.ConfirmRowsAsync(copyReview,[2]);
            Assert.Equal(copyId,Assert.Single(await service.StageOnlineCopyAsync("Again.txt",Copy())));
            Assert.Equal(Copy(),(await service.OriginalAsync(copyId)).Content);Assert.EndsWith(".txt",(await service.OriginalAsync(copyId)).Name);
            await Assert.ThrowsAsync<FinanceRuleException>(()=>service.StageOnlineCopyAsync("unknown.txt",Copy(reference:"9999 8888 7777")));
            await Assert.ThrowsAsync<FinanceRuleException>(()=>service.StageOnlineCopyAsync("bad.txt",Copy(amount:"50,01")));
            await using var check=Open();Assert.Equal(2,await check.Transactions.CountAsync());Assert.Equal(1,await check.Transactions.CountAsync(t=>t.Kind==MovementKind.Opening));Assert.Equal(2,await check.BankImportEntries.CountAsync());
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(folder,true);}
    }
    [Fact] public async Task TwoAccountCopiesLinkOneTransferWithBothSidesClassifiedAndNoIncomeOrExpense()
    {
        var folder=Path.Combine(Path.GetTempPath(),"ffl-pair-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        FinanceDb Open()=>FinanceDb.Open(Path.Combine(folder,"test.sqlite"));
        try
        {
            await using(var db=Open()){await db.Database.MigrateAsync();db.Add(new Household());await db.SaveChangesAsync();}
            var service=new BankImportService(Open,Path.Combine(folder,"archive"));
            await service.StageAsync("one.pdf",BankImportTests.SyntheticPdf());
            await service.StageAsync("two.pdf",BankImportTests.SyntheticPdf("second",reference:"4444 5555 6666"));
            byte[] Source(string reference,string description,string amount,string balance)=>Encoding.UTF8.GetBytes($"Conta {reference} (16-01-2025 21:50:39)\n16-01-2025\t16-01-2025\t{description}\t{amount} €\t{balance} €\n");
            var first=Assert.Single(await service.StageOnlineCopyAsync("one.txt",Source("1111 2222 3333","TRF DE SUZANA","+ 18,00","+ 1.058,00")));
            var second=Assert.Single(await service.StageOnlineCopyAsync("two.txt",Source("4444 5555 6666","TRF P/ ANTONIO","- 18,00","+ 1.022,00")));
            var a=await service.PrepareReviewAsync(first);var b=await service.PrepareReviewAsync(second);
            a.Rows[0].Kind=MovementKind.Transfer;a.Rows[0].CounterpartyAccountId=b.AccountId;await service.ConfirmRowsAsync(a,[1]);
            var match=Assert.Single((await service.PreviewAsync(second)).Duplicates[1]);
            b.Rows[0].Decision="Link";b.Rows[0].ExistingTransactionId=match.TransactionId;b.Rows[0].ExistingVersion=match.Version;
            await service.ConfirmRowsAsync(b,[1]);
            var saved=Assert.Single((await service.PrepareReviewAsync(second)).Rows);Assert.Equal(MovementKind.Transfer,saved.Kind);Assert.Equal(a.AccountId,saved.CounterpartyAccountId);Assert.Null(saved.CategoryId);
            await using var check=Open();var transaction=Assert.Single(await check.Transactions.Where(t=>t.Kind!=MovementKind.Opening).ToListAsync());Assert.Equal(MovementKind.Transfer,transaction.Kind);
            var posts=await check.Postings.Where(p=>p.TransactionId==transaction.Id).ToListAsync();Assert.Equal(2,posts.Count);Assert.Equal(0,posts.Sum(p=>p.AmountUnits));Assert.Equal(2,await check.BankImportEntries.CountAsync());
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(folder,true);}
    }
}
