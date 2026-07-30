using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using D3Parking.Application.Identity;
using D3Parking.Domain.Authorization;

namespace D3Parking.Web.Identity;

/// <summary>
/// Wires Microsoft Entra ID as an external sign-in provider alongside local passwords.
/// </summary>
public static class EntraAuthenticationExtensions
{
    /// <summary>The authentication scheme name; doubles as the ASP.NET Identity login provider key.</summary>
    public const string Scheme = ExternalProviders.EntraId;

    /// <summary>
    /// Registers the OpenID Connect handler and its options without publishing the scheme.
    /// </summary>
    /// <remarks>
    /// The scheme is added at runtime by <see cref="EntraSchemeReloader"/> once the effective
    /// settings are known, and removed again the moment sign-in is switched off. It is deliberately
    /// absent from the startup scheme map: the OpenID Connect handler answers every request as an
    /// <c>IAuthenticationRequestHandler</c>, so a published scheme resolves its options on each one
    /// — and unconfigured options throw rather than sit quietly. An installation with no tenant must
    /// start and behave exactly as it did before.
    ///
    /// This is why the pieces are wired by hand instead of through <c>AddOpenIdConnect</c>, whose
    /// only extra step is the scheme registration this must not perform.
    /// </remarks>
    public static IServiceCollection AddEntraIdAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPostConfigureOptions<OpenIdConnectOptions>, OpenIdConnectPostConfigureOptions>());
        services.AddTransient<OpenIdConnectHandler>();

        // Resolved fresh whenever the options cache is cleared, which is what a save does.
        services.AddOptions<OpenIdConnectOptions>(Scheme)
            .Configure<IEntraSettingsService>((oidc, entra) => Configure(oidc, entra.GetEffective()));

        // Replaces the no-op the infrastructure layer registers for hosts that have no scheme map.
        services.Replace(ServiceDescriptor.Singleton<IEntraRuntimeReloader, EntraSchemeReloader>());

        return services;
    }

    /// <summary>
    /// Brings the running application in line with the settings that were just saved.
    /// </summary>
    /// <remarks>
    /// Called once at startup and after every save, so switching sign-in on is a page action rather
    /// than a deployment. Nothing here touches a request in flight: schemes are resolved per
    /// request, so the next one sees the new arrangement and the current one finishes on the old.
    /// </remarks>
    public static async Task UseEntraIdAuthenticationAsync(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EntraId");

        try
        {
            var settings = await app.Services.GetRequiredService<IEntraSettingsService>().GetEffectiveAsync();
            await app.Services.GetRequiredService<IEntraRuntimeReloader>().ReloadAsync(settings);
        }
        catch (Exception ex)
        {
            // The settings live in the database, which on a fresh checkout has not been migrated
            // yet. Failing to read them must not stop the application that would run the migration
            // — local sign-in is unaffected, and the first save applies the settings anyway.
            logger.LogError(ex, "Could not apply the stored Entra ID settings at startup; external sign-in stays off until they are saved again");
        }
    }

    /// <summary>Translates the effective settings into handler options.</summary>
    private static void Configure(OpenIdConnectOptions oidc, EntraIdOptions options)
    {
        oidc.Authority = options.ResolvedAuthority;
        oidc.ClientId = options.ClientId;
        oidc.ClientSecret = options.ClientSecret;
        oidc.CallbackPath = options.CallbackPath;
        oidc.SignedOutCallbackPath = options.SignedOutCallbackPath;

        // The authorization code flow with PKCE: the only flow that keeps tokens off the
        // browser's address bar and out of its history.
        oidc.ResponseType = OpenIdConnectResponseType.Code;
        oidc.UsePkce = true;
        oidc.SaveTokens = false;
        oidc.GetClaimsFromUserInfoEndpoint = false;

        oidc.Scope.Clear();
        oidc.Scope.Add("openid");
        oidc.Scope.Add("profile");
        oidc.Scope.Add("email");

        // The handler signs into Identity's external cookie, not the application cookie:
        // the account is resolved and signed in by the callback below, so a valid Entra
        // token alone is never a session here.
        oidc.SignInScheme = IdentityConstants.ExternalScheme;

        oidc.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "name",
            RoleClaimType = "roles",
            // Single-tenant: the issuer must be this directory. Left to the metadata alone,
            // a multi-tenant authority would accept any tenant's token.
            ValidateIssuer = true,
            ValidIssuers =
            [
                $"https://login.microsoftonline.com/{options.TenantId}/v2.0",
                $"https://sts.windows.net/{options.TenantId}/",
            ],
        };

        oidc.Events = new OpenIdConnectEvents
        {
            OnRemoteFailure = context =>
            {
                // A cancelled consent screen is not an error worth a stack trace; send the
                // person back to the sign-in page with a message they can act on.
                context.Response.Redirect("/login?error=external");
                context.HandleResponse();
                return Task.CompletedTask;
            },
        };
    }

    /// <summary>
    /// Reads what Entra asserted out of the external cookie principal.
    /// </summary>
    /// <remarks>
    /// <c>oid</c> is the only stable handle — mail, UPN and display name all change with a marriage,
    /// a team move or a rename, and reusing any of them as the key would silently split or merge
    /// accounts. The email is taken from the claims Entra actually sends, in the order it prefers.
    /// </remarks>
    public static ExternalIdentity? ReadEntraIdentity(this ClaimsPrincipal principal)
    {
        var objectId = principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
            ?? principal.FindFirstValue("oid");
        if (string.IsNullOrWhiteSpace(objectId))
        {
            return null;
        }

        var email = principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("email")
            ?? principal.FindFirstValue("preferred_username")
            ?? principal.FindFirstValue("upn");
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var tenantId = principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid")
            ?? principal.FindFirstValue("tid");

        var roles = principal.FindAll("roles")
            .Concat(principal.FindAll(ClaimTypes.Role))
            .Select(claim => claim.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ExternalIdentity(
            Scheme,
            objectId,
            email,
            tenantId,
            principal.FindFirstValue("name") ?? principal.FindFirstValue(ClaimTypes.Name),
            Department: null,
            // An empty array, not null: a sign-in speaks with authority about roles, and someone
            // whose app role assignment was revoked must lose it here too.
            Roles: roles);
    }
}
