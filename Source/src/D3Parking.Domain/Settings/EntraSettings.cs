using D3Parking.Domain.Common;

namespace D3Parking.Domain.Settings;

/// <summary>
/// The administrator-editable half of the Microsoft Entra ID integration. A single instance
/// identified by <see cref="SingletonId"/>.
/// </summary>
/// <remarks>
/// Held apart from <see cref="SiteSettings"/> because it answers to a different question — how this
/// installation federates, not how it addresses itself — and because two of its fields are secrets
/// that must never travel with the rest of the settings DTO.
///
/// Anything the host's configuration sets still wins over what is stored here; the merge happens in
/// the application layer, so this entity is only ever the fallback half of the effective settings.
/// </remarks>
public class EntraSettings : Entity, IAggregateRoot
{
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-0000000000c1");

    /// <summary>Turns on the "sign in with Microsoft" button and the OpenID Connect handler.</summary>
    public bool Enabled { get; private set; }

    /// <summary>The directory (tenant) id. Also the tenant accepted in incoming tokens.</summary>
    public string? TenantId { get; private set; }

    public string? ClientId { get; private set; }

    /// <summary>
    /// The client secret as ciphertext. Encrypted by the infrastructure layer before it ever reaches
    /// this entity, so a database backup, a query tool or a log statement never yields the secret.
    /// </summary>
    public string? ClientSecretProtected { get; private set; }

    /// <summary>Overrides the authority; null falls back to the v2.0 endpoint for <see cref="TenantId"/>.</summary>
    public string? Authority { get; private set; }

    /// <summary>The redirect path registered in Entra. Must match the app registration exactly.</summary>
    public string CallbackPath { get; private set; } = "/signin-entra";

    /// <summary>The sign-out callback path registered in Entra.</summary>
    public string SignedOutCallbackPath { get; private set; } = "/signout-entra";

    /// <summary>Label on the sign-in button, so a tenant can call it what its people call it.</summary>
    public string DisplayName { get; private set; } = "Microsoft";

    /// <summary>Links a first-time Entra sign-in to an existing local account with the same confirmed email.</summary>
    public bool LinkByVerifiedEmail { get; private set; } = true;

    /// <summary>Creates an account on first sign-in for someone the directory has not provisioned.</summary>
    public bool AllowJustInTimeProvisioning { get; private set; } = true;

    /// <summary>Roles a just-in-time account gets when the directory sends no app role at all.</summary>
    public IReadOnlyList<string> DefaultRoles { get; private set; } = [];

    /// <summary>Accepts inbound provisioning from an Entra Enterprise Application.</summary>
    public bool ScimEnabled { get; private set; }

    /// <summary>The SCIM bearer token as ciphertext. See <see cref="ClientSecretProtected"/>.</summary>
    public string? ScimBearerTokenProtected { get; private set; }

    /// <summary>Blocks (rather than ignores) an account the directory deactivates or deletes.</summary>
    public bool ScimBlockOnDeprovision { get; private set; } = true;

    private EntraSettings() { }

    public static EntraSettings CreateDefault()
    {
        var settings = new EntraSettings();
        settings.Id = SingletonId;
        return settings;
    }

    public void UpdateSignIn(
        bool enabled,
        string? tenantId,
        string? clientId,
        string? authority,
        string? callbackPath,
        string? signedOutCallbackPath,
        string? displayName,
        bool linkByVerifiedEmail,
        bool allowJustInTimeProvisioning,
        IReadOnlyList<string> defaultRoles)
    {
        Enabled = enabled;
        TenantId = Trimmed(tenantId);
        ClientId = Trimmed(clientId);
        Authority = Trimmed(authority)?.TrimEnd('/');
        CallbackPath = NormalizePath(callbackPath) ?? "/signin-entra";
        SignedOutCallbackPath = NormalizePath(signedOutCallbackPath) ?? "/signout-entra";
        DisplayName = Trimmed(displayName) ?? "Microsoft";
        LinkByVerifiedEmail = linkByVerifiedEmail;
        AllowJustInTimeProvisioning = allowJustInTimeProvisioning;
        DefaultRoles = defaultRoles
            .Select(Trimmed)
            .Where(role => role is not null)
            .Select(role => role!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void UpdateScim(bool enabled, bool blockOnDeprovision)
    {
        ScimEnabled = enabled;
        ScimBlockOnDeprovision = blockOnDeprovision;
    }

    /// <summary>Replaces the stored client secret; null clears it.</summary>
    public void SetClientSecret(string? protectedValue) => ClientSecretProtected = Trimmed(protectedValue);

    /// <summary>Replaces the stored SCIM bearer token; null clears it.</summary>
    public void SetScimBearerToken(string? protectedValue) => ScimBearerTokenProtected = Trimmed(protectedValue);

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// A callback path only works if it matches the app registration byte for byte, and the OpenID
    /// Connect handler insists on a rooted path. Repairing a missing slash here beats a redirect
    /// loop whose cause is invisible from the browser.
    /// </summary>
    private static string? NormalizePath(string? value)
    {
        var path = Trimmed(value);
        if (path is null)
        {
            return null;
        }

        return path.StartsWith('/') ? path.TrimEnd('/') is { Length: > 0 } trimmed ? trimmed : null : $"/{path.TrimEnd('/')}";
    }
}
