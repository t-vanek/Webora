using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;
using D3Parking.Application.Abstractions.Email;
using D3Parking.Infrastructure.Email;
using D3Parking.Infrastructure.Identity;

namespace D3Parking.Web.Email;

/// <summary>
/// Bridges ASP.NET Core Identity's email notifications (confirmation, password reset) onto the
/// application's <see cref="IEmailSender"/> abstraction, rendered through the shared branded
/// layout. Texts are localized with the request culture — the mails are triggered from the user's
/// own register/reset request, so CurrentUICulture is their negotiated language.
/// </summary>
public sealed class IdentityEmailSender(IEmailSender emailSender, IStringLocalizer<SharedResource> localizer) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        SendAsync(user, email,
            subject: localizer["Email_Confirm_Subject"],
            heading: localizer["Email_Confirm_Heading"],
            bodyText: localizer["Email_Confirm_Intro"],
            actionText: localizer["Email_Confirm_Action"],
            actionUrl: confirmationLink);

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        SendAsync(user, email,
            subject: localizer["Email_Reset_Title"],
            heading: localizer["Email_Reset_Title"],
            bodyText: localizer["Email_Reset_Intro"],
            actionText: localizer["Email_Reset_Action"],
            actionUrl: resetLink);

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        emailSender.SendAsync(new EmailMessage
        {
            To = email,
            ToName = user.DisplayName,
            Subject = localizer["Email_ResetCode_Subject"],
            HtmlBody = BrandedEmail.RenderHtml(new(
                Heading: localizer["Email_Reset_Title"],
                BodyHtml: $"{WebUtility.HtmlEncode(localizer["Email_ResetCode_Intro"].Value)}<br/><span style=\"font-size:1.4rem;\"><strong>{WebUtility.HtmlEncode(resetCode)}</strong></span>",
                FooterReason: localizer["Email_Chrome_Reason"],
                FooterSettings: localizer["Email_Chrome_IfNotYou"])),
        });

    private Task SendAsync(ApplicationUser user, string email, string subject, string heading, string bodyText, string actionText, string actionUrl) =>
        emailSender.SendAsync(new EmailMessage
        {
            To = email,
            ToName = user.DisplayName,
            Subject = subject,
            HtmlBody = BrandedEmail.RenderHtml(new(
                Heading: heading,
                BodyHtml: WebUtility.HtmlEncode(bodyText),
                FooterReason: localizer["Email_Chrome_Reason"],
                FooterSettings: localizer["Email_Chrome_IfNotYou"],
                ActionText: actionText,
                ActionUrl: actionUrl)),
            TextBody = BrandedEmail.RenderText(heading, bodyText, actionUrl, null, localizer["Email_Chrome_Reason"]),
        });
}
