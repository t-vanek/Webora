using D3Parking.Application.Accounts;

namespace D3Parking.Application.Identity;

/// <summary>
/// Turns an identity asserted by an external directory into an account here.
/// </summary>
/// <remarks>
/// Deliberately one service for both ways in. A sign-in and a SCIM push carry the same facts about
/// the same person, and if each built its own account the two paths would drift — different linking
/// rules, different role handling, two places to get the last-administrator guard wrong.
/// </remarks>
public interface IExternalDirectoryService
{
    /// <summary>Creates, links or updates the local account for a directory identity.</summary>
    Task<ExternalSyncResult> SyncAsync(ExternalIdentity identity, CancellationToken cancellationToken = default);

    /// <summary>Finds the local account for a directory object, or null if there is none yet.</summary>
    Task<Guid?> FindAsync(string provider, string externalObjectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the directory's active flag: deactivating blocks the account and ends live sessions.
    /// The account is never deleted — history outlives employment, and a return is common.
    /// </summary>
    Task<AccountResult> SetActiveAsync(
        string provider,
        string externalObjectId,
        bool active,
        CancellationToken cancellationToken = default);
}

/// <summary>What the directory asserts about a person.</summary>
/// <param name="ObjectId">The immutable object id (Entra's <c>oid</c>). The only stable handle.</param>
/// <param name="Roles">
/// The app roles the directory grants, or null when the caller has nothing to say about roles —
/// SCIM pushes accounts without them, and null must not be read as "revoke everything".
/// </param>
public sealed record ExternalIdentity(
    string Provider,
    string ObjectId,
    string Email,
    string? TenantId = null,
    string? DisplayName = null,
    string? Department = null,
    IReadOnlyList<string>? Roles = null,
    bool Active = true);

/// <param name="Created">True when the account did not exist before this call.</param>
/// <param name="Linked">True when an existing local account was adopted rather than created.</param>
public sealed record ExternalSyncResult(
    bool Succeeded,
    Guid UserId,
    bool Created,
    bool Linked,
    IReadOnlyList<string> Errors)
{
    public static ExternalSyncResult Failure(params string[] errors) =>
        new(false, Guid.Empty, false, false, errors);

    public static ExternalSyncResult Success(Guid userId, bool created, bool linked) =>
        new(true, userId, created, linked, []);
}
