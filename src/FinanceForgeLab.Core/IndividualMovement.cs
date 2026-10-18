namespace FinanceForgeLab.Core;

// Independently editable movements identified from a shared payment. The original
// posting remains reconciliation evidence, and is excluded from financial totals.
public sealed class IndividualMovement
{
    public long Id { get; set; }
    public long PaymentTransactionId { get; set; }
    public long SourceCategoryId { get; set; }
    public int Version { get; set; } = 1;
    public string Status { get; set; } = "Confirmed";
    public DateOnly Date { get; set; }
    public MovementKind Kind { get; set; } = MovementKind.Expense;
    public long AmountUnits { get; set; }
    public long? AccountId { get; set; }
    public long? DestinationAccountId { get; set; }
    public bool AccountEdited { get; set; }
    public long? CategoryId { get; set; }
    public long? PersonId { get; set; }
    public string Description { get; set; } = "";
    public string? Notes { get; set; }
}
