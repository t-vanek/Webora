using System.Security.Cryptography;
using System.Text;
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
///
/// Idempotent on purpose: this runs on every poll of <see cref="EntraSchemeSynchronizer"/>, not
/// only on a save, so the case where nothing changed has to cost nothing and change nothing.
/// </remarks>
public sealed class EntraSchemeReloader(
    IAuthenticationSchemeProvider schemes,
    IOptionsMonitorCache<OpenIdConnectOptions> optionsCache,
    ILogger<EntraSchemeReloader> logger) : IEntraRuntimeReloader
{
    /// <summary>
    /// Serialises the withdraw/publish pair. Neither call is individually unsafe, but between them
    /// the scheme does not exist, and a request that resolves it there fails for no reason the
    /// person can act on.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// What was last applied to this process, as a digest — a digest and not the settings so the
    /// client secret does not sit in a second long-lived field for the sake of a comparison.
    /// </summary>
    private string? _applied;

    public async Task ReloadAsync(EntraIdOptions effective, CancellationToken cancellationToken = default)
    {
        var scheme = EntraAuthenticationExtensions.Scheme;
        var digest = Digest(effective);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Nothing changed since this process last reconciled. Rebuilding the registration anyway
            // would throw away the handler's cached discovery document and reopen the window above,
            // every poll, on every instance, forever.
            if (_applied == digest)
            {
                return;
            }

            // The cached options are dropped first: whatever is published next has to resolve the
            // settings that were just saved, not the ones the handler started life with.
            optionsCache.TryRemove(scheme);

            // Removed unconditionally, because AddScheme refuses a name that is already taken and a
            // display-name change alone still has to replace the registration.
            schemes.RemoveScheme(scheme);

            if (effective.IsSignInConfigured)
            {
                schemes.AddScheme(new AuthenticationScheme(scheme, effective.DisplayName, typeof(OpenIdConnectHandler)));
                logger.LogInformation("Entra ID sign-in is on for tenant {TenantId} as {DisplayName}",
                    effective.TenantId, effective.DisplayName);
            }
            else
            {
                logger.LogInformation("Entra ID sign-in is off; the {Scheme} scheme is not published", scheme);
            }

            _applied = digest;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Everything the published scheme and its handler are built from.</summary>
    private static string Digest(EntraIdOptions options)
    {
        // Unit separator: a delimiter no field can contain, so "ab" + "c" cannot collide
        // with "a" + "bc" and read as unchanged when the settings did change.
        var material = string.Join('\u001f',
            options.IsSignInConfigured,
            options.TenantId,
            options.ResolvedAuthority,
            options.ClientId,
            options.ClientSecret,
            options.CallbackPath,
            options.SignedOutCallbackPath,
            options.DisplayName);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
