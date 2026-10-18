using FinanceForgeLab.Core;

namespace FinanceForgeLab.Infrastructure;

public sealed record BankImportSummary(long Id, string Name, string AccountReference, DateOnly From, DateOnly To, int Rows, string Status, int Version)
{
    public string DocumentId { get; init; } = "";
    public bool IsOnlineCopy { get; init; }
    public int SectionIndex { get; init; }
    public bool HasErrors { get; init; }
    public long? AccountId { get; init; }
    public string? AccountName { get; init; }
    public string? PersonName { get; init; }
    public int RegisteredRows { get; init; }
    public int ExcludedRows { get; init; }
    public int ClassifiedRows { get; init; }
    public int AwaitingLinkRows { get; init; }
    public int PendingRows => Status == "Draft" ? Math.Max(0, Rows - RegisteredRows - ExcludedRows) : 0;
    public int UnclassifiedRows => Math.Max(0, PendingRows - ClassifiedRows - AwaitingLinkRows);
    public string AccountLabel => AccountName ?? $"Conta bancária {AccountReference}";
}
public sealed record BankDocumentSummary(string Id, string Name, List<BankImportSummary> Parts)
{
    public bool IsOnlineCopy => Parts.Any(p=>p.IsOnlineCopy);
    public string OriginalLabel => IsOnlineCopy ? "Cópia fornecida" : "PDF original";
    public int Rows => Parts.Sum(p => p.Rows);
    public int PendingRows => Parts.Sum(p => p.PendingRows);
    public int RegisteredRows => Parts.Sum(p => p.RegisteredRows);
    public int ExcludedRows => Parts.Sum(p => p.ExcludedRows);
    public int ClassifiedRows => Parts.Sum(p => p.ClassifiedRows);
    public int AwaitingLinkRows => Parts.Sum(p => p.AwaitingLinkRows);
    public int UnclassifiedRows => Parts.Sum(p => p.UnclassifiedRows);
    public bool HasErrors => Parts.Any(p => p.HasErrors);
    public DateOnly From => Parts.Min(p => p.From);
    public DateOnly To => Parts.Max(p => p.To);
    public BankImportSummary? Next => Parts.Where(p => p.Rows > 0 || p.HasErrors)
        .OrderBy(p => p.HasErrors ? 0 : 1).ThenBy(p => p.PendingRows > 0 ? 0 : 1).ThenBy(p => p.SectionIndex).FirstOrDefault();
    public static List<BankDocumentSummary> Group(IEnumerable<BankImportSummary> batches) => batches
        .GroupBy(b => b.DocumentId)
        .Select(g => new BankDocumentSummary(g.Key, g.First().Name, g.GroupBy(b => b.SectionIndex)
            .Select(parts => parts.OrderBy(p => p.Status == "Voided" ? 1 : 0).ThenByDescending(p => p.Id).First())
            .OrderBy(p => p.SectionIndex).ToList()))
        .OrderByDescending(d => d.Parts.Max(p => p.Id)).ToList();
}
public sealed record BankImportPreview(BankImport Import, StatementAccount Statement, List<string> DocumentErrors, Dictionary<int, List<BankDuplicate>> Duplicates, List<Person> People)
{
    public List<string> Notices { get; init; } = [];
    public Dictionary<int, BankClassificationSuggestion> Suggestions { get; init; } = [];
    public Dictionary<int, string> ReviewWarnings { get; init; } = [];
    public Dictionary<int, long> RecordedTransactions { get; init; } = [];
}
public sealed record BankClassificationSuggestion(MovementKind Kind, long? CategoryId, long? CounterpartyAccountId, string Confidence, string Reason)
{
    public string? Detail { get; init; }
}
public sealed record BankDuplicate(long TransactionId, int Version, string Description, MovementKind Kind)
{
    public List<string> Classifications { get; init; } = [];
    public string ClassificationLabel => Kind == MovementKind.Transfer ? "Transferência" : Classifications.Count == 0 ? "Por classificar" : string.Join(" · ", Classifications);
    public DateOnly Date { get; init; }
    public long Units { get; init; }
    public long? AccountId { get; init; }
    public string Origin { get; init; } = "";
    public string Reason { get; init; } = "";
    public bool StrongMatch { get; init; }
}
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
    public bool SuggestionAccepted { get; set; }
    public bool ClassificationEdited { get; set; }
    public string? Detail { get; set; }
}
public sealed class BankReferenceForm
{
    public bool IsPurchase { get; set; }
    public long Id { get; set; }
    public int Version { get; set; }
    public long AccountId { get; set; }
    public long CategoryId { get; set; }
    public string Merchant { get; set; } = "";
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public DateOnly? PurchaseDate { get; set; }
    public decimal? Amount { get; set; }
}
public sealed record BankImportResult(long Id, int Created, int Linked, int Excluded);
