using Webora.Domain.Authorization;

namespace Webora.Infrastructure.Identity;

/// <summary>Default role → permission grants applied by the seeder. Administrator always gets everything.</summary>
public static class DefaultRolePermissions
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Map { get; } =
        new Dictionary<string, IReadOnlyList<string>>
        {
            [Roles.Administrator] = Permissions.All,
            [Roles.Editor] =
            [
                Permissions.Pages.View, Permissions.Pages.Create, Permissions.Pages.Edit,
                Permissions.Pages.Delete, Permissions.Pages.Publish,
                Permissions.Media.View, Permissions.Media.Upload, Permissions.Media.Delete,
                Permissions.Settings.View,
            ],
            [Roles.Viewer] =
            [
                Permissions.Pages.View, Permissions.Media.View, Permissions.Settings.View,
            ],
        };
}
