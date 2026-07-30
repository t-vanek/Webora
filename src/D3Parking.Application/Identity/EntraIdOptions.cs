namespace D3Parking.Application.Identity;

/// <summary>
/// Configuration for the Microsoft Entra ID integration.
/// </summary>
/// <remarks>
/// Every part is opt-in and off by default: an installation with no <c>EntraId</c> section behaves
/// exactly as it did before, with local passwords and nothing else. Sign-in and provisioning are
/// separate switches because they are separate decisions — a tenant may push accounts in over SCIM
/// long before anyone is asked to sign in with them.
/// </remarks>
public sealed class EntraIdOptions
{
    public const string SectionName = "EntraId";

    /// <summary>Turns on the "sign in with Microsoft" button and the OpenID Connect handler.</summary>
    public bool Enabled { get; set; }

    /// <summary>The directory (tenant) id. Also the tenant accepted in incoming tokens.</summary>
    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    /// <summary>The app registration's client secret. Prefer a secret store or managed identity.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Overrides the authority; defaults to the v2.0 endpoint for <see cref="TenantId"/>.</summary>
    public string? Authority { get; set; }

    /// <summary>The redirect path registered in Entra. Must match the app registration exactly.</summary>
    public string CallbackPath { get; set; } = "/signin-entra";

    /// <summary>The sign-out callback path registered in Entra.</summary>
    public string SignedOutCallbackPath { get; set; } = "/signout-entra";

    /// <summary>Label on the sign-in button, so a tenant can call it what its people call it.</summary>
    public string DisplayName { get; set; } = "Microsoft";

    /// <summary>
    /// Links a first-time Entra sign-in to an existing local account with the same email.
    /// </summary>
    /// <remarks>
    /// Convenient and, with the guard below, safe: the local account must already have a confirmed
    /// email, so the address was proven to belong to whoever registered it. Turn it off to require
    /// every federated account to be provisioned or created fresh — the stricter posture, at the
    /// cost of duplicate accounts for people who already registered locally.
    /// </remarks>
    public bool LinkByVerifiedEmail { get; set; } = true;

    /// <summary>
    /// Creates an account on first sign-in for someone the directory has not provisioned. Turn off
    /// when SCIM is the only way in and an unknown sign-in should be refused.
    /// </summary>
    public bool AllowJustInTimeProvisioning { get; set; } = true;

    /// <summary>
    /// Roles a just-in-time account gets when the directory sends no app role at all. Empty means
    /// the account exists but can do nothing until someone grants it something.
    /// </summary>
    public IList<string> DefaultRoles { get; set; } = [];

    public ScimOptions Scim { get; set; } = new();

    public string ResolvedAuthority => Authority?.TrimEnd('/')
        ?? $"https://login.microsoftonline.com/{TenantId}/v2.0";

    /// <summary>True when sign-in is switched on and configured enough to work.</summary>
    public bool IsSignInConfigured =>
        Enabled && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(TenantId);
}

/// <summary>Inbound provisioning: what Entra's Enterprise Application pushes to this app.</summary>
public sealed class ScimOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// The bearer token Entra presents on every provisioning request. There is no other
    /// authentication on the SCIM endpoints, so this is the whole boundary — treat it as a secret,
    /// keep it long, and rotate it when someone with access to the tenant leaves.
    /// </summary>
    public string? BearerToken { get; set; }

    /// <summary>
    /// What happens to an account the directory deactivates or deletes: block it (reversible, keeps
    /// history) or leave it alone. Deletion is never performed from a provisioning request.
    /// </summary>
    public bool BlockOnDeprovision { get; set; } = true;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BearerToken);
}
