using System.Text.Json;
using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using FinanceForgeLab.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class DataOriginTests : IAsyncLifetime
{
    readonly string folder=Path.Combine(Path.GetTempPath(),"ffl-origins-"+Guid.NewGuid().ToString("N"));
    string DbPath=>Path.Combine(folder,"finance.sqlite");string Inventory=>Path.Combine(folder,"origins.json");
    FinanceDb Open()=>FinanceDb.Open(DbPath);
    DataOriginService Service=>new(Open,Inventory,folder,DbPath,Path.Combine(folder,"private"),Path.Combine(folder,"assistant"));
    public async Task InitializeAsync(){Directory.CreateDirectory(folder);await using var db=Open();await db.Database.MigrateAsync();db.Add(new Household{Id=1});db.Add(new Account{Id=1,HouseholdId=1,Name="Conta sintética"});db.Add(new Category{Id=1,HouseholdId=1,Name="Serviços",Kind=MovementKind.Expense});await db.SaveChangesAsync();}
    public Task DisposeAsync(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(folder,true);return Task.CompletedTask;}

    [Fact]public async Task MapDeduplicatesEmailAttachmentsPreservesCoverageAndExcludesUnauthorizedScopesAndSettings()
    {
        await File.WriteAllTextAsync(Inventory,JsonSerializer.Serialize(new{VerifiedAtUtc=DateTimeOffset.Parse("2026-10-18T10:00:00Z"),
            FileRoots=new[]{new{Name="Financas",PhysicalPath="X:/Financeiro",Description="Arquivo",Complete=true,Folders=new[]{"Faturas"},Files=new[]{"Resumo.xlsx"}}},
            EmailAccounts=new[]{new{Name="Hotmail",Complete=true,Folders=new[]{"Inbox",AssistantEmailPolicy.ViaVerdeFolder}},new{Name="Gmail",Complete=true,Folders=new[]{"INBOX","Sent"}},new{Name="Outra conta",Complete=true,Folders=new[]{"Privado"}}},
            GATEWAY_PROJECT_TOKEN="DUMMY-SECRET-MUST-NOT-APPEAR"}));
        await using(var db=Open())
        {
            var evidence=new PurchaseEvidence{Id=1,HouseholdId=1,SourceKey="synthetic",Source="Synthetic"};db.Add(evidence);await db.SaveChangesAsync();
            var email=new EmailSourceReference("hotmail","Inbox","msg-1","vendor@example.test",DateTimeOffset.Parse("2026-10-01T10:00:00Z"),"Recibo");
            string Payload(EmailSourceReference e)=>JsonSerializer.Serialize(new PurchaseEvidence{Email=e});
            db.AddRange(new PurchaseEvidenceSource{EvidenceId=1,HouseholdId=1,SourceKey="pdf-one",Source="Financas / Faturas/receipt.pdf",PayloadJson=Payload(email)},
                new PurchaseEvidenceSource{EvidenceId=1,HouseholdId=1,SourceKey="pdf-two",Source="Email",PayloadJson=Payload(email)},
                new PurchaseEvidenceSource{EvidenceId=1,HouseholdId=1,SourceKey="outside",PayloadJson=Payload(email with{Account="Gmail",Folder="Sent"})});
            db.AddRange(new AssistantSourceCache{Kind="email",Scope="Hotmail|Inbox",SourceId="msg-1",Status="Processed"},
                new AssistantSourceCache{Kind="email",Scope="Hotmail|Inbox",SourceId="msg-2",Status="Pending"},
                new AssistantSourceCache{Kind="email",Scope="Hotmail|Inbox",SourceId="@cursor",Status="Cursor",CursorJson="{\"complete\":true,\"windowEnd\":\"2026-10-01T00:00:00Z\"}"},
                new AssistantSourceCache{Kind="email",Scope="Gmail|Sent",SourceId="outside",Status="Pending"},
                new AssistantSourceCache{Kind="files",Scope="Financas",SourceId="Faturas/receipt.pdf",Status="Processed"});
            db.Add(new BankReference{AccountId=1,CategoryId=1,Merchant="VENDOR",Code="ABC123",Description="Significado confirmado"});
            await db.SaveChangesAsync();
        }
        var map=await Service.ReadAsync();var hotmail=map.EmailAccounts.Single(a=>a.Name=="Hotmail");
        var inbox=hotmail.Folders.Single(f=>f.Path=="Inbox");Assert.Equal(1,inbox.SavedMessages);Assert.Equal(2,inbox.SavedSources);Assert.Equal(1,inbox.PendingSources);
        Assert.True(hotmail.Complete);Assert.Equal("Janela concluída até 01/10/2026",inbox.Resume);
        Assert.Single(map.EmailAccounts.Single(a=>a.Name=="Gmail").Folders);Assert.Equal("INBOX",map.EmailAccounts.Single(a=>a.Name=="Gmail").Folders[0].Path);
        Assert.Equal(2,map.EmailAccounts.Count);Assert.Single(map.References);Assert.Equal("Significado confirmado",map.References[0].Description);
        Assert.Equal(1,map.FileRoots.Single().Folders.Single().SavedSources);
        var json=JsonSerializer.Serialize(map);Assert.DoesNotContain("DUMMY-SECRET",json);Assert.DoesNotContain("GATEWAY_PROJECT_TOKEN",json);Assert.DoesNotContain("Gmail|Sent",json);Assert.DoesNotContain("Privado",json);
        await using var check=Open();Assert.Empty(await check.Transactions.ToListAsync());Assert.Empty(await check.AuditChanges.ToListAsync());Assert.Equal(3,await check.PurchaseEvidenceSources.CountAsync());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task MissingOrUnreadableInventoryKeepsStoredOriginsWithoutClaimingCompletedDiscovery(bool malformed)
    {
        if(malformed)await File.WriteAllTextAsync(Inventory,"{broken");
        await using(var db=Open()){db.Add(new AssistantSourceCache{Kind="email",Scope="Hotmail|Historical/Shop",SourceId="old",Status="Pending"});await db.SaveChangesAsync();}
        var map=await Service.ReadAsync();Assert.NotNull(map.InventoryNotice);Assert.Null(map.InventoryVerifiedUtc);
        var hotmail=map.EmailAccounts.Single(a=>a.Name=="Hotmail");Assert.False(hotmail.Complete);
        Assert.Contains(hotmail.Folders,f=>f.Path==AssistantEmailPolicy.ViaVerdeFolder&&!f.Inventoried);
        Assert.Contains(hotmail.Folders,f=>f.Path=="Historical/Shop"&&f.PendingSources==1&&!f.Inventoried);
    }
    [Fact]public async Task OverviewShowsLocationsLimitsCoverageAndReferencesAsText()
    {
        var map=new DataOriginReport(DateTimeOffset.Parse("2026-10-18T12:00:00Z"),null,
            [new("Financas","X:/Arquivo financeiro","Arquivo confirmado",true,[new("Faturas",true,0,2,0,null,"")],["Resumo.xlsx"])],
            [new("Gmail","Apenas INBOX e os 1000 emails disponibilizados",true,[new("INBOX",true,1,1,0,null,"Sem ponto de retoma guardado")])],
            [new("Legado","X:/Historic.accdb","Histórico migrado")],[new(1,"Conta sintética","<script>alert(1)</script>","CODE","Referência confirmada","Serviços",null,null)],3,4,5);
        await using var services=new ServiceCollection().AddLogging().BuildServiceProvider();await using var renderer=new HtmlRenderer(services,services.GetRequiredService<ILoggerFactory>());
        var html=await renderer.Dispatcher.InvokeAsync(async()=>(await renderer.RenderComponentAsync<FinanceForgeLab.Web.Components.DataOriginOverview>(ParameterView.FromDictionary(new Dictionary<string,object?>{{"Report",map}}))).ToHtmlString());
        var decoded=System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains("X:/Arquivo financeiro",decoded);Assert.Contains("X:/Historic.accdb",decoded);Assert.Contains("1000 emails",decoded);
        Assert.Contains("não confirmam que toda a pasta esteja revista",decoded);Assert.Contains("Referência confirmada",decoded);Assert.Contains("/api/v1/data-origins",html);
        Assert.DoesNotContain("<script>alert(1)</script>",html);Assert.Contains("&lt;script&gt;",html);
    }
}
