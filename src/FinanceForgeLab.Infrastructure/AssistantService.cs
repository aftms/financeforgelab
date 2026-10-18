using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public sealed record AssistantFile(string Name, byte[] Content);
public sealed record AssistantFileInfo(long Id,string Name,string ContentType,int Size);
public sealed record AssistantDetail(AssistantRequest Request,List<AssistantTurn> Turns,List<AssistantFileInfo> Attachments,List<AssistantOperation> Operations);
public sealed record AssistantQuestion(string Id,string Text,List<string>? Options);
public sealed record AssistantAction(string Key,string Type,JsonElement Data);
public sealed record AssistantResult(int SchemaVersion,string Status,string Summary,List<AssistantQuestion> Questions,List<AssistantAction> Actions);

public sealed partial class AssistantService(Func<FinanceDb> open)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxFileBytes=20*1024*1024, MaxTotalBytes=50*1024*1024, MaxFiles=10;
    public static bool Active(string status)=>status is "Queued" or "Running" or "Applying";
    public static string Label(string status)=>status switch {"Queued"=>"Em fila","Running"=>"Em análise","Applying"=>"A validar e guardar","NeedsInput"=>"A aguardar resposta","Completed"=>"Concluído","Partial"=>"Concluído com pendências","Blocked"=>"Interrompido",_=>status};
    public async Task<List<AssistantRequest>> ListAsync(bool archived=false)
    {await using var db=open();return await db.AssistantRequests.AsNoTracking().Where(x=>x.Archived==archived).OrderByDescending(x=>x.UpdatedUtc).Take(100).ToListAsync();}
    public async Task<AssistantDetail> DetailAsync(string id)
    {
        await using var db=open();var r=await db.AssistantRequests.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id)??throw new FinanceRuleException("Pedido não encontrado.");
        return new(r,await db.AssistantTurns.AsNoTracking().Where(x=>x.RequestId==id).OrderBy(x=>x.Round).ToListAsync(),
            await db.AssistantAttachments.Where(x=>x.RequestId==id).Select(x=>new AssistantFileInfo(x.Id,x.Name,x.ContentType,x.Content.Length)).ToListAsync(),
            await db.AssistantOperations.AsNoTracking().Where(x=>x.RequestId==id).OrderBy(x=>x.Id).ToListAsync());
    }
    public async Task<AssistantAttachment> FileAsync(string requestId,long id)
    {await using var db=open();return await db.AssistantAttachments.AsNoTracking().SingleOrDefaultAsync(x=>x.RequestId==requestId&&x.Id==id)??throw new FinanceRuleException("Anexo não encontrado.");}
    public async Task<string> CreateAsync(Guid clientId,string text,List<AssistantFile> files)
    {
        text=Text(text,files.Count>0);var attachments=ValidateFiles(files);
        if(clientId==Guid.Empty)throw new FinanceRuleException("Identificador de envio inválido.");
        await using var db=open();await using var tx=await db.Database.BeginTransactionAsync();
        var old=await db.AssistantRequests.SingleOrDefaultAsync(x=>x.ClientId==clientId);
        if(old is not null)
        {
            var prior=await db.AssistantTurns.SingleAsync(x=>x.RequestId==old.Id&&x.Round==1);
            var hashes=await db.AssistantAttachments.Where(x=>x.RequestId==old.Id&&x.Round==1).Select(x=>x.Sha256).ToListAsync();
            if(prior.UserText!=text||!hashes.Order().SequenceEqual(attachments.Select(x=>x.Sha256).Order()))throw new FinanceRuleException("Este envio já existe com outros dados.");
            return old.Id;
        }
        var r=new AssistantRequest{ClientId=clientId,Title=text.Length>0?text[..Math.Min(100,text.Length)]:files[0].Name};
        db.Add(r);db.Add(new AssistantTurn{RequestId=r.Id,Round=1,ClientId=clientId,UserText=text,InputFingerprint=Fingerprint(text,attachments)});
        foreach(var a in attachments){a.RequestId=r.Id;a.Round=1;db.Add(a);}
        await db.SaveChangesAsync();await tx.CommitAsync();return r.Id;
    }
    public async Task ReplyAsync(string id,int version,Guid clientId,string text,List<AssistantFile> files)
    {
        text=Text(text,files.Count>0);var attachments=ValidateFiles(files);
        if(clientId==Guid.Empty)throw new FinanceRuleException("Identificador de resposta inválido.");
        await using var db=open();await using var tx=await db.Database.BeginTransactionAsync();
        var prior=await db.AssistantTurns.SingleOrDefaultAsync(x=>x.ClientId==clientId);
        if(prior is not null){if(prior.RequestId!=id||prior.InputFingerprint!=Fingerprint(text,attachments))throw new FinanceRuleException("Resposta repetida com outros dados.");return;}
        var r=await db.AssistantRequests.SingleAsync(x=>x.Id==id);
        if(r.Version!=version||Active(r.Status)||r.Archived)throw new FinanceRuleException("O pedido foi atualizado. Volta a abri-lo.");
        r.Round++;r.Version++;r.Status="Queued";r.Notice="Resposta recebida. Em fila.";r.UpdatedUtc=r.NextCheckUtc=DateTime.UtcNow;
        db.Add(new AssistantTurn{RequestId=id,Round=r.Round,ClientId=clientId,UserText=text,InputFingerprint=Fingerprint(text,attachments)});
        foreach(var a in attachments){if(await db.AssistantAttachments.AnyAsync(x=>x.RequestId==id&&x.Sha256==a.Sha256))continue;a.RequestId=id;a.Round=r.Round;db.Add(a);}
        await db.SaveChangesAsync();await tx.CommitAsync();
    }
    public async Task RetryAsync(string id,int version)
    {
        await using var db=open();var r=await db.AssistantRequests.SingleAsync(x=>x.Id==id);
        if(r.Version!=version||r.Status!="Blocked")throw new FinanceRuleException("Atualiza o pedido antes de retomar.");
        // Keep the exact prompt/idempotency key, and recover the existing delegation.
        var turn=await db.AssistantTurns.SingleAsync(x=>x.RequestId==id&&x.Round==r.Round);
        r.Status=turn.ResultJson.Length>0?"Applying":turn.DelegationId.Length>0?"Running":"Queued";
        r.Notice="A retomar o pedido existente.";r.NextCheckUtc=r.UpdatedUtc=DateTime.UtcNow;r.Version++;await db.SaveChangesAsync();
    }
    public async Task ArchiveAsync(string id,int version,bool archived)
    {
        await using var db=open();var r=await db.AssistantRequests.SingleAsync(x=>x.Id==id);
        if(r.Version!=version||Active(r.Status))throw new FinanceRuleException("Só podes arquivar um pedido sem execução em curso.");
        r.Archived=archived;r.Version++;await db.SaveChangesAsync();
    }
    public static AssistantResult Parse(string json)
    {
        if(json.Length>300_000)throw new FinanceRuleException("A resposta excede o limite permitido.");
        AssistantResult? r;
        try{r=JsonSerializer.Deserialize<AssistantResult>(json,Json);}catch(JsonException){throw new FinanceRuleException("O agente não devolveu uma resposta estruturada válida.");}
        if(r is null||r.SchemaVersion!=1||r.Status is not ("completed" or "needs_input" or "continue")||r.Summary is null||r.Summary.Length>8000||r.Questions is null||r.Actions is null||r.Questions.Count>10||r.Actions.Count>100)
            throw new FinanceRuleException("A resposta do agente não corresponde ao contrato.");
        if(r.Status=="needs_input"&&r.Questions.Count==0||r.Status=="completed"&&r.Questions.Count>0)throw new FinanceRuleException("Estado e perguntas da resposta não correspondem.");
        if(r.Questions.Any(q=>q is null||string.IsNullOrWhiteSpace(q.Id)||string.IsNullOrWhiteSpace(q.Text)||q.Text.Length>2000||q.Options?.Count>8)||r.Questions.Select(q=>q.Id).Distinct().Count()!=r.Questions.Count)
            throw new FinanceRuleException("Perguntas inválidas.");
        if(r.Actions.Any(a=>a is null||string.IsNullOrWhiteSpace(a.Key)||a.Key.Length>100||a.Data.ValueKind!=JsonValueKind.Object||!AssistantActions.Types.Contains(a.Type))||r.Actions.Select(a=>a.Key).Distinct().Count()!=r.Actions.Count)
            throw new FinanceRuleException("A resposta contém operações inválidas ou repetidas.");
        if(r.Actions.Count(a=>a.Type=="source_fetch")>20)throw new FinanceRuleException("Consulta até 20 fontes por etapa e continua no mesmo pedido.");
        return r;
    }
    private static string Text(string text,bool hasFiles)
    {text=(text??"").Trim();if(text.Length>20000||text.Length==0&&!hasFiles)throw new FinanceRuleException("Escreve um pedido ou acrescenta um ficheiro (texto até 20 000 caracteres).");return text;}
    private static string Fingerprint(string text,List<AssistantAttachment> attachments)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{text,hashes=attachments.Select(x=>x.Sha256).Order().ToArray()}))));
    private static List<AssistantAttachment> ValidateFiles(List<AssistantFile> files)
    {
        if(files.Count>MaxFiles||files.Sum(x=>(long)x.Content.Length)>MaxTotalBytes)throw new FinanceRuleException("Até 10 ficheiros e 50 MB por envio.");
        var result=new List<AssistantAttachment>();
        foreach(var file in files)
        {
            var content=file.Content;var name=Path.GetFileName(file.Name.Replace('\\','/'));var ext=Path.GetExtension(name).ToLowerInvariant();
            if(content.Length is 0 or >MaxFileBytes||name.Length>200)throw new FinanceRuleException("Cada ficheiro deve ter entre 1 byte e 20 MB.");
            var mime=ext switch{
                ".pdf" when content.AsSpan().StartsWith("%PDF-"u8)=>"application/pdf",
                ".png" when content.AsSpan().StartsWith(new byte[]{137,80,78,71,13,10,26,10})=>"image/png",
                ".jpg" or ".jpeg" when content.Length>3&&content[0]==255&&content[1]==216&&content[2]==255=>"image/jpeg",
                ".txt" or ".csv" or ".tsv"=>"text/plain; charset=utf-8",
                _=>throw new FinanceRuleException("Usa PDF, PNG, JPG ou texto TXT/CSV/TSV. O conteúdo deve corresponder ao formato.")};
            if(mime.StartsWith("text/")){try{_ = new UTF8Encoding(false,true).GetString(content);}catch(DecoderFallbackException){throw new FinanceRuleException("Guarda os ficheiros de texto em UTF-8.");}}
            var hash=Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            if(result.All(x=>x.Sha256!=hash))result.Add(new(){Name=name,ContentType=mime,Sha256=hash,Content=content});
        }
        return result;
    }
}
