using Microsoft.Extensions.Localization;
using D3Parking.Domain.Authorization;

namespace D3Parking.Web.Authorization;

/// <summary>
/// Turns the authorization model's identifiers into text a person can act on.
/// </summary>
/// <remarks>
/// The catalog stores dotted, English, developer-facing names — "Parking.ReviewMismatches" tells
/// whoever is ticking the box nothing about what they are handing out. Every built-in name has a
/// localized title and a one-line description explaining what it unlocks; custom role names are
/// the administrator's own words and are shown verbatim.
/// </remarks>
public static class AuthorizationDisplay
{
    public static string RoleName(IStringLocalizer localizer, string role) =>
        Roles.IsDefault(role) ? localizer[$"Role_{role}_Name"] : role;

    public static string RoleDescription(IStringLocalizer localizer, string role) =>
        Roles.IsDefault(role) ? localizer[$"Role_{role}_Desc"] : localizer["Admin_Roles_CustomDesc"];

    /// <summary>The catalog area a permission sits in (Parking, Users, …).</summary>
    public static string AreaName(IStringLocalizer localizer, string area) =>
        localizer[$"PermArea_{area}"];

    /// <summary>A permission group. Built-in ones are localized; custom names are the author's own.</summary>
    public static string GroupName(IStringLocalizer localizer, string group) =>
        PermissionGroupNames.IsDefault(group) ? localizer[$"PermGroup_{group}_Name"] : group;

    public static string GroupDescription(IStringLocalizer localizer, string group, string? stored) =>
        PermissionGroupNames.IsDefault(group)
            ? localizer[$"PermGroup_{group}_Desc"]
            : stored ?? localizer["Admin_Groups_CustomDesc"];

    public static string PermissionName(IStringLocalizer localizer, string permission) =>
        localizer[$"Perm_{Key(permission)}"];

    public static string PermissionDescription(IStringLocalizer localizer, string permission) =>
        localizer[$"Perm_{Key(permission)}_Desc"];

    private static string Key(string permission) => permission.Replace('.', '_');
}
