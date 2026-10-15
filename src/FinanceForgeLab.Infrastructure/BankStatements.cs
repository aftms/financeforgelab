using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FinanceForgeLab.Core;
using PDFiumCore;

namespace FinanceForgeLab.Infrastructure;

public sealed record BankPdfLine(int Page, int Number, string Text, string[] Cells);
public sealed class BankStatement
{
    public string Sha256 { get; set; } = "";
    public string ReaderVersion { get; set; } = "novobanco-1";
    public int Pages { get; set; }
    public List<StatementAccount> Accounts { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public bool Valid => Errors.Count == 0 && Accounts.Count > 0 && Accounts.All(a => a.Errors.Count == 0);
}
public sealed class StatementAccount
{
    public string Reference { get; set; } = "";
    public string Name { get; set; } = "";
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public long? OpeningUnits { get; set; }
    public long? ClosingUnits { get; set; }
    public long? DebitTotalUnits { get; set; }
    public long? CreditTotalUnits { get; set; }
    public List<StatementMovement> Rows { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public string DisplayReference => "•••• " + Reference[^Math.Min(4, Reference.Length)..];
}
public sealed class StatementMovement
{
    public int Ordinal { get; set; }
    public int Page { get; set; }
    public int Line { get; set; }
    public DateOnly Date { get; set; }
    public DateOnly ValueDate { get; set; }
    public string Description { get; set; } = "";
    public long Units { get; set; }
    public long BalanceUnits { get; set; }
}

/// <summary>Native PDFium access is serialized: the underlying library is not thread safe.</summary>
public static class BankPdfContent
{
    private static readonly object Gate = new();
    private static bool initialized;
    public static (int Pages, List<BankPdfLine> Lines) Read(string path)
    {
        if (new FileInfo(path).Length > 20 * 1024 * 1024) throw new FinanceRuleException("O PDF excede o limite de 20 MB.");
        lock (Gate)
        {
            if (!initialized) { fpdfview.FPDF_InitLibrary(); initialized = true; }
            var document = fpdfview.FPDF_LoadDocument(path, "");
            if (document is null || document.__Instance == IntPtr.Zero) throw new FinanceRuleException("Não foi possível ler o PDF. Verifica se está protegido por palavra-passe ou danificado.");
            try
            {
                var count = fpdfview.FPDF_GetPageCount(document);
                if (count is < 1 or > 80) throw new FinanceRuleException("O PDF deve ter entre 1 e 80 páginas.");
                var lines = new List<BankPdfLine>();
                for (var p = 0; p < count; p++)
                {
                    var page = fpdfview.FPDF_LoadPage(document, p);
                    if (page is null) throw new FinanceRuleException("Não foi possível ler uma página do PDF.");
                    try
                    {
                        var width = fpdfview.FPDF_GetPageWidthF(page); var height = fpdfview.FPDF_GetPageHeightF(page);
                        var textPage = fpdf_text.FPDFTextLoadPage(page);
                        if (textPage is null) throw new FinanceRuleException("Não foi possível ler o texto do PDF.");
                        try
                        {
                            var chars = fpdf_text.FPDFTextCountChars(textPage);
                            if (chars > 150_000) throw new FinanceRuleException("O PDF contém uma página demasiado complexa.");
                            var glyphs = new List<(double X, double Y, string Text)>();
                            for (var i = 0; i < chars; i++)
                            {
                                var code = fpdf_text.FPDFTextGetUnicode(textPage, i);
                                if (code < 32 || code > 0x10ffff || code is 0xfffe or 0xffff || code is >= 0xd800 and <= 0xdfff) continue;
                                double x = 0, y = 0;
                                if (fpdf_text.FPDFTextGetCharOrigin(textPage, i, ref x, ref y) == 0 || x < width * .035 || x > width * .97 || y < height * .035 || y > height * .965) continue;
                                glyphs.Add((x / width, y, char.ConvertFromUtf32((int)code)));
                            }
                            var grouped = new List<List<(double X, double Y, string Text)>>();
                            foreach (var glyph in glyphs.OrderByDescending(g => g.Y).ThenBy(g => g.X))
                            {
                                if (grouped.Count == 0 || Math.Abs(grouped[^1][0].Y - glyph.Y) > 2) grouped.Add([]);
                                grouped[^1].Add(glyph);
                            }
                            var number = 0; double columnOffset = 0;
                            foreach (var group in grouped)
                            {
                                var ordered = group.OrderBy(g => g.X).ToList(); var cells = new string[6];
                                var fullText = Join(ordered);
                                // The bank indents continuation-page tables. Anchor the six
                                // columns to the first "Data" label instead of the page edge.
                                if (fullText.Contains("Descritivo", StringComparison.OrdinalIgnoreCase) && fullText.Contains("Saldo", StringComparison.OrdinalIgnoreCase)) columnOffset = ordered[0].X - .052;
                                for (var c = 0; c < 6; c++) cells[c] = Join(ordered.Where(g => Column(g.X - columnOffset) == c));
                                lines.Add(new(p + 1, ++number, fullText, cells));
                            }
                        }
                        finally { fpdf_text.FPDFTextClosePage(textPage); }
                    }
                    finally { fpdfview.FPDF_ClosePage(page); }
                }
                return (count, lines);
            }
            finally { fpdfview.FPDF_CloseDocument(document); }
        }
    }
    private static int Column(double x) => x < .105 ? 0 : x < .165 ? 1 : x < .548 ? 2 : x < .665 ? 3 : x < .781 ? 4 : 5;
    private static string Join(IEnumerable<(double X, double Y, string Text)> glyphs)
    {
        var result = new StringBuilder(); double? previous = null;
        foreach (var glyph in glyphs)
        {
            if (previous is double x && glyph.X - x > .014) result.Append(' ');
            result.Append(glyph.Text); previous = glyph.X;
        }
        return Regex.Replace(result.ToString(), @"\s+", " ").Trim();
    }
}

public static partial class NovoBancoStatementReader
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-PT");
    public static BankStatement Read(string path)
    {
        var (pages, lines) = BankPdfContent.Read(path);
        var result = Parse(lines); result.Pages = pages;
        using var stream = File.OpenRead(path); result.Sha256 = Convert.ToHexString(SHA256.HashData(stream));
        return result;
    }
    public static BankStatement Parse(IEnumerable<BankPdfLine> source)
    {
        var result = new BankStatement(); StatementAccount? current = null; StatementMovement? pending = null; bool table = false; int footerPage = -1;
        void Flush() { if (pending is not null && current is not null) { pending.Ordinal = current.Rows.Count + 1; current.Rows.Add(pending); pending = null; } }
        foreach (var line in source)
        {
            var normalized = Fold(line.Text);
            if (line.Page == footerPage) continue;
            if (normalized.StartsWith("NOVO BANCO, S.A., COM SEDE", StringComparison.Ordinal) || normalized.StartsWith("O NOVO BANCO, S.A. E UMA ENTIDADE", StringComparison.Ordinal) || normalized.StartsWith("SEDE: CAMPUS DO NOVOBANCO", StringComparison.Ordinal)) { footerPage = line.Page; continue; }
            var header = AccountHeader().Match(line.Text);
            if (header.Success)
            {
                Flush();
                var reference = Regex.Replace(header.Groups["ref"].Value, @"\s", "");
                var from = DateOnly.ParseExact(header.Groups["from"].Value, "dd.MM.yyyy", Portuguese);
                var to = DateOnly.ParseExact(header.Groups["to"].Value, "dd.MM.yyyy", Portuguese);
                current = result.Accounts.FirstOrDefault(a => a.Reference == reference && a.From == from && a.To == to);
                if (current is null) { current = new() { Reference = reference, Name = header.Groups["name"].Value.Trim(), From = from, To = to }; if(from>to||to.DayNumber-from.DayNumber>370)current.Errors.Add("O período do extrato não é suportado."); result.Accounts.Add(current); }
                table = false; continue;
            }
            if (current is null) continue;
            if (normalized.Contains("DESCRITIVO") && normalized.Contains("DEBITO") && normalized.Contains("CREDITO"))
            {
                if (!normalized.Contains("SALDO (EUROS)")) current.Errors.Add("A tabela não identifica saldos em EUR. Esta moeda não é suportada.");
                table = true; continue;
            }
            if (!table) continue;
            var cells = line.Cells;
            if (cells.Length != 6) { current.Errors.Add($"Página {line.Page}, linha {line.Number}: colunas inválidas."); continue; }
            if (normalized.Contains("SALDO ANTERIOR"))
            {
                Flush(); var opening = Amount(cells[5]);
                if (current.OpeningUnits is not null && current.OpeningUnits != opening) current.Errors.Add("Foram encontrados saldos iniciais incompatíveis.");
                current.OpeningUnits = opening; continue;
            }
            if (normalized.Contains("SALDO CONTABILISTICO")) { Flush(); current.ClosingUnits = Amount(cells[5]); table = false; continue; }
            if (normalized.StartsWith("TOTAL ", StringComparison.Ordinal))
            {
                Flush(); current.DebitTotalUnits = Amount(cells[3]); current.CreditTotalUnits = Amount(cells[4]); continue;
            }
            var dates = RowDates().Match(line.Text);
            if (dates.Success)
            {
                Flush();
                try
                {
                    var date = Date(dates.Groups["date"].Value, current); var valueDate = Date(dates.Groups["value"].Value, current);
                    var debit = string.IsNullOrWhiteSpace(cells[3]) ? 0 : Amount(cells[3]);
                    var credit = string.IsNullOrWhiteSpace(cells[4]) ? 0 : Amount(cells[4]);
                    if ((debit == 0) == (credit == 0) || debit < 0 || credit < 0) throw new FormatException();
                    if (date < current.From || date > current.To) throw new FormatException();
                    pending = new() { Page = line.Page, Line = line.Number, Date = date, ValueDate = valueDate, Description = cells[2], Units = checked(credit - debit), BalanceUnits = Amount(cells[5]) };
                }
                catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
                { current.Errors.Add($"Página {line.Page}, linha {line.Number}: data, débito, crédito ou saldo inválido."); }
                continue;
            }
            if (pending is not null && !string.IsNullOrWhiteSpace(cells[2]) && cells.Where((_, i) => i != 2).All(string.IsNullOrWhiteSpace)) pending.Description += " " + cells[2];
            else if (cells[0].Any(char.IsDigit) || cells[3].Any(char.IsDigit) || cells[4].Any(char.IsDigit) || cells[5].Any(char.IsDigit))
                current.Errors.Add($"Página {line.Page}, linha {line.Number}: conteúdo da tabela não reconhecido.");
        }
        Flush();
        if (result.Accounts.Count == 0) result.Errors.Add("Não foi encontrada uma tabela de movimentos novobanco suportada. PDFs digitalizados ainda não são suportados.");
        foreach (var account in result.Accounts)
        {
            if (account.OpeningUnits is null || account.ClosingUnits is null || account.DebitTotalUnits is null || account.CreditTotalUnits is null) { account.Errors.Add("O extrato não contém todos os saldos e totais necessários à conferência."); continue; }
            try
            {
                long balance = account.OpeningUnits.Value, debit = 0, credit = 0;
                foreach (var row in account.Rows)
                {
                    balance = checked(balance + row.Units);
                    if (balance != row.BalanceUnits) account.Errors.Add($"Página {row.Page}, linha {row.Line}: o saldo não confere com o movimento.");
                    if (row.Units < 0) debit = checked(debit - row.Units); else credit = checked(credit + row.Units);
                    if (string.IsNullOrWhiteSpace(row.Description)) account.Errors.Add($"Página {row.Page}, linha {row.Line}: descrição vazia.");
                }
                if (balance != account.ClosingUnits || debit != account.DebitTotalUnits || credit != account.CreditTotalUnits) account.Errors.Add("Os movimentos extraídos não conferem com os totais e o saldo final.");
            }
            catch (OverflowException) { account.Errors.Add("Um total excede os limites suportados."); }
        }
        return result;
    }
    private static long Amount(string value)
    {
        value = value.Trim();
        if (!MoneyPattern().IsMatch(value)) throw new FormatException("Formato monetário não suportado.");
        return Money.ToUnits(decimal.Parse(value.Replace(".", "").Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
    }
    private static DateOnly Date(string value, StatementAccount account)
    {
        if (!ShortDate().IsMatch(value)) throw new FormatException();
        var fields = value.Split('.'); var suffix = int.Parse(fields[2], CultureInfo.InvariantCulture);
        var year = Enumerable.Range(account.From.Year - 1, account.To.Year - account.From.Year + 3).SingleOrDefault(y => y % 100 == suffix);
        return year == 0 ? throw new FormatException() : new(year, int.Parse(fields[1], CultureInfo.InvariantCulture), int.Parse(fields[0], CultureInfo.InvariantCulture));
    }
    private static string Fold(string value) => new string(value.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToUpperInvariant();
    [GeneratedRegex(@"^(?<name>CONTA .+?)\s+n[º°o]\s*(?<ref>\d{4}\s*\d{4}\s*\d{4})\s+de\s+(?<from>\d{2}\.\d{2}\.\d{4})\s+a\s+(?<to>\d{2}\.\d{2}\.\d{4})$", RegexOptions.IgnoreCase)]
    private static partial Regex AccountHeader();
    [GeneratedRegex(@"^\d{2}\.\d{2}\.\d{2}$")]
    private static partial Regex ShortDate();
    [GeneratedRegex(@"^(?<date>\d{2}\.\d{2}\.\d{2})\s+(?<value>\d{2}\.\d{2}\.\d{2})\s")]
    private static partial Regex RowDates();
    [GeneratedRegex(@"^-?(?:\d{1,3}(?:\.\d{3})*|\d+),\d{2}$")]
    private static partial Regex MoneyPattern();
}
