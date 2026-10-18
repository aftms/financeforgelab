using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed record ReviewIssue(string Key,string Kind,string Description,string Url);
public sealed record ReviewSnapshot(DateTime CheckedUtc,int Transactions,int Purchases,int Statements,List<ReviewIssue> Issues);
public sealed record ReviewNote(string Key,string Reason);

public sealed partial class AssistantService
{
    public const string ReviewInstruction="""
        Revê todos os dados já guardados, de todos os períodos e contas. Lê review-check.json: é o inventário atual de pendências e incongruências, com chaves estáveis. Percorre-o integralmente, em lotes, começando pelas datas mais recentes. Cruza movements, purchases, purchase-sources, bank-rows, bank-entries, allocations, reimbursements e o catálogo; verifica também problemas não detetados automaticamente. Não limites a análise aos extratos nem ao ano atual.
        Tenta resolver classificações em falta, compras ainda não registadas, contas por associar, ligações incompletas, repartições incoerentes e possíveis duplicados. Antes de criar, cruza todas as fontes; ao encontrar o débito de uma compra existente, usa bank_confirm/Link para associar a conta sem duplicar. Respeita decisões explícitas e titulares. Não anules nem fundas possíveis duplicados sem confirmação. Consulta fontes adicionais autorizadas apenas quando ajudarem a esclarecer uma pendência, mantendo Gmail na INBOX e nos 1000 emails; não reinicies uma importação integral de pastas/emails.
        Usa as operações financeiras suportadas. Para cada item que não consigas resolver, devolve review_note com a key e uma razão concreta: o que verificaste, o conflito ou informação em falta e o próximo passo. As notas não resolvem nem ocultam a pendência. Não uses uma justificação genérica para dar milhares de casos como verificados. Consulta as notas e operações das etapas anteriores e não repitas trabalho concluído. Se faltar uma operação suportada, regista a limitação. Para problemas adicionais encontrados, identifica os registos e fontes na análise e nas perguntas.
        Usa continue enquanto houver itens ainda por analisar; completed apenas após percorrer o inventário, mesmo que persistam dúvidas documentadas. A aplicação volta a conferir os dados após cada etapa e impede uma conclusão sem cobertura. O relatório distingue operações realmente guardadas, pendências que deixaram de ser detetadas e problemas que persistem. Nunca afirma que tudo ficou corrigido só por terminar a análise.
        """;

