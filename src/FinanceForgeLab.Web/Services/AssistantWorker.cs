using System.Text.Json;
using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FinanceForgeLab.Web.Services;

public sealed class AssistantWorker(Func<FinanceDb> open,IAssistantGateway gateway,AssistantBundle bundle,AssistantActions actions,ILogger<AssistantWorker> logger,IConfiguration? executionSettings=null):BackgroundService
{
    private readonly SemaphoreSlim gate=new(1,1);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try{await TickAsync(stoppingToken);}
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogError("Assistente: ciclo interrompido ({Type}). O pedido fica preservado.",ex.GetType().Name);}
            try{await Task.Delay(TimeSpan.FromSeconds(3),stoppingToken);}catch(OperationCanceledException){break;}
        }
    }
    public async Task TickAsync(CancellationToken cancellation=default)
    {
        if(executionSettings is not null&&!executionSettings.GetValue<bool>("Finance:AssistantEnabled"))return;
        if(!await gate.WaitAsync(0,cancellation))return;
        try
        {
            await using var db=open();
            // Only one application-owned execution at a time, including across restarts.
            var r=await db.AssistantRequests.Where(x=>x.Status=="Running"||x.Status=="Applying").OrderBy(x=>x.CreatedUtc).FirstOrDefaultAsync(cancellation)
                ??await db.AssistantRequests.Where(x=>x.Status=="Queued").OrderBy(x=>x.CreatedUtc).FirstOrDefaultAsync(cancellation);
            if(r is null||r.NextCheckUtc>DateTime.UtcNow)return;
            var turn=await db.AssistantTurns.SingleAsync(x=>x.RequestId==r.Id&&x.Round==r.Round,cancellation);
            try
            {
                if(turn.ResultJson.Length==0)
                {
                    if(turn.Prompt.Length==0){turn.Prompt=await bundle.PrepareAsync(r.Id,r.Round,cancellation);await db.SaveChangesAsync(cancellation);}
                    var response=turn.DelegationId.Length==0?await gateway.SubmitAsync($"ffl-assistant-{r.Id}-{r.Round}",turn.Prompt,cancellation):await gateway.ReadAsync(turn.DelegationId,cancellation);
                    turn.DelegationId=response.Id;turn.PollCount++;
                    if(response.Failed){r.Status="Blocked";r.Notice="A execução terminou com erro. Os dados e resultados anteriores estão preservados; podes enviar uma instrução para continuar.";}
                    else if(response.Terminal)
                    {
                        var result=AssistantService.Parse(response.Result);
                        turn.ResultJson=response.Result;turn.Summary=result.Summary;turn.QuestionsJson=JsonSerializer.Serialize(result.Questions,AssistantService.Json);
                        r.Status="Applying";r.Notice="Análise recebida. A validar as operações.";
                    }
                    else
                    {
                        r.Status="Running";r.Notice=response.Phase is "accepted"?"Aceite pelo Gateway; a aguardar execução.":"A aguardar o resultado do Codex.";
                        r.NextCheckUtc=DateTime.UtcNow.AddSeconds(Math.Min(60,3*Math.Pow(1.5,Math.Min(turn.PollCount,9))));
                        if(turn.CreatedUtc<DateTime.UtcNow.AddMinutes(-30))r.Notice="A execução está a demorar. Continuo a verificar o mesmo pedido, sem repetir o trabalho.";
                    }
                    Touch(r);await db.SaveChangesAsync(cancellation);
                }
                if(r.Status=="Applying")
                {
                    var result=AssistantService.Parse(turn.ResultJson);var errors=new List<string>();
                    foreach(var action in result.Actions.OrderBy(a=>a.Type=="source_cursor"?2:a.Type=="source_record"?1:0))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if(action.Type=="source_cursor"&&errors.Count>0)continue;
                        try{await actions.ExecuteAsync(r.Id,r.Round,action);}
                        catch(FinanceRuleException ex){errors.Add(ex.Message);}
                    }
                    if(r.Workflow is "files" or "email"&&result.Status=="completed"&&errors.Count==0)
                    {
                        if(!await db.AssistantOperations.AnyAsync(o=>o.RequestId==r.Id&&o.Type=="source_cursor"&&o.Status=="Completed",cancellation)
                            ||await db.AssistantSources.AnyAsync(s=>s.RequestId==r.Id&&s.Status=="Pending",cancellation))
                            errors.Add("Falta guardar o ponto de retoma ou resolver fontes pendentes. A atualização não foi marcada como concluída.");
                    }
                    if(r.Workflow=="review")
                    {
                        var service=new AssistantService(open);
                        var after=await service.SaveReviewSnapshotAsync(r.Id,r.Round,"after",cancellation);
                        var notes=AssistantService.ReviewNotes((await service.DetailAsync(r.Id)).Operations);
                        var unseen=after.Issues.Count(i=>!notes.ContainsKey(i.Key));
                        if(result.Status=="completed"&&unseen>0)
                            errors.Add($"A conferência final ainda tem {unseen} pendências sem análise documentada. Retoma a revisão; não foi marcada como integralmente concluída.");
                    }
                    if(errors.Count>0)
                    {
                        r.Status="Partial";r.Notice="Há operações por resolver. Consulta os resultados verificados e responde para continuar.";
                        turn.QuestionsJson=JsonSerializer.Serialize(result.Questions.Concat(errors.Distinct().Take(10).Select((e,i)=>new AssistantQuestion($"validation-{i}",e,null))),AssistantService.Json);
                    }
                    else if(result.Status=="continue"&&r.Round<(r.Workflow=="review"?64:8))
                    {
                        r.Round++;db.Add(new AssistantTurn{RequestId=r.Id,Round=r.Round,ClientId=Guid.NewGuid(),UserText="Continua a análise a partir dos resultados verificados da ronda anterior. Não repitas operações concluídas."});
                        r.Status="Queued";r.Notice="Etapa guardada. A preparar a continuação.";r.NextCheckUtc=DateTime.UtcNow;
                    }
                    else {r.Status=result.Status=="needs_input"?"NeedsInput":result.Status=="continue"?"Partial":"Completed";r.Notice=result.Status=="continue"?"Limite de etapas automáticas atingido. Podes responder para continuar.":AssistantService.Label(r.Status);}
                    Touch(r);await db.SaveChangesAsync(cancellation);
                }
            }
            catch(AssistantGatewayException ex){r.Status=ex.Retryable?(turn.DelegationId.Length>0?"Running":"Queued"):"Blocked";r.Notice=ex.Message;r.NextCheckUtc=DateTime.UtcNow.AddSeconds(ex.RetryAfterSeconds);Touch(r);await db.SaveChangesAsync(cancellation);}
            catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
            {r.Notice="Ligação interrompida. Vou consultar novamente o mesmo pedido.";r.NextCheckUtc=DateTime.UtcNow.AddSeconds(60);Touch(r);await db.SaveChangesAsync(CancellationToken.None);}
            catch(FinanceRuleException ex){r.Status="Blocked";r.Notice=ex.Message;Touch(r);await db.SaveChangesAsync(cancellation);}
            catch(Exception ex) when(ex is JsonException or InvalidOperationException or ArgumentException or OverflowException)
            {r.Status="Blocked";r.Notice="Não foi possível validar esta etapa. Os resultados anteriores foram preservados; responde para continuar.";Touch(r);await db.SaveChangesAsync(cancellation);logger.LogWarning("Assistente {Id}: validação interrompida ({Type}).",r.Id,ex.GetType().Name);}
        }
        finally{gate.Release();}
    }
    private static void Touch(AssistantRequest r){r.Version++;r.UpdatedUtc=DateTime.UtcNow;}
}
