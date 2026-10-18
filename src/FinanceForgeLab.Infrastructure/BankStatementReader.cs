using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FinanceForgeLab.Core;

namespace FinanceForgeLab.Infrastructure;

public static class BankStatementReader
{
    public static BankStatement Read(string path)
    {
        var (pages, lines) = BankPdfContent.Read(path);
        var statement = lines.Any(l => l.Text.Contains("Revolut Bank UAB", StringComparison.Ordinal))
            ? RevolutStatementReader.Parse(lines) : NovoBancoStatementReader.Parse(lines);
        statement.Pages = pages;
        using var stream = File.OpenRead(path);
        statement.Sha256 = Convert.ToHexString(SHA256.HashData(stream));
        return statement;
    }
}

public static class RevolutStatementReader
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    public static BankStatement Parse(IEnumerable<BankPdfLine> source)
    {
        var lines = source.ToList();
        if (lines.Any(l => Regex.Replace(l.Text, @"\s", "") == "ExtratodeEUR")) return ParsePortuguese(lines);
        var result = new BankStatement { BankName = "Revolut", ReaderVersion = "revolut-eur-1" };
        if (!lines.Any(l => Regex.Replace(l.Text, @"\s", "").Contains("EURStatement", StringComparison.Ordinal)))
        { result.Errors.Add("Só são suportados extratos Revolut em EUR."); return result; }
        var headers = lines.Where(l => l.Text.StartsWith("Account transactions from ", StringComparison.Ordinal)).ToList();
        var totals = lines.Where(l => l.Text.StartsWith("Account (Current Account)", StringComparison.Ordinal)).ToList();
        var references = Regex.Matches(string.Join("\n", lines.Select(l => l.Text)), @"\bIBAN\s+([A-Z]{2}\d{2}[A-Z0-9]{11,30})\b").Select(m => m.Groups[1].Value).Distinct().ToList();
        if (headers.Count != 1 || totals.Count != 1 || references.Count != 1)
        { result.Errors.Add("Confere o período, o IBAN e os totais da conta Revolut."); return result; }
        var period = Regex.Match(headers[0].Text, @"^Account transactions from (.+) to (.+)$");
        if (!period.Success || !Date(period.Groups[1].Value, out var from) || !Date(period.Groups[2].Value, out var to) || from > to || to.DayNumber - from.DayNumber > 370)
        { result.Errors.Add("O período do extrato Revolut não é suportado."); return result; }
        var amounts = Regex.Matches(totals[0].Text, @"€(-?(?:\d{1,3}(?:,\d{3})*|\d+)\.\d{2})").Select(m => Money.ToUnits(decimal.Parse(m.Groups[1].Value, NumberStyles.Number, English))).ToList();
        if (amounts.Count != 4 || amounts[1] < 0 || amounts[2] < 0)
        { result.Errors.Add("Os saldos e os totais Revolut não foram reconhecidos."); return result; }
        var account = new StatementAccount { Reference = references[0], Name = "Conta à ordem", From = from, To = to, OpeningUnits = amounts[0], DebitTotalUnits = amounts[1], CreditTotalUnits = amounts[2], ClosingUnits = amounts[3] };
        result.Accounts.Add(account);
        var ibanLine = lines.FindIndex(l => Regex.IsMatch(l.Text, @"\bIBAN\s+"));
        if (ibanLine > 0 && Regex.IsMatch(lines[ibanLine - 1].Text, @"^[\p{L}\s.-]{8,}$")) result.AccountHolder = lines[ibanLine - 1].Text;
        StatementMovement? pending = null;long balance = amounts[0], debits = 0, credits = 0;
        foreach (var line in lines)
        {
            var dated = Regex.Match(line.Text, @"^([A-Z][a-z]{2} \d{1,2}, \d{4})\s");
            if (dated.Success)
            {
                if (!Date(dated.Groups[1].Value, out var date) || date < from || date > to || line.Cells.Length != 6)
                { account.Errors.Add($"Página {line.Page}, linha {line.Number}: data ou colunas inválidas."); continue; }
                try
                {
                    var debit = Amount(line.Cells[3]);var credit = Amount(line.Cells[4]);var closing = Amount(line.Cells[5], true);
                    if ((debit == 0) == (credit == 0) || debit < 0 || credit < 0 || line.Cells[2].Length == 0) throw new FormatException();
                    var units = checked(credit - debit);balance = checked(balance + units);debits = checked(debits + debit);credits = checked(credits + credit);
                    if (balance != closing) account.Errors.Add($"Página {line.Page}, linha {line.Number}: o saldo Revolut não confere.");
                    pending = new StatementMovement { Ordinal = account.Rows.Count + 1, Page = line.Page, Line = line.Number, Date = date, ValueDate = date, Description = line.Cells[2], Units = units, BalanceUnits = closing };
                    account.Rows.Add(pending);
                }
                catch (Exception e) when (e is FormatException or OverflowException)
                { account.Errors.Add($"Página {line.Page}, linha {line.Number}: montante Revolut não reconhecido."); }
                continue;
            }
            // Foreign-currency amounts and fee breakdowns are supplementary: the main EUR debit already includes the fee.
            if (pending is null || line.Page != pending.Page) continue;
            var continuation = line.Number == pending.Line + 1 && line.Cells.Take(2).All(string.IsNullOrWhiteSpace) && line.Cells.Skip(3).All(string.IsNullOrWhiteSpace);
            if (line.Text.StartsWith("Reference: ", StringComparison.Ordinal) || line.Text.StartsWith("From: ", StringComparison.Ordinal) || line.Text.StartsWith("To: ", StringComparison.Ordinal))
                pending.Description += " · " + line.Text;
            else if (continuation && line.Cells[2].Length > 0) pending.Description += " " + line.Cells[2];
        }
        if (balance != account.ClosingUnits || debits != account.DebitTotalUnits || credits != account.CreditTotalUnits)
            account.Errors.Add("Os movimentos Revolut não conferem com os totais e o saldo final.");
        if (account.Rows.Any(r => r.Description.Length > 500)) account.Errors.Add("Uma descrição Revolut excede 500 caracteres.");
        return result;
    }
    private static bool Date(string text, out DateOnly date) => DateOnly.TryParseExact(Regex.Replace(text, @"(?<=[A-Za-z])\s+(?=[A-Za-z])", ""), ["MMMM d, yyyy", "MMM d, yyyy"], English, DateTimeStyles.None, out date);
    private static BankStatement ParsePortuguese(List<BankPdfLine> lines)
    {
        var result = new BankStatement { BankName = "Revolut", ReaderVersion = "revolut-pt-eur-1" };
        StatementAccount? account = null; StatementMovement? pending = null;
        string currency = ""; int footerPage = -1; var otherCurrencies = new HashSet<string>();
        foreach (var line in lines)
        {
            var compact = Regex.Replace(line.Text, @"\s", "");
            if (Regex.IsMatch(compact, @"^Extratode[A-Z]{3}$"))
            {
                var next = compact[^3..];
                if (next != currency) { account = null; pending = null; }
                currency = next;
            }
            if (line.Page == footerPage) continue;
            if (line.Text.StartsWith("Comunicar perda ou roubo", StringComparison.Ordinal)) { footerPage = line.Page; pending = null; continue; }
            if (currency != "EUR")
            { if (currency.Length > 0) otherCurrencies.Add(currency); continue; }
            if (line.Text.StartsWith("Conta (Conta Corrente)", StringComparison.Ordinal))
            {
                var values = Regex.Matches(line.Text, @"(-?(?:\d{1,3}(?:[. ]\d{3})*|\d+),\d{2})€").Select(m => PortugueseAmount(m.Value)).ToList();
                var header = lines.Where(l => l.Page == line.Page && l.Number < line.Number).ToList();
                var refs = Regex.Matches(string.Join("\n", header.Select(l => l.Text)), @"\bIBAN\s+([A-Z]{2}\d{2}[A-Z0-9]{11,30})\b").Select(m => m.Groups[1].Value).Distinct().ToList();
                // Two identifiers are printed together when the customer's account
                // migrates to the Portuguese branch; retain that explicit relationship.
                if (values.Count != 4 || refs.Count is < 1 or > 2 || (refs.Count == 2 && (!refs.Any(r => r.StartsWith("PT")) || !refs.Any(r => r.StartsWith("LT")))))
                { result.Errors.Add($"Página {line.Page}: conta ou totais Revolut não reconhecidos."); account = null; continue; }
                account = new() { Reference = refs.FirstOrDefault(r => r.StartsWith("PT")) ?? refs[0], ReferenceAliases = refs, Name = "Conta à ordem", OpeningUnits = values[0], DebitTotalUnits = values[1], CreditTotalUnits = values[2], ClosingUnits = values[3] };
                result.Accounts.Add(account); pending = null;
                if (result.AccountHolder.Length == 0)
                {
                    var iban = header.FindIndex(l => Regex.IsMatch(l.Text, @"\bIBAN\s+"));
                    if (iban > 0 && Regex.IsMatch(header[iban - 1].Text, @"^[\p{L}\s.-]{8,}$")) result.AccountHolder = header[iban - 1].Text;
                }
                continue;
            }
            if (account is null) continue;
            var period = Regex.Match(compact, @"^Operaçõesdacontade(\d{1,2}de[\p{L}]+de\d{4})a(\d{1,2}de[\p{L}]+de\d{4})$");
            if (period.Success)
            {
                if (!PortugueseDate(period.Groups[1].Value, out var from) || !PortugueseDate(period.Groups[2].Value, out var to) || from > to || to.DayNumber - from.DayNumber > 370)
                    account.Errors.Add("O período do extrato Revolut não é suportado.");
                else { account.From = from; account.To = to; }
                continue;
            }
            var row = Regex.Match(line.Text, @"^(?<date>\d{2}/\d{2}/\d{4})\s+(?:(?<value>\d{2}/\d{2}/\d{4})\s+)?(?<description>.+?)\s+(?<amount>-?(?:\d{1,3}(?:[. ]\d{3})*|\d+),\d{2}€)\s+(?<balance>-?(?:\d{1,3}(?:[. ]\d{3})*|\d+),\d{2}€)$");
            if (row.Success)
            {
                try
                {
                    var date = DateOnly.ParseExact(row.Groups["date"].Value, "dd/MM/yyyy", CultureInfo.InvariantCulture);
                    var valueDate = row.Groups["value"].Success ? DateOnly.ParseExact(row.Groups["value"].Value, "dd/MM/yyyy", CultureInfo.InvariantCulture) : date;
                    var balance = PortugueseAmount(row.Groups["balance"].Value);
                    var previous = account.Rows.LastOrDefault()?.BalanceUnits ?? account.OpeningUnits!.Value;
                    var units = checked(balance - previous);
                    if (date > account.To || (date < account.From && (date < account.From.AddDays(-7) || valueDate < account.From || valueDate > account.To)) || Math.Abs(units) != PortugueseAmount(row.Groups["amount"].Value) || units == 0) throw new FormatException();
                    pending = new() { Ordinal = account.Rows.Count + 1, Page = line.Page, Line = line.Number, Date = date, ValueDate = valueDate, Description = row.Groups["description"].Value, Units = units, BalanceUnits = balance };
                    account.Rows.Add(pending);
                }
                catch (Exception e) when (e is FormatException or OverflowException) { account.Errors.Add($"Página {line.Page}, linha {line.Number}: movimento Revolut inválido."); pending = null; }
                continue;
            }
            if (Regex.IsMatch(line.Text, @"^\d{2}/\d{2}/\d{4}\s")) { account.Errors.Add($"Página {line.Page}, linha {line.Number}: movimento Revolut não reconhecido."); pending = null; continue; }
            if (pending is not null && line.Page == pending.Page && !Regex.IsMatch(line.Text, @"€|\b(?:CZK|DKK|USD|GBP)\b") && (line.Text.StartsWith("Referência:") || line.Text.StartsWith("De:") || line.Text.StartsWith("Para:") || line.Number == pending.Line + 1))
                pending.Description += " · " + line.Text;
        }
        if (result.Accounts.Count == 0) result.Errors.Add("Não foram encontradas operações Revolut em EUR.");
        foreach (var a in result.Accounts)
        {
            if (a.From == default || a.To == default || a.DebitTotalUnits < 0 || a.CreditTotalUnits < 0) a.Errors.Add("Faltam datas ou totais válidos no extrato Revolut.");
            if (a.OpeningUnits + a.Rows.Sum(r => r.Units) != a.ClosingUnits || a.Rows.Where(r => r.Units < 0).Sum(r => -r.Units) != a.DebitTotalUnits || a.Rows.Where(r => r.Units > 0).Sum(r => r.Units) != a.CreditTotalUnits)
                a.Errors.Add("Os movimentos Revolut não conferem com os totais e o saldo final.");
            if (a.Rows.Any(r => r.Description.Length > 500)) a.Errors.Add("Uma descrição Revolut excede 500 caracteres.");
        }
        if (otherCurrencies.Count > 0) result.Notices.Add("O PDF também contém secções em " + string.Join(", ", otherCurrencies.Order()) + ". Estas secções estão preservadas no original e não foram importadas para contas em EUR.");
        return result;
    }
    private static long PortugueseAmount(string text) => Money.ToUnits(decimal.Parse(text.Replace("€", "").Replace(" ", "").Replace(".", "").Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
    private static bool PortugueseDate(string text, out DateOnly date)
    {
        var match = Regex.Match(text, @"^(\d{1,2})de([\p{L}]+)de(\d{4})$");
        var months = new[] { "janeiro", "fevereiro", "março", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro" };
        var month = match.Success ? Array.IndexOf(months, match.Groups[2].Value) + 1 : 0;
        return DateOnly.TryParseExact(month > 0 ? $"{match.Groups[3].Value}-{month:00}-{int.Parse(match.Groups[1].Value):00}" : "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
    private static long Amount(string cell, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(cell) && !required) return 0;
        if (!Regex.IsMatch(cell, @"^€-?(?:\d{1,3}(?:,\d{3})*|\d+)\.\d{2}$")) throw new FormatException();
        return Money.ToUnits(decimal.Parse(cell[1..], NumberStyles.Number, English));
    }
}
