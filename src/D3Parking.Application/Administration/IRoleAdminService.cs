using D3Parking.Application.Accounts;
using D3Parking.Domain.Authorization;

namespace D3Parking.Application.Administration;

/// <summary>
/// Administrative management of roles. A role is composed of permission groups and nothing else;
/// its permissions are the union of those groups and are derived, never edited directly. Built-in
/// roles (<see cref="Roles.Defaults"/>) are system-managed: they cannot be renamed, deleted, or
/// recomposed, because the seeder re-asserts them on every start.
/// </summary>
/// <remarks>
/// Every mutating operation carries the acting administrator's id. It is not only for the audit
/// trail: a group can only be composed into a role by someone who already holds everything that
/// group grants, so <see cref="Permissions.Roles.Edit"/> cannot be turned into more than its holder
/// already has.
/// </remarks>
public interface IRoleAdminService
{
    Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<RoleDetail?> GetAsync(Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the accounts holding the role, ordered by email and narrowed by an optional
    /// search over email and display name.
    /// </summary>
    /// <remarks>
    /// Paged in SQL rather than filtered in the page: a role can be held by the whole company, and
    /// "who exactly holds this" is asked precisely when the answer is too long to read at once.
    /// </remarks>
    Task<PagedResult<RoleMember>> ListMembersPageAsync(
        Guid roleId,
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<AccountResult> CreateAsync(string name, IReadOnlyList<Guid> groupIds, Guid actorId, CancellationToken cancellationToken = default);

    Task<AccountResult> RenameAsync(Guid roleId, string name, CancellationToken cancellationToken = default);

    /// <summary>Replaces the groups the role is composed of and re-derives its permissions.</summary>
    Task<AccountResult> SetGroupsAsync(Guid roleId, IReadOnlyList<Guid> groupIds, Guid actorId, CancellationToken cancellationToken = default);

    Task<AccountResult> DeleteAsync(Guid roleId, Guid actorId, CancellationToken cancellationToken = default);
}
