namespace D3Parking.Infrastructure.Email;

public enum SmtpAuthMode
{
    None,
    Basic,
    OAuth2,
}

public enum SmtpSecurity
{
    None,
    Auto,
    StartTls,
    SslOnConnect,
}

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 25;

    /// <summary>Upper bound for one SMTP network operation.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;

    public SmtpAuthMode Authentication { get; set; } = SmtpAuthMode.None;

    /// <summary>Login for Basic auth, or the account/mailbox for OAuth2 (XOAUTH2).</summary>
    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string SenderEmail { get; set; } = "no-reply@d3parking.local";

    public string SenderName { get; set; } = "D3Parking";
}
