using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Webora.Application.Abstractions.Email;

namespace Webora.Infrastructure.Email;

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));
        services.Configure<SmtpOAuth2Options>(configuration.GetSection(SmtpOAuth2Options.SectionName));

        services.AddHttpClient(ClientCredentialsTokenProvider.HttpClientName);
        services.AddSingleton<ISmtpAccessTokenProvider, ClientCredentialsTokenProvider>();

        services.AddScoped<IEmailSender, SmtpEmailSender>();

        return services;
    }
}
