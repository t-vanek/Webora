namespace D3Parking.Application.Notifications;

/// <summary>
/// VAPID configuration for Web Push. Push delivery stays disabled (and the client UI hidden)
/// until both keys are provided; generate a pair once per deployment and keep the private key
/// out of source control for production.
/// </summary>
public sealed class WebPushOptions
{
    public const string SectionName = "WebPush";

    /// <summary>Operator contact passed to the push service, e.g. "mailto:admin@example.com".</summary>
    public string Subject { get; set; } = "mailto:admin@d3parking.local";

    /// <summary>VAPID public key (base64url, uncompressed P-256 point).</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>VAPID private key (base64url, P-256 scalar).</summary>
    public string PrivateKey { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(PrivateKey);
}
