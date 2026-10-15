using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinanceForgeLab.Core;

namespace FinanceForgeLab.Infrastructure;

public sealed class LegacyPackage
{
    public int FormatVersion { get; set; } = 1;
    public string DatasetId { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public long SourceLength { get; set; }
    public string ExporterVersion { get; set; } = "1";
    public JsonElement Schema { get; set; }
    public Dictionary<string, List<Dictionary<string, string?>>> Tables { get; set; } = [];

    public static readonly string[] FinancialTables = ["Elemento", "TipoMovimento", "Categoria", "SubCategoria", "Movimento", "Total"];
    public static readonly string[] MappedTables = ["Elemento", "Categoria", "SubCategoria", "Movimento"];
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Canonical(Dictionary<string, string?> row) => JsonSerializer.Serialize(new SortedDictionary<string, string?>(row, StringComparer.Ordinal));
    public static string Required(Dictionary<string, string?> row, string field) =>
        row.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new FormatException($"Campo obrigatório: {field}.");
    public static string? Text(Dictionary<string, string?> row, string field) =>
        row.TryGetValue(field, out var value) ? value : throw new FormatException($"Campo ausente: {field}.");
    public static int Integer(Dictionary<string, string?> row, string field) => int.Parse(Required(row, field), NumberStyles.Integer, CultureInfo.InvariantCulture);
    public static decimal Amount(Dictionary<string, string?> row) => decimal.Parse(Required(row, "Valor"), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    public static DateOnly Date(Dictionary<string, string?> row)
    {
        var date = DateTime.ParseExact(Required(row, "Dia"), "yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture, DateTimeStyles.None);
        if (date.TimeOfDay != TimeSpan.Zero) throw new FormatException("Data com hora exige revisão antes da conversão.");
        return DateOnly.FromDateTime(date);
    }
    public static MovementKind Kind(Dictionary<string, string?> row, string field = "IdTipo") => Integer(row, field) switch
    {
        1 => MovementKind.Income,
        2 => MovementKind.Expense,
        _ => throw new FormatException("Tipo de movimento desconhecido.")
    };
}

public sealed record MigrationIssue(string Severity, string Code, string Table, string LegacyId);
public sealed class MigrationReport
{
    public string ToolVersion { get; init; } = "1.0.0";
    public string RulesVersion { get; init; } = "1";
    public string SchemaVersion { get; set; } = "";
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }
    public string Mode { get; set; } = "Simulation";
    public string Status { get; set; } = "Pending";
    public string Currency { get; init; } = "EUR";
    public string DatasetId { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public Dictionary<string, int> SourceCounts { get; set; } = [];
    public Dictionary<string, int> Inserted { get; set; } = [];
    public Dictionary<string, int> Skipped { get; set; } = [];
    public List<MigrationIssue> Issues { get; set; } = [];
    public int RawRowsArchived { get; set; }
    public int DuplicateGroups { get; set; }
    public int DuplicateCandidateRows { get; set; }
    public int LegacyTotalDifferences { get; set; }
    public int MissingLegacyTotalKeys { get; set; }
    public int UnassignedAccountCount { get; set; }
    public bool BackupVerified { get; set; }
    public bool Reconciled { get; set; }
    public bool IntegrityVerified { get; set; }
    public Dictionary<string, string> SourceTotals { get; set; } = [];
    public Dictionary<string, string> TargetTotals { get; set; } = [];
    public string? ErrorCode { get; set; }
    public bool HasErrors => Issues.Any(x => x.Severity == "Error");
}

public static class LegacyValidation
{
    public static MigrationReport Validate(LegacyPackage p)
    {
        var report = new MigrationReport { DatasetId = p.DatasetId, SourceSha256 = p.SourceSha256,
            SourceCounts = p.Tables.ToDictionary(x => x.Key, x => x.Value.Count) };
        void Error(string code, string table, string id) => report.Issues.Add(new("Error", code, table, id));
        if (p.FormatVersion != 1 || string.IsNullOrWhiteSpace(p.DatasetId) || p.SourceSha256.Length != 64 || p.Schema.ValueKind != JsonValueKind.Object)
            Error("InvalidManifest", "", "");
        foreach (var table in LegacyPackage.FinancialTables)
            if (!p.Tables.ContainsKey(table)) Error("MissingTable", table, "");
        foreach (var (table, rows) in p.Tables)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var id = row.GetValueOrDefault("ID");
                if (id is null || !int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number <= 0)
                    Error("InvalidSourceKey", table, id ?? "");
                else if (!ids.Add(id)) Error("DuplicateSourceKey", table, id);
            }
        }
        if (report.HasErrors) return report;
        var lookup = p.Tables.ToDictionary(t => t.Key, t => t.Value.ToDictionary(r => r["ID"]!, StringComparer.Ordinal));
        Dictionary<string, string?> Ref(Dictionary<string, string?> row, string table, string field) =>
            lookup[table].TryGetValue(LegacyPackage.Required(row, field), out var r) ? r : throw new FormatException("Referência inválida.");
        var aggregate = new Dictionary<(int Year, int Month, int Cat, int Sub, int Type), decimal>();
        var financialGroups = new Dictionary<string, List<string>>();
        foreach (var table in LegacyPackage.FinancialTables)
        foreach (var row in p.Tables[table])
        {
            var id = row["ID"]!;
            try
            {
                switch (table)
                {
                    case "Elemento": LegacyPackage.Required(row, "Nome"); break;
                    case "TipoMovimento":
                        var k = LegacyPackage.Integer(row, "ID");
                        if (k is not (1 or 2) || LegacyPackage.Required(row, "Descricao") != (k == 1 ? "Receita" : "Despesa"))
                            throw new FormatException("Convenção de tipos incompatível.");
                        break;
                    case "Categoria":
                        LegacyPackage.Required(row, "Descricao"); LegacyPackage.Kind(row); Ref(row, "TipoMovimento", "IdTipo"); break;
                    case "SubCategoria":
                        LegacyPackage.Required(row, "Descricao"); Ref(row, "Categoria", "IdCategoria"); break;
                    case "Movimento":
                        var date = LegacyPackage.Date(row);
                        LegacyPackage.Text(row, "Observacoes");
                        var kind = LegacyPackage.Kind(row);
                        Ref(row, "TipoMovimento", "IdTipo"); Ref(row, "Elemento", "IdElemento");
                        var cat = Ref(row, "Categoria", "IdCategoria"); var sub = Ref(row, "SubCategoria", "IdSubCategoria");
                        if (sub["IdCategoria"] != row["IdCategoria"] || cat["IdTipo"] != row["IdTipo"]) throw new FormatException("Classificação incoerente.");
                        var value = LegacyPackage.Amount(row);
                        if (value == 0) throw new FormatException("Valor zero.");
                        Money.SignedUnits(value, kind);
                        if (value < 0) report.Issues.Add(new("Warning", "NegativeLegacyAmountPreserved", table, id));
                        var content = new Dictionary<string, string?>(row); content.Remove("ID");
                        // Normalize monetary scale for candidate detection, but preserve the raw source record.
                        content["Valor"] = value.ToString("G29", CultureInfo.InvariantCulture);
                        var contentHash = LegacyPackage.Hash(LegacyPackage.Canonical(content));
                        if (!financialGroups.TryGetValue(contentHash, out var group)) financialGroups[contentHash] = group = [];
                        group.Add(id);
                        foreach (var month in new[] { date.Month, 0 })
                        foreach (var (c, s) in new[] { (0, 0), (LegacyPackage.Integer(row, "IdCategoria"), 0), (LegacyPackage.Integer(row, "IdCategoria"), LegacyPackage.Integer(row, "IdSubCategoria")) })
                        {
                            var key = (date.Year, month, c, s, (int)kind);
                            aggregate[key] = checked(aggregate.GetValueOrDefault(key) + value);
                        }
                        break;
                    case "Total":
                        Money.ToUnits(LegacyPackage.Amount(row));
                        foreach (var f in new[] { "Ano", "Mes", "IdCategoria", "IdSubCategoria", "IdTipoMovimento" }) LegacyPackage.Integer(row, f);
                        if (LegacyPackage.Integer(row, "Mes") is < 0 or > 12) throw new FormatException("Mês inválido.");
                        break;
                }
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            { Error("InvalidLegacyRecord", table, id); }
        }
        foreach (var group in financialGroups.Values.Where(x => x.Count > 1))
        {
            report.DuplicateGroups++; report.DuplicateCandidateRows += group.Count;
            foreach (var id in group) report.Issues.Add(new("Warning", "PossibleDuplicatePreserved", "Movimento", id));
        }
        if (report.HasErrors) return report;
        var stored = new HashSet<(int, int, int, int, int)>();
        foreach (var row in p.Tables["Total"])
        {
            var key = (LegacyPackage.Integer(row, "Ano"), LegacyPackage.Integer(row, "Mes"), LegacyPackage.Integer(row, "IdCategoria"), LegacyPackage.Integer(row, "IdSubCategoria"), LegacyPackage.Integer(row, "IdTipoMovimento"));
            if (!stored.Add(key)) report.Issues.Add(new("Warning", "DuplicateLegacyAggregate", "Total", row["ID"]!));
            if (aggregate.GetValueOrDefault(key) != LegacyPackage.Amount(row))
            { report.LegacyTotalDifferences++; report.Issues.Add(new("Warning", "StaleLegacyAggregate", "Total", row["ID"]!)); }
        }
        report.MissingLegacyTotalKeys = aggregate.Keys.Count(x => !stored.Contains(x));
        return report;
    }
}
