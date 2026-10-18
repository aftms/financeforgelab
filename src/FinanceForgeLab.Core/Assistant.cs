namespace FinanceForgeLab.Core;

public sealed class AssistantRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public Guid ClientId { get; set; }
    public string Title { get; set; } = "";
    public string Workflow { get; set; } = "";
    public string Status { get; set; } = "Queued";
    public string Notice { get; set; } = "Em fila";
    public bool Archived { get; set; }
    public int Round { get; set; } = 1;
    public int Version { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime NextCheckUtc { get; set; } = DateTime.UtcNow;
}
public sealed class AssistantTurn
{
    public long Id { get; set; }
    public string RequestId { get; set; } = "";
    public int Round { get; set; }
    public Guid ClientId { get; set; }
    public string UserText { get; set; } = "";
    public string InputFingerprint { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string DelegationId { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public string Summary { get; set; } = "";
    public string QuestionsJson { get; set; } = "[]";
    public int PollCount { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class AssistantAttachment
{
    public long Id { get; set; }
    public string RequestId { get; set; } = "";
    public int Round { get; set; }
    public string Name { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public byte[] Content { get; set; } = [];
    public long? SourceCacheId { get; set; }
}

public sealed class AssistantSourceCache
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public string Scope { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string Note { get; set; } = "";
    public string CursorJson { get; set; } = "";
    public string RequestId { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class AssistantOperation
{
    public long Id { get; set; }
    public string RequestId { get; set; } = "";
    public int Round { get; set; }
    public string Key { get; set; } = "";
    public string Type { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public Guid CommandId { get; set; } = Guid.NewGuid();
    public string Status { get; set; } = "Started";
    public string ResultJson { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
