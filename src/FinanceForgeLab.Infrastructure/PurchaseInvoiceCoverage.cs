using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;
namespace FinanceForgeLab.Infrastructure;

public sealed record InvoiceGap(DateOnly Date,long Units,string Description,string Account,string? Person,bool PossibleTopUp);
public sealed record InvoiceCoverage(DateOnly? From,DateOnly? To,List<InvoiceGap> Gaps);
public sealed partial class BankImportService
{
    private static bool PossibleContinenteFunding(StatementMovement row)=>row.Units<0&&row.Units%100000==0&&ContainsWords(Normalize(row.Description),"CONTINENTE PORTUGAL");
    private static async Task<Dictionary<int,string>> FundingReviewWarningsAsync(FinanceDb db,StatementAccount section,long? accountId)
    {
        var rows=section.Rows.Where(PossibleContinenteFunding).ToList();
        if(rows.Count==0)return [];
        var household=await db.Accounts.Where(a=>a.Id==accountId).Select(a=>(long?)a.HouseholdId).SingleOrDefaultAsync();
        var receipts=await db.PurchaseEvidence.AsNoTracking().Where(e=>e.HouseholdId==household&&(e.AccountId==null||e.AccountId==accountId)&&e.Channel=="Continente"&&e.Date!=null&&e.PaymentMethod!="Bank"&&!e.HasSourceConflict).ToListAsync();
        return rows.ToDictionary(r=>r.Ordinal,r=>
        {
            var nearby=receipts.Where(e=>Math.Min(Math.Abs(e.Date!.Value.DayNumber-r.Date.DayNumber),Math.Abs(e.Date.Value.DayNumber-r.ValueDate.DayNumber))<=3).OrderBy(e=>e.Date).ToList();
            return "Possível carregamento de saldo. Confere a origem do pagamento antes de criar uma despesa de supermercado."+
                (nearby.Count==0?"": " Fatura(s) próxima(s) já guardada(s), paga(s) total ou parcialmente com Cartão Continente: "+string.Join("; ",nearby.Select(e=>$"{e.Date:dd/MM/yyyy}, {Money.FromUnits(e.Units??0).ToString("N2",System.Globalization.CultureInfo.GetCultureInfo("pt-PT"))} € (compra #{e.Id})"))+". A proximidade, por si só, não confirma a associação.");
        });
    }
    public async Task<InvoiceCoverage> InvoiceCoverageAsync(string channel)
    {
        await using var db=open();
        var evidence=(await db.PurchaseEvidence.AsNoTracking().Where(e=>e.Date!=null&&!e.HasSourceConflict).ToListAsync()).Where(e=>Normalize(e.Channel)==Normalize(channel)).ToList();
        if(evidence.Count==0)return new(null,null,[]);
        var from=evidence.Min(e=>e.Date!.Value);
        var batches=await(from b in db.BankImports.AsNoTracking() join d in db.BankDocuments.AsNoTracking() on b.DocumentId equals d.Id join a in db.Accounts.AsNoTracking() on b.AccountId equals a.Id where b.Status!="Voided" select new{b.Id,b.SectionIndex,b.AccountId,b.PersonId,a.Name,d.StatementJson}).ToListAsync();
        var people=await db.People.AsNoTracking().ToDictionaryAsync(p=>p.Id,p=>p.Name);
        var lines=new List<(long ImportId,StatementMovement Row,long? AccountId,string Account,long? PersonId)>();DateOnly? to=null;
        foreach(var b in batches)
        {
            var statement=JsonSerializer.Deserialize<BankStatement>(b.StatementJson)!;var section=statement.Accounts[b.SectionIndex];
            if(statement.Errors.Count>0||section.Errors.Count>0||section.To<from)continue;
            if(to is null||section.To>to)to=section.To;
            foreach(var row in section.Rows.Where(r=>r.Date>=from&&r.Units<0&&ContainsWords(Normalize(r.Description),Normalize(channel))))lines.Add((b.Id,row,b.AccountId,b.Name,b.PersonId));
        }
        var covered=new HashSet<(long,int)>();
        var linkedIds=evidence.Where(e=>e.TransactionId!=null).Select(e=>e.TransactionId!.Value).ToArray();
        var linkedRows=await db.BankImportEntries.AsNoTracking().Where(e=>linkedIds.Contains(e.TransactionId)&&db.Transactions.Any(t=>t.Id==e.TransactionId&&t.Status=="Confirmed")).Select(e=>new{e.ImportId,e.Ordinal}).ToListAsync();
        foreach(var link in linkedRows)covered.Add((link.ImportId,link.Ordinal));
        foreach(var e in evidence.Where(e=>(e.FinancialUnits??e.Units)>0))
        {
            var candidates=lines.Where(l=>Matches(e,l.Row.Date,l.Row.Units,l.Row.Description,l.AccountId)).ToList();
            if(candidates.Count==1)covered.Add((candidates[0].ImportId,candidates[0].Row.Ordinal));
        }
        return new(from,to,lines.Where(l=>!covered.Contains((l.ImportId,l.Row.Ordinal))).OrderBy(l=>l.Row.Date).ThenBy(l=>l.Account).Select(l=>new InvoiceGap(l.Row.Date,-l.Row.Units,l.Row.Description,l.Account,l.PersonId is {} person?people.GetValueOrDefault(person):null,PossibleContinenteFunding(l.Row))).ToList());
    }
}
