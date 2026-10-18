using Microsoft.EntityFrameworkCore;
namespace FinanceForgeLab.Infrastructure;

public sealed partial class FinanceService
{
    // Sources belong to the same payment and are shared by its category records.
    // Loading provenance never creates or changes financial postings.
    private static async Task<List<MovementRow>> WithSourcesAsync(FinanceDb db,List<MovementRow> rows)
    {
        var ids=rows.Select(r=>r.Id).Distinct().ToArray();
        if(ids.Length==0)return rows;
        var evidence=await db.PurchaseEvidence.AsNoTracking()
            .Where(e=>e.TransactionId!=null&&ids.Contains(e.TransactionId.Value))
            .Select(e=>new{e.Id,e.TransactionId,e.Source}).ToListAsync();
        var evidenceIds=evidence.Select(e=>e.Id).ToArray();
        var documents=await db.PurchaseEvidenceSources.AsNoTracking().Where(s=>evidenceIds.Contains(s.EvidenceId))
            .Select(s=>new{s.EvidenceId,s.Id,s.Source,s.DocumentName,s.DocumentSha256,s.PayloadJson}).ToListAsync();
        var bank=await (from entry in db.BankImportEntries.AsNoTracking()
                        join import in db.BankImports on entry.ImportId equals import.Id
                        join document in db.BankDocuments on import.DocumentId equals document.Id
                        where ids.Contains(entry.TransactionId)
                        select new{entry.TransactionId,entry.ImportId,entry.Ordinal,document.Name,document.ReaderVersion}).ToListAsync();
        return rows.Select(row=>
        {
            var sources=new List<MovementSource>();
            foreach(var e in evidence.Where(e=>e.TransactionId==row.Id))
            {
                var items=documents.Where(d=>d.EvidenceId==e.Id).ToList();
                foreach(var d in items)
                    sources.Add(new(SourceKind(d.Source,d.DocumentName),d.Source.Length>0?d.Source:d.DocumentName,
                        $"/compras?compra={e.Id}#fonte-{d.Id}"){Email=EmailProvenance.Read(d.PayloadJson)});
                if(items.Count==0)sources.Add(new(SourceKind(e.Source,""),e.Source,$"/compras?compra={e.Id}"));
            }
            foreach(var b in bank.Where(b=>b.TransactionId==row.Id))
                sources.Add(new(b.ReaderVersion.Contains("online",StringComparison.OrdinalIgnoreCase)?"Texto bancário":"Extrato",
                    $"{b.Name} · movimento {b.Ordinal}",$"/api/v1/statements/{b.ImportId}/original",true));
            return row with{Sources=sources.Distinct().ToList()};
        }).ToList();
    }
    private static string SourceKind(string source,string name)
    {
        if(source.Contains("email",StringComparison.OrdinalIgnoreCase))return "Email";
        if(source.Contains("imagem",StringComparison.OrdinalIgnoreCase)||source.Contains("imagens",StringComparison.OrdinalIgnoreCase))return "Imagem";
        if(name.EndsWith(".pdf",StringComparison.OrdinalIgnoreCase)||source.Contains("fatura",StringComparison.OrdinalIgnoreCase))return "Fatura / documento";
        return "Comprovativo";
    }
}
