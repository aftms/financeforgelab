using System.Security.Claims;
using FinanceForgeLab.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FinanceForgeLab.Web.Pages;

[AllowAnonymous]
public sealed class AccessModel(OwnerAuth auth) : PageModel
{
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string Confirm { get; set; } = "";
    [BindProperty] public bool Setup { get; set; }
    public bool FirstAccess { get; private set; }
    public string? Error { get; private set; }
    public async Task<IActionResult> OnGetAsync() { if (User.Identity?.IsAuthenticated == true) return Redirect("/"); FirstAccess = !await auth.ExistsAsync(); return Page(); }
    public async Task<IActionResult> OnPostAsync()
    {
        FirstAccess = !await auth.ExistsAsync();
        var success = FirstAccess && Setup ? await auth.SetupAsync(Password ?? "", Confirm ?? "") : !FirstAccess && !Setup && await auth.VerifyAsync(Password ?? "");
        if (!success) { Error = FirstAccess ? "Confirma uma palavra-passe com pelo menos 12 caracteres." : "Não foi possível entrar. Verifica a palavra-passe ou aguarda alguns minutos."; return Page(); }
        var expires = DateTimeOffset.UtcNow.AddHours(8);
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "owner"), new(ClaimTypes.Name, "Proprietário"), new("expires", expires.ToUnixTimeSeconds().ToString())], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { ExpiresUtc = expires, IsPersistent = false });
        return Redirect("/");
    }
    public async Task<IActionResult> OnPostLogoutAsync() { await HttpContext.SignOutAsync(); return Redirect("/Access"); }
}
