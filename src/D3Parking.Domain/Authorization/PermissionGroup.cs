using D3Parking.Domain.Common;

namespace D3Parking.Domain.Authorization;

/// <summary>
/// A named bundle of permissions — the unit a role is built from.
/// </summary>
/// <remarks>
/// Roles hold groups and nothing else, so every permission a user ends up with is traceable to the
/// group that carried it: the answer to "why can this person see the mismatch photos" is a group
/// name, not a checkbox somebody ticked a year ago. It also splits the two administrative jobs —
/// deciding what a bundle contains (<see cref="Permissions.PermissionGroups.Edit"/>) is separate
/// from composing roles out of bundles (<see cref="Permissions.Roles.Edit"/>), so whoever assembles
/// roles cannot invent new power, only combine what already exists.
/// </remarks>
public class PermissionGroup : Entity, IAggregateRoot
{
    private readonly List<PermissionGroupEntry> _entries = [];

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>Built-in groups are maintained by the seeder and cannot be edited or deleted.</summary>
    public bool IsBuiltIn { get; private set; }

    public IReadOnlyCollection<PermissionGroupEntry> Entries => _entries;

    /// <summary>The permissions this group grants, in catalog order.</summary>
    public IReadOnlyList<string> Permissions =>
        Authorization.Permissions.All.Where(p => _entries.Any(e => e.Permission == p)).ToArray();

    private PermissionGroup() { }

    public PermissionGroup(string name, string? description, IEnumerable<string> permissions, bool isBuiltIn = false)
    {
        Name = name.Trim();
        Description = Normalize(description);
        IsBuiltIn = isBuiltIn;
        SetPermissions(permissions);
    }

    public void Rename(string name, string? description)
    {
        Name = name.Trim();
        Description = Normalize(description);
    }

    /// <summary>
    /// Replaces the group's permissions. The set is closed over the catalog's dependencies, so a
    /// group can never carry a grant without the one it is useless without.
    /// </summary>
    public void SetPermissions(IEnumerable<string> permissions)
    {
        var target = Authorization.Permissions.Expand(permissions);

        _entries.RemoveAll(e => !target.Contains(e.Permission, StringComparer.Ordinal));
        foreach (var permission in target.Where(p => _entries.All(e => e.Permission != p)))
        {
            _entries.Add(new PermissionGroupEntry(Id, permission));
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One permission carried by a <see cref="PermissionGroup"/>.</summary>
public class PermissionGroupEntry
{
    public Guid PermissionGroupId { get; private set; }

    public string Permission { get; private set; } = string.Empty;

    private PermissionGroupEntry() { }

    public PermissionGroupEntry(Guid permissionGroupId, string permission)
    {
        PermissionGroupId = permissionGroupId;
        Permission = permission;
    }
}

/// <summary>Membership of a <see cref="PermissionGroup"/> in a role.</summary>
public class RolePermissionGroup
{
    public Guid RoleId { get; private set; }

    public Guid PermissionGroupId { get; private set; }

    private RolePermissionGroup() { }

    public RolePermissionGroup(Guid roleId, Guid permissionGroupId)
    {
        RoleId = roleId;
        PermissionGroupId = permissionGroupId;
    }
}
