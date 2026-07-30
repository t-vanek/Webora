using D3Parking.Application.Accounts;
using D3Parking.Domain.Authorization;

namespace D3Parking.Application.Administration;

/// <summary>
/// Administration of the permission groups roles are built from.
/// </summary>
/// <remarks>
/// Held apart from <see cref="IRoleAdminService"/> because the two jobs carry different risk:
/// composing a role can only combine bundles that already exist, while editing a bundle changes
/// what everyone holding it can do. Every mutation is bounded by the actor's own permissions, so
/// group administration cannot be used to mint privilege either.
/// </remarks>
public interface IPermissionGroupAdminService
{
    /// <summary>The permission catalog by area, for rendering the permission editor.</summary>
    IReadOnlyList<PermissionArea> PermissionCatalog { get; }

    Task<IReadOnlyList<PermissionGroupSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<PermissionGroupDetail?> GetAsync(Guid groupId, CancellationToken cancellationToken = default);

    Task<AccountResult> CreateAsync(
        string name,
        string? description,
        IReadOnlyList<string> permissions,
        Guid actorId,
        CancellationToken cancellationToken = default);

    Task<AccountResult> UpdateAsync(
        Guid groupId,
        string name,
        string? description,
        IReadOnlyList<string> permissions,
        Guid actorId,
        CancellationToken cancellationToken = default);

    Task<AccountResult> DeleteAsync(Guid groupId, Guid actorId, CancellationToken cancellationToken = default);
}

/// <summary>A permission group as the administration list shows it.</summary>
public sealed record PermissionGroupSummary(
    Guid Id,
    string Name,
    string? Description,
    bool IsBuiltIn,
    IReadOnlyList<string> Permissions,
    int RoleCount);

/// <summary>Full detail of a group, including which roles are composed of it.</summary>
public sealed record PermissionGroupDetail(
    Guid Id,
    string Name,
    string? Description,
    bool IsBuiltIn,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<GroupUsage> Roles);

/// <summary>A role built from the group, shown so the blast radius of an edit is visible.</summary>
public sealed record GroupUsage(Guid RoleId, string RoleName, int UserCount);
