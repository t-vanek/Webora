using Microsoft.AspNetCore.Authorization;

namespace D3Parking.Web.Authorization;

/// <summary>Requires the current user to hold a specific permission, e.g. [HasPermission(Permissions.Pages.Edit)].</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    public HasPermissionAttribute(string permission) => Policy = PermissionPolicies.For(permission);
}

/// <summary>
/// Requires any one of the listed permissions. Stacking <see cref="HasPermissionAttribute"/> would
/// mean AND — this is the OR, for a page that hosts several independently gated queues.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class HasAnyPermissionAttribute : AuthorizeAttribute
{
    public HasAnyPermissionAttribute(params string[] permissions) => Policy = PermissionPolicies.ForAny(permissions);
}
