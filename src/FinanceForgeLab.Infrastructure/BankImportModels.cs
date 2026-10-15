using FinanceForgeLab.Core;

namespace FinanceForgeLab.Infrastructure;

public sealed record BankImportSummary(long Id, string Name, string AccountReference, DateOnly From, DateOnly To, int Rows, string Status, int Version);
public sealed record BankImportPreview(BankImport Import, StatementAccount Statement, List<string> DocumentErrors, Dictionary<int, List<BankDuplicate>> Duplicates, List<Person> People);
public sealed record BankDuplicate(long TransactionId, int Version, string Description, MovementKind Kind);
public sealed class BankImportReview
{
    public long Id { get; set; }
    public int Version { get; set; }
    public long AccountId { get; set; }
    public long? PersonId { get; set; }
    public List<BankRowDecision> Rows { get; set; } = [];
}
public sealed class BankRowDecision
{
    public int Ordinal { get; set; }
    public string Decision { get; set; } = "Import"; // Import, Exclude, Link, Pending.
    public MovementKind Kind { get; set; }
    public long? CategoryId { get; set; }
    public long? CounterpartyAccountId { get; set; }
    public long? ExistingTransactionId { get; set; }
    public int? ExistingVersion { get; set; }
    public bool AcknowledgeDuplicate { get; set; }
}
public sealed record BankImportResult(long Id, int Created, int Linked, int Excluded);
