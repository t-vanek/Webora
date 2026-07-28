namespace D3Parking.Web.Authorization;

/// <summary>Builds the policy name for a permission, for use with &lt;AuthorizeView Policy="…"&gt;.</summary>
public static class PermissionPolicies
{
    public static string For(string permission) => PermissionPolicyProvider.Prefix + permission;
}
