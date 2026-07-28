using Microsoft.AspNetCore.Authorization;

namespace D3Parking.Web.Authorization;

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}
