namespace Webora.Infrastructure.Email;

/// <summary>Generic OAuth2 client-credentials settings used to obtain an SMTP access token.</summary>
public sealed class SmtpOAuth2Options
{
    public const string SectionName = "Smtp:OAuth2";

    public string TokenEndpoint { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string? Scope { get; set; }
}
