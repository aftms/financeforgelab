namespace FinanceForgeLab.Core;

// An owner's explanation of a recurring charge or an individual purchase.
public sealed class BankReference
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public long CategoryId { get; set; }
    public string Merchant { get; set; } = "";
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public DateOnly? PurchaseDate { get; set; }
    public long? AmountUnits { get; set; }
    public bool Active { get; set; } = true;
    public int Version { get; set; } = 1;
}
