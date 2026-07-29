using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;
using D3Parking.Application.Abstractions.Email;
using D3Parking.Infrastructure.Identity;

namespace D3Parking.Web.Email;

/// <summary>
/// Bridges ASP.NET Core Identity's email notifications (confirmation, password reset) onto the
/// application's <see cref="IEmailSender"/> abstraction, so self-service flows send email out of
/// the box. Lives in the web host because <see cref="IEmailSender{TUser}"/> ships in the ASP.NET
/// Core shared framework. Texts are localized with the request culture — the mails are triggered
/// from the user's own register/reset request, so CurrentUICulture is their negotiated language.
/// </summary>
public sealed class IdentityEmailSender(IEmailSender emailSender, IStringLocalizer<SharedResource> localizer) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        emailSender.SendAsync(new EmailMessage
        {
            To = email,
            ToName = user.DisplayName,
            Subject = localizer["Email_Confirm_Subject"],
            HtmlBody = Layout(
                localizer["Email_Confirm_Heading"],
                $"<p>{localizer["Email_Confirm_Intro"]}</p>" +
                $"<p><a href=\"{confirmationLink}\">{localizer["Email_Confirm_Action"]}</a></p>"),
        });

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        emailSender.SendAsync(new EmailMessage
        {
            To = email,
            ToName = user.DisplayName,
            Subject = localizer["Email_Reset_Title"],
            HtmlBody = Layout(
                localizer["Email_Reset_Title"],
                $"<p>{localizer["Email_Reset_Intro"]}</p>" +
                $"<p><a href=\"{resetLink}\">{localizer["Email_Reset_Action"]}</a></p>" +
                $"<p>{localizer["Email_Reset_Ignore"]}</p>"),
        });

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        emailSender.SendAsync(new EmailMessage
        {
            To = email,
            ToName = user.DisplayName,
            Subject = localizer["Email_ResetCode_Subject"],
            HtmlBody = Layout(
                localizer["Email_Reset_Title"],
                $"<p>{localizer["Email_ResetCode_Intro"]}</p><p style=\"font-size:1.25rem;\"><strong>{resetCode}</strong></p>"),
        });

    private static string Layout(string heading, string content) =>
        $"<div style=\"font-family:sans-serif;max-width:480px;margin:auto;\">" +
        $"<h1 style=\"font-size:1.25rem;\">{heading}</h1>{content}" +
        "<hr/><p style=\"color:#888;font-size:.8rem;\">D3Parking</p></div>";
}
