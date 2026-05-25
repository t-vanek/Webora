using Microsoft.AspNetCore.Identity;
using Webora.Application.Abstractions.Email;
using Webora.Infrastructure.Identity;

namespace Webora.Web.Email;

/// <summary>
/// Bridges ASP.NET Core Identity's email notifications (confirmation, password reset) onto the
/// application's <see cref="IEmailSender"/> abstraction, so self-service flows send email out of
/// the box. Lives in the web host because <see cref="IEmailSender{TUser}"/> ships in the ASP.NET
/// Core shared framework.
/// </summary>
public sealed class IdentityEmailSender(IEmailSender emailSender) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        emailSender.SendAsync(new EmailMessage
        {
            To = email,
            ToName = user.DisplayName,
            Subject = "Potvrďte svůj e-mail",
            HtmlBody = Layout(
                "Vítejte ve Webora",
                "<p>Potvrďte prosím svou e-mailovou adresu kliknutím na následující odkaz:</p>" +
                $"<p><a href=\"{confirmationLink}\">Potvrdit e-mail</a></p>"),
        });

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        emailSender.SendAsync(new EmailMessage
        {
            To = email,
            ToName = user.DisplayName,
            Subject = "Obnovení hesla",
            HtmlBody = Layout(
                "Obnovení hesla",
                "<p>Pro nastavení nového hesla klikněte na následující odkaz:</p>" +
                $"<p><a href=\"{resetLink}\">Obnovit heslo</a></p>" +
                "<p>Pokud jste o obnovení hesla nežádali, tento e-mail ignorujte.</p>"),
        });

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        emailSender.SendAsync(new EmailMessage
        {
            To = email,
            ToName = user.DisplayName,
            Subject = "Kód pro obnovení hesla",
            HtmlBody = Layout(
                "Obnovení hesla",
                $"<p>Váš kód pro obnovení hesla je:</p><p style=\"font-size:1.25rem;\"><strong>{resetCode}</strong></p>"),
        });

    private static string Layout(string heading, string content) =>
        $"<div style=\"font-family:sans-serif;max-width:480px;margin:auto;\">" +
        $"<h1 style=\"font-size:1.25rem;\">{heading}</h1>{content}" +
        "<hr/><p style=\"color:#888;font-size:.8rem;\">Webora</p></div>";
}
