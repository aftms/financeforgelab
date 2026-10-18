using FinanceForgeLab.Infrastructure;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class RevolutStatementTests
{
    private static BankPdfLine Line(int number,string text,params string[] cells)=>new(1,number,text,cells.Length==0?["","","","","",""]:cells);
    private static List<BankPdfLine> Example()=>[
        Line(1,"E U R S ta te m e n t"),Line(2,"Revolut Bank UAB"),Line(3,"EXAMPLE ACCOUNT HOLDER"),Line(4,"Address IBAN LT000000000000000000"),
        Line(5,"Account (Current Account) €0.00 €23.09 €120.00 €96.91"),Line(6,"Account transactions from Septem ber 1, 2025 to January 30, 2026"),
        Line(7,"Sep 11, 2025 Payment from EXAMPLE €120.00 €120.00","Sep 1","1, 2025","Payment from EXAMPLE","","€120.00","€120.00"),
        Line(8,"Reference: Own account"),
        Line(9,"Sep 14, 2025 Cafe €23.09 €96.91","Sep 1","4, 2025","Cafe","€23.09","","€96.91"),
        Line(10,"Fee: €0.23 €22.86","","","Fee: €0.23","€22.86","",""),
        Line(11,"Revolut Rate €1.00 = 7.44 DKK 170.00 DKK")];
    [Fact] public void EuroBalancesAndFeesAreCountedOnceAcrossYears()
    {
        var statement=RevolutStatementReader.Parse(Example());Assert.True(statement.Valid);Assert.Equal("Revolut",statement.BankName);
        Assert.Equal("EXAMPLE ACCOUNT HOLDER",statement.AccountHolder);var account=Assert.Single(statement.Accounts);
        Assert.Equal(new DateOnly(2026,1,30),account.To);Assert.Equal(2,account.Rows.Count);Assert.Equal(-230900,account.Rows[1].Units);
        Assert.Equal(969100,account.ClosingUnits);Assert.Contains("Reference: Own account",account.Rows[0].Description);
    }
    [Fact] public void MisreadAmountOrMissingMovementBlocksImport()
    {
        var lines=Example();lines[4]=lines[4] with{Text="Account (Current Account) €0.00 €23.09 €120.00 €96.90"};
        Assert.False(RevolutStatementReader.Parse(lines).Valid);
        lines=Example();lines.RemoveAt(8);Assert.False(RevolutStatementReader.Parse(lines).Valid);
    }
    [Fact] public void OtherCurrenciesAndAmbiguousAccountsAreNotAssumedToBeEuro()
    {
        var lines=Example();lines[0]=Line(1,"GBP Statement");Assert.False(RevolutStatementReader.Parse(lines).Valid);
        lines=Example();lines.Add(Line(12,"IBAN LT000000000000000001"));Assert.False(RevolutStatementReader.Parse(lines).Valid);
    }
    [Fact] public void PortugueseMigrationKeepsBothAccountIdentifiersAndIgnoresForeignSections()
    {
        var lines = new List<BankPdfLine> {
            Line(1,"E x tra to d e E U R"),Line(2,"Revolut Bank UAB"),Line(3,"EXAMPLE ACCOUNT HOLDER"),
            Line(4,"Address IBAN PT50000000000000000000000"),Line(5,"Other IBAN LT000000000000000000"),
            Line(6,"Conta (Conta Corrente) 10,00€ 2,00€ 0,00€ 8,00€"),
            Line(7,"O perações da conta de 1 de abril de 2026 a 30 de abril de 2026"),
            Line(8,"31/03/2026 01/04/2026 Loja 2,00€ 8,00€"),
            Line(9,"Extrato de CZK"),Line(10,"01/04/2026 01/04/2026 Loja estrangeira 425,00 CZK 0,00 CZK") };
        var result=RevolutStatementReader.Parse(lines);Assert.True(result.Valid);
        var account=Assert.Single(result.Accounts);Assert.Equal("PT50000000000000000000000",account.Reference);
        Assert.Contains("LT000000000000000000",account.ReferenceAliases);
        Assert.Single(account.Rows);Assert.Equal(new DateOnly(2026,3,31),account.Rows[0].Date);Assert.Equal(new DateOnly(2026,4,1),account.Rows[0].ValueDate);
        Assert.Single(result.Notices);
        lines[5]=Line(6,"Conta (Conta Corrente) 10,00€ 2,00€ 0,00€ 8,02€");
        Assert.False(RevolutStatementReader.Parse(lines).Valid);
    }
}
