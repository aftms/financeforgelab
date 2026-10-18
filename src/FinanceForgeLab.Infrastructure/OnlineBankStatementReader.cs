using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace FinanceForgeLab.Infrastructure;

/// <summary>A copy supplied by the owner, with bank booking/value dates and running balances.</summary>
public static class OnlineBankStatementReader
{
    public const string Version = "novobanco-online-copy-1";
    public static BankStatement Read(byte[] content)
    {
        if (content.Length is < 1 or > 1024 * 1024) throw new FinanceRuleException("A cópia de movimentos deve ter até 1 MB.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(content).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new FinanceRuleException("A cópia deve estar em UTF-8."); }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var header = Regex.Match(lines[0].Trim(), @"^Conta\s+(\d{4})\s+(\d{4})\s+(\d{4})\s+\((\d{2}-\d{2}-\d{4}) \d{2}:\d{2}:\d{2}\)$");
        if (!header.Success || !DateOnly.TryParseExact(header.Groups[4].Value, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var observed))
            throw new FinanceRuleException("Indica a conta completa e a data da consulta na primeira linha.");
        var section = new StatementAccount { Reference = header.Groups[1].Value + header.Groups[2].Value + header.Groups[3].Value, Name = "Conta à ordem" };
        var statement = new BankStatement { ReaderVersion = Version, Sha256 = Convert.ToHexString(SHA256.HashData(content)), Accounts = [section],
            Notices = ["Movimentos copiados do banco e fornecidos pelo titular. Os saldos conferem a sequência apresentada; esta cópia não garante a cobertura integral do período."] };
        var pattern = @"^(\d{2}-\d{2}-\d{4})\s+(\d{2}-\d{2}-\d{4})\s+(.+?)\s+([+-]\s*[\d .\u00A0]+,\d{2})\s*€\s+([+-]\s*[\d .\u00A0]+,\d{2})\s*€\s*$";
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line == "Ver Detalhes") continue;
            var m = Regex.Match(line, pattern);
            if (!m.Success || !DateOnly.TryParseExact(m.Groups[1].Value,"dd-MM-yyyy",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date) ||
                !DateOnly.TryParseExact(m.Groups[2].Value,"dd-MM-yyyy",CultureInfo.InvariantCulture,DateTimeStyles.None,out var valueDate))
                throw new FinanceRuleException($"Linha {i+1}: não foi possível ler todos os campos. A cópia não foi importada.");
            if (date > observed || section.Rows.Count > 0 && date > section.Rows[^1].Date)
                throw new FinanceRuleException($"Linha {i+1}: as datas devem estar da mais recente para a mais antiga, até à data da consulta.");
            long Money(string s) => checked((long)(decimal.Parse(Regex.Replace(s,@"[\s.\u00A0]",""),NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.GetCultureInfo("pt-PT"))*10_000));
            var row = new StatementMovement { Ordinal = section.Rows.Count+1, Line = i+1, Date = date, ValueDate = valueDate,
                Description = m.Groups[3].Value, Units = Money(m.Groups[4].Value), BalanceUnits = Money(m.Groups[5].Value) };
            if (row.Units == 0 || row.Description.Length > 500) throw new FinanceRuleException($"Linha {i+1}: descrição ou montante inválido.");
            section.Rows.Add(row);
        }
        if (section.Rows.Count is < 1 or > 10_000) throw new FinanceRuleException("A cópia deve conter entre 1 e 10000 movimentos.");
        section.From = section.Rows[^1].Date; section.To = section.Rows[0].Date;
        section.OpeningUnits = checked(section.Rows[^1].BalanceUnits - section.Rows[^1].Units);
        section.ClosingUnits = section.Rows[0].BalanceUnits;
        section.DebitTotalUnits = -section.Rows.Where(r=>r.Units<0).Sum(r=>r.Units);
        section.CreditTotalUnits = section.Rows.Where(r=>r.Units>0).Sum(r=>r.Units);
        for (int i=0; i<section.Rows.Count-1; i++)
            if (checked(section.Rows[i+1].BalanceUnits + section.Rows[i].Units) != section.Rows[i].BalanceUnits)
                section.Errors.Add($"Linha {section.Rows[i].Line}: o saldo não confere com o movimento anterior.");
        return statement;
    }
}

public sealed partial class BankImportService
{
    public async Task<List<long>> StageOnlineCopyAsync(string name, byte[] content) => await ReconcileStagedAsync(await StageOnlineCopyCoreAsync(name,content));
    private async Task<List<long>> StageOnlineCopyCoreAsync(string name, byte[] content)
    {
        var statement = OnlineBankStatementReader.Read(content);
        // Copies supplement an existing account. Never infer a new account/opening from a partial list.
        if (!statement.Valid) throw new FinanceRuleException(string.Join(" ",statement.Accounts.SelectMany(s=>s.Errors)));
        await StageGate.WaitAsync();
        try
        {
            await using var db = open(); await using var tx = await db.Database.BeginTransactionAsync();
            var sha = statement.Sha256;
            var existing = await db.BankImports.Where(b=>b.DocumentId==sha&&b.Status!="Voided").Select(b=>b.Id).ToListAsync();
            if (existing.Count>0) return existing;
            var reference = await CanonicalReferenceAsync(db,statement.Accounts[0]);
            var known = await db.BankImports.Where(b=>b.SourceReference==reference&&b.AccountId!=null).ToListAsync();
            if (known.Select(b=>b.AccountId).Distinct().Count()!=1) throw new FinanceRuleException("Associa primeiro esta conta a um extrato bancário original.");
            var people = known.Where(b=>b.PersonId!=null).Select(b=>b.PersonId).Distinct().ToList();
            if (!await db.BankDocuments.AnyAsync(d=>d.Id==sha)) db.Add(new BankDocument {Id=sha,Name=Path.GetFileNameWithoutExtension(name)+".txt",ReaderVersion=statement.ReaderVersion,StatementJson=JsonSerializer.Serialize(statement),Content=content.ToArray(),CreatedUtc=DateTime.UtcNow});
            var batch = new BankImport {DocumentId=sha,SourceReference=reference,AccountId=known[0].AccountId,PersonId=people.Count==1?people[0]:null};
            db.Add(batch); await db.SaveChangesAsync();
            db.Add(new AuditChange {Entity="BankImport",EntityId=batch.Id,Action="StageOnlineCopy",Actor="Owner",CreatedUtc=DateTime.UtcNow,
                DetailJson=JsonSerializer.Serialize(new{Source="Cópia fornecida pelo titular",Sha256=sha,batch.SourceReference,batch.AccountId,batch.PersonId,Rows=statement.Accounts[0].Rows.Count})});
            await db.SaveChangesAsync(); await tx.CommitAsync(); return [batch.Id];
        }
        finally {StageGate.Release();}
    }
}
