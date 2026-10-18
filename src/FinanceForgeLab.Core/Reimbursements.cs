namespace FinanceForgeLab.Core;

public sealed class ReimbursementClaim
{
    public long Id { get; set; }
    public long HouseholdId { get; set; }
    public long ExpenseTransactionId { get; set; }
    public string Debtor { get; set; } = "";
    public long AmountUnits { get; set; }
    public int Version { get; set; } = 1;
}

public sealed class ReimbursementAllocation
{
    public long Id { get; set; }
    public long ClaimId { get; set; }
    public long ReceiptTransactionId { get; set; }
    public long AmountUnits { get; set; }
}