    // Read-only, consistent inventory. No monetary or classification changes happen here.
    public async Task<ReviewSnapshot> InspectReviewAsync(CancellationToken cancellation=default)
    {
        await using var db=open();await using var read=await db.Database.BeginTransactionAsync(cancellation);
        var ts=await db.Transactions.AsNoTracking().Where(t=>t.Status=="Confirmed").OrderByDescending(t=>t.Date).ThenByDescending(t=>t.Id).ToListAsync(cancellation);
        var transactions=ts.ToDictionary(t=>t.Id);
        var postings=(await db.Postings.AsNoTracking().ToListAsync(cancellation)).ToLookup(p=>p.TransactionId);
        var categories=await db.Categories.AsNoTracking().ToDictionaryAsync(c=>c.Id,cancellation);
        var individualRows=await FinanceService.IndividualRowsAsync(db);
        var individualParents=individualRows.Select(r=>r.Id).ToHashSet();
        var purchases=await db.PurchaseEvidence.AsNoTracking().OrderByDescending(e=>e.Date).ThenByDescending(e=>e.Id).ToListAsync(cancellation);
        var byTransaction=purchases.Where(e=>e.TransactionId!=null).ToLookup(e=>e.TransactionId!.Value);
        var issues=new List<ReviewIssue>();
        void Add(string key,string kind,string description,string url)=>issues.Add(new(key,kind,description,url));
        foreach(var t in ts)
        {
            if(individualParents.Contains(t.Id))continue;
            var ps=postings[t.Id].ToList();var url=$"/movimentos?registo={t.Id}";var label=$"#{t.Id} · {t.Date:dd/MM/yyyy} · {t.Description}";
            if(ps.Count==0)Add($"movement:{t.Id}:postings","Integridade",label+" — sem lançamento financeiro.",url);
            if(ps.Any(p=>p.AccountId==null))Add($"movement:{t.Id}:account","Conta em falta",label,url);
            if(t.Kind is MovementKind.Expense or MovementKind.Income)
            {
                var allocations=byTransaction[t.Id].Any(e=>e.CategoryAllocationsJson is not ("" or "[]"));
                if(!allocations&&ps.Any(p=>p.CategoryId==null))Add($"movement:{t.Id}:category","Classificação em falta",label,url);
                if(ps.Any(p=>p.CategoryId is {} c&&(!categories.TryGetValue(c,out var cat)||cat.Kind!=t.Kind)))Add($"movement:{t.Id}:category-kind","Categoria incompatível",label,url);
            }
            if(t.Kind==MovementKind.Transfer&&(ps.Count!=2||ps.Sum(p=>p.AmountUnits)!=0||ps.Select(p=>p.AccountId).Distinct().Count()!=2))
                Add($"movement:{t.Id}:transfer","Transferência incoerente",label+" — conferir as duas contas e o equilíbrio dos valores.",url);
        }
        foreach(var row in individualRows.Where(r=>r.Status=="Confirmed"))
        {
            var url=$"/movimentos?registo={row.Id}";var label=$"Movimento #{row.IndividualId} · {row.Date:dd/MM/yyyy} · {row.Description}";
            if(row.AccountId==null)Add($"individual:{row.IndividualId}:account","Conta em falta",label,url);
            if(row.Kind is MovementKind.Expense or MovementKind.Income&&row.CategoryId==null)Add($"individual:{row.IndividualId}:category","Classificação em falta",label,url);
        }
        foreach(var row in individualRows.Where(r=>r.ReconciliationWarning!=null).DistinctBy(r=>r.Id))
            Add($"payment:{row.Id}:reconciliation","Movimentos e origem divergem",$"Origem #{row.Id} · {row.ReconciliationWarning}",$"/movimentos?registo={row.Id}");
        foreach(var e in purchases)
        {
            var label=$"Compra #{e.Id} · {e.Date:dd/MM/yyyy} · {e.Merchant} · {e.OrderReference}";
            var charged=e.PaymentMethod is not ("Split" or "Benefits")&&(e.FinancialUnits??e.Units)!=0;
            if(e.HasSourceConflict)Add($"purchase:{e.Id}:conflict","Fontes divergentes",label,"/compras");
            if(charged&&(e.TransactionId is not {} tid||!transactions.ContainsKey(tid)))Add($"purchase:{e.Id}:unposted","Compra por registar",label,"/compras");
            if(e.TransactionId is {} id&&transactions.TryGetValue(id,out var t))
            {
                var ps=postings[id].ToList();
                if(e.PaymentMethod!="TopUp"&&(t.Kind!=MovementKind.Expense||ps.Sum(p=>p.AmountUnits)!=-(e.FinancialUnits??e.Units)))Add($"purchase:{e.Id}:amount","Compra e movimento divergem",label+$" — conferir o total e o movimento #{id}.","/compras");
                if(e.PaymentMethod!="TopUp"&&(e.AccountId is {} a&&ps.Any(p=>p.AccountId!=null&&p.AccountId!=a)||e.AccountId==null&&ps.Any(p=>p.AccountId!=null)))
                    Add($"purchase:{e.Id}:account","Conta da compra divergente",label+$" — conferir a conta no movimento #{id} e na compra.","/compras");
            }
            try
            {
                var parts=JsonSerializer.Deserialize<List<PurchaseCategoryAllocation>>(string.IsNullOrEmpty(e.CategoryAllocationsJson)?"[]":e.CategoryAllocationsJson,Json)??[];
                if(parts.Count>0&&(parts.Sum(p=>p.Units)!=e.Units||parts.Any(p=>p.Units<=0||!categories.TryGetValue(p.CategoryId,out var cat)||cat.Kind!=MovementKind.Expense)))
                    Add($"purchase:{e.Id}:allocation","Repartição incoerente",label+" — categorias ou soma diferentes do total.","/compras");
                if(charged&&e.PaymentMethod!="TopUp"&&parts.Count==0&&e.CategoryId==null)Add($"purchase:{e.Id}:category","Classificação em falta",label,"/compras");
            }
            catch(JsonException){Add($"purchase:{e.Id}:allocation","Repartição ilegível",label,"/compras");}
        }
        // A potential duplicate is only a warning, never an automatic deletion/merge.
        var signatures=ts.Where(t=>t.Kind is MovementKind.Expense or MovementKind.Income)
            .Select(t=>new{T=t,P=postings[t.Id].ToList()}).Where(x=>x.P.Count==1&&x.P[0].AccountId!=null&&!string.IsNullOrWhiteSpace(x.T.Description))
            .GroupBy(x=>new{x.T.Date,x.T.Kind,x.T.PersonId,x.P[0].AccountId,x.P[0].AmountUnits,Description=x.T.Description!.Trim().ToUpperInvariant()});
        foreach(var group in signatures.Where(g=>g.Count()>1))
        {
            var ids=group.Select(x=>x.T.Id).Order().ToArray();
            Add("duplicate:"+string.Join("-",ids),"Possível duplicado","Mesma conta, titular, data, descrição e valor: "+string.Join(", ",ids.Select(id=>$"#{id}"))+". Conferir referências e documentos antes de decidir.",$"/movimentos?registo={ids[0]}");
        }
        var batches=await db.BankImports.AsNoTracking().Where(b=>b.Status!="Voided").OrderByDescending(b=>b.Id).ToListAsync(cancellation);
        var documents=await db.BankDocuments.AsNoTracking().Select(d=>new{d.Id,d.StatementJson}).ToDictionaryAsync(d=>d.Id,cancellation);
        var entries=(await db.BankImportEntries.AsNoTracking().ToListAsync(cancellation)).ToLookup(e=>e.ImportId);
        foreach(var b in batches)
        {
            var url=$"/extratos?rever={b.Id}";
            try
            {
                var doc=JsonSerializer.Deserialize<BankStatement>(documents[b.DocumentId].StatementJson,Json)!;
                var section=doc.Accounts[b.SectionIndex];
                if(!BankStatementConformance.CanImport(doc,section))Add($"bank:{b.Id}:reading","Verificar leitura",$"Extrato #{b.Id} — leitura ou saldos com erros.",url);
                if(BankStatementConformance.Inconsistency(doc,section) is {} discrepancy)
                    Add($"bank:{b.Id}:inconsistency","Incongruência de extrato",$"Extrato #{b.Id} — {discrepancy.Description} "+(discrepancy.AllowsImport?"Não bloqueia as linhas válidas; aguarda análise do titular, sem criar acertos.":"Conferência necessária antes de importar."),url);
                if(b.AccountId==null)Add($"bank:{b.Id}:account","Conta do extrato em falta",$"Extrato #{b.Id} · {section.Reference}",url);
                var decisions=string.IsNullOrEmpty(b.ReviewJson)?new BankImportReview():JsonSerializer.Deserialize<BankImportReview>(b.ReviewJson,Json)!;
                var excluded=decisions.Rows.Where(r=>r.Decision=="Exclude").Select(r=>r.Ordinal).ToHashSet();
                var linked=entries[b.Id].ToLookup(e=>e.Ordinal);
                foreach(var row in section.Rows)
                {
                    var links=linked[row.Ordinal].ToList();
                    if(links.Count==0&&!excluded.Contains(row.Ordinal))Add($"bank:{b.Id}:row:{row.Ordinal}","Linha bancária por tratar",$"Extrato #{b.Id} · linha {row.Ordinal} · {row.Date:dd/MM/yyyy} · {row.Description}",url);
                    foreach(var link in links)
                        if(!transactions.ContainsKey(link.TransactionId)||!postings[link.TransactionId].Any(p=>p.AccountId==b.AccountId&&p.AmountUnits==row.Units))
                            Add($"bank:{b.Id}:link:{link.Id}","Associação bancária incoerente",$"Extrato #{b.Id} · linha {row.Ordinal} · movimento #{link.TransactionId}: conferir estado, conta e montante.",url);
                }
            }
            catch(Exception ex) when(ex is JsonException or KeyNotFoundException or ArgumentOutOfRangeException or NullReferenceException)
            {Add($"bank:{b.Id}:data","Extrato ilegível",$"Extrato #{b.Id}: os dados guardados precisam de verificação.",url);}
        }
        var reimbursements=(await db.ReimbursementAllocations.AsNoTracking().ToListAsync(cancellation)).ToLookup(a=>a.ClaimId);
        foreach(var claim in await db.ReimbursementClaims.AsNoTracking().ToListAsync(cancellation))
        {
            var paid=reimbursements[claim.Id].Sum(a=>a.AmountUnits);
            if(paid!=claim.AmountUnits)Add($"claim:{claim.Id}:balance",paid>claim.AmountUnits?"Reembolso excessivo":"Reembolso por conferir",$"Adiantamento #{claim.Id} · {claim.Debtor} · saldo {Money.FromUnits(claim.AmountUnits-paid):0.00} EUR. Cruzar recebimentos; saldo por receber não prova erro.","/reembolsos");
        }
        foreach(var source in await db.AssistantSources.AsNoTracking().Where(s=>s.Status=="Pending"&&s.SourceId!="@cursor").ToListAsync(cancellation))
            Add($"source:{source.Id}:pending","Fonte por tratar",$"{source.Kind} · {source.Scope} · {source.SourceId} · {source.Note}","/assistente");
        return new(DateTime.UtcNow,ts.Count-individualParents.Count+individualRows.Count(r=>r.Status=="Confirmed"),purchases.Count,batches.Count,issues);
    }

