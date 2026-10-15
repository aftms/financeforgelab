namespace FinanceForgeLab.Core;

public sealed class Household
{
    public long Id { get; set; }
    public string Name { get; set; } = "Família";
    public string Currency { get; set; } = "EUR";
}
public sealed class Person
{
    public long Id { get; set; }
    public long HouseholdId { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
}
public sealed class Category
{
    public long Id { get; set; }
    public long HouseholdId { get; set; }
    public long? ParentId { get; set; }
    public string Name { get; set; } = "";
    public MovementKind Kind { get; set; }
    public bool Active { get; set; } = true;
    public int Version { get; set; } = 1;
}
public sealed class Account
{
    public long Id { get; set; }
    public long HouseholdId { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Bank";
    public string Currency { get; set; } = "EUR";
    public bool Active { get; set; } = true;
    public DateOnly? BalanceStartDate { get; set; }
    public int Version { get; set; } = 1;
}
public sealed class FinancialTransaction
{
    public long Id { get; set; }
    public long HouseholdId { get; set; }
    public DateOnly Date { get; set; }
    public MovementKind Kind { get; set; }
    public string? Description { get; set; }
    public string? Notes { get; set; }
    public long? PersonId { get; set; }
    public string Origin { get; set; } = "Legacy";
    public string Status { get; set; } = "Confirmed";
    public int Version { get; set; } = 1;
}
public sealed class Posting
{
    public long Id { get; set; }
    public long TransactionId { get; set; }
    public long? AccountId { get; set; }
    public long? CategoryId { get; set; }
    public long AmountUnits { get; set; }
    public string Currency { get; set; } = "EUR";
    public DateOnly Date { get; set; }
    public DateOnly? ValueDate { get; set; }
}
public sealed class SourceDataset
{
    public string Id { get; set; } = "";
    public long HouseholdId { get; set; }
    public string Currency { get; set; } = "EUR";
}
public sealed class SourceSnapshot
{
    public long Id { get; set; }
    public string DatasetId { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public string SchemaJson { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}
public sealed class SourceRecord
{
    public long Id { get; set; }
    public long SnapshotId { get; set; }
    public string Table { get; set; } = "";
    public string LegacyId { get; set; } = "";
    public string RawJson { get; set; } = "";
    public string Sha256 { get; set; } = "";
}
public sealed class LegacyMap
{
    public long Id { get; set; }
    public string DatasetId { get; set; } = "";
    public string Table { get; set; } = "";
    public string LegacyId { get; set; } = "";
    public string RawSha256 { get; set; } = "";
    public string TargetKind { get; set; } = "";
    public long TargetId { get; set; }
    public string TargetSha256 { get; set; } = "";
}
public sealed class ImportBatch
{
    public long Id { get; set; }
    public long SnapshotId { get; set; }
    public string Mode { get; set; } = "Simulation";
    public string Status { get; set; } = "Confirmed";
    public string RulesVersion { get; set; } = "1";
    public DateTime CreatedUtc { get; set; }
}
public sealed class ImportRow
{
    public long Id { get; set; }
    public long BatchId { get; set; }
    public long SourceRecordId { get; set; }
    public long? TransactionId { get; set; }
    public string Decision { get; set; } = "Imported";
}
public sealed class AuditChange
{
    public long Id { get; set; }
    public string Entity { get; set; } = "";
    public long EntityId { get; set; }
    public string Action { get; set; } = "";
    public string Actor { get; set; } = "Migration";
    public DateTime CreatedUtc { get; set; }
    public string DetailJson { get; set; } = "{}";
}

public sealed class OwnerCredential
{
    public int Id { get; set; } = 1;
    public string PasswordHash { get; set; } = "";
    public int FailedAttempts { get; set; }
    public DateTime? LockoutUntilUtc { get; set; }
}
public sealed class CommandReceipt
{
    public string Id { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public string Action { get; set; } = "";
    public long EntityId { get; set; }
}
