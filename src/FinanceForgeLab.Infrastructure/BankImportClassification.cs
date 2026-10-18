using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed partial class BankImportService
{
    // Suggestions are local and read-only. Only ConfirmAsync creates financial movements.
    public async Task<BankImportReview> PrepareReviewAsync(long id)
    {
        var preview = await PreviewAsync(id);
        var saved = !string.IsNullOrEmpty(preview.Import.ReviewJson)
            ? JsonSerializer.Deserialize<BankImportReview>(preview.Import.ReviewJson) : null;
        var review = saved ?? new BankImportReview
        {
            Id = id, AccountId = preview.Import.AccountId ?? await SuggestedAccountAsync(id) ?? 0,
            PersonId = preview.Import.PersonId,
            Rows = preview.Statement.Rows.Select(r => new BankRowDecision { Ordinal = r.Ordinal, Decision = r.Units == 0 ? "Exclude" : "Import", Kind = r.Units > 0 ? MovementKind.Income : MovementKind.Expense }).ToList()
        };
        review.Version = preview.Import.Version;
        if (preview.Import.Status != "Draft") return review;
        if (preview.Import.AccountId != review.AccountId) preview = await PreviewAsync(id, review.AccountId);
        var repeated = preview.Statement.Rows.GroupBy(r => new { r.Date, r.Units })
            .Where(g => g.Count() > 1).SelectMany(g => g.Select(r => r.Ordinal)).ToHashSet();
        var shared = preview.Duplicates.Values.SelectMany(candidates => candidates.Select(c => c.TransactionId).Distinct())
            .GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var row in review.Rows)
        {
            // A description/category edit does not undo an already recorded bank link.
            // A document may precede its bank charge. Keep that proven date difference;
            // refresh only while the amount, accounts and documentary match still agree.
            if (row.Decision == "Link" && row.ExistingTransactionId is {} recordedId &&
                preview.RecordedTransactions.GetValueOrDefault(row.Ordinal) == recordedId &&
                preview.Duplicates.GetValueOrDefault(row.Ordinal)?.SingleOrDefault(c => c.TransactionId == recordedId) is {} recorded &&
                recorded.Version != row.ExistingVersion && recorded.Kind == row.Kind && recorded.AccountId == review.AccountId &&
                preview.Statement.Rows.Single(r => r.Ordinal == row.Ordinal) is {} source && recorded.Units == source.Units)
            {
                await using var db = open();
                var posts = await db.Postings.AsNoTracking().Where(p => p.TransactionId == recordedId).ToListAsync();
                var dateAgrees = recorded.Date == source.Date || recorded.Kind == MovementKind.Transfer &&
                    posts.Any(p=>p.AccountId==review.AccountId&&p.AmountUnits==source.Units&&p.Date==source.Date) || recorded.Origin == "Purchase" && recorded.StrongMatch &&
                    await db.PurchaseEvidence.AsNoTracking().AnyAsync(e => e.TransactionId == recordedId && !e.HasSourceConflict &&
                        e.Date == recorded.Date && e.AccountId == review.AccountId && (e.FinancialUnits ?? e.Units) == -source.Units);
                var consistent = recorded.Kind == MovementKind.Transfer
                    ? posts.Count == 2 && posts.Sum(p => p.AmountUnits) == 0 && posts.Any(p => p.AccountId == review.AccountId && p.AmountUnits == source.Units) && posts.Any(p => p.AccountId == row.CounterpartyAccountId && p.AccountId != review.AccountId)
                    : posts.Count == 1 && posts[0].AccountId == review.AccountId && posts[0].AmountUnits == source.Units;
                if (dateAgrees && consistent) row.ExistingVersion = recorded.Version;
            }
            if (saved is null && row.Decision == "Exclude") continue;
            // Preserve saved decisions, including deliberately empty classifications.
            if (saved is null && preview.Suggestions.TryGetValue(row.Ordinal, out var suggestion))
            {
                row.Kind = suggestion.Kind; row.CategoryId = suggestion.CategoryId; row.CounterpartyAccountId = suggestion.CounterpartyAccountId; row.Detail = suggestion.Detail;
            }
            if (saved is null && (preview.Duplicates.ContainsKey(row.Ordinal) || repeated.Contains(row.Ordinal)))
            {
                row.Decision = "Pending";
                var clear = preview.Duplicates.GetValueOrDefault(row.Ordinal)?.Where(c => c.StrongMatch && !shared.Contains(c.TransactionId)).ToList() ?? [];
                if (!repeated.Contains(row.Ordinal) && clear.Count == 1)
                {
                    row.Decision = "Link"; row.ExistingTransactionId = clear[0].TransactionId; row.ExistingVersion = clear[0].Version;
                }
            }
        }
        // Older saved drafts can ask for suggestions explicitly without losing their edits.
        return review;
    }

    private sealed record ClassificationHistory(long TransactionId, long? AccountId, long? CategoryId, long AmountUnits, MovementKind Kind, string? Description);
    private sealed record ClassificationRule(string[] Descriptions, string[] Categories);
    private static readonly ClassificationRule[] ClassificationRules =
    [
        new(["CONTINENTE", "PINGO DOCE", "LIDL", "ALDI", "AUCHAN", "INTERMARCHE", "MERCADONA", "SUPERMERCADO", "MINIPRECO", "TALHO", "TALHOS", "FRUTARIA", "PEIXARIA", "PONTO DA FRUTA", "PIQUETE DA FRUTA"], ["Supermercado e mercearia", "Supermercado", "Supermercados", "Mercearia", "Alimentação"]),
        new(["VETERINARIO", "VETERINARIA", "VETCLINIC", "VET CLINIC", "HOSP ESC VETERINARIOLISBOA", "HOSPITAL ESCOLAR LISBOA", "HOSPITAL ESCOLAR AJUDA"], ["Veterinário e medicamentos", "Animais de companhia"]),
        new(["CUF", "LUZ SAUDE", "HOSPITAL DA LUZ", "HOSPITAL", "CLINICA", "CLINICAS", "CLIN MEDI DENT", "LUSIADAS", "TROFA SAUDE", "UNILABS", "GERMANO SOUSA", "SYNLAB", "DENTISTA", "DENTARIA"], ["Saúde", "Consultas", "Consultas e exames", "Serviços de saúde"]),
        new(["FARMACIA", "PARAFARMACIA"], ["Farmácia", "Medicamentos", "Saúde"]),
        new(["RESTAURANTE", "RESTAURACAO", "REST", "TASCA", "TASQUINHA", "MARISQUEIRA", "CHURRASQUEIRA", "CANTINHO DO CHURRASCLJ", "CANTINHO DO CHURRASC1006", "CANTINHO DO CHURRASCL006", "PIZZARIA", "PIZZERIA", "PIZZA", "TELEPIZZA", "PASTELARIA", "PASTELERIAS", "CAFETARIA", "CAFETERIA", "CAFET", "CAFE", "CAFECOP", "A CAFETEIRA CARTAXO", "BAGGA", "PADARIA PORTUGUESA", "PAD PORT", "IKEA FOOD", "UBER EATS", "GLOVO", "MCDONALDS", "BURGER KING", "BURGER RANCH", "SNACK BAR", "BAR BENTOS", "BAR DO TO", "IN NOODLES", "IN NOODIE", "KONGFU NOODLES", "HAMBURGUERI CRIATIVALOURES", "XXL BURGUER", "FALAFEL OCH BURGERS"], ["Refeições fora e entregas", "Restaurantes", "Restauração", "Restaurantes e cafés", "Refeições fora", "Alimentação fora de casa"]),
        new(["TELPARK", "ESTACIONAMENTO", "PARQUIMETRO", "PARQUES TEJO", "PARQUE ALVALADE XXI"], ["Estacionamento", "Transportes"]),
        new(["PRIMARK", "CORTEFIEL", "SPRINGFIELD", "MR BLUE", "C A COLOMBO"], ["Roupa", "Outras despesas de vestuário"]),
        new(["DEICHMANN"], ["Calçado", "Outras despesas de vestuário"]),
        new(["CABELEIREIRO", "CABELEIREIROS", "BARBEARIA"], ["Cabeleireiro", "Estética e cuidados pessoais"]),
        new(["LAVANDARIA", "ENGOMADORIA"], ["Lavandaria e engomadoria"]),
        new(["CINEMA", "CINEMAS", "CINEPLACE"], ["Espetáculos e cultura"]),
        new(["GALP", "REPSOL", "BP", "CEPSA", "PRIO", "COMBUSTIVEL"], ["Combustíveis", "Combustível", "Transportes"]),
        new(["VIA VERDE", "PORTAGEM", "PORTAGENS"], ["Portagens", "Transportes"]),
        new(["CP COMBOIOS", "CARRIS", "METRO", "UBER", "BOLT"], ["Transportes públicos", "Transportes"]),
        new(["EDP", "ENDESA", "IBERDROLA", "ELETRICIDADE"], ["Eletricidade", "Energia", "Água, luz e gás", "Água, eletricidade e gás"]),
        new(["EPAL", "AGUAS", "SMAS", "SIMAR"], ["Água", "Água, luz e gás", "Água, eletricidade e gás"]),
        new(["VODAFONE", "MEO", "NOS", "DIGI"], ["Internet, televisão e telefone", "Telecomunicações", "Comunicações", "Internet e telefone"]),
        new(["NETFLIX", "SPOTIFY", "DISNEY", "HBO"], ["Streaming", "Subscrições", "Assinaturas", "Lazer"]),
        new(["RENDER COM"], ["Software e serviços digitais"]),
        new(["PRESTACAO CRD PESSOAL", "PRESTACAO CREDITO PESSOAL", "PREST CRED PESSOAL"], ["Crédito pessoal", "Outras prestações e dívidas", "Créditos e dívidas"]),
        new(["PRESTACAO CH", "PRESTACAO CREDITO HABITACAO"], ["Crédito à habitação", "Créditos e dívidas"]),
        new(["SOLINCA", "FITNESS HUT", "GINASIO"], ["Ginásio", "Modalidades desportivas"]),
        new(["LISBOAGAS", "LUSITANIAGAS", "GAS NATURAL"], ["Gás", "Casa"]),
        new(["ASISA VIDA", "GAMALIFE"], ["Vida", "Seguros"]),
        new(["MUDUM", "FIDELIDADE", "TRANQUILIDADE", "SEGURO"], ["Seguros"]),
        new(["DECO PROTESTE"], ["Associações de consumidores"]),
        new(["IMPOSTO RENDIMENTO S JUROS"], ["IRS e outras taxas"]),
        new(["COMISSAO", "COM S TRF", "IMP SELO", "IMPOSTO DO SELO", "MANUTENCAO CONTA", "IMPOSTO SELO"], ["Comissões bancárias", "Despesas bancárias", "Comissões", "Banco"]),
        new(["SALARIO", "VENCIMENTO", "ORDENADO"], ["Salário", "Salários", "Vencimento", "Ordenado", "Trabalho"]),
        new(["PENSAO", "REFORMA"], ["Pensão", "Pensões", "Reforma"]),
        new(["JUROS"], ["Juros", "Rendimentos financeiros"])
    ];

    private static async Task<Dictionary<int, BankClassificationSuggestion>> ClassifyAsync(FinanceDb db, StatementAccount section, long? accountId)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId && a.Active && a.Currency == "EUR");
        if (account is null) return [];
        var allCategories = await db.Categories.AsNoTracking().Where(c => c.HouseholdId == account.HouseholdId).ToListAsync();
        var categories = allCategories.Where(c => c.Active && (c.ParentId is null || allCategories.Any(p => p.Id == c.ParentId && p.Active))).ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.HouseholdId == account.HouseholdId && a.Active && a.Currency == "EUR").Select(a => a.Id).ToListAsync();
        var history = await (from t in db.Transactions.AsNoTracking()
                             join p in db.Postings.AsNoTracking() on t.Id equals p.TransactionId
                             where t.HouseholdId == account.HouseholdId && t.Status == "Confirmed" &&
                                 (t.Kind == MovementKind.Income || t.Kind == MovementKind.Expense || t.Kind == MovementKind.Transfer)
                             select new ClassificationHistory(t.Id, p.AccountId, p.CategoryId, p.AmountUnits, t.Kind, t.Description)).ToListAsync();
        var indexed = history.GroupBy(h => MerchantKey(h.Description ?? "")).Where(g => g.Key.Length >= 4).ToDictionary(g => g.Key, g => g.ToList());
        var references = await db.BankReferences.AsNoTracking().Where(r => r.AccountId == account.Id && r.Active).ToListAsync();
        var referenceMatches = section.Rows.ToDictionary(r => r.Ordinal, r => references.Where(reference => ReferenceMatches(reference, r, categories)).ToList());
        var sharedPurchases = referenceMatches.Values.SelectMany(matches => matches.Where(r => r.PurchaseDate != null)).GroupBy(r => r.Id)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var result = new Dictionary<int, BankClassificationSuggestion>();
        foreach (var row in section.Rows)
        {
            var known = referenceMatches[row.Ordinal];
            // A purchase-specific explanation takes priority over a recurring merchant rule.
            if (known.Any(r => r.PurchaseDate != null)) known = known.Where(r => r.PurchaseDate != null).ToList();
            else if (known.Any(r => r.Code.Length > 0)) known = known.Where(r => r.Code.Length > 0).ToList();
            if (known.Count > 0)
            {
                var choices = known.Select(r => (r.CategoryId, r.Description)).Distinct().ToList();
                if (choices.Count == 1 && !known.Any(r => sharedPurchases.Contains(r.Id)))
                {
                    var reference = known[0]; var category = categories.Single(c => c.Id == reference.CategoryId);
                    result[row.Ordinal] = new(category.Kind, category.Id, null, "Alta",
                        reference.PurchaseDate != null ? "Compra indicada por ti: entidade, montante e data compatíveis. Confere a correspondência." :
                        $"Referência indicada por ti: {reference.Merchant} {reference.Code}.") { Detail = reference.Description };
                }
                // Conflicting explanations or one purchase matching multiple charges require review.
                continue;
            }
            // The generic descriptor can identify a wallet load rather than the
            // later purchase. Neither old merchant history nor the supermarket
            // keyword establishes what was bought or whether it is already booked.
            if (PossibleContinenteFunding(row)) continue;
            var key = IsPaymentPlatform(Normalize(row.Description)) ? "" : MerchantKey(row.Description);
            if (key.Length >= 4 && indexed.TryGetValue(key, out var matches))
            {
                var candidates = new List<(MovementKind Kind, long? Category, long? Counterpart)>();
                foreach (var match in matches.Where(h => Math.Sign(h.AmountUnits) == Math.Sign(row.Units)))
                {
                    if (match.Kind == MovementKind.Transfer)
                    {
                        if (match.AccountId != account.Id) continue;
                        var posts = history.Where(h => h.TransactionId == match.TransactionId).ToList();
                        var other = posts.Count == 2 ? posts.FirstOrDefault(h => h.AccountId != account.Id) : null;
                        if (other?.AccountId is long otherId && accounts.Contains(otherId) && other.AmountUnits == -match.AmountUnits)
                            candidates.Add((match.Kind, null, otherId));
                    }
                    else if (categories.Any(c => c.Id == match.CategoryId && c.Kind == match.Kind))
                        candidates.Add((match.Kind, match.CategoryId, null));
                }
                var choices = candidates.Distinct().ToList();
                if (choices.Count == 1)
                {
                    var choice = choices[0];
                    result[row.Ordinal] = new(choice.Kind, choice.Category, choice.Counterpart, candidates.Count >= 2 ? "Alta" : "Média", "Histórico: mesma descrição ou entidade em movimentos já classificados.");
                    continue;
                }
                // Conflicting history deserves a human decision, not an arbitrary rule.
                if (choices.Count > 1) continue;
            }
            var kind = row.Units > 0 ? MovementKind.Income : MovementKind.Expense;
            var description = Normalize(row.Description);
            foreach (var rule in ClassificationRules.Where(r => r.Descriptions.Any(d => ContainsWords(description, d))).Take(1))
            {
                Category? category = null;
                foreach (var alias in rule.Categories)
                {
                    var found = categories.Where(c => c.Kind == kind && Normalize(c.Name) == Normalize(alias)).ToList();
                    if (found.Count > 1 && found.Count(c => c.ParentId == null) == 1) found = found.Where(c => c.ParentId == null).ToList();
                    if (found.Count == 1) { category = found[0]; break; }
                }
                if (category is null) continue;
                result[row.Ordinal] = new(kind, category.Id, null, "Média", "Descrição do banco: entidade ou operação reconhecida. Confere a categoria sugerida.");
                break;
            }
        }
        return result;
    }

    private static bool ContainsWords(string text, string words) => (" " + text + " ").Contains(" " + words + " ", StringComparison.Ordinal);
    private static string Normalize(string text)
    {
        var plain = new string(text.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToUpperInvariant();
        return Regex.Replace(Regex.Replace(plain, "[^A-Z0-9]+", " "), @"\s+", " ").Trim();
    }
    private static string MerchantKey(string description)
    {
        var noise = new HashSet<string>(["COMPRA", "COMPRAS", "PAG", "PAGAMENTO", "PAGAMENTOS", "POS", "TPA", "MB", "MULTIBANCO", "CARTAO", "DE", "DO", "DA", "EM", "REF", "REFERENCIA", "TRF", "TRANSFERENCIA", "SEPA", "DD", "DEBITO", "DIRETO"], StringComparer.Ordinal);
        var normalized = Normalize(description);
        var keepCodes = IsPaymentPlatform(normalized) || ContainsWords(normalized, "PRESTACAO") || ContainsWords(normalized, "CREDITO") || ContainsWords(normalized, "CRD");
        return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !noise.Contains(t) && (!t.Any(char.IsDigit) || (keepCodes && !Regex.IsMatch(t, @"^20\d{6}$")))));
    }
}
