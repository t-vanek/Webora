using System.Net;

namespace D3Parking.Infrastructure.Email;

/// <summary>
/// The one email layout every outgoing mail uses: brand header, white card with heading and body,
/// optional CTA button and deadline line, and a footer explaining why the mail arrived. Inline
/// CSS only (email clients), system font stack (Sora is not reliably available in mail clients),
/// brand colors from the manual: Sherpa Blue #023444, violet #884DFF.
/// </summary>
public static class BrandedEmail
{
    /// <param name="BodyHtml">Already-safe HTML (encode any user-provided text before passing).</param>
    public sealed record Content(
        string Heading,
        string BodyHtml,
        string FooterReason,
        string FooterSettings,
        string? ActionText = null,
        string? ActionUrl = null,
        string? DeadlineText = null);

    public static string RenderHtml(Content content)
    {
        var action = content is { ActionText: not null, ActionUrl: not null }
            ? $"""
               <a href="{WebUtility.HtmlEncode(content.ActionUrl)}" style="display:inline-block;background:#884DFF;color:#ffffff;padding:12px 22px;border-radius:8px;text-decoration:none;font-weight:600;font-size:15px;">{WebUtility.HtmlEncode(content.ActionText)}</a>
               """
            : string.Empty;

        var deadline = content.DeadlineText is not null
            ? $"""<p style="margin:16px 0 0;font-size:13px;color:#a33;">&#9200; {WebUtility.HtmlEncode(content.DeadlineText)}</p>"""
            : string.Empty;

        return $"""
            <body style="margin:0;background:#f4f5f7;font-family:'Segoe UI',Roboto,Arial,sans-serif;">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0">
                <tr><td align="center" style="padding:24px 12px;">
                  <table role="presentation" width="560" cellpadding="0" cellspacing="0" style="max-width:560px;width:100%;">
                    <tr><td style="background:#023444;border-radius:12px 12px 0 0;padding:20px 28px;">
                      <span style="color:#ffffff;font-size:18px;font-weight:700;letter-spacing:.06em;">D3PARKING</span>
                    </td></tr>
                    <tr><td style="background:#ffffff;padding:28px;border:1px solid #e3e6ea;border-top:0;">
                      <h1 style="margin:0 0 12px;font-size:20px;color:#0b1f28;">{WebUtility.HtmlEncode(content.Heading)}</h1>
                      <p style="margin:0 0 16px;font-size:15px;line-height:1.55;color:#333333;">{content.BodyHtml}</p>
                      {action}
                      {deadline}
                    </td></tr>
                    <tr><td style="background:#ffffff;border:1px solid #e3e6ea;border-top:0;border-radius:0 0 12px 12px;padding:16px 28px;font-size:12px;color:#888888;">
                      {WebUtility.HtmlEncode(content.FooterReason)} {WebUtility.HtmlEncode(content.FooterSettings)}
                    </td></tr>
                  </table>
                </td></tr>
              </table>
            </body>
            """;
    }

    /// <summary>Plain-text alternative for clients that prefer it.</summary>
    public static string RenderText(string heading, string bodyText, string? actionUrl, string? deadlineText, string footerReason)
    {
        var lines = new List<string> { heading, string.Empty, bodyText };
        if (actionUrl is not null)
        {
            lines.Add(string.Empty);
            lines.Add(actionUrl);
        }

        if (deadlineText is not null)
        {
            lines.Add(deadlineText);
        }

        lines.Add(string.Empty);
        lines.Add("--");
        lines.Add(footerReason);
        return string.Join(Environment.NewLine, lines);
    }
}
