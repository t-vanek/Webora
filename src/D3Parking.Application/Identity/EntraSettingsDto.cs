namespace D3Parking.Application.Identity;

/// <summary>The administrator-editable, non-secret half of the Entra ID configuration.</summary>
public sealed record EntraSettingsDto(
    bool Enabled,
    string? TenantId,
    string? ClientId,
    string? Authority,
    string CallbackPath,
    string SignedOutCallbackPath,
    string DisplayName,
    bool LinkByVerifiedEmail,
    bool AllowJustInTimeProvisioning,
    IReadOnlyList<string> DefaultRoles,
    bool ScimEnabled,
    bool ScimBlockOnDeprovision);

/// <summary>
/// What the settings page needs: the effective values, whether each secret exists, and which fields
/// the host's configuration has taken out of the administrator's hands.
/// </summary>
/// <remarks>
/// Secrets are reported as present or absent and never returned. A page that could render a client
/// secret is a page that leaks it to anyone who can open the browser's developer tools, and an
/// administrator does not need to read a secret back to replace it.
/// </remarks>
public sealed record EntraSettingsView(
    EntraSettingsDto Settings,
    bool ClientSecretSet,
    bool ScimBearerTokenSet,
    IReadOnlySet<string> LockedFields)
{
    public bool IsLocked(string field) => LockedFields.Contains(field);

    /// <summary>True when configuration governs every field, so the page is a read-only view.</summary>
    public bool IsFullyLocked => EntraFields.All.All(LockedFields.Contains);
}

/// <summary>
/// Field names shared by the merge and the settings page, so a locked field and its disabled input
/// can never drift apart.
/// </summary>
public static class EntraFields
{
    public const string Enabled = "Enabled";
    public const string TenantId = "TenantId";
    public const string ClientId = "ClientId";
    public const string ClientSecret = "ClientSecret";
    public const string Authority = "Authority";
    public const string CallbackPath = "CallbackPath";
    public const string SignedOutCallbackPath = "SignedOutCallbackPath";
    public const string DisplayName = "DisplayName";
    public const string LinkByVerifiedEmail = "LinkByVerifiedEmail";
    public const string AllowJustInTimeProvisioning = "AllowJustInTimeProvisioning";
    public const string DefaultRoles = "DefaultRoles";
    public const string ScimEnabled = "Scim:Enabled";
    public const string ScimBearerToken = "Scim:BearerToken";
    public const string ScimBlockOnDeprovision = "Scim:BlockOnDeprovision";

    public static readonly IReadOnlyList<string> All =
    [
        Enabled, TenantId, ClientId, ClientSecret, Authority, CallbackPath, SignedOutCallbackPath,
        DisplayName, LinkByVerifiedEmail, AllowJustInTimeProvisioning, DefaultRoles,
        ScimEnabled, ScimBearerToken, ScimBlockOnDeprovision,
    ];
}

/// <summary>What a save should do with a secret the page never showed.</summary>
public enum EntraSecretAction
{
    /// <summary>Leave whatever is stored alone — the administrator typed nothing into the field.</summary>
    Keep,

    /// <summary>Replace it with <see cref="EntraSecretUpdate.Value"/>.</summary>
    Set,

    /// <summary>Remove it, which also switches off whatever it was the credential for.</summary>
    Clear,
}

public readonly record struct EntraSecretUpdate(EntraSecretAction Action, string? Value)
{
    public static EntraSecretUpdate Keep { get; } = new(EntraSecretAction.Keep, null);

    public static EntraSecretUpdate Clear { get; } = new(EntraSecretAction.Clear, null);

    public static EntraSecretUpdate Set(string value) => new(EntraSecretAction.Set, value);

    /// <summary>Maps a form field to an intent: blank means "keep", anything typed means "replace".</summary>
    public static EntraSecretUpdate FromInput(string? typed) =>
        string.IsNullOrWhiteSpace(typed) ? Keep : Set(typed.Trim());
}

public sealed record EntraSettingsUpdate(
    EntraSettingsDto Settings,
    EntraSecretUpdate ClientSecret,
    EntraSecretUpdate ScimBearerToken);
