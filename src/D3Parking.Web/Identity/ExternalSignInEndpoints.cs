using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using D3Parking.Application.Identity;
using D3Parking.Domain.Accounts;
using D3Parking.Infrastructure.Identity;

namespace D3Parking.Web.Identity;

/// <summary>
/// The two hops of an external sign-in: send the browser to the directory, and turn what comes back
/// into a session here.
/// </summary>
/// <remarks>
/// Endpoints rather than Razor components because both hops need the raw <c>HttpContext</c> — one
/// to write a challenge, the other to read the external cookie and write the application cookie.
/// A component would only be able to do that during a form post, which is not how a redirect back
/// from an identity provider arrives.
/// </remarks>
public static class ExternalSignInEndpoints
{
    public const string ChallengePath = "/account/external/challenge";
    public const string CallbackPath = "/account/external/callback";

    public static IEndpointRouteBuilder MapExternalSignInApi(this IEndpointRouteBuilder app)
    {
        app.MapGet(ChallengePath, async (
            string? returnUrl,
            HttpContext context,
            IOptions<EntraIdOptions> options,
            ILoggerFactory loggerFactory) =>
        {
            if (!options.Value.IsSignInConfigured)
            {
                return Results.NotFound();
            }

            var properties = new AuthenticationProperties
            {
                RedirectUri = $"{CallbackPath}?returnUrl={Uri.EscapeDataString(Safe(returnUrl))}",
            };

            try
            {
                // Challenged inline rather than returned as a result, so the failure below is
                // catchable: the handler fetches the tenant's discovery document on the first
                // challenge, and a typo in the tenant id or an unreachable Entra would otherwise
                // surface as a raw 500 on a page the person reached by clicking "sign in".
                await context.ChallengeAsync(EntraAuthenticationExtensions.Scheme, properties);
                return Results.Empty;
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("ExternalSignIn")
                    .LogError(ex, "Could not start an external sign-in; check the EntraId configuration and connectivity");
                return Results.Redirect("/login?error=external");
            }
        });

        app.MapGet(CallbackPath, async (
            string? returnUrl,
            HttpContext context,
            IExternalDirectoryService directory,
            SignInManager<ApplicationUser> signInManager,
            UserManager<ApplicationUser> userManager,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("ExternalSignIn");

            var result = await context.AuthenticateAsync(IdentityConstants.ExternalScheme);
            if (!result.Succeeded || result.Principal is null)
            {
                logger.LogWarning("An external sign-in came back without a usable principal");
                return Results.Redirect("/login?error=external");
            }

            // Whatever happens next, the external cookie has done its job. Leaving it behind would
            // let a stale directory assertion be replayed into a later sign-in attempt.
            await context.SignOutAsync(IdentityConstants.ExternalScheme);

            var identity = result.Principal.ReadEntraIdentity();
            if (identity is null)
            {
                logger.LogWarning("An external sign-in carried no object id or no email address");
                return Results.Redirect("/login?error=external");
            }

            var synced = await directory.SyncAsync(identity, cancellationToken);
            if (!synced.Succeeded)
            {
                logger.LogWarning("Refused an external sign-in for {ObjectId}: {Errors}",
                    identity.ObjectId, string.Join("; ", synced.Errors));
                return Results.Redirect("/login?error=external-account");
            }

            var user = await userManager.FindByIdAsync(synced.UserId.ToString());
            if (user is null)
            {
                return Results.Redirect("/login?error=external");
            }

            // The directory says who they are; this application still decides whether that account
            // may sign in. A blocked or deactivated account is refused here exactly as it is on the
            // password path.
            if (user.Status != AccountStatus.Active)
            {
                logger.LogInformation("An external sign-in was refused for {UserId}: the account is {Status}",
                    user.Id, user.Status);
                return Results.Redirect($"/login?error=status-{user.Status}");
            }

            // Recorded as an Identity login so the account shows its provider in the usual places
            // and so a later local password can never be added silently alongside it.
            if (await userManager.FindByLoginAsync(EntraAuthenticationExtensions.Scheme, identity.ObjectId) is null)
            {
                await userManager.AddLoginAsync(user, new UserLoginInfo(
                    EntraAuthenticationExtensions.Scheme, identity.ObjectId, EntraAuthenticationExtensions.Scheme));
            }

            await signInManager.SignInAsync(user, isPersistent: false, EntraAuthenticationExtensions.Scheme);
            logger.LogInformation("{UserId} signed in through {Provider}", user.Id, EntraAuthenticationExtensions.Scheme);

            return Results.Redirect(Safe(returnUrl));
        });

        return app;
    }

    /// <summary>
    /// Only same-site relative paths are followed back. An absolute URL here would turn the sign-in
    /// into an open redirect — and one that arrives carrying a freshly minted session cookie.
    /// </summary>
    private static string Safe(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? returnUrl
            : "/";
}
