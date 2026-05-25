using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Webora.Domain.Authorization;

namespace Webora.Infrastructure.Identity;

/// <summary>
/// Augments the signed-in principal with the union of permission claims granted by the user's
/// roles. Permissions are baked into the auth cookie at sign-in, so authorization checks need no
/// database round-trip per request.
/// </summary>
public class ApplicationUserClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<ApplicationUser, ApplicationRole>(userManager, roleManager, optionsAccessor)
{
    public override async Task<ClaimsPrincipal> CreateAsync(ApplicationUser user)
    {
        var principal = await base.CreateAsync(user);

        if (principal.Identity is not ClaimsIdentity identity)
        {
            return principal;
        }

        var permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var roleName in await UserManager.GetRolesAsync(user))
        {
            var role = await RoleManager.FindByNameAsync(roleName);
            if (role is null)
            {
                continue;
            }

            foreach (var claim in await RoleManager.GetClaimsAsync(role))
            {
                if (claim.Type == WeboraClaimTypes.Permission)
                {
                    permissions.Add(claim.Value);
                }
            }
        }

        foreach (var permission in permissions)
        {
            identity.AddClaim(new Claim(WeboraClaimTypes.Permission, permission));
        }

        return principal;
    }
}
