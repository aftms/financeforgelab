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
    [Fact]public void EncryptedBackupsAuthenticatePasswordAndDetectTampering()
    {
        var data="SQLite synthetic"u8.ToArray();var encrypted=BackupCrypto.Encrypt(data,"test-password-123");Assert.Equal(data,BackupCrypto.Decrypt(encrypted,"test-password-123"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(()=>BackupCrypto.Decrypt(encrypted,"incorrect"));
        encrypted[^1]^=1;Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(()=>BackupCrypto.Decrypt(encrypted,"test-password-123"));
    }
    [Fact]public async Task PortugueseSearchIgnoresCaseAndAccentsAndRemainsParameterized()
    {
        var a=await Account("Água");var form=Movement(a);form.Description="Água e AÇÃO";await Service.SaveMovementAsync(form);
        Assert.Single((await Service.MovementsAsync(new(){Search="agua e acao"})).Rows);
        Assert.Empty((await Service.MovementsAsync(new(){Search="' OR 1=1 --"})).Rows);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Account("água"));
    }
}
