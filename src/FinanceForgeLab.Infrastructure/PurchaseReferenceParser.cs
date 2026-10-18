using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FinanceForgeLab.Core;

namespace FinanceForgeLab.Infrastructure;

// Proposals only: the owner checks OCR, dates, amounts and categories before saving.
public static class PurchaseReferenceParser
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-PT");
    public static List<BankReferenceForm> Parse(string text, string merchant, long accountId, List<Category> categories)
    {
        if (text.Length > 100_000) throw new FinanceRuleException("Divide a lista em blocos menores.");
        var result = new List<BankReferenceForm>();
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // Documented list format: date;amount;description;code (optional).
        if (lines.Any(l => l.Contains(';') || l.Contains('\t')))
        {
            foreach (var line in lines)
            {
                var cells = SplitCells(line, line.Contains('\t') ? '\t' : ';');
                if (cells.Count < 3 || cells[0].Equals("Data", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(Create(cells[2], ParseDate(cells[0]), ParseAmount(cells[1]), cells.ElementAtOrDefault(3) ?? ""));
            }
        }
        else
        {
            // Group a purchase from its date through its items/total, until the next date.
            var blocks = new List<string>(); var block = new List<string>();
            foreach (var line in lines)
            {
                if (FindDate(line) != null && block.Any(l => FindDate(l) != null)) { blocks.Add(string.Join('\n', block)); block.Clear(); }
                block.Add(line);
            }
            if (block.Count > 0) blocks.Add(string.Join('\n', block));
            foreach (var part in blocks)
            {
                var prices = Regex.Matches(part, @"(?<![\d/.-])\d+(?:[ .]\d{3})*[,.]\d{2}(?!\d)").Select(m => ParseAmount(m.Value)).Where(a => a != null).Distinct().ToList();
                var code = Regex.Match(part, @"\b\d{3}-\d{7}-\d{7}\b").Value;
                var description = string.Join(" · ", part.Split('\n').Where(l => !Regex.IsMatch(l, @"^(ORDER PLACED|ENCOMENDA EFETUADA|TOTAL|ENVIAR PARA|SHIP TO)$", RegexOptions.IgnoreCase)));
                result.Add(Create(description[..Math.Min(description.Length, 500)], FindDate(part), prices.Count == 1 ? prices[0] : null, code));
            }
        }
        if (result.Count > 100) throw new FinanceRuleException("Carrega até 100 compras de cada vez.");
        if (result.Count == 0) throw new FinanceRuleException("Não encontrei compras na lista. Confere o texto ou usa Data;Montante;Descrição, uma compra por linha.");
        return result;

        BankReferenceForm Create(string description, DateOnly? date, decimal? amount, string code) => new BankReferenceForm()
        {
            IsPurchase = true, AccountId = accountId, Merchant = merchant, Description = description, PurchaseDate = date, Amount = amount,
            // Order IDs are kept in the detail, not required in the bank descriptor (which often omits them).
            Code = "", CategoryId = SuggestedCategory(description, categories) ?? 0
        }.WithOrderCode(code);
    }
    private static BankReferenceForm WithOrderCode(this BankReferenceForm form, string code)
    {
        if (!string.IsNullOrWhiteSpace(code) && !form.Description.Contains(code, StringComparison.Ordinal))
            form.Description = $"{form.Description} · Encomenda {code}";
        return form;
    }
    private static List<string> SplitCells(string line, char separator)
    {
        var cells = new List<string>(); var current = new StringBuilder(); bool quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; } else quoted = !quoted; }
            else if (line[i] == separator && !quoted) { cells.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(line[i]);
        }
        cells.Add(current.ToString().Trim()); return cells;
    }
    private static DateOnly? ParseDate(string text) => DateOnly.TryParseExact(text.Trim(), ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd.MM.yyyy", "d MMMM yyyy", "d 'de' MMMM 'de' yyyy"], Portuguese, DateTimeStyles.None, out var date) ? date :
        DateOnly.TryParseExact(text.Trim(), ["MMMM d, yyyy", "d MMMM yyyy"], CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out date) ? date : null;
    private static DateOnly? FindDate(string text)
    {
        var matches = Regex.Matches(text, @"\b(?:\d{4}-\d{2}-\d{2}|\d{1,2}[/.]\d{1,2}[/.]\d{4}|\d{1,2}(?: de)? [\p{L}]+(?: de)? \d{4}|[A-Za-z]+ \d{1,2}, \d{4})\b");
        return matches.Select(m => ParseDate(m.Value)).FirstOrDefault(d => d != null);
    }
    private static decimal? ParseAmount(string text)
    {
        text = text.Replace("€", "").Replace("EUR", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "").Trim();
        var culture = text.Contains(',') ? Portuguese : CultureInfo.InvariantCulture;
        return decimal.TryParse(text, NumberStyles.Number, culture, out var amount) && amount > 0 ? amount : null;
    }
    public static long? SuggestedCategory(string description, List<Category> categories)
    {
        var text = new string(description.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToUpperInvariant();
        (string Pattern, string[] Names)[] rules =
        [
            (@"\b(LIVRO|LIVROS|BOOK|BOOKS|KINDLE)\b", ["Livros e imprensa"]),
            (@"\b(COMPUTADOR|TECLADO|RATO|MONITOR|HEADPHONES|AUSCULTADORES|SSD|USB|LAPTOP|PRINTER)\b", ["Equipamentos eletrónicos", "Tecnologia"]),
            (@"\b(ROUPA|CAMISOLA|CAMISA|CALCAS|T SHIRT|SHIRT)\b", ["Roupa"]),
            (@"\b(SAPATOS|SAPATILHAS|SHOES)\b", ["Calçado"]),
            (@"\b(LEGO|BRINQUEDO|BRINQUEDOS|TOY|TOYS|PUZZLE)\b", ["Brinquedos e jogos"]),
            (@"\b(ASPIRADOR|TORRADEIRA|MICROONDAS|ELETRODOMESTICO|CAFETEIRA)\b", ["Mobiliário e eletrodomésticos", "Casa"])
        ];
        var matches = rules.Where(r => Regex.IsMatch(text, r.Pattern)).Select(r => r.Names.Select(name =>
            categories.SingleOrDefault(c => c.Name == name && c.Active && c.Kind == MovementKind.Expense &&
                (c.ParentId == null || categories.Any(p => p.Id == c.ParentId && p.Active)))?.Id).FirstOrDefault(id => id != null)).Where(id => id != null).Distinct().ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
