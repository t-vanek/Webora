using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
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
    public const string SignOutPath = "/account/signout";

    /// <summary>Where a first-time federated account is sent to be asked for what the token cannot carry.</summary>
    public const string WelcomePath = "/account/welcome";

    public static IEndpointRouteBuilder MapExternalSignInApi(this IEndpointRouteBuilder app)
    {
        app.MapGet(ChallengePath, async (
            string? returnUrl,
            HttpContext context,
            IEntraSettingsService entra,
            ILoggerFactory loggerFactory) =>
        {
            if (!(await entra.GetEffectiveAsync(context.RequestAborted)).IsSignInConfigured)
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

            // The id token moves from the external cookie, which is already gone, into the
            // application cookie — the only place signing out can still reach it.
            var properties = new AuthenticationProperties();
            if (result.Properties?.GetTokenValue(EntraAuthenticationExtensions.IdTokenName) is { } idToken)
            {
                properties.StoreTokens(
                    [new AuthenticationToken { Name = EntraAuthenticationExtensions.IdTokenName, Value = idToken }]);
            }

            await signInManager.SignInAsync(user, properties, EntraAuthenticationExtensions.Scheme);
            logger.LogInformation("{UserId} signed in through {Provider}", user.Id, EntraAuthenticationExtensions.Scheme);

            var destination = Safe(returnUrl);
            if (synced.Created)
            {
                // The directory carries a name and an address but not a licence plate, and the
                // fleet pairing is built on the plate. Asked once, on the way in, and skippable —
                // an account created for someone must not open onto a form they cannot get past.
                destination = $"{WelcomePath}?returnUrl={Uri.EscapeDataString(destination)}";
            }

            return Results.Redirect(destination);
        });

        // Ending the local session is only half the job: the directory session outlives it, and the
        // next click on "sign in with Microsoft" would sign the same person straight back in with
        // nothing asked — on a shared computer, as whoever used it last.
        //
        // One POST handles both local and federated sessions. Binding the return URL from the form
        // attaches ASP.NET Core's antiforgery metadata to this minimal endpoint, so UseAntiforgery
        // validates the cookie/token pair before the handler can clear either session. As a GET,
        // another site could sign a visitor out merely by embedding the URL.
        app.MapPost(SignOutPath, async (
            [FromForm] string? returnUrl,
            HttpContext context,
            SignInManager<ApplicationUser> signInManager,
            IEntraSettingsService entra) =>
        {
            // Read the account kind and token before the sign-out below deletes the cookie. Entra
            // may be configured for the site while this particular session still belongs to a
            // local account; only a federated account should visit Microsoft's end-session page.
            var user = await signInManager.UserManager.GetUserAsync(context.User);
            var isFederated = user?.IsFederated == true;
            var idToken = await context.GetTokenAsync(
                IdentityConstants.ApplicationScheme, EntraAuthenticationExtensions.IdTokenName);

            await signInManager.SignOutAsync();
            var destination = Safe(returnUrl);

            // Sign-in switched off between issuing the cookie and this click: there is no scheme to
            // redirect to, and the local session is already ended, which is the part that matters.
            if (!isFederated || !(await entra.GetEffectiveAsync(context.RequestAborted)).IsSignInConfigured)
            {
                return Results.LocalRedirect(destination);
            }

            var properties = new AuthenticationProperties { RedirectUri = destination };
            if (!string.IsNullOrEmpty(idToken))
            {
                properties.Items[EntraAuthenticationExtensions.IdTokenHintItem] = idToken;
            }

            return Results.SignOut(properties, [EntraAuthenticationExtensions.Scheme]);
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Only same-site relative paths are followed back. An absolute URL here would turn the sign-in
    /// into an open redirect — and one that arrives carrying a freshly minted session cookie.
    /// </summary>
    /// <remarks>
    /// The backslash matters as much as the second slash: browsers normalise <c>/\evil.example</c>
    /// to <c>//evil.example</c> and follow it off-site, which is why the framework's own
    /// <c>IsLocalUrl</c> rejects both. Checking only for <c>//</c> leaves the same door open.
    /// </remarks>
    internal static string Safe(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl)
        && returnUrl.StartsWith('/')
        && (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'))
            ? returnUrl
            : "/";
}
