using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Webora.Application.Abstractions.Email;

namespace Webora.Infrastructure.Email;

public sealed class SmtpEmailSender(
    IOptions<SmtpOptions> options,
    ISmtpAccessTokenProvider tokenProvider,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.SenderName, settings.SenderEmail));
        mime.To.Add(new MailboxAddress(message.ToName ?? message.To, message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder
        {
            HtmlBody = message.HtmlBody,
            TextBody = message.TextBody,
        }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.Host, settings.Port, ToSocketOptions(settings.Security), cancellationToken);

        switch (settings.Authentication)
        {
            case SmtpAuthMode.Basic:
                if (string.IsNullOrEmpty(settings.UserName) || settings.Password is null)
                {
                    throw new InvalidOperationException("SMTP basic auth requires 'Smtp:UserName' and 'Smtp:Password'.");
                }

                await client.AuthenticateAsync(settings.UserName, settings.Password, cancellationToken);
                break;

            case SmtpAuthMode.OAuth2:
                if (string.IsNullOrEmpty(settings.UserName))
                {
                    throw new InvalidOperationException("SMTP OAuth2 requires 'Smtp:UserName' (the mailbox/account).");
                }

                var token = await tokenProvider.GetAccessTokenAsync(cancellationToken);
                await client.AuthenticateAsync(new SaslMechanismOAuth2(settings.UserName, token), cancellationToken);
                break;

            case SmtpAuthMode.None:
            default:
                break;
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);

        logger.LogInformation("Sent email to {Recipient} (subject: {Subject})", message.To, message.Subject);
    }

    private static SecureSocketOptions ToSocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.Auto,
    };
}
