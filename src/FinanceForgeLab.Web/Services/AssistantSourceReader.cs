using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinanceForgeLab.Infrastructure;
namespace FinanceForgeLab.Web.Services;

public sealed class AssistantSourceReader(HttpClient http,IConfiguration configuration,IWebHostEnvironment environment):IAssistantSourceReader
{
    public async Task<AssistantRetrievedSource> ReadAsync(AssistantSourceInput input,CancellationToken cancellation=default)
    {
        if(input.Kind=="files")
        {
            var path=input.SourceId.Replace('\\','/');
            if(input.Scope!="Financas"||path.StartsWith('/')||path.Split('/').Any(p=>p is ".." or "." or "")||path.Contains(':'))throw new FinanceRuleException("Escolhe um documento da partilha Financas.");
            var receipt=await Post("/api/filesystem",new(){["operation"]="files.read_binary",["root"]="Financas",["path"]=path,["receipt_only"]=true},cancellation);
            if(!receipt.TryGetProperty("download_path",out var download)||receipt.GetProperty("root").GetString()!="Financas"||receipt.GetProperty("path").GetString()!=path||receipt.GetProperty("size").GetInt64()>AssistantService.MaxFileBytes)throw new FinanceRuleException("O Gateway não devolveu um documento válido dentro do limite de 20 MB.");
            var endpoint=download.GetString()??"";
            if(!endpoint.StartsWith("/api/projects/financeforgelab/files/content?",StringComparison.Ordinal))throw new FinanceRuleException("Ligação de leitura inesperada.");
            var data=await Send(endpoint,null,cancellation);
            if(data.LongLength!=receipt.GetProperty("size").GetInt64()||!Convert.ToHexString(SHA256.HashData(data)).Equals(receipt.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase))throw new FinanceRuleException("O documento mudou durante a leitura. Volta a verificar esta fonte.");
            return new(Path.GetFileName(path),data,"files","Financas",path);
        }
        if(input.Kind=="email")
        {
            var pair=input.Scope.Split('|',2);
            if(pair.Length!=2||pair[1].Length==0||pair[0] is not ("Hotmail" or "hotmail" or "Gmail")||pair[0]=="Gmail"&&pair[1]!="INBOX"||string.IsNullOrWhiteSpace(input.SourceId))throw new FinanceRuleException("Usa Hotmail e as pastas autorizadas, ou Gmail|INBOX.");
            var account=pair[0]=="Gmail"?"Gmail":"hotmail";
            var scope=(account=="Gmail"?"Gmail":"Hotmail")+"|"+pair[1];
            var message=await Post("/api/email",new(){["operation"]="email.read_message",["account"]=account,["folder"]=pair[1],["messageId"]=input.SourceId},cancellation);
            // Read/unread flags do not change the identity/content of a receipt.
            var stable=message.EnumerateObject().Where(p=>p.Name is not ("isRead" or "flag" or "@odata.etag" or "@odata.context")).OrderBy(p=>p.Name).ToDictionary(p=>p.Name,p=>p.Value);
            var data=JsonSerializer.SerializeToUtf8Bytes(new{account,folder=pair[1],messageId=input.SourceId,message=stable});
            return new("email-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope+input.SourceId)))[..16]+".txt",data,"email",scope,input.SourceId);
        }
        throw new FinanceRuleException("Origem de leitura não suportada.");
    }
    private async Task<JsonElement> Post(string endpoint,Dictionary<string,object> payload,CancellationToken token)
    {
        var content=await Send(endpoint,payload,token);using var doc=JsonDocument.Parse(content);
        var json=doc.RootElement;
        if(json.TryGetProperty("error",out _)||json.TryGetProperty("ok",out var ok)&&ok.ValueKind==JsonValueKind.False)throw new FinanceRuleException("O Gateway não conseguiu ler esta fonte. A pesquisa ficou por concluir.");
        return json.Clone();
    }
    private async Task<byte[]> Send(string endpoint,Dictionary<string,object>? payload,CancellationToken token)
    {
        var s=AssistantGateway.LoadSettings(configuration,environment);
        if(s.GetValueOrDefault("GATEWAY_PROJECT_ID")!="financeforgelab"||!s.TryGetValue("GATEWAY_BASE_URL",out var address)||!Uri.TryCreate(address,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https")||!s.ContainsKey("GATEWAY_PROJECT_TOKEN")||!s.ContainsKey("URG_GATEWAY_TOKEN"))throw new FinanceRuleException("Ligação de leitura ao Gateway não configurada.");
        using var request=new HttpRequestMessage(payload is null?HttpMethod.Get:HttpMethod.Post,address.TrimEnd('/')+endpoint);
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",s["URG_GATEWAY_TOKEN"]);request.Headers.Add("X-Project-Token",s["GATEWAY_PROJECT_TOKEN"]);request.Headers.Add("X-URG-Project-Id","financeforgelab");
        if(payload is not null){payload["project_id"]="financeforgelab";payload["project_pass"]=s["GATEWAY_PROJECT_TOKEN"];request.Content=JsonContent.Create(payload);}
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
        if(!response.IsSuccessStatusCode)throw new FinanceRuleException("O Gateway recusou ou interrompeu a leitura da fonte. Não foi marcada como tratada.");
        using var stream=await response.Content.ReadAsStreamAsync(token);using var output=new MemoryStream();var buffer=new byte[81920];int length;
        while((length=await stream.ReadAsync(buffer,token))>0){if(output.Length+length>AssistantService.MaxFileBytes)throw new FinanceRuleException("A fonte excede o limite de leitura.");await output.WriteAsync(buffer.AsMemory(0,length),token);}
        return output.ToArray();
    }
}
