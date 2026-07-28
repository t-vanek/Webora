using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using D3Parking.Application.Abstractions.Email;

namespace D3Parking.Infrastructure.Email;

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));
        services.Configure<SmtpOAuth2Options>(configuration.GetSection(SmtpOAuth2Options.SectionName));

        services.AddHttpClient(ClientCredentialsTokenProvider.HttpClientName);
        services.AddSingleton<ISmtpAccessTokenProvider, ClientCredentialsTokenProvider>();

        // Callers send through IEmailSender, which only enqueues; the Wolverine handler is the
        // single place that touches the SMTP transport.
        services.AddScoped<IEmailTransport, SmtpEmailSender>();
        services.AddScoped<IEmailSender, QueuedEmailSender>();

        return services;
    }
}
