using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Identity;

/// <summary>
/// Keeps a role's permission claims equal to the union of the groups composing it.
/// </summary>
/// <remarks>
/// Roles store groups; authorization reads permission claims. Rather than teach the authorization
/// layer about the new indirection — and pay for the join on every request — the effective set is
/// materialized into <c>AspNetRoleClaims</c> whenever either side changes. The claims table is
/// therefore derived data: nothing writes to it except this class.
/// <para>
/// Everything runs through the scoped <see cref="D3ParkingDbContext"/>, the same instance
/// <see cref="RoleManager{T}"/> writes through, so a caller that wrapped the whole change in a
/// transaction (the seeder does) has its uncommitted groups visible here.
/// </para>
/// </remarks>
public sealed class RolePermissionMaterializer(
    RoleManager<ApplicationRole> roleManager,
    UserManager<ApplicationUser> userManager,
    D3ParkingDbContext dbContext,
    ILogger<RolePermissionMaterializer> logger)
{
    /// <summary>Recomputes one role's claims. Returns true when anything actually changed.</summary>
    public async Task<bool> MaterializeAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var role = await roleManager.FindByIdAsync(roleId.ToString());
        if (role is null)
        {
            return false;
        }

        var target = (await EffectiveAsync(roleId, cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var existing = (await roleManager.GetClaimsAsync(role))
            .Where(c => c.Type == D3ParkingClaimTypes.Permission)
            .ToList();

        var stale = existing.Where(c => !target.Contains(c.Value)).ToArray();
        var missing = target
            .Except(existing.Select(c => c.Value), StringComparer.Ordinal)
            .ToArray();

        if (stale.Length == 0 && missing.Length == 0)
        {
            return false;
        }

        foreach (var claim in stale)
        {
            await roleManager.RemoveClaimAsync(role, claim);
        }

        foreach (var permission in missing)
        {
            await roleManager.AddClaimAsync(role, new Claim(D3ParkingClaimTypes.Permission, permission));
        }

        logger.LogInformation("Materialized role {Role}: +{Added} -{Removed} permissions",
            role.Name, missing.Length, stale.Length);
        return true;
    }

    /// <summary>
    /// Recomputes every role composed of <paramref name="groupId"/> and refreshes their members'
    /// claims. Editing a group is the one action whose blast radius is not the role in front of
    /// you, so it has to fan out.
    /// </summary>
    public async Task MaterializeGroupUsageAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        var roleIds = await dbContext.RolePermissionGroups
            .Where(link => link.PermissionGroupId == groupId)
            .Select(link => link.RoleId)
            .ToListAsync(cancellationToken);

        foreach (var roleId in roleIds)
        {
            if (await MaterializeAsync(roleId, cancellationToken))
            {
                await RefreshMembersAsync(roleId, cancellationToken);
            }
        }
    }

    /// <summary>The permissions a role's groups add up to, closed over the catalog's dependencies.</summary>
    public async Task<IReadOnlyList<string>> EffectiveAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var permissions = await (from link in dbContext.RolePermissionGroups
                                 join grp in dbContext.PermissionGroups on link.PermissionGroupId equals grp.Id
                                 from entry in grp.Entries
                                 where link.RoleId == roleId
                                 select entry.Permission)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Unknown names can survive in the table across a release that retires a permission; the
        // catalog is the authority, and Expand drops anything it no longer declares.
        return Permissions.Expand(permissions);
    }

    /// <summary>
    /// Re-issues the claims of everyone in the role, so a permission change takes effect promptly
    /// rather than at the next sign-in. Role membership is small in practice.
    /// </summary>
    public async Task RefreshMembersAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var memberIds = await dbContext.UserRoles
            .Where(ur => ur.RoleId == roleId)
            .Select(ur => ur.UserId)
            .ToListAsync(cancellationToken);

        foreach (var id in memberIds)
        {
            if (await userManager.FindByIdAsync(id.ToString()) is { } member)
            {
                await userManager.UpdateSecurityStampAsync(member);
            }
        }
    }
}
