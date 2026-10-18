using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed record BankSimilarRow(long ImportId, int Ordinal, DateOnly Date, long Units, string Description, string Document);
public sealed record BankSimilarApproval(long ImportId, int Ordinal, string Account, string Classification, string Entity,
    List<BankSimilarRow> Rows, string Fingerprint);
public sealed record BankSimilarApprovalResult(int Created, int Statements);

public sealed partial class BankImportService
{
    public async Task<BankSimilarApproval> SimilarApprovalAsync(long importId, int ordinal)
        => (await BuildSimilarApprovalAsync(importId, ordinal)).Plan;

    public async Task<BankSimilarApprovalResult> ConfirmSimilarAsync(BankSimilarApproval expected)
    {
        var fresh = await BuildSimilarApprovalAsync(expected.ImportId, expected.Ordinal);
        if (fresh.Plan.Fingerprint != expected.Fingerprint || fresh.Plan.Rows.Count == 0)
            throw new FinanceRuleException("Os movimentos do grupo mudaram. Prepara a aprovação dos semelhantes novamente.");
        await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var (review, ordinals) in fresh.Selections)
            await ConfirmCoreAsync(review, ordinals, db);
        await tx.CommitAsync();
        return new(fresh.Plan.Rows.Count, fresh.Selections.Count);
    }

    private async Task<(BankSimilarApproval Plan, List<(BankImportReview Review, HashSet<int> Ordinals)> Selections)> BuildSimilarApprovalAsync(long importId, int ordinal)
    {
        var anchorPreview = await PreviewAsync(importId); var anchorReview = await PrepareReviewAsync(importId);
        var anchor = anchorPreview.Statement.Rows.SingleOrDefault(r => r.Ordinal == ordinal)
            ?? throw new FinanceRuleException("Movimento de referência não encontrado.");
        var classification = anchorReview.Rows.Single(r => r.Ordinal == ordinal);
        await using var db = open();
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == anchorReview.AccountId && a.Active && a.Currency == "EUR")
            ?? throw new FinanceRuleException("Associa a conta antes de preparar o grupo.");
        var categories = await db.Categories.AsNoTracking().Where(c => c.HouseholdId == account.HouseholdId).ToListAsync();
        bool ActiveCategory(long? id, MovementKind kind) => categories.Any(c => c.Id == id && c.Active && c.Kind == kind && (c.ParentId == null || categories.Any(p => p.Id == c.ParentId && p.Active)));
        if (classification.Kind == MovementKind.Transfer ? classification.CounterpartyAccountId is null : !ActiveCategory(classification.CategoryId, classification.Kind))
            throw new FinanceRuleException("Escolhe a classificação antes de preparar movimentos semelhantes.");
        var references = await db.BankReferences.AsNoTracking().Where(r => r.AccountId == account.Id && r.Active && r.PurchaseDate == null).ToListAsync();
        var matchingReferences = references.Where(r => r.CategoryId == classification.CategoryId && ReferenceMatches(r, anchor, categories)).ToList();
        if (matchingReferences.Select(r => (r.Merchant, r.Code, r.Description)).Distinct().Count() > 1)
            throw new FinanceRuleException("Há referências diferentes para esta cobrança. Confere-as antes de agrupar.");
        var reference = matchingReferences.FirstOrDefault();
        var merchant = MerchantKey(anchor.Description);
        if (reference is null && (merchant.Length < 4 || IsPaymentPlatform(Normalize(anchor.Description))))
            throw new FinanceRuleException("Esta plataforma pode cobrar compras diferentes. Guarda uma referência específica antes de agrupar.");
        var batches = await (from b in db.BankImports.AsNoTracking() join d in db.BankDocuments.AsNoTracking() on b.DocumentId equals d.Id
                             where b.Status == "Draft" && b.AccountId == account.Id select new { Batch = b, d.Name, d.StatementJson }).ToListAsync();
        var recorded = (await db.BankImportEntries.Select(e => new { e.ImportId, e.Ordinal }).ToListAsync()).Select(e => (e.ImportId, e.Ordinal)).ToHashSet();
        var repeatedSources = batches.SelectMany(item => JsonSerializer.Deserialize<BankStatement>(item.StatementJson)!.Accounts[item.Batch.SectionIndex].Rows
                .Where(r => !recorded.Contains((item.Batch.Id, r.Ordinal))))
            .GroupBy(r => (r.Date, r.Units, Description: Normalize(r.Description))).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var rows = new List<BankSimilarRow>(); var selections = new List<(BankImportReview, HashSet<int>)>();
        foreach (var item in batches.OrderBy(x => x.Batch.Id))
        {
            var statement = JsonSerializer.Deserialize<BankStatement>(item.StatementJson)!; var section = statement.Accounts[item.Batch.SectionIndex];
            if (!BankStatementConformance.CanImport(statement, section)) continue;
            var candidates = section.Rows.Where(r => r.Units != 0 && Math.Sign(r.Units) == Math.Sign(anchor.Units) && !recorded.Contains((item.Batch.Id, r.Ordinal)) &&
                (reference is not null ? ReferenceMatches(reference, r, categories) : MerchantKey(r.Description) == merchant)).ToList();
            if (candidates.Count == 0) continue;
            var review = await PrepareReviewAsync(item.Batch.Id); var preview = await PreviewAsync(item.Batch.Id, account.Id);
            var chosen = new HashSet<int>();
            foreach (var row in candidates)
            {
                var choice = review.Rows.Single(r => r.Ordinal == row.Ordinal);
                if (PossibleContinenteFunding(row) && !choice.ClassificationEdited) continue;
                if (choice.Decision != "Import" || preview.Duplicates.ContainsKey(row.Ordinal) || repeatedSources.Contains((row.Date, row.Units, Normalize(row.Description))) || section.Rows.Count(r => r.Date == row.Date && r.Units == row.Units) > 1) continue;
                if (choice.CategoryId is null && choice.CounterpartyAccountId is null && !choice.ClassificationEdited && !choice.SuggestionAccepted && preview.Suggestions.TryGetValue(row.Ordinal, out var suggestion))
                {
                    choice.Kind = suggestion.Kind; choice.CategoryId = suggestion.CategoryId; choice.CounterpartyAccountId = suggestion.CounterpartyAccountId; choice.Detail = suggestion.Detail ?? choice.Detail;
                }
                if (choice.Kind != classification.Kind || choice.CategoryId != classification.CategoryId || choice.CounterpartyAccountId != classification.CounterpartyAccountId) continue;
                choice.SuggestionAccepted = true; chosen.Add(row.Ordinal);
                rows.Add(new(item.Batch.Id, row.Ordinal, row.Date, row.Units, row.Description, item.Name));
            }
            if (chosen.Count > 0) selections.Add((review, chosen));
        }
        var selectedCategory = categories.SingleOrDefault(c => c.Id == classification.CategoryId);
        var label = classification.Kind == MovementKind.Transfer ? "Transferência" : selectedCategory is not null
            ? (selectedCategory.ParentId is {} parent ? categories.Single(p => p.Id == parent).Name + " / " : "") + selectedCategory.Name : "";
        var entity = reference is not null ? reference.Description : anchor.Description;
        var fingerprint = LegacyPackage.Hash(JsonSerializer.Serialize(new { importId, ordinal, account.Id, classification.Kind, classification.CategoryId, classification.CounterpartyAccountId,
            Rows = rows, Reviews = selections.Select(s => s.Item1) }));
        return (new(importId, ordinal, account.Name, label, entity, rows.OrderBy(r => r.Date).ThenBy(r => r.ImportId).ToList(), fingerprint), selections);
    }
}
