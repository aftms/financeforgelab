using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FinanceForgeLab.Infrastructure;

namespace FinanceForgeLab.Web.Services;

public sealed record AssistantGatewayReply(string Id,bool Terminal,bool Failed,string Result,string Phase);
public interface IAssistantGateway
{
    Task<AssistantGatewayReply> SubmitAsync(string key,string prompt,CancellationToken cancellation);
    Task<AssistantGatewayReply> ReadAsync(string id,CancellationToken cancellation);
}
public sealed class AssistantGatewayException(string message,bool retryable,int retryAfterSeconds=60):Exception(message)
{public bool Retryable{get;}=retryable;public int RetryAfterSeconds{get;}=Math.Clamp(retryAfterSeconds,1,3600);}

public sealed class AssistantGateway(HttpClient http,IConfiguration configuration,IWebHostEnvironment environment):IAssistantGateway
{
    private Dictionary<string,string> Settings()=>LoadSettings(configuration,environment);
    internal static Dictionary<string,string> LoadSettings(IConfiguration configuration,IWebHostEnvironment environment)
    {
        var values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var path=Path.GetFullPath(Path.Combine(environment.ContentRootPath,"../../.env"));
        if(File.Exists(path))foreach(var line in File.ReadLines(path))
        {var index=line.IndexOf('=');if(index<=0||line.TrimStart().StartsWith('#'))continue;values[line[..index].Trim()]=line[(index+1)..].Trim().Trim('"','\'');}
        foreach(var key in new[]{"GATEWAY_BASE_URL","GATEWAY_PROJECT_ID","GATEWAY_PROJECT_TOKEN","URG_GATEWAY_TOKEN","FFL_ASSISTANT_TARGET"})
            if(configuration[key] is {Length:>0} value)values[key]=value;
        return values;
    }
    public Task<AssistantGatewayReply> SubmitAsync(string key,string prompt,CancellationToken cancellation)=>SendAsync(null,key,prompt,cancellation);
    public Task<AssistantGatewayReply> ReadAsync(string id,CancellationToken cancellation)=>SendAsync(id,null,null,cancellation);
    private async Task<AssistantGatewayReply> SendAsync(string? id,string? key,string? prompt,CancellationToken cancellation)
    {
        var s=Settings();
        if(s.GetValueOrDefault("GATEWAY_PROJECT_ID")!="financeforgelab"||!s.TryGetValue("GATEWAY_BASE_URL",out var address)||!Uri.TryCreate(address,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https")||!s.ContainsKey("GATEWAY_PROJECT_TOKEN")||!s.ContainsKey("URG_GATEWAY_TOKEN"))
            throw new AssistantGatewayException("A ligação do Assistente ao Gateway ainda não está configurada.",false);
        using var request=new HttpRequestMessage(id is null?HttpMethod.Post:HttpMethod.Get,address.TrimEnd('/')+(id is null?"/api/delegate":"/api/delegate/"+Uri.EscapeDataString(id)+"?project_id=financeforgelab"));
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",s["URG_GATEWAY_TOKEN"]);
        request.Headers.Add("X-Project-Token",s["GATEWAY_PROJECT_TOKEN"]);request.Headers.Add("X-URG-Project-Id","financeforgelab");
        if(id is null)request.Content=JsonContent.Create(new{operation="delegate.request",project_id="financeforgelab",target_project_id="financeforgelab",project_pass=s["GATEWAY_PROJECT_TOKEN"],target_id=s.GetValueOrDefault("FFL_ASSISTANT_TARGET","financeforgelab-assistant"),wait_timeout_seconds=0,idempotency_key=key,text=prompt});
        using var response=await http.SendAsync(request,cancellation);
        if(!response.IsSuccessStatusCode)
        {
            string code="";
            try{var error=await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:cancellation);if(error.TryGetProperty("error",out var field)&&field.ValueKind==JsonValueKind.String)code=field.GetString()??"";}catch(JsonException){}
            var message=code=="GPT_TARGET_DISABLED"?"O destino do Assistente está temporariamente desativado no Gateway. O pedido ficou guardado.":response.StatusCode switch {System.Net.HttpStatusCode.NotFound=>"O destino de execução do Assistente ainda não está disponível no Gateway.",System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized=>"O Gateway recusou o acesso deste projeto ao Assistente.",_=>"O Gateway não conseguiu responder. O pedido foi preservado."};
            var retryAfter=response.Headers.RetryAfter?.Delta?.TotalSeconds??(response.Headers.RetryAfter?.Date-DateTimeOffset.UtcNow)?.TotalSeconds??60;
            throw new AssistantGatewayException(message,(int)response.StatusCode>=500||(int)response.StatusCode is 408 or 429,(int)Math.Clamp(retryAfter,1,3600));
        }
        if(response.Content.Headers.ContentLength>1_000_000)throw new AssistantGatewayException("Resposta demasiado longa do Gateway.",false);
        var json=await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:cancellation);
        string Str(string name)=>json.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:"";
        bool Yes(string name)=>json.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.True;
        var phase=Str("status");var failed=phase is "failed" or "cancelled" or "expired"||Str("callback_status") is "failed" or "error";
        var delegation=Str("delegation_id");if(delegation.Length==0)delegation=id??"";
        if(delegation.Length==0)throw new AssistantGatewayException("O Gateway não devolveu um identificador de execução.",false);
        return new(delegation,Yes("terminal")||Yes("completed")||failed,failed,Str("result_text"),phase);
    }
}
