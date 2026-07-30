using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
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
    /// Adds the OpenID Connect handler when the tenant is configured, and does nothing otherwise.
    /// </summary>
    /// <remarks>
    /// Registering nothing is deliberate: an installation with no <c>EntraId</c> section must start
    /// and behave exactly as before rather than fail on a missing authority. The sign-in page asks
    /// the same options whether to offer the button, so the two never disagree.
    /// </remarks>
    public static IServiceCollection AddEntraIdAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EntraIdOptions>(configuration.GetSection(EntraIdOptions.SectionName));

        var options = configuration.GetSection(EntraIdOptions.SectionName).Get<EntraIdOptions>() ?? new EntraIdOptions();
        if (!options.IsSignInConfigured)
        {
            return services;
        }

        services.AddAuthentication()
            .AddOpenIdConnect(Scheme, options.DisplayName, oidc =>
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
            });

        return services;
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
