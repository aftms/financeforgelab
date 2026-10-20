using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinanceForgeLab.Core;
using FinanceForgeLab.Infrastructure;
using FinanceForgeLab.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FinanceForgeLab.Tests;

public sealed class RemoteAccessTests : IAsyncLifetime
{
    const string Origin = "https://finance.example.test";
    readonly string folder = Path.Combine(Path.GetTempPath(), "ffl-remote-" + Guid.NewGuid().ToString("N"));
    WebApplicationFactory<Program> app = null!;
    string DbPath => Path.Combine(folder, "test.sqlite");
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(folder);
        await using (var db = FinanceDb.Open(DbPath)) { await db.Database.MigrateAsync(); db.Add(new Household()); await db.SaveChangesAsync(); }
        app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Testing")
            .UseSetting("Finance:DatabasePath", DbPath).UseSetting("Finance:PrivatePath", Path.Combine(folder,"private"))
            .UseSetting("Finance:PublicOrigin", Origin).ConfigureTestServices(s => s.AddSingleton<IStartupFilter, LoopbackPeer>()));
    }
    public async Task DisposeAsync() { await app.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
    sealed class LoopbackPeer : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder => {
            builder.Use((context, nextMiddleware) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; return nextMiddleware(); }); next(builder);
        };
    }
    HttpClient Client(bool forwarded = true)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new(Origin), AllowAutoRedirect = false });
        if (forwarded) client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        return client;
    }
    Task<bool> Setup() => new OwnerAuth(() => FinanceDb.Open(DbPath)).SetupAsync("synthetic-remote-password", "synthetic-remote-password");
    static string Token(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

    [Theory]
    [InlineData("http://finance.example.test")]
    [InlineData("https://localhost")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://finance.example.test/path")]
    [InlineData("https://finance.example.test?secret=bad")]
    [InlineData("https://owner:secret@finance.example.test")]
    [InlineData("https://finance.example.test:1234")]
    public void InvalidPublicOriginsFailClosed(string value) => Assert.Throws<InvalidOperationException>(() => new RemoteAccessPolicy(value));

    [Fact] public void LocalAccessIsDefaultAndForwardedHeadersNeverBypassIt()
    {
        var policy = new RemoteAccessPolicy(null); var context = new DefaultHttpContext();
        context.Request.Host = new("localhost", 5148); context.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.True(policy.Allows(context));
        context.Request.Headers["X-Forwarded-Proto"] = "https"; Assert.False(policy.Allows(context));
        context.Request.Headers.Clear(); context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.25"); Assert.False(policy.Allows(context));
        context.Connection.RemoteIpAddress = IPAddress.Loopback; context.Request.Host = new("finance.example.test"); Assert.False(policy.Allows(context));
    }
    [Fact] public void RemoteRequestsRequireLoopbackHttpsAndSameOriginForWritesAndSockets()
    {
        var policy = new RemoteAccessPolicy(Origin); var context = new DefaultHttpContext();
        context.Request.Method = "GET"; context.Request.Host = new("finance.example.test"); context.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.False(policy.Allows(context)); context.Request.Headers["X-Forwarded-Proto"] = "https"; Assert.True(policy.Allows(context));
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.25"); Assert.False(policy.Allows(context)); context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Method = "POST"; Assert.False(policy.Allows(context));
        context.Request.Headers.Origin = "https://other.example.test"; Assert.False(policy.Allows(context));
        context.Request.Headers.Origin = Origin; Assert.True(policy.Allows(context));
        context.Request.Headers["X-Forwarded-Host"] = "other.example.test"; Assert.False(policy.Allows(context));
        context.Request.Headers.Remove("X-Forwarded-Host"); context.Request.Headers["X-Forwarded-Proto"] = "https,http"; Assert.False(policy.Allows(context));
        context.Request.Headers["X-Forwarded-Proto"] = "https"; context.Request.Method = "GET"; context.Request.Headers.Remove("Origin");
        context.Request.Headers.Upgrade = "websocket"; Assert.False(policy.Allows(context));
        context.Request.Headers.Origin = Origin; Assert.True(policy.Allows(context));
    }
    [Fact] public async Task RemoteSetupIsClosedAndPublicReadsDoNotExposeFinancialData()
    {
        using var client = Client(); var response = await client.GetAsync("/Access"); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        response = await client.PostAsync("/Access", new FormUrlEncodedContent(new Dictionary<string,string>{{"Setup","true"},{"Password","synthetic-remote-password"},{"Confirm","synthetic-remote-password"}}));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        client.DefaultRequestHeaders.Add("Origin", Origin);
        response = await client.PostAsync("/Access", new FormUrlEncodedContent(new Dictionary<string,string>{{"Setup","true"},{"Password","synthetic-remote-password"},{"Confirm","synthetic-remote-password"}}));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using (var db = FinanceDb.Open(DbPath)) Assert.Empty(await db.Owners.ToListAsync());
        using var local = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("http://localhost"), AllowAutoRedirect = false });
        var localPage = await local.GetStringAsync("/Access");
        response = await local.PostAsync("/Access", new FormUrlEncodedContent(new Dictionary<string,string>{{"__RequestVerificationToken",Token(localPage)},{"Setup","true"},{"Password","synthetic-remote-password"},{"Confirm","synthetic-remote-password"}}));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Access")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/export.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
        using var missingProxy = Client(false); Assert.Equal(HttpStatusCode.Forbidden, (await missingProxy.GetAsync("/Access")).StatusCode);
    }
    [Fact] public async Task RemoteLoginUsesSecureCookieAndWritesStillRequireAntiforgery()
    {
        Assert.True(await Setup()); using var client = Client(); client.DefaultRequestHeaders.Add("Origin", Origin);
        var page = await client.GetStringAsync("/Access");
        var response = await client.PostAsync("/Access", new FormUrlEncodedContent(new Dictionary<string,string>{{"__RequestVerificationToken",Token(page)},{"Password","synthetic-remote-password"},{"Setup","false"}}));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith("FinanceForgeLab.Owner="));
        Assert.Contains("secure", cookie); Assert.Contains("httponly", cookie); Assert.Contains("samesite=strict", cookie);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/accounts")).StatusCode);
        var form = new AccountForm { Name = "Synthetic remote account", BalanceStartDate = new(2026,1,1) };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/accounts",form)).StatusCode);
        var token = await client.GetFromJsonAsync<JsonElement>("/api/v1/security/token"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.GetProperty("token").GetString());
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Add("Origin","https://other.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/accounts",form)).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Add("Origin",Origin);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/accounts",form)).StatusCode);
        await using var db = FinanceDb.Open(DbPath); Assert.Single(await db.Accounts.ToListAsync());
    }
}
