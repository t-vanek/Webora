using System.Reflection;

namespace Webora.Domain.Authorization;

/// <summary>
/// The catalog of fine-grained permissions. Each constant is the canonical permission name
/// stored as a role claim and checked by the authorization layer.
/// </summary>
public static class Permissions
{
    public static class Users
    {
        public const string View = "Users.View";
        public const string Create = "Users.Create";
        public const string Edit = "Users.Edit";
        public const string Delete = "Users.Delete";
    }

    public static class Roles
    {
        public const string View = "Roles.View";
        public const string Create = "Roles.Create";
        public const string Edit = "Roles.Edit";
        public const string Delete = "Roles.Delete";
    }

    public static class Pages
    {
        public const string View = "Pages.View";
        public const string Create = "Pages.Create";
        public const string Edit = "Pages.Edit";
        public const string Delete = "Pages.Delete";
        public const string Publish = "Pages.Publish";
    }

    public static class Media
    {
        public const string View = "Media.View";
        public const string Upload = "Media.Upload";
        public const string Delete = "Media.Delete";
    }

    public static class Settings
    {
        public const string View = "Settings.View";
        public const string Edit = "Settings.Edit";
    }

    private static readonly Lazy<IReadOnlyList<PermissionGroup>> GroupedPermissions = new(() =>
        typeof(Permissions)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.Static)
            .Select(group => new PermissionGroup(
                group.Name,
                group.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                    .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
                    .Select(field => (string)field.GetRawConstantValue()!)
                    .ToArray()))
            .ToArray());

    private static readonly Lazy<IReadOnlyList<string>> AllPermissions = new(() =>
        GroupedPermissions.Value.SelectMany(group => group.Permissions).ToArray());

    private static readonly Lazy<HashSet<string>> KnownPermissions = new(() =>
        AllPermissions.Value.ToHashSet(StringComparer.Ordinal));

    /// <summary>The permission catalog grouped by area (Users, Roles, Pages, …) for editor UIs.</summary>
    public static IReadOnlyList<PermissionGroup> Groups => GroupedPermissions.Value;

    /// <summary>Every permission declared in the catalog.</summary>
    public static IReadOnlyList<string> All => AllPermissions.Value;

    /// <summary>True when <paramref name="permission"/> is a known permission from the catalog.</summary>
    public static bool IsKnown(string permission) => KnownPermissions.Value.Contains(permission);
}

/// <summary>A named group of related permissions (e.g. all "Users.*" permissions).</summary>
public sealed record PermissionGroup(string Name, IReadOnlyList<string> Permissions);