    public async Task<ReviewSnapshot> SaveReviewSnapshotAsync(string requestId,int round,string phase,CancellationToken cancellation=default)
    {
        var key=phase=="before"&&round==1?"system:review:baseline":"system:review:current";
        await using var db=open();var old=await db.AssistantOperations.SingleOrDefaultAsync(o=>o.RequestId==requestId&&o.Key==key,cancellation);
        if(old is not null&&key.EndsWith("baseline"))return JsonSerializer.Deserialize<AssistantActionReceipt>(old.ResultJson,Json)!.Review!;
        var snapshot=await InspectReviewAsync(cancellation);
        var receipt=new AssistantActionReceipt($"Conferência: {snapshot.Issues.Count} pendências/avisos detetados.",[],[],[],"/assistente/"+requestId){Review=snapshot};
        var operation=old??new AssistantOperation{RequestId=requestId,Key=key,Type="review_snapshot",Status="Completed"};
        if(old is null)db.Add(operation);
        operation.Round=round;operation.Message=receipt.Message;operation.ResultJson=JsonSerializer.Serialize(receipt,Json);
        await db.SaveChangesAsync(cancellation);return snapshot;
    }

    public static Dictionary<string,string> ReviewNotes(IEnumerable<AssistantOperation> operations)=>operations.Where(o=>o.Status=="Completed"&&o.Type=="review_note")
        .SelectMany(o=>JsonSerializer.Deserialize<AssistantActionReceipt>(o.ResultJson,Json)?.ReviewNotes??[]).GroupBy(n=>n.Key).ToDictionary(g=>g.Key,g=>g.Last().Reason);
    public static ReviewSnapshot? LatestReview(IEnumerable<AssistantOperation> operations)=>operations.Where(o=>o.Status=="Completed"&&o.Type=="review_snapshot").OrderBy(o=>o.Id)
        .Select(o=>JsonSerializer.Deserialize<AssistantActionReceipt>(o.ResultJson,Json)?.Review).LastOrDefault();
}
