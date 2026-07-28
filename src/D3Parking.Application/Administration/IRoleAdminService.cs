using D3Parking.Application.Accounts;
using D3Parking.Domain.Authorization;

namespace D3Parking.Application.Administration;

/// <summary>
/// Administrative management of roles and the permissions granted to them. Built-in roles
/// (<see cref="Roles.Defaults"/>) cannot be renamed or deleted, and the Administrator role always
/// holds every permission.
/// </summary>
public interface IRoleAdminService
{
    /// <summary>The permission catalog grouped by area, for rendering the permission editor.</summary>
    IReadOnlyList<PermissionGroup> PermissionCatalog { get; }

    Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<RoleDetail?> GetAsync(Guid roleId, CancellationToken cancellationToken = default);

    Task<AccountResult> CreateAsync(string name, IReadOnlyList<string> permissions, CancellationToken cancellationToken = default);

    Task<AccountResult> RenameAsync(Guid roleId, string name, CancellationToken cancellationToken = default);

    Task<AccountResult> SetPermissionsAsync(Guid roleId, IReadOnlyList<string> permissions, CancellationToken cancellationToken = default);

    Task<AccountResult> DeleteAsync(Guid roleId, CancellationToken cancellationToken = default);
}
