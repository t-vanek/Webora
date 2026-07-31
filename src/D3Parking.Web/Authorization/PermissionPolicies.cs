namespace D3Parking.Web.Authorization;

/// <summary>Builds the policy name for a permission, for use with &lt;AuthorizeView Policy="…"&gt;.</summary>
public static class PermissionPolicies
{
    /// <summary>Separates the permissions of an any-of policy. Permission names are dotted, so it never collides.</summary>
    public const char AnySeparator = '|';

    public static string For(string permission) => PermissionPolicyProvider.Prefix + permission;

    /// <summary>Policy satisfied by holding any one of <paramref name="permissions"/>.</summary>
    public static string ForAny(params string[] permissions) =>
        PermissionPolicyProvider.Prefix + string.Join(AnySeparator, permissions);
}
