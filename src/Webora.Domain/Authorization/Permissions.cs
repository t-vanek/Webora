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

    private static readonly Lazy<IReadOnlyList<string>> AllPermissions = new(() =>
        typeof(Permissions)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(group => group.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray());

    /// <summary>Every permission declared in the catalog.</summary>
    public static IReadOnlyList<string> All => AllPermissions.Value;
}
