using Microsoft.AspNetCore.Authorization;
using D3Parking.Domain.Authorization;

namespace D3Parking.Web.Authorization;

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        if (requirement.Permissions.Any(p => context.User.HasClaim(D3ParkingClaimTypes.Permission, p)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
