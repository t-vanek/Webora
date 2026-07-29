namespace D3Parking.Web.Identity;

/// <summary>
/// PFX certificates for OpenIddict token signing and encryption. When a path is not configured,
/// the development certificates are used — fine locally, but in production they regenerate with
/// the machine/container, invalidating every issued token on redeploy (Program.cs logs a warning).
/// </summary>
public sealed class IdentityServerCertificateOptions
{
    public const string SectionName = "IdentityServer";

    public string? SigningCertificatePath { get; set; }

    public string? SigningCertificatePassword { get; set; }

    public string? EncryptionCertificatePath { get; set; }

    public string? EncryptionCertificatePassword { get; set; }

    public bool HasSigning => !string.IsNullOrWhiteSpace(SigningCertificatePath);

    public bool HasEncryption => !string.IsNullOrWhiteSpace(EncryptionCertificatePath);
}
