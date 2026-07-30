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

/// <summary>Full detail of a role: its composition, what it grants, and who holds it.</summary>
public sealed record RoleDetail(
    Guid Id,
    string Name,
    bool IsDefault,
    IReadOnlyList<RoleGroupRef> Groups,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<RoleMember> Members);

/// <summary>A permission group a role is composed of.</summary>
public sealed record RoleGroupRef(Guid Id, string Name, bool IsBuiltIn, IReadOnlyList<string> Permissions);

/// <summary>An account holding a role, shown on the role's members list.</summary>
public sealed record RoleMember(Guid Id, string Email, string? DisplayName);
