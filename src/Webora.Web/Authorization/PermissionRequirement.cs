using Microsoft.AspNetCore.Authorization;

namespace Webora.Web.Authorization;

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}
