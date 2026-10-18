namespace FinanceForgeLab.Core;

public sealed record EmailSourceReference(string Account,string Folder,string MessageId,string Sender,DateTimeOffset? ReceivedAt,string Subject)
{
    public string? WebLink { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SafeWebLink => Uri.TryCreate(WebLink,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&new[]{"outlook.live.com","outlook.office.com","outlook.office365.com","mail.google.com"}.Contains(uri.Host)?uri.AbsoluteUri:null;
}

// Purchase/payment evidence is not a second financial transaction.
public sealed class PurchaseEvidence
{
    // Email metadata belongs to each source's JSON, not to the merged purchase.
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public EmailSourceReference? Email { get; set; }
    public long Id { get; set; }
    public long HouseholdId { get; set; }
    public string SourceKey { get; set; } = "";
    public string Source { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Merchant { get; set; } = "";
    public string OrderReference { get; set; } = "";
    public DateOnly? Date { get; set; }
    public DateOnly? ExpectedPaymentDate { get; set; }
    public long? Units { get; set; }
    public long? FinancialUnits { get; set; }
    public long? ParentEvidenceId { get; set; }
    public string PaymentMethod { get; set; } = "";
    public string CategoryAllocationsJson { get; set; } = "[]";
    public long? CategoryId { get; set; }
    public string Description { get; set; } = "";
    public string Details { get; set; } = "";
    public string ReviewNotes { get; set; } = "";
    public long? AccountId { get; set; }
    public long? BankReferenceId { get; set; }
    public string? OrderIdentity { get; set; }
    public bool HasSourceConflict { get; set; }
    public long? TransactionId { get; set; }
    public int Version { get; set; } = 1;
}

// Each screenshot/email keeps its own provenance, even when it describes the same purchase.
public sealed class PurchaseEvidenceSource
{
    public long Id { get; set; }
    public long HouseholdId { get; set; }
    public long EvidenceId { get; set; }
    public string SourceKey { get; set; } = "";
    public string Source { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string DocumentName { get; set; } = "";
    public string DocumentSha256 { get; set; } = "";
    public byte[] DocumentContent { get; set; } = [];
}

public sealed record PurchaseCategoryAllocation(long CategoryId, long Units, string Description = "");
