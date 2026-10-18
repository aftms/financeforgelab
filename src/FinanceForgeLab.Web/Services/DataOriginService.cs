using System.Text.Json;
using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Web.Services;

public sealed record OriginFolder(string Path, bool Inventoried, int SavedMessages, int SavedSources, int PendingSources, DateTime? LastActivityUtc, string Resume);
public sealed record FileOrigin(string Name, string PhysicalPath, string Description, bool Complete, List<OriginFolder> Folders, List<string> Files);
public sealed record EmailOrigin(string Name, string Policy, bool Complete, List<OriginFolder> Folders);
public sealed record OriginLocation(string Name, string Path, string Description);
public sealed record IdentificationReference(long Id, string Account, string Merchant, string Code, string Description, string Category, DateOnly? Date, long? AmountUnits);
public sealed record DataOriginReport(DateTimeOffset? InventoryVerifiedUtc, string? InventoryNotice, List<FileOrigin> FileRoots,
    List<EmailOrigin> EmailAccounts, List<OriginLocation> Locations, List<IdentificationReference> References,
    int BankDocuments, int PurchaseSources, int LegacyMovements);

// This inventory is local metadata, separate from credentials and financial writes.
// Only the explicit fields below are read; never serialize configuration or .env.
public sealed class DataOriginService(Func<FinanceDb> open, string inventoryPath, string workspace, string databasePath, string privatePath, string assistantPath)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public async Task<DataOriginReport> ReadAsync()
    {
        var inventory = new OriginInventory(); string? notice = null;
        try
        {
            if (File.Exists(inventoryPath))
            {
                if (new FileInfo(inventoryPath).Length > 2_000_000) throw new InvalidDataException();
                inventory = JsonSerializer.Deserialize<OriginInventory>(await File.ReadAllTextAsync(inventoryPath), Json) ?? throw new InvalidDataException();
            }
            else notice = "Ainda não existe um inventário local das pastas disponíveis. As origens já guardadas continuam apresentadas abaixo.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { notice = "O inventário local não está disponível. As origens guardadas nos registos continuam acessíveis."; }
        if (!inventory.FileRoots.Any(r => r.Name == "Financas")) inventory.FileRoots.Add(new() { Name = "Financas", Description = "Partilha de pesquisa financeira. Caminho físico ainda não inventariado nesta instalação." });
        await using var db = open();
        var sources = await db.PurchaseEvidenceSources.AsNoTracking().Select(s => new { s.Source, s.PayloadJson }).ToListAsync();
        var memory = await db.AssistantSources.AsNoTracking().Where(s => s.Kind == "email" || s.Kind == "files").ToListAsync();
        var messages = new Dictionary<(string Account, string Folder), HashSet<string>>();
        foreach (var source in sources)
        {
            try
            {
                var email = JsonSerializer.Deserialize<PurchaseEvidence>(source.PayloadJson, Json)?.Email;
                var account = AllowedAccount(email?.Account);
                if (account is null || email is null || string.IsNullOrWhiteSpace(email.MessageId) || !AllowedFolder(account, email.Folder)) continue;
                var key = (account, email.Folder);
                if (!messages.TryGetValue(key, out var ids)) messages[key] = ids = [];
                ids.Add(email.MessageId); // Email plus its PDF attachments is one message.
            }
            catch (JsonException) { /* Preserve incomplete provenance; do not infer missing metadata. */ }
        }
        var emailOrigins = new List<EmailOrigin>();
        foreach (var account in new[] { "Hotmail", "Gmail" })
        {
            var listed = inventory.EmailAccounts.FirstOrDefault(a => AllowedAccount(a.Name) == account);
            var folders = (listed?.Folders ?? []).Where(f => AllowedFolder(account, f)).ToHashSet(StringComparer.Ordinal);
            var scopes = memory.Where(s => s.Kind == "email" && SplitScope(s.Scope) is {} scope && scope.Account == account && AllowedFolder(account, scope.Folder)).ToList();
            var allFolders = folders.Concat(messages.Keys.Where(k => k.Account == account).Select(k => k.Folder))
                .Concat(scopes.Select(s => SplitScope(s.Scope)!.Value.Folder))
                .Concat(account == "Hotmail" ? [AssistantEmailPolicy.ViaVerdeFolder] : ["INBOX"]).Distinct().Order(StringComparer.CurrentCulture);
            var rows = allFolders.Select(folder =>
            {
                var cached = scopes.Where(s => SplitScope(s.Scope)!.Value.Folder == folder).ToList();
                return new OriginFolder(folder, folders.Contains(folder), messages.GetValueOrDefault((account, folder))?.Count ?? 0,
                    cached.Count(s => s.SourceId != "@cursor"), cached.Count(s => s.Status == "Pending" && s.SourceId != "@cursor"),
                    cached.Select(s => (DateTime?)s.UpdatedUtc).Max(), Resume(cached.FirstOrDefault(s => s.SourceId == "@cursor")?.CursorJson));
            }).ToList();
            emailOrigins.Add(new(account, account == "Hotmail" ? "Pesquisa financeira com subpastas, incluindo compras, serviços, comércio e Estado. A pasta Via Verde é obrigatória em cada verificação."
                : "Apenas INBOX e os 1000 emails disponibilizados, conforme autorização. As restantes pastas não fazem parte da pesquisa.", listed?.Complete == true, rows));
        }
        var fileRoots = inventory.FileRoots.Where(r => r.Name == "Financas").Select(root =>
        {
            var listed = root.Folders.ToHashSet(StringComparer.Ordinal);
            var documentFolders = sources.Where(s => s.Source.StartsWith("Financas / ", StringComparison.Ordinal))
                .Select(s => Parent(s.Source[11..])).ToList();
            var known = listed.Concat(documentFolders).Concat(memory.Where(s => s.Kind == "files" && s.Scope == "Financas" && s.SourceId != "@cursor").Select(s => Parent(s.SourceId))).Distinct().Order(StringComparer.CurrentCulture);
            return new FileOrigin(root.Name, root.PhysicalPath, root.Description, root.Complete, known.Select(folder =>
            {
                var cached = memory.Where(s => s.Kind == "files" && s.Scope == "Financas" && s.SourceId != "@cursor" && Parent(s.SourceId) == folder).ToList();
                return new OriginFolder(folder, listed.Contains(folder), 0, documentFolders.Count(f => f == folder), cached.Count(s => s.Status == "Pending"), cached.Select(s => (DateTime?)s.UpdatedUtc).Max(), "");
            }).ToList(), root.Files);
        }).ToList();
        var locations = new List<OriginLocation> {
            new("Base e originais guardados", databasePath, "Registos, auditoria, extratos e PDFs associados às compras. Estes originais ficam incluídos nas cópias da base."),
            new("Cópias locais", Path.Combine(privatePath, "backups"), "Cópias diárias, semanais e manuais criadas pela aplicação."),
            new("Documentos do Assistente", assistantPath, "Anexos e resultados de processamento. Não são uma pasta adicional de pesquisa financeira.") };
        if (Directory.Exists(Path.Combine(workspace, "Extratos"))) locations.Insert(0, new("Extratos disponibilizados no computador", Path.Combine(workspace, "Extratos"), "Originais disponibilizados localmente. A pesquisa automática de pastas usa Financas; novos extratos também podem ser carregados em Importar extratos."));
        locations.AddRange(inventory.HistoricalSources.Select(s => new OriginLocation(s.Name, s.PhysicalPath, s.Description)));
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name);
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id);
        var references = (await db.BankReferences.AsNoTracking().Where(r => r.Active).OrderBy(r => r.Merchant).ThenBy(r => r.Code).ToListAsync()).Select(r =>
        {
            var category = categories.GetValueOrDefault(r.CategoryId);
            var label = category?.ParentId is {} parent ? categories.GetValueOrDefault(parent)?.Name + " / " + category.Name : category?.Name ?? "Por classificar";
            return new IdentificationReference(r.Id, accounts.GetValueOrDefault(r.AccountId, "Conta por identificar"), r.Merchant, r.Code, r.Description, label, r.PurchaseDate, r.AmountUnits);
        }).ToList();
        return new(inventory.VerifiedAtUtc, notice, fileRoots, emailOrigins, locations, references,
            await db.BankDocuments.CountAsync(), sources.Count, await db.Transactions.CountAsync(t => t.Origin == "Legacy"));
    }
    private static string Parent(string path) { path = path.Replace('\\', '/'); var index = path.LastIndexOf('/'); return index < 0 ? "" : path[..index]; }
    private static string? AllowedAccount(string? account) => account?.ToUpperInvariant() switch { "HOTMAIL" => "Hotmail", "GMAIL" => "Gmail", _ => null };
    private static bool AllowedFolder(string account, string folder) => !string.IsNullOrWhiteSpace(folder) && (account != "Gmail" || folder == "INBOX");
    private static (string Account, string Folder)? SplitScope(string scope) { var pair = scope.Split('|', 2); return pair.Length == 2 && AllowedAccount(pair[0]) is {} account ? (account, pair[1]) : null; }
    private static string Resume(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "Sem ponto de retoma guardado";
        try
        {
            using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "Ponto de retoma guardado";
            var complete = root.TryGetProperty("complete", out var value) && value.ValueKind == JsonValueKind.True;
            var end = root.TryGetProperty("windowEnd", out var date) && date.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(date.GetString(), out var parsed) ? $" até {parsed:dd/MM/yyyy}" : "";
            return (complete ? "Janela concluída" : "Janela por concluir") + end;
        }
        catch (JsonException) { return "Ponto de retoma precisa de verificação"; }
    }
    public sealed class OriginInventory
    {
        public DateTimeOffset? VerifiedAtUtc { get; set; }
        public List<InventoryRoot> FileRoots { get; set; } = [];
        public List<InventoryAccount> EmailAccounts { get; set; } = [];
        public List<InventoryRoot> HistoricalSources { get; set; } = [];
    }
    public sealed class InventoryRoot
    {
        public string Name { get; set; } = ""; public string PhysicalPath { get; set; } = ""; public string Description { get; set; } = "";
        public bool Complete { get; set; } public List<string> Folders { get; set; } = []; public List<string> Files { get; set; } = [];
    }
    public sealed class InventoryAccount
    {
        public string Name { get; set; } = ""; public bool Complete { get; set; } public List<string> Folders { get; set; } = [];
    }
}
