namespace D3Parking.Application.Administration;

/// <summary>
/// A role as the administration list shows it. Both sides come along: the groups it is composed of
/// (what an administrator picked) and the permissions they add up to (what it actually grants).
/// </summary>
public sealed record RoleSummary(
    Guid Id,
    string Name,
    IReadOnlyList<RoleGroupRef> Groups,
    IReadOnlyList<string> Permissions,
    int UserCount,
    bool IsDefault)
{
    public int PermissionCount => Permissions.Count;
}

/// <summary>
/// Full detail of a role: its composition, what it grants, and how many accounts hold it.
/// </summary>
/// <remarks>
/// The members are a count here, not a list. The detail screen pages through them
/// (<see cref="IRoleAdminService.ListMembersPageAsync"/>), so carrying every holder along would
/// materialise a whole department to render ten rows — and the count is what the rest of the screen
/// actually asks about, down to whether the role may be deleted at all.
/// </remarks>
public sealed record RoleDetail(
    Guid Id,
    string Name,
    bool IsDefault,
    IReadOnlyList<RoleGroupRef> Groups,
    IReadOnlyList<string> Permissions,
    int MemberCount);

/// <summary>A permission group a role is composed of.</summary>
public sealed record RoleGroupRef(Guid Id, string Name, bool IsBuiltIn, IReadOnlyList<string> Permissions);

/// <summary>An account holding a role, shown on the role's members list.</summary>
public sealed record RoleMember(Guid Id, string Email, string? DisplayName);
