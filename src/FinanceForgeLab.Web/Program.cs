using System.Globalization;
using System.Net;
using System.Text;
using FinanceForgeLab.Infrastructure;
using FinanceForgeLab.Web.Components;
using FinanceForgeLab.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
// The local launcher runs build output outside Development. Load its static-asset
// manifest so compressed CSS/JS are served from their build locations as well.
builder.WebHost.UseStaticWebAssets();
var workspace = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "../.."));
var dbPath = Path.GetFullPath(builder.Configuration["Finance:DatabasePath"] ?? Path.Combine(workspace, ".private/finance.sqlite"));
var privatePath = Path.GetFullPath(builder.Configuration["Finance:PrivatePath"] ?? Path.Combine(workspace, ".private/web"));
if (!File.Exists(dbPath)) throw new InvalidOperationException("Cria a base operacional antes de iniciar a aplicação.");
await using (var check = FinanceDb.Open(dbPath))
    if ((await check.Database.GetPendingMigrationsAsync()).Any()) throw new InvalidOperationException("Existem migrações pendentes. Executa a atualização com backup descrita no README.");
Directory.CreateDirectory(privatePath);
builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
builder.Services.AddSingleton<Func<FinanceDb>>(() => FinanceDb.Open(dbPath));
builder.Services.AddSingleton<FinanceService>(); builder.Services.AddSingleton<OwnerAuth>();
builder.Services.AddSingleton(new BankImportService(() => FinanceDb.Open(dbPath), Path.Combine(privatePath, "statements")));
builder.Services.AddSingleton(new BackupManager(dbPath, Path.Combine(privatePath, "backups")));
if (!builder.Environment.IsEnvironment("Testing")) builder.Services.AddHostedService<DailyBackup>();
var protection = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(privatePath, "keys"))).SetApplicationName("FinanceForgeLab");
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => {
    o.LoginPath = "/Access"; o.Cookie.Name = "FinanceForgeLab.Owner"; o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = false;
    o.Events.OnRedirectToLogin = c => { if (c.Request.Path.StartsWithSegments("/api")) c.Response.StatusCode = 401; else c.Response.Redirect(c.RedirectUri); return Task.CompletedTask; };
});
builder.Services.AddAuthorization(); builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, OwnerAuthenticationState>();
builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddRazorPages(); builder.Services.AddRazorComponents().AddInteractiveServerComponents(o => o.DetailedErrors = false);
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("pt-PT"); CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("pt-PT");
var app = builder.Build();
app.Use(async (ctx, next) => {
    if (ctx.Connection.RemoteIpAddress is IPAddress ip && !IPAddress.IsLoopback(ip)) { ctx.Response.StatusCode = 403; return; }
    ctx.Response.Headers.XContentTypeOptions = "nosniff"; ctx.Response.Headers.XFrameOptions = "DENY"; ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    ctx.Response.Headers.CacheControl = "no-store"; await next();
});
app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseAuthentication(); app.UseAuthorization(); app.UseAntiforgery();
app.MapStaticAssets().AllowAnonymous(); app.MapRazorPages();
var api = app.MapGroup("/api/v1").RequireAuthorization();
api.AddEndpointFilter(async (context, next) => {
    try {
        if (!HttpMethods.IsGet(context.HttpContext.Request.Method)) await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext);
        return await next(context);
    }
    catch (AntiforgeryValidationException) { return Results.BadRequest(new { Error = "Pedido inválido. Atualiza a página." }); }
    catch (FinanceRuleException e) { return Results.BadRequest(new { Error = e.Message }); }
    catch (DbUpdateConcurrencyException) { return Results.Conflict(new { Error = "Os dados foram alterados noutra sessão." }); }
});
api.MapGet("/security/token", (HttpContext ctx, IAntiforgery anti) => Results.Ok(new { Token = anti.GetAndStoreTokens(ctx).RequestToken }));
api.MapGet("/dashboard", (int? year, FinanceService service) => service.DashboardAsync(year));
api.MapGet("/movements", ([AsParameters] ApiMovementFilter filter, FinanceService service) => service.MovementsAsync(filter.ToFilter()));
api.MapGet("/accounts", (FinanceService service) => service.AccountsAsync());
api.MapGet("/movements/{id:long}", (long id, FinanceService service) => service.MovementAsync(id));
api.MapPost("/movements/{id:long}/void", async (long id, VoidRequest form, FinanceService service) => { await service.VoidMovementAsync(id, form.Version, form.RequestId); return Results.NoContent(); });
api.MapGet("/categories", (FinanceService service) => service.CategoriesAsync());
api.MapGet("/statements/{id:long}/original", async (long id, BankImportService service) => { var file = await service.OriginalAsync(id); return Results.File(file.Content, "application/pdf", file.Name); });
api.MapPost("/movements", async (MovementForm form, FinanceService service) => Results.Ok(new { Id = await service.SaveMovementAsync(form) }));
api.MapPost("/accounts", async (AccountForm form, FinanceService service) => Results.Ok(new { Id = await service.SaveAccountAsync(form) }));
api.MapPost("/categories", async (CategoryForm form, FinanceService service) => Results.Ok(new { Id = await service.SaveCategoryAsync(form) }));
api.MapGet("/export.csv", async ([AsParameters] ApiMovementFilter filter, FinanceService service) => Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(await service.ExportCsvAsync(filter.ToFilter()))).ToArray(), "text/csv; charset=utf-8", "FinanceForgeLab-movimentos.csv"));
api.MapPost("/backups/export", async (HttpContext ctx, BackupManager backups) => {
    var form = await ctx.Request.ReadFormAsync(); var password = form["password"].ToString();
    if (password.Length < 12 || password.Length > 200) return Results.BadRequest("Usa uma palavra-passe de cópia com pelo menos 12 caracteres.");
    var path = await backups.CreateAsync("export");
    return Results.File(BackupCrypto.Encrypt(await File.ReadAllBytesAsync(path), password), "application/octet-stream", $"FinanceForgeLab-{DateTime.Today:yyyy-MM-dd}.fflbackup");
});
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization();
app.Run();

public sealed class ApiMovementFilter
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public long? AccountId { get; set; }
    public long? CategoryId { get; set; }
    public long? PersonId { get; set; }
    public FinanceForgeLab.Core.MovementKind? Kind { get; set; }
    public string? Search { get; set; }
    public bool? IncludeVoided { get; set; }
    public int? Page { get; set; }
    public MovementFilter ToFilter() => new() { From = From, To = To, AccountId = AccountId, CategoryId = CategoryId, PersonId = PersonId, Kind = Kind, Search = Search, IncludeVoided = IncludeVoided ?? false, Page = Page ?? 1 };
}
public partial class Program;
public sealed record VoidRequest(int Version, Guid RequestId);
