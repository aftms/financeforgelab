using System.Text.Json;
using System.Text.Json.Nodes;
using FinanceForgeLab.Core;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Infrastructure;

public static class EmailProvenance
{
    public static EmailSourceReference? Read(string payload)
    {
        try {using var doc=JsonDocument.Parse(payload);return doc.RootElement.TryGetProperty("Email",out var email)&&email.ValueKind==JsonValueKind.Object?email.Deserialize<EmailSourceReference>():null;}
        catch(JsonException){return null;}
    }
    public static EmailSourceReference FromRetrieved(byte[] content)
    {
        using var doc=JsonDocument.Parse(content);var root=doc.RootElement;var message=root.GetProperty("message");
        string Text(JsonElement e,string key)=>e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:"";
        var from=message.TryGetProperty("from",out var sender)&&sender.ValueKind==JsonValueKind.Object&&sender.TryGetProperty("emailAddress",out var address)?Text(address,"address"):Text(message,"from");
        var result=new EmailSourceReference(Text(root,"account"),Text(root,"folder"),Text(root,"messageId"),from,
            DateTimeOffset.TryParse(Text(message,"receivedDateTime"),out var date)?date:null,Text(message,"subject")){WebLink=Text(message,"webLink") is {Length:>0} link?link:null};
        Validate(result);return result;
    }
    public static void Validate(EmailSourceReference value)
    {
        if(string.IsNullOrWhiteSpace(value.MessageId)||value.MessageId.Length>4000||value.Account is null||value.Folder is null||value.Sender is null||value.Subject is null||value.Account.Length>200||value.Folder.Length>1000||value.Sender.Length>500||value.Subject.Length>2000||value.WebLink?.Length>8000)
            throw new FinanceRuleException("A referência do email está incompleta ou excede o limite permitido.");
    }
}

public sealed partial class BankImportService
{
    public async Task SaveEmailSourceReferencesAsync(IReadOnlyDictionary<long,EmailSourceReference> references)
    {
        if(references.Count is <1 or >500)throw new FinanceRuleException("Atualiza até 500 referências de email de cada vez.");
        await using var db=open();await using var tx=await db.Database.BeginTransactionAsync();
        var ids=references.Keys.ToArray();var sources=await db.PurchaseEvidenceSources.Where(s=>ids.Contains(s.Id)).ToListAsync();
        if(sources.Count!=ids.Length)throw new FinanceRuleException("Uma das origens já não existe.");
        foreach(var source in sources)
        {
            var reference=references[source.Id];EmailProvenance.Validate(reference);
            if(!source.Source.Contains("email",StringComparison.OrdinalIgnoreCase))throw new FinanceRuleException("A origem indicada não é um email.");
            var old=EmailProvenance.Read(source.PayloadJson);
            if(old==reference)continue;
            if(old is not null)throw new FinanceRuleException("Este email já tem outra referência. Confere a origem antes de a alterar.");
            var payload=JsonNode.Parse(source.PayloadJson)!.AsObject();payload["Email"]=JsonSerializer.SerializeToNode(reference);source.PayloadJson=payload.ToJsonString();
            db.Add(new AuditChange{Entity="PurchaseEvidenceSource",EntityId=source.Id,Action="IdentifyEmailSource",Actor="Owner",CreatedUtc=DateTime.UtcNow,
                DetailJson=JsonSerializer.Serialize(new{source.EvidenceId,Email=reference})});
        }
        await db.SaveChangesAsync();await tx.CommitAsync();
    }
}
