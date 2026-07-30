using D3Parking.Application.Accounts;

namespace D3Parking.Application.Identity;

/// <summary>
/// The single source of the Entra ID configuration this installation is actually running on.
/// </summary>
/// <remarks>
/// Two layers are merged field by field: whatever the host's configuration sets wins, and the
/// administrator-editable row in the database fills in the rest. That order is what lets a
/// deployment pin its tenant in an environment variable or a vault while everything it did not pin
/// stays adjustable from the settings page — and it is why the page reports which fields it may not
/// touch instead of accepting an edit the merge would silently discard.
/// </remarks>
public interface IEntraSettingsService
{
    /// <summary>The merged settings the sign-in, provisioning and directory paths run on.</summary>
    Task<EntraIdOptions> GetEffectiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The merged settings, resolved synchronously.
    /// </summary>
    /// <remarks>
    /// Only for the OpenID Connect options callback, which the options infrastructure calls
    /// synchronously and which therefore has nowhere to await. Every other caller has an async
    /// path — use it.
    /// </remarks>
    EntraIdOptions GetEffective();

    /// <summary>The merged settings plus what the settings page needs to render itself honestly.</summary>
    Task<EntraSettingsView> GetForAdminAsync(CancellationToken cancellationToken = default);

    Task<AccountResult> UpdateAsync(EntraSettingsUpdate update, Guid actingUserId, CancellationToken cancellationToken = default);
}
