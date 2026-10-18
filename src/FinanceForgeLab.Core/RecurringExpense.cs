namespace FinanceForgeLab.Core;

public sealed class RecurringExpense
{
    public long Id { get; set; }
    public long HouseholdId { get; set; }
    public string Name { get; set; } = "";
    public long? CategoryId { get; set; }
    public long? AccountId { get; set; }
    public string ContainsText { get; set; } = "";
    public string MatchKey { get; set; } = "";
    public string Frequency { get; set; } = "Auto";
    public bool Included { get; set; } = true;
    public int Version { get; set; } = 1;
}
