using D3Parking.Application.Accounts;

namespace D3Parking.Application.Administration;

/// <summary>
/// Administers the translation from a directory's roles to this application's roles.
/// </summary>
/// <remarks>
/// Bounded by the same rule as every other grant: nobody may point a directory role at a local role
/// that reaches past their own permissions. Otherwise this screen would be the easiest escalation
/// in the app — map an app role you already hold in Entra onto Administrator and sign in again.
/// </remarks>
public interface IExternalRoleMappingService
{
    Task<IReadOnlyList<ExternalRoleMappingSummary>> ListAsync(string provider, CancellationToken cancellationToken = default);

    Task<AccountResult> CreateAsync(string provider, string externalRole, Guid roleId, Guid actorId, CancellationToken cancellationToken = default);

    Task<AccountResult> RetargetAsync(Guid mappingId, Guid roleId, Guid actorId, CancellationToken cancellationToken = default);

    Task<AccountResult> DeleteAsync(Guid mappingId, Guid actorId, CancellationToken cancellationToken = default);
}

/// <param name="AssignedUserCount">How many accounts currently hold the role because of this mapping.</param>
public sealed record ExternalRoleMappingSummary(
    Guid Id,
    string Provider,
    string ExternalRole,
    Guid RoleId,
    string RoleName,
    int AssignedUserCount);
