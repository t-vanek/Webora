using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using D3Parking.Application.Identity;

namespace D3Parking.Web.Identity;

/// <summary>
/// Publishes or withdraws the Entra sign-in scheme to match the settings, without a restart.
/// </summary>
/// <remarks>
/// The scheme exists exactly when sign-in is configured. That is the same invariant the sign-in
/// page and the challenge endpoint check, so a button can never appear for a scheme that is not
/// there, and a scheme can never sit in the pipeline resolving options that would throw.
/// </remarks>
public sealed class EntraSchemeReloader(
    IAuthenticationSchemeProvider schemes,
    IOptionsMonitorCache<OpenIdConnectOptions> optionsCache,
    ILogger<EntraSchemeReloader> logger) : IEntraRuntimeReloader
{
    public Task ReloadAsync(EntraIdOptions effective, CancellationToken cancellationToken = default)
    {
        var scheme = EntraAuthenticationExtensions.Scheme;

        // The cached options are dropped first: whatever is published next has to resolve the
        // settings that were just saved, not the ones the handler started life with.
        optionsCache.TryRemove(scheme);

        // Removed unconditionally, because AddScheme refuses a name that is already taken and a
        // display-name change alone still has to replace the registration.
        schemes.RemoveScheme(scheme);

        if (!effective.IsSignInConfigured)
        {
            logger.LogInformation("Entra ID sign-in is off; the {Scheme} scheme is not published", scheme);
            return Task.CompletedTask;
        }

        schemes.AddScheme(new AuthenticationScheme(scheme, effective.DisplayName, typeof(OpenIdConnectHandler)));
        logger.LogInformation("Entra ID sign-in is on for tenant {TenantId} as {DisplayName}",
            effective.TenantId, effective.DisplayName);

        return Task.CompletedTask;
    }
}
