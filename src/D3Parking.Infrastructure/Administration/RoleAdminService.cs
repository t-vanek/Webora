using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using D3Parking.Application.Accounts;
using D3Parking.Application.Administration;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Administration;

public sealed class RoleAdminService(
    RoleManager<ApplicationRole> roleManager,
    UserManager<ApplicationUser> userManager,
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IStringLocalizer<AccountMessages> messages,
    ILogger<RoleAdminService> logger) : IRoleAdminService
{
    public IReadOnlyList<PermissionGroup> PermissionCatalog { get; } = Permissions.Groups;

    public async Task<IReadOnlyList<RoleSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await dbContext.Roles.AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new
            {
                r.Id,
                r.Name,
                PermissionCount = dbContext.RoleClaims.Count(c => c.RoleId == r.Id && c.ClaimType == D3ParkingClaimTypes.Permission),
                UserCount = dbContext.UserRoles.Count(ur => ur.RoleId == r.Id),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new RoleSummary(r.Id, r.Name!, r.PermissionCount, r.UserCount, Roles.IsDefault(r.Name!)))
            .ToArray();
    }

    public async Task<RoleDetail?> GetAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var role = await roleManager.FindByIdAsync(roleId.ToString());
        if (role is null)
        {
            return null;
        }

        var permissions = (await roleManager.GetClaimsAsync(role))
            .Where(c => c.Type == D3ParkingClaimTypes.Permission)
            .Select(c => c.Value)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        return new RoleDetail(role.Id, role.Name!, Roles.IsDefault(role.Name!), permissions);
    }

    public async Task<AccountResult> CreateAsync(string name, IReadOnlyList<string> permissions, CancellationToken cancellationToken = default)
    {
        name = name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            return AccountResult.Failure(messages["Error_RoleNameRequired"]);
        }

        if (ValidatePermissions(permissions) is { } invalid)
        {
            return invalid;
        }

        var role = new ApplicationRole(name);
        var created = await roleManager.CreateAsync(role);
        if (!created.Succeeded)
        {
            return ToFailure(created);
        }

        foreach (var permission in permissions.Distinct(StringComparer.Ordinal))
        {
            var added = await roleManager.AddClaimAsync(role, new Claim(D3ParkingClaimTypes.Permission, permission));
            if (!added.Succeeded)
            {
                return ToFailure(added);
            }
        }

        logger.LogInformation("Created role {Role} with {Count} permissions", name, permissions.Count);
        return AccountResult.Success;
    }

    public async Task<AccountResult> RenameAsync(Guid roleId, string name, CancellationToken cancellationToken = default)
    {
        var role = await roleManager.FindByIdAsync(roleId.ToString());
        if (role is null)
        {
            return AccountResult.Failure(messages["Error_RoleNotFound"]);
        }

        if (Roles.IsDefault(role.Name!))
        {
            return AccountResult.Failure(messages["Error_DefaultRoleProtected"]);
        }

        name = name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            return AccountResult.Failure(messages["Error_RoleNameRequired"]);
        }

        var renamed = await roleManager.SetRoleNameAsync(role, name);
        if (!renamed.Succeeded)
        {
            return ToFailure(renamed);
        }

        var updated = await roleManager.UpdateAsync(role);
        return updated.Succeeded ? AccountResult.Success : ToFailure(updated);
    }

    public async Task<AccountResult> SetPermissionsAsync(Guid roleId, IReadOnlyList<string> permissions, CancellationToken cancellationToken = default)
    {
        var role = await roleManager.FindByIdAsync(roleId.ToString());
        if (role is null)
        {
            return AccountResult.Failure(messages["Error_RoleNotFound"]);
        }

        // The Administrator role is all-powerful by design and is re-granted every permission on
        // startup, so editing its permission set is not allowed.
        if (string.Equals(role.Name, Roles.Administrator, StringComparison.Ordinal))
        {
            return AccountResult.Failure(messages["Error_AdministratorAllPermissions"]);
        }

        if (ValidatePermissions(permissions) is { } invalid)
        {
            return invalid;
        }

        var target = permissions.ToHashSet(StringComparer.Ordinal);
        var existing = (await roleManager.GetClaimsAsync(role))
            .Where(c => c.Type == D3ParkingClaimTypes.Permission)
            .ToList();
        var existingValues = existing.Select(c => c.Value).ToHashSet(StringComparer.Ordinal);

        foreach (var claim in existing.Where(c => !target.Contains(c.Value)))
        {
            var removed = await roleManager.RemoveClaimAsync(role, claim);
            if (!removed.Succeeded)
            {
                return ToFailure(removed);
            }
        }

        foreach (var permission in target.Where(p => !existingValues.Contains(p)))
        {
            var added = await roleManager.AddClaimAsync(role, new Claim(D3ParkingClaimTypes.Permission, permission));
            if (!added.Succeeded)
            {
                return ToFailure(added);
            }
        }

        await InvalidateRoleMembersAsync(roleId, cancellationToken);

        logger.LogInformation("Set {Count} permissions on role {Role}", target.Count, role.Name);
        return AccountResult.Success;
    }

    public async Task<AccountResult> DeleteAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var role = await roleManager.FindByIdAsync(roleId.ToString());
        if (role is null)
        {
            return AccountResult.Failure(messages["Error_RoleNotFound"]);
        }

        if (Roles.IsDefault(role.Name!))
        {
            return AccountResult.Failure(messages["Error_DefaultRoleProtected"]);
        }

        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            if (await dbContext.UserRoles.AnyAsync(ur => ur.RoleId == roleId, cancellationToken))
            {
                return AccountResult.Failure(messages["Error_RoleHasMembers"]);
            }
        }

        var deleted = await roleManager.DeleteAsync(role);
        if (!deleted.Succeeded)
        {
            return ToFailure(deleted);
        }

        logger.LogInformation("Deleted role {Role}", role.Name);
        return AccountResult.Success;
    }

    private AccountResult? ValidatePermissions(IReadOnlyList<string> permissions)
    {
        foreach (var permission in permissions)
        {
            if (!Permissions.IsKnown(permission))
            {
                return AccountResult.Failure(messages["Error_UnknownPermission", permission]);
            }
        }

        return null;
    }

    // Re-issue claims for everyone in the role so permission changes take effect promptly rather
    // than only on next sign-in. Role membership is small in practice.
    private async Task InvalidateRoleMembersAsync(Guid roleId, CancellationToken cancellationToken)
    {
        List<Guid> memberIds;
        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            memberIds = await dbContext.UserRoles
                .Where(ur => ur.RoleId == roleId)
                .Select(ur => ur.UserId)
                .ToListAsync(cancellationToken);
        }

        foreach (var id in memberIds)
        {
            if (await userManager.FindByIdAsync(id.ToString()) is { } member)
            {
                await userManager.UpdateSecurityStampAsync(member);
            }
        }
    }

    private static AccountResult ToFailure(IdentityResult result) =>
        AccountResult.Failure(result.Errors.Select(e => e.Description).ToArray());
}
