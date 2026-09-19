using Microsoft.EntityFrameworkCore;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Identity;

/// <summary>
/// Reads the permissions a user actually holds, straight from their role claims.
/// </summary>
/// <remarks>
/// Deliberately not taken from the caller's <c>ClaimsPrincipal</c>. The principal is a snapshot
/// baked into a cookie up to the security-stamp validation interval ago, and it is the very thing
/// an escalation attempt would be trying to widen; a privilege boundary has to be checked against
/// the database that stores it.
/// </remarks>
internal static class EffectivePermissions
{
    /// <summary>
    /// Checks a protected command against the current account and role assignments, including
    /// changes made after a Blazor circuit received its principal. The actor id must come from
    /// that authenticated principal, never from an editable request field.
    /// </summary>
    public static Task<bool> HasActiveUserPermissionAsync(
        D3ParkingDbContext dbContext,
        Guid userId,
        string permission,
        CancellationToken cancellationToken = default) =>
        (from user in dbContext.Users
         join userRole in dbContext.UserRoles on user.Id equals userRole.UserId
         join claim in dbContext.RoleClaims on userRole.RoleId equals claim.RoleId
         where user.Id == userId && user.Status == AccountStatus.Active
             && claim.ClaimType == D3ParkingClaimTypes.Permission
             && claim.ClaimValue == permission
         select user.Id).AnyAsync(cancellationToken);

    /// <summary>Every permission granted to <paramref name="userId"/> through their roles.</summary>
    public static async Task<HashSet<string>> ForUserAsync(
        D3ParkingDbContext dbContext,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var permissions = await (from userRole in dbContext.UserRoles
                                 join claim in dbContext.RoleClaims on userRole.RoleId equals claim.RoleId
                                 where userRole.UserId == userId
                                     && claim.ClaimType == D3ParkingClaimTypes.Permission
                                     && claim.ClaimValue != null
                                 select claim.ClaimValue!)
            .Distinct()
            .ToListAsync(cancellationToken);

        return permissions.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every permission granted to the named roles, keyed by role name.</summary>
    public static async Task<Dictionary<string, HashSet<string>>> ForRolesAsync(
        D3ParkingDbContext dbContext,
        IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken)
    {
        if (roleNames.Count == 0)
        {
            return [];
        }

        var rows = await (from role in dbContext.Roles
                          join claim in dbContext.RoleClaims on role.Id equals claim.RoleId
                          where role.Name != null && roleNames.Contains(role.Name)
                              && claim.ClaimType == D3ParkingClaimTypes.Permission
                              && claim.ClaimValue != null
                          select new { Role = role.Name!, Permission = claim.ClaimValue! })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.Role, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Permission).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
    }
}
