namespace Webora.Web;

public static class DependencyInjection
{
    /// <summary>
    /// Adds the OpenIddict authorization server and token validation. The OpenIddict EF
    /// stores themselves are registered by the infrastructure layer (AddCore).
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

        services.AddAuthorization();

        return services;
    }
}
