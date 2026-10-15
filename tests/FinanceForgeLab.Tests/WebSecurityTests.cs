using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class WebSecurityTests : IAsyncLifetime
{
    private readonly string folder=Path.Combine(Path.GetTempPath(),"ffl-web-"+Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> app=null!;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(folder);var path=Path.Combine(folder,"test.sqlite");
        await using(var db=FinanceDb.Open(path)){await db.Database.MigrateAsync();db.Add(new Household());await db.SaveChangesAsync();}
        app=new WebApplicationFactory<Program>().WithWebHostBuilder(b=>b.UseEnvironment("Testing").UseSetting("Finance:DatabasePath",path).UseSetting("Finance:PrivatePath",Path.Combine(folder,"private")));
    }
    public async Task DisposeAsync(){await app.DisposeAsync();Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(folder,true);}
    private HttpClient Client()=>app.CreateClient(new WebApplicationFactoryClientOptions{AllowAutoRedirect=false});
    private static string Token(string html)=>Regex.Match(html,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
    private async Task Login(HttpClient client)
    {
        var page=await client.GetStringAsync("/Access");var token=Token(page);Assert.NotEmpty(token);
        var response=await client.PostAsync("/Access",new FormUrlEncodedContent(new Dictionary<string,string>{{"__RequestVerificationToken",token},{"Password","synthetic-owner-password"},{"Confirm","synthetic-owner-password"},{"Setup","true"}}));
        Assert.Equal(HttpStatusCode.Redirect,response.StatusCode);
    }
    [Fact]public async Task FinancialEndpointsRequireAuthentication()
    {
        using var client=Client();Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/v1/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect,(await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/v1/export.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/v1/statements/1/original")).StatusCode);
    }
    [Fact]public async Task SetupRequiresAntiforgeryAndCannotReplaceAnExistingOwner()
    {
        using var client=Client();var rejected=await client.PostAsync("/Access",new FormUrlEncodedContent(new Dictionary<string,string>{{"Password","synthetic-owner-password"},{"Confirm","synthetic-owner-password"},{"Setup","true"}}));Assert.Equal(HttpStatusCode.BadRequest,rejected.StatusCode);
        await Login(client);Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/v1/accounts")).StatusCode);
        using var stranger=Client();var html=await stranger.GetStringAsync("/Access");Assert.DoesNotContain("name=\"Confirm\"",html);Assert.Contains("current-password",html);
        var replace=await stranger.PostAsync("/Access",new FormUrlEncodedContent(new Dictionary<string,string>{{"__RequestVerificationToken",Token(html)},{"Password","different-owner-password"},{"Confirm","different-owner-password"},{"Setup","true"}}));Assert.Equal(HttpStatusCode.OK,replace.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await stranger.GetAsync("/api/v1/accounts")).StatusCode);
    }
    [Fact]public async Task AuthenticatedApiWritesRequireAntiforgeryAndPersistData()
    {
        using var client=Client();await Login(client);var form=new AccountForm{Name="Synthetic account",BalanceStartDate=new(2025,1,1),OpeningBalance=100};
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/v1/accounts",form)).StatusCode);
        var token=await client.GetFromJsonAsync<JsonElement>("/api/v1/security/token");client.DefaultRequestHeaders.Add("X-CSRF-TOKEN",token.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsJsonAsync("/api/v1/accounts",form)).StatusCode);
        var accounts=await client.GetFromJsonAsync<JsonElement>("/api/v1/accounts");Assert.Equal(1,accounts.GetArrayLength());
        Assert.Equal(1_000_000,accounts[0].GetProperty("balanceUnits").GetInt64());
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/v1/dashboard?year=2025")).StatusCode);
    }
    [Fact]public async Task AllMvpPagesRenderForTheOwner()
    {
        using var client=Client();await Login(client);
        foreach(var pair in new[]{("/","Visão geral"),("/movimentos","Movimentos"),("/contas","As tuas contas"),("/categorias","Categorias"),("/dados","Dados e cópias"),("/extratos","Extratos")})
        {
            var response=await client.GetAsync(pair.Item1);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            var html=WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());Assert.Contains(pair.Item2,html);Assert.DoesNotContain("Não foi possível concluir a operação.",html);
        }
        foreach(var asset in new[]{("/app.css","text/css"),("/access.css","text/css"),("/access.js","text/javascript"),("/statements.css","text/css"),("/brand/financeforge-family.png","image/png")})
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,asset.Item1);
            request.Headers.AcceptEncoding.ParseAdd("gzip");
            var response=await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            Assert.Equal(asset.Item2,response.Content.Headers.ContentType?.MediaType);
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
        }
    }
    [Fact]public async Task BackupFormExportsAnAuthenticatedEncryptedSqliteCopy()
    {
        using var client=Client();await Login(client);var html=await client.GetStringAsync("/dados");var token=Token(html);Assert.NotEmpty(token);
        var response=await client.PostAsync("/api/v1/backups/export",new FormUrlEncodedContent(new Dictionary<string,string>{{"__RequestVerificationToken",token},{"password","synthetic-backup-password"}}));
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var raw=BackupCrypto.Decrypt(await response.Content.ReadAsByteArrayAsync(),"synthetic-backup-password");Assert.True(raw.AsSpan().StartsWith("SQLite format 3"u8));
        var restored=Path.Combine(folder,"restored.sqlite");await File.WriteAllBytesAsync(restored,raw);
        await using var db=FinanceDb.Open(restored);Assert.True(await DatabaseFiles.CheckIntegrityAsync(db));Assert.Equal(1,await db.Owners.CountAsync());
    }
}
