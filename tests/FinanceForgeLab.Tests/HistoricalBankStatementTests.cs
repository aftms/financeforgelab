using FinanceForgeLab.Infrastructure;
using Xunit;

namespace FinanceForgeLab.Tests;

public class HistoricalBankStatementTests
{
    private static BankPdfLine L(int n, string text, params string[] cells) => new(1, n, text, cells.Length == 0 ? ["", "", "", "", "", ""] : cells);
    private static List<BankPdfLine> Savings() => [
        L(1,"POUP PROGRAMADA 10 ANOS nº 1003 2195 1435 de 01.10.2020 a 01.11.2020"),
        L(2,"Data Descritivo Débito Crédito Saldo (Euros)"),
        L(3,"01.10.20 SALDO ANTERIOR 10,00","01.10.20","","SALDO ANTERIOR","","","10,00"),
        L(4,"02.10.20 02.10.20 Juros A) 1,00 11,00","02.10.20","02.10.20","Juros A",")","1,00","11,00"),
        L(5,"02.10.20 02.10.20 Imposto 0,00 11,00","02.10.20","02.10.20","Imposto","0,00","","11,00"),
        L(6,"TOTAL 0,00 1,00","","","TOTAL","0,00","1,00",""),
        L(7,"01.11.20 SALDO CONTABILÍSTICO 11,00","01.11.20","","SALDO CONTABILÍSTICO","","","11,00")];
    [Fact] public void ProgrammedSavingsAndZeroTaxKeepTheirOwnBalancedSection()
    {
        var result = NovoBancoStatementReader.Parse(Savings()); Assert.True(result.Valid);
        var account = Assert.Single(result.Accounts); Assert.Equal("100321951435",account.Reference);
        Assert.Equal("Juros A)",account.Rows[0].Description); Assert.Equal(0,account.Rows[1].Units);
    }
    [Fact] public void LongMerchantReferenceKeepsDigitsThatCrossTheAmountBoundary()
    {
        var lines = Savings(); lines[3] = L(4,"02.10.20 02.10.20 Compra Spotify 353 1,00 9,00","02.10.20","02.10.20","Compra Spotify 35","3 1,00","","9,00");
        lines.RemoveAt(4); lines[4] = L(6,"TOTAL 1,00 0,00","","","TOTAL","1,00","0,00","");
        lines[5] = L(7,"01.11.20 SALDO CONTABILÍSTICO 9,00","01.11.20","","SALDO CONTABILÍSTICO","","","9,00");
        var result = NovoBancoStatementReader.Parse(lines); Assert.True(result.Valid); Assert.Equal("Compra Spotify 353",result.Accounts[0].Rows[0].Description);
    }
    [Fact] public void BankContactFooterDoesNotBecomeAnUnrecognizedFinancialRow()
    {
        var lines = Savings(); lines.Insert(6,L(7,"Linha Direta | 707247365 |www.novobanco.pt","","","Linha Direta","707247365","",""));
        // A continuation page still contains the closing balance.
        lines[^1] = lines[^1] with { Page = 2 };
        Assert.True(NovoBancoStatementReader.Parse(lines).Valid);
    }
}
