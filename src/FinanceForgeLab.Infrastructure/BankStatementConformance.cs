using FinanceForgeLab.Core;

namespace FinanceForgeLab.Infrastructure;

public sealed record StatementInconsistency(long DeclaredClosingUnits, long CalculatedClosingUnits,
    long DeclaredDebitUnits, long CalculatedDebitUnits, long DeclaredCreditUnits, long CalculatedCreditUnits, bool AllowsImport)
{
    public long DifferenceUnits => checked(DeclaredClosingUnits - CalculatedClosingUnits);
    public string Description => $"Saldo final no resumo: {Money.FromUnits(DeclaredClosingUnits):0.00} €; pelas operações: {Money.FromUnits(CalculatedClosingUnits):0.00} €. Diferença: {Money.FromUnits(DifferenceUnits):0.00} €. " +
        $"Débitos no resumo / nas linhas: {Money.FromUnits(DeclaredDebitUnits):0.00} € / {Money.FromUnits(CalculatedDebitUnits):0.00} €; créditos: {Money.FromUnits(DeclaredCreditUnits):0.00} € / {Money.FromUnits(CalculatedCreditUnits):0.00} €.";
}

// Keep the immutable reading and its errors. A small summary discrepancy is a
// review item, not a reason to hold independently balanced, otherwise valid rows.
public static class BankStatementConformance
{
    private static bool IsTotalError(string error) => error is
        "Os movimentos Revolut não conferem com os totais e o saldo final." or
        "Os movimentos extraídos não conferem com os totais e o saldo final.";

    public static StatementInconsistency? Inconsistency(BankStatement document, StatementAccount section)
    {
        if (!section.Errors.Any(IsTotalError) || section.Rows.Count == 0 || section.OpeningUnits is not {} opening ||
            section.ClosingUnits is not {} closing || section.DebitTotalUnits is not {} debit || section.CreditTotalUnits is not {} credit)
            return null;
        decimal balance = opening, debits = 0, credits = 0;
        var balanced = true;
        foreach (var row in section.Rows)
        {
            balance += row.Units;
            if (row.Units < 0) debits -= row.Units; else credits += row.Units;
            balanced &= balance == row.BalanceUnits && row.Date >= section.From && row.Date <= section.To &&
                !string.IsNullOrWhiteSpace(row.Description) && row.Description.Length <= 500;
        }
        if (balance < long.MinValue || balance > long.MaxValue || debits > long.MaxValue || credits > long.MaxValue) return null;
        var difference = (decimal)closing - balance;
        if (difference < long.MinValue || difference > long.MaxValue) return null;
        var allowed = document.Errors.Count == 0 && section.Errors.All(IsTotalError) && balanced &&
            section.From <= section.To && debit >= 0 && credit >= 0 &&
            section.Rows.Select(r => r.Ordinal).Distinct().Count() == section.Rows.Count &&
            (decimal)opening - debit + credit == closing && Math.Abs(difference) <= Money.ToUnits(0.02m);
        return new(closing, (long)balance, debit, (long)debits, credit, (long)credits, allowed);
    }

    public static List<string> BlockingErrors(BankStatement document, StatementAccount section) =>
        document.Errors.Concat(Inconsistency(document, section)?.AllowsImport == true ? [] : section.Errors).ToList();
    public static bool CanImport(BankStatement document, StatementAccount section) => BlockingErrors(document, section).Count == 0;
}

public sealed record PendingStatementInconsistency(long ImportId, string Name, string Account, DateOnly From, DateOnly To, StatementInconsistency Detail);
