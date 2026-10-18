using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;
namespace FinanceForgeLab.Infrastructure;

public sealed record AssistantSyncSummary(string Kind,int Processed,int Pending,DateTime? LastCompleted,string? OpenRequest);
public sealed record AssistantSourceInput(string Kind,string Scope,string SourceId);
public sealed record AssistantRetrievedSource(string Name,byte[] Content,string Kind,string Scope,string SourceId);
public interface IAssistantSourceReader
{
    Task<AssistantRetrievedSource> ReadAsync(AssistantSourceInput input,CancellationToken cancellation=default);
}

public sealed partial class AssistantService
{
    private static readonly SemaphoreSlim scanGate=new(1,1);
    public static string ScanTitle(string kind)=>kind switch {"files"=>"Atualizar documentos das pastas","email"=>"Verificar novos emails","review"=>"Rever pendências e verificar dados",_=>throw new FinanceRuleException("Tipo de atualização inválido.")};
    public async Task<string> StartScanAsync(string kind)
    {
        if(kind is not ("files" or "email" or "review"))throw new FinanceRuleException("Tipo de atualização inválido.");
        await scanGate.WaitAsync();
        try
        {
            await using var db=open();await using var tx=await db.Database.BeginTransactionAsync();
            var pending=await db.AssistantRequests.Where(r=>r.Workflow==kind&&!r.Archived&&r.Status!="Completed").OrderByDescending(r=>r.CreatedUtc).FirstOrDefaultAsync();
            if(pending is not null)return pending.Id;
            var text=kind=="files"
                ?"Atualiza os documentos financeiros nas pastas autorizadas da partilha Financas, incluindo extratos de António e Suzana e faturas (Continente e outros). Descobre subpastas e segue a paginação. Compara identidades e hashes com o histórico e a memória de sincronização; trata apenas documentos novos ou alterados e pendências. Cruza cada documento com os movimentos existentes antes de criar despesas ou receitas. Classifica compras por categoria/subcategoria, preserva os originais e associa os documentos da mesma operação."
                :"Verifica os emails financeiros nas contas autorizadas: Hotmail e respetivas subpastas de compras, serviços, comércio e Estado; Gmail exclusivamente INBOX e apenas os 1000 emails disponibilizados. Identifica novas despesas/receitas, faturas e comprovativos. Retoma a memória por conta/pasta, segue paginação, verifica pendências e cruza com documentos e movimentos existentes. Não marques mensagens como lidas nem alteres pastas. Não interpretes publicidade, carrinhos ou lembretes como pagamentos confirmados.";
            if(kind=="email")text+=" "+AssistantEmailPolicy.ViaVerdeInstruction;
            text=kind=="review"?ReviewInstruction:text+" Guarda os itens tratados e o ponto de retoma através das operações de sincronização. Usa o histórico já importado como ponto de partida. Não voltes a importar o que está registado. Mantém dúvidas pendentes e apresenta um resumo verificável.";
            var r=new AssistantRequest{ClientId=Guid.NewGuid(),Title=ScanTitle(kind),Workflow=kind};db.Add(r);
            db.Add(new AssistantTurn{RequestId=r.Id,Round=1,ClientId=r.ClientId,UserText=text,InputFingerprint=Fingerprint(text,[])});
            await db.SaveChangesAsync();await tx.CommitAsync();return r.Id;
        }
        finally{scanGate.Release();}
    }
    public async Task<List<AssistantSyncSummary>> SyncSummaryAsync()
    {
        await using var db=open();var result=new List<AssistantSyncSummary>();
        foreach(var kind in new[]{"files","email","review"})
        {
            var sources=db.AssistantSources.Where(s=>s.Kind==kind&&s.SourceId!="@cursor");
            var completed=await db.AssistantRequests.Where(r=>r.Workflow==kind&&r.Status=="Completed").OrderByDescending(r=>r.UpdatedUtc).Select(r=>(DateTime?)r.UpdatedUtc).FirstOrDefaultAsync();
            var active=await db.AssistantRequests.Where(r=>r.Workflow==kind&&!r.Archived&&r.Status!="Completed").OrderByDescending(r=>r.CreatedUtc).Select(r=>r.Id).FirstOrDefaultAsync();
            result.Add(new(kind,await sources.CountAsync(s=>s.Status=="Processed"||s.Status=="Ignored"),await sources.CountAsync(s=>s.Status=="Pending"),completed,active));
        }
        return result;
    }
    public async Task<long> AddSourceAsync(string requestId,int round,AssistantRetrievedSource source)
    {
        if(source.Kind is not ("files" or "email")||source.Scope.Length is <1 or >500||source.SourceId.Length is <1 or >1000||source.SourceId=="@cursor")throw new FinanceRuleException("Origem inválida.");
        var file=ValidateFiles([new(source.Name,source.Content)]).Single();
        await using var db=open();await using var tx=await db.Database.BeginTransactionAsync();
        if(!await db.AssistantTurns.AnyAsync(t=>t.RequestId==requestId&&t.Round==round))throw new FinanceRuleException("Etapa inexistente.");
        var cache=await db.AssistantSources.SingleOrDefaultAsync(s=>s.Kind==source.Kind&&s.Scope==source.Scope&&s.SourceId==source.SourceId&&s.Fingerprint==file.Sha256);
        if(cache is null)
        {
            var same=await db.AssistantSources.FirstOrDefaultAsync(s=>s.Kind==source.Kind&&s.Fingerprint==file.Sha256&&(s.Status=="Processed"||s.Status=="Ignored"));
            cache=new(){Kind=source.Kind,Scope=source.Scope,SourceId=source.SourceId,Fingerprint=file.Sha256,RequestId=requestId,Status=same?.Status??"Pending",Note=same?.Note??"",CursorJson=same?.CursorJson??""};db.Add(cache);await db.SaveChangesAsync();
        }
        var prior=await db.AssistantAttachments.SingleOrDefaultAsync(f=>f.RequestId==requestId&&f.Sha256==file.Sha256);
        if(prior is not null){prior.SourceCacheId??=cache.Id;await db.SaveChangesAsync();await tx.CommitAsync();return prior.Id;}
        file.RequestId=requestId;file.Round=round;file.SourceCacheId=cache.Id;db.Add(file);await db.SaveChangesAsync();await tx.CommitAsync();return file.Id;
    }
    public async Task RecordSourceAsync(string requestId,long attachmentId,string status,string note,string[] operationKeys,long[] evidenceIds,long[] importIds)
    {
        if(status is not ("Processed" or "Ignored" or "Pending")||string.IsNullOrWhiteSpace(note)||note.Length>2000)throw new FinanceRuleException("Indica o resultado da verificação da fonte.");
        await using var db=open();var file=await db.AssistantAttachments.SingleOrDefaultAsync(f=>f.Id==attachmentId&&f.RequestId==requestId);
        if(file?.SourceCacheId is not {} cacheId)throw new FinanceRuleException("O anexo não tem uma origem de sincronização verificada.");
        var cache=await db.AssistantSources.SingleAsync(s=>s.Id==cacheId);
        var keys=operationKeys.Distinct().ToArray();
        if(await db.AssistantOperations.CountAsync(o=>o.RequestId==requestId&&keys.Contains(o.Key)&&o.Status=="Completed"&&o.Type!="source_fetch"&&o.Type!="source_record"&&o.Type!="source_cursor")!=keys.Length)throw new FinanceRuleException("A fonte tem operações que ainda não foram concluídas.");
        foreach(var id in evidenceIds)
            if(!await db.PurchaseEvidenceSources.AnyAsync(s=>s.EvidenceId==id&&(s.DocumentSha256.ToLower()==file.Sha256||s.SourceKey.StartsWith("assistant:"+file.Sha256+":"))))throw new FinanceRuleException("O comprovativo indicado não corresponde a esta fonte.");
        foreach(var id in importIds)
            if(!await db.BankImports.AnyAsync(i=>i.Id==id&&i.DocumentId.ToLower()==file.Sha256))throw new FinanceRuleException("O extrato indicado não corresponde a esta fonte.");
        if(status=="Processed"&&keys.Length==0&&evidenceIds.Length==0&&importIds.Length==0)throw new FinanceRuleException("Falta um resultado guardado para marcar a fonte como tratada.");
        var related=await db.AssistantSources.Where(s=>s.Kind==cache.Kind&&s.Fingerprint==cache.Fingerprint).ToListAsync();
        foreach(var item in related){item.Status=status;item.Note=note;item.RequestId=requestId;item.UpdatedUtc=DateTime.UtcNow;item.CursorJson=JsonSerializer.Serialize(new{operationKeys=keys,evidenceIds,importIds},Json);}
        if(status is "Processed" or "Ignored")
            foreach(var old in await db.AssistantSources.Where(s=>s.Kind==cache.Kind&&s.Scope==cache.Scope&&s.SourceId==cache.SourceId&&s.Fingerprint!=cache.Fingerprint&&s.Status=="Pending").ToListAsync()){old.Status="Superseded";old.Note="Versão anterior substituída pela versão agora verificada.";}
        await db.SaveChangesAsync();
    }
    public async Task SaveCursorAsync(string requestId,string kind,string scope,JsonElement cursor)
    {
        if(kind is not ("files" or "email")||scope.Length is <1 or >500||cursor.ValueKind!=JsonValueKind.Object||cursor.GetRawText().Length>8000)throw new FinanceRuleException("Ponto de retoma inválido.");
        if(kind=="files"&&scope!="Financas"||kind=="email"&&scope!="Gmail|INBOX"&&!scope.StartsWith("Hotmail|",StringComparison.Ordinal))throw new FinanceRuleException("Âmbito de pesquisa não autorizado.");
        if(!cursor.TryGetProperty("complete",out var complete)||complete.ValueKind!=JsonValueKind.True||!cursor.TryGetProperty("windowEnd",out var end)||!end.TryGetDateTimeOffset(out var through)||through>DateTimeOffset.UtcNow.AddMinutes(1))throw new FinanceRuleException("Só é possível avançar até uma janela de pesquisa concluída.");
        await using var db=open();
        if(!await db.AssistantRequests.AnyAsync(r=>r.Id==requestId&&r.Workflow==kind))throw new FinanceRuleException("Este pedido não corresponde à pesquisa.");
        if(await db.AssistantSources.AnyAsync(s=>s.Kind==kind&&s.Scope==scope&&s.Status=="Pending"))throw new FinanceRuleException("Existem fontes pendentes nesta pasta; o ponto anterior foi preservado.");
        var row=await db.AssistantSources.SingleOrDefaultAsync(s=>s.Kind==kind&&s.Scope==scope&&s.SourceId=="@cursor")??new(){Kind=kind,Scope=scope,SourceId="@cursor",Status="Cursor"};
        if(row.Id==0)db.Add(row);row.CursorJson=cursor.GetRawText();row.RequestId=requestId;row.UpdatedUtc=DateTime.UtcNow;await db.SaveChangesAsync();
    }
}
