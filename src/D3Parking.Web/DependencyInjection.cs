using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;
using D3Parking.Web.Authorization;
using D3Parking.Web.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace D3Parking.Web;

public static class DependencyInjection
{
    /// <summary>
    /// Wires ASP.NET Core Identity with cookie sign-in, EF Core stores, and default token
    /// providers. This lives in the web host because SignInManager/cookies require the ASP.NET
    /// Core shared framework. The built-in role-aware claims factory already projects each role's
    /// claims (our permissions) onto the signed-in principal, so no custom factory is needed.
    /// </summary>
    public static IServiceCollection AddD3ParkingIdentity(this IServiceCollection services)
    {
        services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
                options.SignIn.RequireConfirmedAccount = false;

                // Brute-force protection: repeated failed sign-ins temporarily lock the account
                // (PasswordSignInAsync is called with lockoutOnFailure: true in Login.razor).
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddEntityFrameworkStores<D3ParkingDbContext>()
            .AddSignInManager<D3Parking.Web.Identity.D3ParkingSignInManager>()
            .AddErrorDescriber<D3Parking.Infrastructure.Identity.LocalizedIdentityErrorDescriber>()
            .AddDefaultTokenProviders();

        // Route Identity's confirmation/reset emails through the application's email abstraction.
        services.AddScoped<IEmailSender<ApplicationUser>, D3Parking.Web.Email.IdentityEmailSender>();

        // Blocking/suspending an account rotates its security stamp; these two make that rotation
        // actually end live access. The cookie is re-validated against the stamp every 5 minutes
        // (default 30), and interactive circuits — whose principal is otherwise fixed for the
        // circuit's whole life — re-check the user, status and stamp on the same cadence.
        services.Configure<SecurityStampValidatorOptions>(options =>
            options.ValidationInterval = TimeSpan.FromMinutes(5));
        services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider,
            CircuitAuthenticationRevalidator>();

        // Align Identity's claim types with what OpenIddict expects when it issues tokens.
        services.Configure<IdentityOptions>(options =>
        {
            options.ClaimsIdentity.UserIdClaimType = Claims.Subject;
            options.ClaimsIdentity.UserNameClaimType = Claims.Name;
            options.ClaimsIdentity.RoleClaimType = Claims.Role;
        });

        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/login";
            options.LogoutPath = ExternalSignInEndpoints.SignOutPath;
            options.AccessDeniedPath = "/access-denied";

            // API and SignalR clients need real status codes. The default login redirect surfaces
            // to fetch() as a followed 302 → 200 login HTML, which JSON callers can neither detect
            // nor handle — an expired session would make POSTs look like successes. Pages keep the
            // redirect behaviour.
            options.Events.OnRedirectToLogin = context =>
            {
                if (IsApiRequest(context.Request))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                }
                else
                {
                    context.Response.Redirect(context.RedirectUri);
                }

                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                if (IsApiRequest(context.Request))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                }
                else
                {
                    context.Response.Redirect(context.RedirectUri);
                }

                return Task.CompletedTask;
            };
        });

        return services;
    }

    /// <summary>Requests that expect status codes rather than login-page redirects.</summary>
    private static bool IsApiRequest(HttpRequest request) =>
        request.Path.StartsWithSegments("/api") || request.Path.StartsWithSegments("/hubs");

    /// <summary>
    /// Adds the OpenIddict authorization server and token validation. The OpenIddict EF stores
    /// themselves are registered by the infrastructure layer (AddCore).
    /// </summary>
    public static IServiceCollection AddIdentityServer(this IServiceCollection services, IConfiguration configuration)
    {
        var certificates = configuration.GetSection(IdentityServerCertificateOptions.SectionName)
            .Get<IdentityServerCertificateOptions>() ?? new IdentityServerCertificateOptions();

        services.AddOpenIddict()
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris("connect/authorize")
                       .SetTokenEndpointUris("connect/token")
                       .SetUserInfoEndpointUris("connect/userinfo");

                options.AllowAuthorizationCodeFlow()
                       .AllowClientCredentialsFlow()
                       .AllowRefreshTokenFlow();

                // Real certificates come from configuration ("IdentityServer" section, PFX files).
                // Without them the development certificates keep everything working, but they
                // regenerate with the machine/container — Program.cs warns outside Development.
                if (certificates.HasSigning)
                {
                    options.AddSigningCertificate(X509CertificateLoader.LoadPkcs12FromFile(
                        certificates.SigningCertificatePath!, certificates.SigningCertificatePassword));
                }
                else
                {
                    options.AddDevelopmentSigningCertificate();
                }

                if (certificates.HasEncryption)
                {
                    options.AddEncryptionCertificate(X509CertificateLoader.LoadPkcs12FromFile(
                        certificates.EncryptionCertificatePath!, certificates.EncryptionCertificatePassword));
                }
                else
                {
                    options.AddDevelopmentEncryptionCertificate();
                }

                options.UseAspNetCore()
                       .EnableAuthorizationEndpointPassthrough()
                       .EnableTokenEndpointPassthrough()
                       .EnableUserInfoEndpointPassthrough();
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });

        return services;
    }

    /// <summary>Registers permission-based authorization (role → permission claims, checked via policies).</summary>
    public static IServiceCollection AddPermissionAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization();
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        return services;
    }
}
