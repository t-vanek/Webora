using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;
using D3Parking.Web.Authorization;
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
            })
            .AddEntityFrameworkStores<D3ParkingDbContext>()
            .AddSignInManager<D3Parking.Web.Identity.D3ParkingSignInManager>()
            .AddErrorDescriber<D3Parking.Infrastructure.Identity.LocalizedIdentityErrorDescriber>()
            .AddDefaultTokenProviders();

        // Route Identity's confirmation/reset emails through the application's email abstraction.
        services.AddScoped<IEmailSender<ApplicationUser>, D3Parking.Web.Email.IdentityEmailSender>();

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
            options.LogoutPath = "/logout";
            options.AccessDeniedPath = "/access-denied";
        });

        return services;
    }

    /// <summary>
    /// Adds the OpenIddict authorization server and token validation. The OpenIddict EF stores
    /// themselves are registered by the infrastructure layer (AddCore).
    /// </summary>
    public static IServiceCollection AddIdentityServer(this IServiceCollection services)
    {
        services.AddOpenIddict()
            .AddServer(options =>
            {
                options.SetAuthorizationEndpointUris("connect/authorize")
                       .SetTokenEndpointUris("connect/token")
                       .SetUserInfoEndpointUris("connect/userinfo");

                options.AllowAuthorizationCodeFlow()
                       .AllowClientCredentialsFlow()
                       .AllowRefreshTokenFlow();

                // Development credentials. Replace with real certificates before production.
                options.AddDevelopmentEncryptionCertificate()
                       .AddDevelopmentSigningCertificate();

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
