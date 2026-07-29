using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using D3Parking.Domain.Accounts;
using D3Parking.Infrastructure.Identity;

namespace D3Parking.Web.Identity;

/// <summary>
/// Periodically re-checks the authentication state of live Blazor Server circuits. A circuit fixes
/// its principal when it starts, so without this an account that is blocked, suspended or deleted
/// mid-session would keep its interactive session (and all permission claims) until the user
/// navigated away. Validation mirrors sign-in: the user must still exist, be
/// <see cref="AccountStatus.Active"/> (the <see cref="D3ParkingSignInManager"/> rule) and carry the
/// current security stamp — which account transitions rotate exactly so that this check fails.
/// </summary>
public sealed class CircuitAuthenticationRevalidator(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityOptions> identityOptions)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(5);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        // UserManager is scoped (it rides a scoped DbContext), while this provider lives with the
        // circuit — resolve a fresh scope per validation pass.
        await using var scope = scopeFactory.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = await userManager.GetUserAsync(authenticationState.User);
        if (user is null || user.Status != AccountStatus.Active)
        {
            return false;
        }

        if (!userManager.SupportsUserSecurityStamp)
        {
            return true;
        }

        var principalStamp = authenticationState.User.FindFirstValue(
            identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);
        return principalStamp == await userManager.GetSecurityStampAsync(user);
    }
}
