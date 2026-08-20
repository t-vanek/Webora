using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;
using D3Parking.Application.Abstractions.Email;
using D3Parking.Application.Settings;

namespace D3Parking.Infrastructure.Email;

public sealed class SmtpEmailSender(
    IOptions<SmtpOptions> options,
    ISmtpAccessTokenProvider tokenProvider,
    ISiteSettingsService siteSettings,
    ILogger<SmtpEmailSender> logger) : IEmailTransport
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var charset = await siteSettings.GetEmailCharsetAsync(cancellationToken);

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.SenderName, settings.SenderEmail));
        mime.To.Add(new MailboxAddress(message.ToName ?? message.To, message.To));
        mime.Subject = message.Subject;
        mime.Body = BuildBody(message, charset);

        using var client = new SmtpClient
        {
            Timeout = checked(settings.TimeoutSeconds * 1000),
        };
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

        // Once SendAsync succeeds the SMTP server has accepted responsibility for the message.
        // A broken QUIT response must not turn that success into an outbox retry and a duplicate.
        try
        {
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "SMTP accepted email to {Recipient}, but disconnect failed.", message.To);
        }

        logger.LogInformation("Sent email to {Recipient} (subject: {Subject})", message.To, message.Subject);
    }

    private static MimeEntity BuildBody(EmailMessage message, string charset)
    {
        var html = new TextPart(TextFormat.Html);
        html.SetText(charset, message.HtmlBody);

        if (string.IsNullOrEmpty(message.TextBody))
        {
            return html;
        }

        var text = new TextPart(TextFormat.Plain);
        text.SetText(charset, message.TextBody);
        return new MultipartAlternative { text, html };
    }

    private static SecureSocketOptions ToSocketOptions(SmtpSecurity security) => security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.Auto,
    };
}
