using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
namespace FinanceForgeLab.Web.Services;
public sealed class OwnerAuthenticationState(ILoggerFactory loggerFactory) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);
    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken) => Task.FromResult(
        state.User.HasClaim(ClaimTypes.NameIdentifier, "owner") && long.TryParse(state.User.FindFirstValue("expires"), out var expiry) && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < expiry);
}
