namespace FinanceForgeLab.Core;

public sealed class BankDocument
{
    public string Id { get; set; } = ""; // SHA-256 of the immutable PDF.
    public string Name { get; set; } = "";
    public string ReaderVersion { get; set; } = "";
    public string StatementJson { get; set; } = "";
    public byte[] Content { get; set; } = [];
    public DateTime CreatedUtc { get; set; }
}
public sealed class BankImport
{
    public long Id { get; set; }
    public string DocumentId { get; set; } = "";
    public int SectionIndex { get; set; }
    public string SourceReference { get; set; } = "";
    public long? AccountId { get; set; }
    public long? PersonId { get; set; }
    public string Status { get; set; } = "Draft";
    public int Version { get; set; } = 1;
    public string ReviewJson { get; set; } = "";
    public string ReviewHash { get; set; } = "";
    public DateTime? ImportedUtc { get; set; }
}
public sealed class BankImportEntry
{
    public long Id { get; set; }
    public long ImportId { get; set; }
    public int Ordinal { get; set; }
    public long TransactionId { get; set; }
    public int TransactionVersion { get; set; }
    public bool Created { get; set; }
}
