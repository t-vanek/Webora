using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using D3Parking.Application.Abstractions.Email;

namespace D3Parking.Infrastructure.Email;

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SmtpOptions>()
            .Bind(configuration.GetSection(SmtpOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Host), "Smtp:Host is required.")
            .Validate(o => o.Port is >= 1 and <= 65535, "Smtp:Port must be between 1 and 65535.")
            .Validate(o => o.TimeoutSeconds is >= 5 and <= 300,
                "Smtp:TimeoutSeconds must be between 5 and 300.")
            .Validate(o => System.Net.Mail.MailAddress.TryCreate(o.SenderEmail, out _),
                "Smtp:SenderEmail must be a valid email address.")
            .Validate(o => o.Authentication != SmtpAuthMode.Basic
                    || (!string.IsNullOrWhiteSpace(o.UserName) && !string.IsNullOrWhiteSpace(o.Password)),
                "SMTP basic authentication requires Smtp:UserName and Smtp:Password.")
            .Validate(o => o.Authentication != SmtpAuthMode.OAuth2 || !string.IsNullOrWhiteSpace(o.UserName),
                "SMTP OAuth2 authentication requires Smtp:UserName.")
            .ValidateOnStart();
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
