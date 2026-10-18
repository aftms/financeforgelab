using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FinanceForgeLab.Web.Services;

// A stale login form must remain rejected, but give the browser a fresh form
// instead of leaving it on an empty HTTP 400 response. Never replay its POST.
public sealed class AccessAntiforgeryRecoveryFilter(ILogger<AccessAntiforgeryRecoveryFilter> logger) : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not IAntiforgeryValidationFailedResult) return;
        logger.LogInformation("Formulário de acesso desatualizado ou inválido; a renovar a página de entrada.");
        context.Result = new RedirectResult("/Access?refresh=true");
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}
