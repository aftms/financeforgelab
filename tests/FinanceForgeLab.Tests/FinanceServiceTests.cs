using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class FinanceServiceTests : IAsyncLifetime
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ffl-service-"+Guid.NewGuid().ToString("N"));
    private string PathDb => Path.Combine(folder,"test.sqlite");
    private FinanceDb Open()=>FinanceDb.Open(PathDb);
    private FinanceService Service=>new(Open);
    private long expense, income;
    private readonly DateOnly day=new(2025,1,1);
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(folder); await using var db=Open();await db.Database.MigrateAsync();
        var h=new Household();db.Add(h);await db.SaveChangesAsync();
        var a=new Category{HouseholdId=h.Id,Kind=MovementKind.Expense,Name="Casa"};var b=new Category{HouseholdId=h.Id,Kind=MovementKind.Income,Name="Trabalho"};
        db.AddRange(a,b);await db.SaveChangesAsync();expense=a.Id;income=b.Id;
    }
    public Task DisposeAsync(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(folder,true);return Task.CompletedTask;}
    private Task<long> Account(string name,decimal opening=0)=>Service.SaveAccountAsync(new(){Name=name,OpeningBalance=opening,BalanceStartDate=day});
    private MovementForm Movement(long account,decimal amount=20,MovementKind kind=MovementKind.Expense)=>new(){AccountId=account,Amount=amount,Kind=kind,Date=day.AddDays(1),Description="Teste",CategoryId=kind==MovementKind.Expense?expense:income};

    [Fact] public async Task ProvisionalFilterIncludesEveryCategoryAndInvoicePartWithoutDuplicatingTotals()
    {
        var account=await Account("Conta");var other=await Account("Outra");
        var unknown=await Service.SaveCategoryAsync(new(){Name="Por classificar",ParentId=expense,Kind=MovementKind.Expense});
        var root=await Service.SaveCategoryAsync(new(){Name="Diversos",Kind=MovementKind.Expense});
        var miscellaneous=await Service.SaveCategoryAsync(new(){Name="Por classificar",ParentId=root,Kind=MovementKind.Expense});
        var known=await Service.SaveCategoryAsync(new(){Name="Eletricidade",ParentId=expense,Kind=MovementKind.Expense});
        var first=Movement(account);first.CategoryId=unknown;var firstId=await Service.SaveMovementAsync(first);
        var second=Movement(account,30);second.CategoryId=miscellaneous;await Service.SaveMovementAsync(second);
        var third=Movement(account,40);third.CategoryId=known;await Service.SaveMovementAsync(third);
        await Service.SaveMovementAsync(new(){AccountId=account,DestinationAccountId=other,Amount=5,Date=day,Kind=MovementKind.Transfer,Description="Transferência"});
        var imports=new BankImportService(Open,Path.Combine(folder,"archive"));
        var eid=(await imports.StageEvidenceAsync([new(){SourceKey="mixed-provisional",Source="Fatura de teste",Channel="Loja",OrderReference="mixed-123",Date=day,Units=1000000,Description="Compra mista"}])).Single();
        await imports.SaveEvidenceAllocationsAsync(eid,1,[new(unknown,300000),new(known,700000)]);
        var e=(await imports.EvidenceAsync()).Single();await imports.ConfirmEvidenceMovementAsync(eid,e.Version,account,known,true);
        var result=await Service.MovementsAsync(new(){CategoryId=0});
        Assert.Equal(3,result.Total);Assert.Equal(-800000,result.Rows.Sum(r=>r.Units));Assert.All(result.Rows,r=>Assert.Equal(MovementKind.Expense,r.Kind));
        var before=(await Service.DashboardAsync(2025)).ExpenseUnits;Assert.Equal(1900000,before);
        var edit=await Service.MovementAsync(firstId);edit.CategoryId=known;await Service.SaveMovementAsync(edit);
        Assert.Equal(2,(await Service.MovementsAsync(new(){CategoryId=0})).Total);
        Assert.Equal(before,(await Service.DashboardAsync(2025)).ExpenseUnits);
        Assert.Equal(1,(await Service.MovementsAsync(new(){CategoryId=miscellaneous})).Total);
    }

    [Theory]
    [InlineData(MovementSort.Date)]
    [InlineData(MovementSort.Description)]
    [InlineData(MovementSort.Category)]
    [InlineData(MovementSort.Account)]
    [InlineData(MovementSort.Kind)]
    [InlineData(MovementSort.Amount)]
    public async Task SortingCoversAllResultsBeforePaginationAndMatchesCsv(MovementSort sort)
    {
        var a = await Account("Alfa"); var b = await Account("Zeta");
        var seeded = new List<(long Id, MovementForm Form)>();
        for (var i = 0; i < 23; i++)
        {
            var form = Movement(i % 2 == 0 ? a : b, i + 1, i % 3 == 0 ? MovementKind.Income : MovementKind.Expense);
            form.Description = $"Registo {22 - i:00}"; form.Date = day.AddDays((i * 7) % 23 + 1);
            seeded.Add((await Service.SaveMovementAsync(form), form));
        }
        IComparable Key((long Id, MovementForm Form) x) => sort switch
        {
            MovementSort.Description => x.Form.Description,
            MovementSort.Category => x.Form.Kind == MovementKind.Income ? "Trabalho" : "Casa",
            MovementSort.Account => x.Form.AccountId == a ? "Alfa" : "Zeta",
            MovementSort.Kind => x.Form.Kind == MovementKind.Income ? "Receita" : "Despesa",
            MovementSort.Amount => x.Form.Kind == MovementKind.Income ? x.Form.Amount : -x.Form.Amount,
            _ => x.Form.Date
        };
        foreach (var descending in new[] { false, true })
        {
            var expected = (descending ? seeded.OrderByDescending(Key).ThenByDescending(x => x.Id) : seeded.OrderBy(Key).ThenBy(x => x.Id)).ToList();
            var filter = new MovementFilter { Sort = sort, Descending = descending, PageSize = 10 };
            var actual = new List<long>();
            for (var page = 1; page <= 3; page++)
            {
                filter.Page = page;
                var result = await Service.MovementsAsync(filter);
                Assert.Equal(23, result.Total); actual.AddRange(result.Rows.Select(x => x.Id));
            }
            Assert.Equal(expected.Select(x => x.Id), actual);
            var lines = (await Service.ExportCsvAsync(filter)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
            Assert.Equal(23, lines.Length);
            for (var i = 0; i < lines.Length; i++) Assert.Contains(expected[i].Form.Description, lines[i]);
        }
    }

    [Fact]public async Task DashboardShowsFoodEvenWhenItIsBelowTheEightLargestCategories()
    {
        var account=await Account("A");
        for(var i=0;i<9;i++)
        {
            var category=await Service.SaveCategoryAsync(new(){Name=i==8?"Alimentação":"Categoria "+i,Kind=MovementKind.Expense});
            var form=Movement(account,100-i*10);form.CategoryId=category;await Service.SaveMovementAsync(form);
        }
        var dashboard=await Service.DashboardAsync(2025);
        Assert.Equal(9,dashboard.Expenses.Count);Assert.Equal("Alimentação",dashboard.Expenses[^1].Name);
        Assert.Equal(dashboard.ExpenseUnits,dashboard.Expenses.Sum(c=>c.Units));
    }
    [Fact]public async Task DashboardDefaultsToLatestExpensesAndIncomeInsteadOfAccountOpenings()
    {
        var a=await Account("A",100);
        var movement=Movement(a);movement.Date=new(2019,12,5);await Service.SaveMovementAsync(movement);
        await Service.SaveAccountAsync(new(){Name="Newer account",OpeningBalance=200,BalanceStartDate=new(2026,1,1)});
        var result=await Service.DashboardAsync(null);
        Assert.Equal(2019,result.Year);Assert.Equal(200_000,result.ExpenseUnits);Assert.Equal(1,result.Count);
        Assert.Equal(new[]{2019},result.Years);
        var empty=await Service.DashboardAsync(2026);Assert.Contains(2026,empty.Years);Assert.Equal(0,empty.Count);
    }
    [Fact]public async Task TransfersAreAtomicBalancedAndExcludedFromFinancialTotals()
    {
        var a=await Account("A",100);var b=await Account("B");
        await Service.SaveMovementAsync(Movement(a));await Service.SaveMovementAsync(Movement(a,50,MovementKind.Income));
        var transfer=Movement(a,10,MovementKind.Transfer);transfer.DestinationAccountId=b;await Service.SaveMovementAsync(transfer);
        var accounts=await Service.AccountsAsync();Assert.Equal(1_200_000,accounts.Single(x=>x.Account.Id==a).BalanceUnits);Assert.Equal(100_000,accounts.Single(x=>x.Account.Id==b).BalanceUnits);
        var dashboard=await Service.DashboardAsync(2025);Assert.Equal(500_000,dashboard.IncomeUnits);Assert.Equal(200_000,dashboard.ExpenseUnits);Assert.Equal(2,dashboard.Count);
        var page=await Service.MovementsAsync(new(){AccountId=b});Assert.Single(page.Rows);Assert.Equal(MovementKind.Transfer,page.Rows[0].Kind);
    }
    [Fact]public async Task RepeatedSubmissionsDoNotDuplicateMovementsOrAccounts()
    {
        var form=new AccountForm{Name="A",BalanceStartDate=day};var a=await Service.SaveAccountAsync(form);Assert.Equal(a,await Service.SaveAccountAsync(form));
        var movement=Movement(a);var id=await Service.SaveMovementAsync(movement);Assert.Equal(id,await Service.SaveMovementAsync(movement));
        await using var db=Open();Assert.Equal(1,await db.Transactions.CountAsync());Assert.Equal(1,await db.Accounts.CountAsync());
        movement.Amount=30;await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.SaveMovementAsync(movement));
    }
    [Fact]public async Task InvalidTransferWritesNothing()
    {
        var a=await Account("A",100);var transfer=Movement(a,10,MovementKind.Transfer);transfer.DestinationAccountId=a;
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.SaveMovementAsync(transfer));
        await using var db=Open();Assert.Equal(1,await db.Transactions.CountAsync());Assert.Equal(1,await db.Postings.CountAsync());
    }
    [Fact]public async Task TransferEditsAndVoidsAffectBothAccountsTogether()
    {
        var a=await Account("A",100);var b=await Account("B");var transfer=Movement(a,10,MovementKind.Transfer);transfer.DestinationAccountId=b;
        var id=await Service.SaveMovementAsync(transfer);var edit=await Service.MovementAsync(id);edit.Amount=25;await Service.SaveMovementAsync(edit);
        var balances=await Service.AccountsAsync();Assert.Equal(750_000,balances.Single(x=>x.Account.Id==a).BalanceUnits);Assert.Equal(250_000,balances.Single(x=>x.Account.Id==b).BalanceUnits);
        var current=await Service.MovementAsync(id);await Service.VoidMovementAsync(id,current.Version,Guid.NewGuid());
        balances=await Service.AccountsAsync();Assert.Equal(1_000_000,balances.Single(x=>x.Account.Id==a).BalanceUnits);Assert.Equal(0,balances.Single(x=>x.Account.Id==b).BalanceUnits);
        await using var db=Open();Assert.Equal(2,await db.Postings.CountAsync(x=>x.TransactionId==id));Assert.True(await db.AuditChanges.AnyAsync(x=>x.Action=="Void"));
    }
    [Fact]public async Task HistoricalBeforeOpeningIsNotCountedTwice()
    {
        var a=await Account("A",100);var form=Movement(a,20);form.Date=day.AddDays(-10);await Service.SaveMovementAsync(form);
        var account=(await Service.AccountsAsync()).Single();Assert.Equal(1_000_000,account.BalanceUnits);Assert.Equal(1,account.HistoricalBeforeOpening);
        var year=await Service.DashboardAsync(2024);Assert.Equal(200_000,year.ExpenseUnits);
    }
    [Fact]public async Task StaleEditCannotOverwriteConcurrentChanges()
    {
        var a=await Account("A");var id=await Service.SaveMovementAsync(Movement(a));var first=await Service.MovementAsync(id);var second=await Service.MovementAsync(id);
        first.Amount=21;await Service.SaveMovementAsync(first);second.Amount=40;await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.SaveMovementAsync(second));
        Assert.Equal(21,(await Service.MovementAsync(id)).Amount);
    }
    [Fact]public async Task CategoryMismatchAndInactiveAccountsAreRejected()
    {
        var a=await Account("A");var movement=Movement(a);movement.CategoryId=income;
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.SaveMovementAsync(movement));
        var acct=(await Service.AccountsAsync()).Single().Account;await Service.SaveAccountAsync(new(){Id=acct.Id,Version=acct.Version,Name=acct.Name,Active=false,BalanceStartDate=day});
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.SaveMovementAsync(Movement(a)));
    }
    [Fact]public async Task ReclassifyingAUsedCategoryCannotRewriteItsNature()
    {
        var a=await Account("A");await Service.SaveMovementAsync(Movement(a));
        var cat=(await Service.CategoriesAsync()).Single(x=>x.Id==expense);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.SaveCategoryAsync(new(){Id=cat.Id,Version=cat.Version,Name=cat.Name,Kind=MovementKind.Income}));
    }
    [Fact]public async Task CsvUsesTheSameFiltersAndNeutralizesFormulaText()
    {
        var a=await Account("A");var first=Movement(a);first.Description="=HYPERLINK(\"bad\")";await Service.SaveMovementAsync(first);
        var second=Movement(a);second.Description="other";await Service.SaveMovementAsync(second);
        var filter=new MovementFilter{Search="HYPERLINK",AccountId=a};Assert.Single((await Service.MovementsAsync(filter)).Rows);
        var csv=await Service.ExportCsvAsync(filter);Assert.Contains("'=HYPERLINK",csv);Assert.DoesNotContain("other",csv);Assert.Contains("2025-01-02",csv);
    }
    [Fact]public async Task ArchivedCategoryReviewCountsOnlyConfirmedAndShrinksAfterReclassification()
    {
        var account=await Account("A");
        var first=await Service.SaveMovementAsync(Movement(account));
        var second=await Service.SaveMovementAsync(Movement(account));
        var discarded=await Service.SaveMovementAsync(Movement(account));
        var voidForm=await Service.MovementAsync(discarded);
        await Service.VoidMovementAsync(discarded,voidForm.Version,Guid.NewGuid());
        var category=(await Service.CategoriesAsync()).Single(x=>x.Id==expense);
        await Service.SaveCategoryAsync(new(){Id=category.Id,Version=category.Version,Name=category.Name,Kind=category.Kind,Active=false});
        var counts=await Service.InactiveCategoryUsageAsync();Assert.Equal(2,counts[expense]);
        var replacement=await Service.SaveCategoryAsync(new(){Name="Nova categoria",Kind=MovementKind.Expense});
        var edit=await Service.MovementAsync(first);edit.CategoryId=replacement;await Service.SaveMovementAsync(edit);
        counts=await Service.InactiveCategoryUsageAsync();Assert.Equal(1,counts[expense]);
        var filtered=await Service.MovementsAsync(new(){CategoryId=expense});Assert.Single(filtered.Rows);Assert.Equal(second,filtered.Rows[0].Id);
    }
    [Fact]public void EncryptedBackupsAuthenticatePasswordAndDetectTampering()
    {
        var data="SQLite synthetic"u8.ToArray();var encrypted=BackupCrypto.Encrypt(data,"test-password-123");Assert.Equal(data,BackupCrypto.Decrypt(encrypted,"test-password-123"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(()=>BackupCrypto.Decrypt(encrypted,"incorrect"));
        encrypted[^1]^=1;Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(()=>BackupCrypto.Decrypt(encrypted,"test-password-123"));
    }
    [Fact]public async Task ReviewCountsSeparateDocumentsFromRegisteredPaymentsWithoutChangingTotals()
    {
        var account=await Account("A");var linked=await Service.SaveMovementAsync(Movement(account));
        var voided=await Service.SaveMovementAsync(Movement(account));var edit=await Service.MovementAsync(voided);
        await Service.VoidMovementAsync(voided,edit.Version,Guid.NewGuid());
        await using(var db=Open())
        {
            var household=await db.Households.Select(h=>h.Id).SingleAsync();
            var payment=new FinancialTransaction{HouseholdId=household,Kind=MovementKind.Expense,Date=day,Description="Compra com duas parcelas"};
            db.Add(payment);await db.SaveChangesAsync();
            db.AddRange(new Posting{TransactionId=payment.Id,Date=day,CategoryId=expense,AmountUnits=-50_000},new Posting{TransactionId=payment.Id,Date=day,CategoryId=expense,AmountUnits=-150_000});
            PurchaseEvidence Evidence(string key)=>new(){HouseholdId=household,SourceKey=key,Date=day,Units=200_000};
            var pending=Evidence("pending");var old=Evidence("old");old.Date=day.AddYears(-1);
            var undated=Evidence("undated");undated.Date=null;
            var benefits=Evidence("benefits");benefits.PaymentMethod="Benefits";
            var split=Evidence("split");split.PaymentMethod="Split";
            var zero=Evidence("zero");zero.FinancialUnits=0;
            var topup=Evidence("topup");topup.PaymentMethod="TopUp";
            var done=Evidence("done");done.TransactionId=linked;
            var conflict=Evidence("conflict");conflict.TransactionId=payment.Id;conflict.HasSourceConflict=true;
            var broken=Evidence("broken");broken.TransactionId=voided;
            db.AddRange(pending,old,undated,benefits,split,zero,topup,done,conflict,broken);await db.SaveChangesAsync();
        }
        var summary=await Service.DashboardAsync(2025);
        Assert.Equal(0,summary.PendingBankRows);Assert.True(summary.HasReviewPending);
        Assert.Equal(4,summary.PendingPurchases);Assert.Equal(1,summary.UndatedPurchases);
        Assert.Equal(1,summary.UnassignedExpenses);Assert.Equal(400_000,summary.ExpenseUnits);
        Assert.Equal(6,(await Service.PendingPurchaseIdsAsync()).Count);
        var previous=await Service.DashboardAsync(2024);Assert.Equal(1,previous.PendingPurchases);Assert.Equal(0,previous.UnassignedExpenses);
    }
    [Fact]public async Task PortugueseSearchIgnoresCaseAndAccentsAndRemainsParameterized()
    {
        var a=await Account("Água");var form=Movement(a);form.Description="Água e AÇÃO";await Service.SaveMovementAsync(form);
        Assert.Single((await Service.MovementsAsync(new(){Search="agua e acao"})).Rows);
        Assert.Empty((await Service.MovementsAsync(new(){Search="' OR 1=1 --"})).Rows);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Account("água"));
    }
}
