using Microsoft.AspNetCore.Authorization;

namespace Webora.Web.Authorization;

/// <summary>Requires the current user to hold a specific permission, e.g. [HasPermission(Permissions.Pages.Edit)].</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class HasPermissionAttribute : AuthorizeAttribute
{
    public HasPermissionAttribute(string permission) => Policy = PermissionPolicyProvider.Prefix + permission;
}
