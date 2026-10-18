using System.Text;
using System.Text.Json;
using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using FinanceForgeLab.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed partial class AssistantTests:IAsyncLifetime
{
    private readonly string folder=Path.Combine(Path.GetTempPath(),"ffl-assistant-"+Guid.NewGuid().ToString("N"));
    private string PathDb=>Path.Combine(folder,"data.sqlite");
    private FinanceDb Open()=>FinanceDb.Open(PathDb);
    private AssistantService Service=>new(Open);
    private BankImportService Imports=>new(Open,Path.Combine(folder,"bank"));
    private AssistantActions Actions=>new(Open,new(Open),Imports,new(Open),Service);
    private AssistantWorker Worker(IAssistantGateway gateway)=>new(Open,gateway,new(Open,Service,Imports,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Finance:AssistantPath",Path.Combine(folder,"exchange")}}).Build(),new Environment(folder)),Actions,NullLogger<AssistantWorker>.Instance);
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(folder);await using var db=Open();await db.Database.MigrateAsync();
        db.Add(new Household{Id=1});db.Add(new Category{Id=1,HouseholdId=1,Name="Alimentação",Kind=MovementKind.Expense,Active=true});db.Add(new Category{Id=2,HouseholdId=1,Name="Trabalho",Kind=MovementKind.Income,Active=true});
        db.Add(new Account{Id=1,HouseholdId=1,Name="António",Active=true,Currency="EUR"});db.Add(new Account{Id=2,HouseholdId=1,Name="Suzana",Active=true,Currency="EUR"});await db.SaveChangesAsync();
    }
    public Task DisposeAsync(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(folder,true);return Task.CompletedTask;}
    private static AssistantAction Action(string key,string type,object data)=>new(key,type,JsonSerializer.SerializeToElement(data,AssistantService.Json));
    private static string Result(string status="completed",List<AssistantAction>? actions=null,List<AssistantQuestion>? questions=null)=>JsonSerializer.Serialize(new AssistantResult(1,status,"Análise sintética",questions??[],actions??[]),AssistantService.Json);
    private async Task Due(string id){await using var db=Open();var r=await db.AssistantRequests.SingleAsync(x=>x.Id==id);r.NextCheckUtc=DateTime.UtcNow.AddSeconds(-1);await db.SaveChangesAsync();}
    [Fact]public async Task UploadIsDurableDeduplicatedAndScoped()
    {
        var key=Guid.NewGuid();var file=new AssistantFile("../invoice.txt",Encoding.UTF8.GetBytes("Uma compra"));
        var id=await Service.CreateAsync(key,"Confere",[file,file]);Assert.Equal(id,await Service.CreateAsync(key,"Confere",[file]));
        var detail=await Service.DetailAsync(id);Assert.Single(detail.Attachments);Assert.Equal("invoice.txt",detail.Attachments[0].Name);
        Assert.Equal(file.Content,(await Service.FileAsync(id,detail.Attachments[0].Id)).Content);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.FileAsync("other",detail.Attachments[0].Id));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.CreateAsync(key,"Outro pedido",[file]));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.CreateAsync(Guid.NewGuid(),"Confere",[new("file.pdf",Encoding.UTF8.GetBytes("not pdf"))]));
    }
    [Fact]public async Task EveryAssistantRequestReceivesTheSameMandatoryManualShownInTheApp()
    {
        var id=await Service.CreateAsync(Guid.NewGuid(),"Atualiza os documentos",[]);var fake=new FakeGateway();await Worker(fake).TickAsync();
        Assert.Contains("procedures.txt",Assert.Single(fake.Prompts));
        var text=await File.ReadAllTextAsync(Path.Combine(folder,"exchange","exchange",id,"1","procedures.txt"));
        Assert.Equal(FinanceOperatingManual.Text,text);
        Assert.Contains("preencher a conta do extrato no movimento e na compra",text);
        Assert.Contains("Gmail mantém apenas INBOX",text);
        using var policy=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder,"exchange","exchange",id,"1","sync-policy.json")));
        var required=Assert.Single(policy.RootElement.GetProperty("requiredEmailFolders").EnumerateArray());
        Assert.Equal(AssistantEmailPolicy.ViaVerdeFolder,required.GetProperty("folder").GetString());
    }
    [Fact]public async Task WorkerWaitsThenAsksAndContinuesSameRequest()
    {
        var id=await Service.CreateAsync(Guid.NewGuid(),"Qual a conta?",[]);var fake=new FakeGateway{Pending=true};
        await Worker(fake).TickAsync();Assert.Equal("Running",(await Service.DetailAsync(id)).Request.Status);Assert.Empty(fake.ReadIds);
        fake.Response=Result("needs_input",questions:[new("account","Em que conta?",["António","Suzana"])]);await Due(id);
        await Worker(fake).TickAsync();var detail=await Service.DetailAsync(id);Assert.Equal("NeedsInput",detail.Request.Status);Assert.Single(fake.ReadIds);
        var reply=Guid.NewGuid();await Service.ReplyAsync(id,detail.Request.Version,reply,"António",[]);await Service.ReplyAsync(id,detail.Request.Version,reply,"António",[]);
        fake.Pending=false;fake.Response=Result();await Worker(fake).TickAsync();detail=await Service.DetailAsync(id);
        Assert.Equal("Completed",detail.Request.Status);Assert.Equal(2,detail.Turns.Count);Assert.Equal(2,fake.Keys.Distinct().Count());
        var exported=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder,"exchange","exchange",id,"2","request.json")));
        Assert.Equal("António",exported.RootElement.GetProperty("turns")[1].GetProperty("userText").GetString());
    }
    [Fact]public async Task TimeoutAfterAcceptanceReusesExactPromptAndKey()
    {
        var id=await Service.CreateAsync(Guid.NewGuid(),"Consulta apenas",[]);var fake=new FakeGateway{ThrowOnce=true};
        await Worker(fake).TickAsync();Assert.Equal("Queued",(await Service.DetailAsync(id)).Request.Status);
        await Due(id);await Worker(fake).TickAsync();Assert.Equal("Completed",(await Service.DetailAsync(id)).Request.Status);
        Assert.Equal(2,fake.Keys.Count);Assert.Single(fake.Keys.Distinct());Assert.Single(fake.Prompts.Distinct());
    }
    [Fact]public async Task InvalidAgentOutputNeverWritesFinancialData()
    {
        var id=await Service.CreateAsync(Guid.NewGuid(),"Analisa",[]);await Worker(new FakeGateway{Response="Feito!"}).TickAsync();
        Assert.Equal("Blocked",(await Service.DetailAsync(id)).Request.Status);await using var db=Open();Assert.Empty(await db.Transactions.ToListAsync());Assert.Empty(await db.AssistantOperations.ToListAsync());
        Assert.Throws<FinanceRuleException>(()=>AssistantService.Parse(Result(actions:[Action("evil","sql",new{sql="DELETE"})])));
    }
    [Fact]public async Task PurchaseReplayAndLostOperationReceiptDoNotDuplicate()
    {
        var file=new AssistantFile("fatura.txt",Encoding.UTF8.GetBytes("Compra teste 12 EUR"));var id=await Service.CreateAsync(Guid.NewGuid(),"Regista",[file]);var detail=await Service.DetailAsync(id);
        var data=new AssistantActions.PurchaseAction(new PurchaseEvidence{Date=new(2026,10,1),Units=120000,Channel="Loja Teste",Merchant="Loja Teste",OrderReference="ORDER-1",Description="Alimentos",CategoryId=1},"ORDER-1",detail.Attachments[0].Id,1,null,true);
        var action=Action("purchase-1","purchase",data);var first=await Actions.ExecuteAsync(id,1,action);Assert.Single(first.TransactionIds);
        await using(var db=Open()){var op=await db.AssistantOperations.SingleAsync();op.Status="Started";op.ResultJson="";await db.SaveChangesAsync();}
        var replay=await Actions.ExecuteAsync(id,1,action);Assert.Equal(first.TransactionIds,replay.TransactionIds);
        var secondRequest=await Service.CreateAsync(Guid.NewGuid(),"Confere outra vez",[file]);var secondDetail=await Service.DetailAsync(secondRequest);
        var second=await Actions.ExecuteAsync(secondRequest,1,Action("same-purchase","purchase",data with{AttachmentId=secondDetail.Attachments[0].Id}));
        Assert.Equal(first.TransactionIds,second.TransactionIds);await using var check=Open();Assert.Single(await check.Transactions.ToListAsync());Assert.Single(await check.PurchaseEvidence.ToListAsync());
    }
    [Fact]public async Task SameValueAcrossKnownAccountsRemainsDistinctAndTransferIsBalanced()
    {
        var id=await Service.CreateAsync(Guid.NewGuid(),"Regista",[]);
        var form=new MovementForm{Date=new(2026,10,1),Kind=MovementKind.Expense,Amount=12,AccountId=1,CategoryId=1,Description="Loja A"};
        await Actions.ExecuteAsync(id,1,Action("a","movement",form));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Actions.ExecuteAsync(id,1,Action("possible-duplicate","movement",form)));
        form.AccountId=2;await Actions.ExecuteAsync(id,1,Action("b","movement",form));
        var transfer=new MovementForm{Date=new(2026,10,2),Kind=MovementKind.Transfer,Amount=20,AccountId=2,DestinationAccountId=1,Description="Transferência interna"};
        var result=await Actions.ExecuteAsync(id,1,Action("transfer","movement",transfer));
        await using var db=Open();var posts=await db.Postings.Where(p=>p.TransactionId==result.TransactionIds.Single()).ToListAsync();Assert.Equal(2,posts.Count);Assert.Equal(0,posts.Sum(p=>p.AmountUnits));
        Assert.Equal(240000,(await new FinanceService(Open).DashboardAsync(2026)).ExpenseUnits);Assert.Equal(0,(await new FinanceService(Open).DashboardAsync(2026)).IncomeUnits);
    }
    [Fact]public async Task PartialFailureReportsActualSuccessfulOperations()
    {
        var id=await Service.CreateAsync(Guid.NewGuid(),"Regista",[]);
        var form=new MovementForm{Date=new(2026,10,1),Kind=MovementKind.Expense,Amount=12,AccountId=1,CategoryId=1,Description="Loja A"};
        var fake=new FakeGateway{Response=Result(actions:[Action("a","movement",form),Action("b","movement",form)])};
        await Worker(fake).TickAsync();var detail=await Service.DetailAsync(id);Assert.Equal("Partial",detail.Request.Status);Assert.Single(detail.Operations,o=>o.Status=="Completed");Assert.Single(detail.Operations,o=>o.Status=="Rejected");
        await using var db=Open();Assert.Single(await db.Transactions.ToListAsync());
    }
    [Fact]public async Task ConcurrentEditRejectsStaleClassification()
    {
        var id=await Service.CreateAsync(Guid.NewGuid(),"Classifica",[]);var service=new FinanceService(Open);
        var movement=await service.SaveMovementAsync(new(){Date=new(2026,10,1),Kind=MovementKind.Expense,Amount=12,AccountId=1,CategoryId=1,Description="Loja A"});
        var before=await service.MovementAsync(movement);var current=await service.MovementAsync(movement);current.Description="Editada pelo utilizador";await service.SaveMovementAsync(current);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Actions.ExecuteAsync(id,1,Action("classify","classify",new AssistantActions.ClassificationAction(movement,before.Version,1))));
        Assert.Equal("Editada pelo utilizador",(await service.MovementAsync(movement)).Description);
    }
    [Fact]public async Task CompletedRequestsCanBeArchivedButRunningRequestsCannot()
    {
        var id=await Service.CreateAsync(Guid.NewGuid(),"Consulta",[]);var detail=await Service.DetailAsync(id);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.ArchiveAsync(id,detail.Request.Version,true));
        await Worker(new FakeGateway()).TickAsync();detail=await Service.DetailAsync(id);await Service.ArchiveAsync(id,detail.Request.Version,true);
        Assert.Empty(await Service.ListAsync());Assert.Single(await Service.ListAsync(true));
    }
    [Fact]public async Task BankStagesAndConfirmsThroughExistingBalanceAndDuplicateChecks()
    {
        var id=await Service.CreateAsync(Guid.NewGuid(),"Importa este extrato",[new("test.pdf",BankImportTests.SyntheticPdf())]);
        var attachment=(await Service.DetailAsync(id)).Attachments.Single();
        var staged=await Actions.ExecuteAsync(id,1,Action("stage","bank_stage",new AssistantActions.StageAction(attachment.Id,0)));
        var importId=Assert.Single(staged.ImportIds);var review=await Imports.PrepareReviewAsync(importId);
        var row=review.Rows.Single(r=>r.Ordinal==1);row.CategoryId=1;row.Decision="Import";
        var action=Action("confirm","bank_confirm",new AssistantActions.BankConfirmAction(importId,review.Version,[row]));
        var confirmed=await Actions.ExecuteAsync(id,1,action);Assert.Single(confirmed.TransactionIds);
        await using(var db=Open()){var op=await db.AssistantOperations.SingleAsync(x=>x.Key=="confirm");op.Status="Started";op.ResultJson="";await db.SaveChangesAsync();}
        Assert.Equal(confirmed.TransactionIds,(await Actions.ExecuteAsync(id,1,action)).TransactionIds);
        await using var check=Open();Assert.Single(await check.BankImportEntries.ToListAsync());
    }
    [Fact]public async Task GatewayKeepsAcceptedPendingAndChecksCallbackFailure()
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"GATEWAY_BASE_URL","http://gateway.invalid"},{"GATEWAY_PROJECT_ID","financeforgelab"},{"GATEWAY_PROJECT_TOKEN","test-project"},{"URG_GATEWAY_TOKEN","test-gateway"}}).Build();
        var handler=new GatewayHandler();var gateway=new AssistantGateway(new HttpClient(handler),config,new Environment(folder));
        handler.Content="{\"ok\":false,\"accepted\":true,\"delegation_id\":\"d1\",\"status\":\"accepted\",\"terminal\":false}";
        var accepted=await gateway.SubmitAsync("key","instruction",default);Assert.False(accepted.Terminal);Assert.False(accepted.Failed);Assert.Equal("d1",accepted.Id);
        handler.Content="{\"status\":\"completed\",\"terminal\":true,\"callback_status\":\"error\",\"result_text\":\"not success\"}";
        Assert.True((await gateway.ReadAsync("d1",default)).Failed);
        handler.Status=System.Net.HttpStatusCode.TooManyRequests;handler.Content="{\"error\":\"RATE_LIMIT\"}";
        var exception=await Assert.ThrowsAsync<AssistantGatewayException>(()=>gateway.ReadAsync("d1",default));Assert.True(exception.Retryable);Assert.Equal(120,exception.RetryAfterSeconds);
    }
    [Fact]public async Task DisabledExecutionPreservesQueueWithoutDispatching()
    {
        var id=await Service.CreateAsync(Guid.NewGuid(),"Guardar para mais tarde",[]);var gateway=new FakeGateway();var config=new ConfigurationBuilder().Build();
        var worker=new AssistantWorker(Open,gateway,new(Open,Service,Imports,config,new Environment(folder)),Actions,NullLogger<AssistantWorker>.Instance,config);
        await worker.TickAsync();Assert.Empty(gateway.Keys);Assert.Equal("Queued",(await Service.DetailAsync(id)).Request.Status);
    }
    [Fact]public async Task ResultMovementLinkFiltersExactlyOneFinancialRecord()
    {
        var service=new FinanceService(Open);var one=await service.SaveMovementAsync(new(){Date=new(2020,1,1),Kind=MovementKind.Expense,Amount=10,AccountId=1,CategoryId=1,Description="Primeiro"});
        await service.SaveMovementAsync(new(){Date=new(2026,10,1),Kind=MovementKind.Expense,Amount=20,AccountId=1,CategoryId=1,Description="Segundo"});
        Assert.Equal(one,Assert.Single((await service.MovementsAsync(new(){TransactionId=one})).Rows).Id);
    }
    [Fact]public async Task ScanButtonsReusePendingRequestsAndKeepSeparateWorkflows()
    {
        var ids=await Task.WhenAll(Service.StartScanAsync("files"),Service.StartScanAsync("files"));Assert.Equal(ids[0],ids[1]);
        var mail=await Service.StartScanAsync("email");Assert.NotEqual(ids[0],mail);
        Assert.Contains("1000",(await Service.DetailAsync(mail)).Turns[0].UserText);
        Assert.Contains(AssistantEmailPolicy.ViaVerdeFolder,(await Service.DetailAsync(mail)).Turns[0].UserText);
        Assert.Contains(AssistantEmailPolicy.ViaVerdeInstruction,FinanceOperatingManual.Text);
        await Worker(new FakeGateway()).TickAsync();Assert.Equal("Partial",(await Service.DetailAsync(ids[0])).Request.Status);
        Assert.Equal(ids[0],await new AssistantService(Open).StartScanAsync("files"));
    }
    [Fact]public async Task SyncMemoryPreservesPendingItemsDetectsChangedFilesAndNeverAdvancesOnFailure()
    {
        var id=await Service.StartScanAsync("files");
        var source=new AssistantRetrievedSource("receipt.txt",Encoding.UTF8.GetBytes("documento um"),"files","Financas","Continente/receipt.txt");
        var file=await Service.AddSourceAsync(id,1,source);Assert.Equal(file,await Service.AddSourceAsync(id,1,source));
        var cursor=JsonSerializer.SerializeToElement(new{windowEnd=DateTimeOffset.UtcNow.AddMinutes(-1),complete=true});
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.SaveCursorAsync(id,"files","Financas",cursor));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.RecordSourceAsync(id,file,"Processed","tratada",[],[],[]));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>Service.RecordSourceAsync(id,file,"Processed","tratada",["missing"],[],[]));
        await Service.RecordSourceAsync(id,file,"Ignored","Documento conferido: publicidade sem compra.",[],[],[]);
        await Service.SaveCursorAsync(id,"files","Financas",cursor);
        var changed=await Service.AddSourceAsync(id,1,source with{Content=Encoding.UTF8.GetBytes("documento alterado")});Assert.NotEqual(file,changed);
        var restarted=new AssistantService(Open);Assert.Equal(1,(await restarted.SyncSummaryAsync()).Single(s=>s.Kind=="files").Pending);
        await Assert.ThrowsAsync<FinanceRuleException>(()=>restarted.SaveCursorAsync(id,"files","Financas",cursor));
        await using var db=Open();Assert.Equal(cursor.GetRawText(),(await db.AssistantSources.SingleAsync(s=>s.Status=="Cursor")).CursorJson);
        Assert.Equal(2,await db.AssistantSources.CountAsync(s=>s.SourceId==source.SourceId));
    }
    [Fact]public async Task SourceFetchThenPurchaseAndCheckpointSurviveARepeatedScanWithoutDuplicateExpense()
    {
        var source=new FakeSource();var actions=new AssistantActions(Open,new(Open),Imports,new(Open),Service,source);
        var fake=new FakeGateway();
        AssistantWorker worker()=>new(Open,fake,new(Open,Service,Imports,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Finance:AssistantPath",Path.Combine(folder,"sync")}}).Build(),new Environment(folder)),actions,NullLogger<AssistantWorker>.Instance);
        for(var n=0;n<2;n++)
        {
            var id=await Service.StartScanAsync("files");
            fake.Response=Result("continue",[Action("fetch","source_fetch",new AssistantSourceInput("files","Financas","invoice.txt"))]);await worker().TickAsync();
            var file=(await Service.DetailAsync(id)).Attachments.Single();
            var purchase=new AssistantActions.PurchaseAction(new(){Channel="Teste",Merchant="Teste",OrderReference="invoice-123",Date=new(2026,10,1),Units=123400,CategoryId=1,Description="Compra de alimentos"},"invoice-123",file.Id,1,null,true);
            fake.Response=Result(actions:[Action("cursor","source_cursor",new{kind="files",scope="Financas",cursor=new{windowEnd=DateTimeOffset.UtcNow.AddMinutes(-1),complete=true}}),Action("purchase","purchase",purchase),Action("record","source_record",new{attachmentId=file.Id,status="Processed",note="Compra registada e original guardado",operationKeys=new[]{"purchase"}})]);
            await worker().TickAsync();Assert.Equal("Completed",(await Service.DetailAsync(id)).Request.Status);
        }
        await using var db=Open();Assert.Single(await db.Transactions.ToListAsync());Assert.Single(await db.PurchaseEvidence.ToListAsync());
        Assert.Equal(2,await db.AssistantSources.CountAsync());Assert.Equal(1,(await Service.SyncSummaryAsync()).Single(s=>s.Kind=="files").Processed);
    }
    [Fact]public async Task SourceReaderEnforcesGmailInboxAndUsesTheGatewaysMessageIdContract()
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"GATEWAY_BASE_URL","http://gateway.invalid"},{"GATEWAY_PROJECT_ID","financeforgelab"},{"GATEWAY_PROJECT_TOKEN","test-project"},{"URG_GATEWAY_TOKEN","test-gateway"}}).Build();
        var handler=new EmailReadHandler();var reader=new AssistantSourceReader(new HttpClient(handler),config,new Environment(folder));
        await Assert.ThrowsAsync<FinanceRuleException>(()=>reader.ReadAsync(new("email","Gmail|Archive","123")));Assert.Equal(0,handler.Calls);
        var result=await reader.ReadAsync(new("email","Gmail|INBOX","123"));Assert.Equal("email",result.Kind);Assert.Contains("invoice",Encoding.UTF8.GetString(result.Content));Assert.DoesNotContain("isRead",Encoding.UTF8.GetString(result.Content));
    }
    private sealed class FakeSource:IAssistantSourceReader
    {public Task<AssistantRetrievedSource> ReadAsync(AssistantSourceInput input,CancellationToken cancellation=default)=>Task.FromResult(new AssistantRetrievedSource("invoice.txt",Encoding.UTF8.GetBytes("Teste invoice-123 12,34 EUR"),input.Kind,input.Scope,input.SourceId));}
    private sealed class EmailReadHandler:HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {Calls++;using var doc=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));Assert.Equal("123",doc.RootElement.GetProperty("messageId").GetString());Assert.Equal("INBOX",doc.RootElement.GetProperty("folder").GetString());return new(System.Net.HttpStatusCode.OK){Content=new StringContent("{\"id\":\"123\",\"body\":\"invoice\",\"isRead\":false}",Encoding.UTF8,"application/json")};}
    }
    private sealed class GatewayHandler:HttpMessageHandler
    {
        public string Content="{}";public System.Net.HttpStatusCode Status=System.Net.HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)
        {Assert.Equal("test-gateway",request.Headers.Authorization?.Parameter);Assert.Equal("test-project",request.Headers.GetValues("X-Project-Token").Single());var response=new HttpResponseMessage(Status){Content=new StringContent(Content,Encoding.UTF8,"application/json")};response.Headers.RetryAfter=new(TimeSpan.FromSeconds(120));return Task.FromResult(response);}
    }
    private sealed class FakeGateway:IAssistantGateway
    {
        public bool Pending,ThrowOnce;public string Response=Result();public List<string> Keys=[],Prompts=[],ReadIds=[];
        public Task<AssistantGatewayReply> SubmitAsync(string key,string prompt,CancellationToken cancellation)
        {Keys.Add(key);Prompts.Add(prompt);if(ThrowOnce){ThrowOnce=false;throw new HttpRequestException("connection lost after accepted");}return Task.FromResult(new AssistantGatewayReply("delegation-test",!Pending,false,Pending?"":Response,Pending?"accepted":"completed"));}
        public Task<AssistantGatewayReply> ReadAsync(string id,CancellationToken cancellation){ReadIds.Add(id);return Task.FromResult(new AssistantGatewayReply(id,true,false,Response,"completed"));}
    }
    private sealed class Environment(string folder):IWebHostEnvironment
    {
        public string ApplicationName{get;set;}="FinanceForgeLab.Web";public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();public string WebRootPath{get;set;}=folder;public string EnvironmentName{get;set;}="Testing";public string ContentRootPath{get;set;}=folder;public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();
    }
}
