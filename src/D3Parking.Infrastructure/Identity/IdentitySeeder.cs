using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Identity;

/// <summary>
/// Idempotently ensures the built-in permission groups, the roles composed from them, their
/// materialized permission claims, and the admin account exist.
/// </summary>
public class IdentitySeeder(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    // The scoped context the managers write through; the seeding transaction and its app lock
    // must open on this instance so the managers' saves ride inside it.
    D3ParkingDbContext identityDbContext,
    RolePermissionMaterializer materializer,
    IOptions<IdentitySeedOptions> options,
    ILogger<IdentitySeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        // The steps below are check-then-insert with no unique index behind the role claims, so
        // two instances starting at once (scale-out, rolling deploy) could both see a permission
        // as missing and both insert it. A session-scoped application lock serializes seeding
        // across processes; @LockOwner=Transaction ties its lifetime to this transaction, so the
        // lock can never leak past the commit or rollback.
        await using var transaction = await identityDbContext.Database.BeginTransactionAsync(cancellationToken);
        await identityDbContext.Database.ExecuteSqlRawAsync(
            """
            DECLARE @r int;
            EXEC @r = sp_getapplock @Resource = N'D3Parking:IdentitySeed', @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = 60000;
            IF @r < 0 THROW 51000, 'Could not acquire the identity seeding lock.', 1;
            """,
            cancellationToken);

        await SeedPermissionGroupsAsync(cancellationToken);
        await SeedRolesAsync(cancellationToken);
        await AdoptLegacyRolePermissionsAsync(cancellationToken);
        await MaterializeAllRolesAsync(cancellationToken);
        await SeedAdminAsync();

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Creates the built-in groups and keeps their contents matching the map. They are
    /// system-managed, so a release that moves a permission between groups lands on existing
    /// installations; the UI shows them read-only and offers duplication for variants.
    /// </summary>
    private async Task SeedPermissionGroupsAsync(CancellationToken cancellationToken)
    {
        var existing = await identityDbContext.PermissionGroups
            .Include(g => g.Entries)
            .Where(g => g.IsBuiltIn)
            .ToDictionaryAsync(g => g.Name, StringComparer.Ordinal, cancellationToken);

        foreach (var (name, permissions) in DefaultPermissionGroups.Map)
        {
            if (existing.TryGetValue(name, out var group))
            {
                group.SetPermissions(permissions);
                continue;
            }

            identityDbContext.PermissionGroups.Add(new PermissionGroup(name, null, permissions, isBuiltIn: true));
            logger.LogInformation("Created built-in permission group {Group}", name);
        }

        // A group retired by a release must not linger: it would still be composable into roles.
        var retired = existing.Values.Where(g => !DefaultPermissionGroups.Map.ContainsKey(g.Name)).ToArray();
        foreach (var group in retired)
        {
            identityDbContext.RolePermissionGroups.RemoveRange(
                identityDbContext.RolePermissionGroups.Where(l => l.PermissionGroupId == group.Id));
            identityDbContext.PermissionGroups.Remove(group);
            logger.LogInformation("Removed retired built-in permission group {Group}", group.Name);
        }

        await identityDbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Creates the built-in roles and makes their group composition match the map.</summary>
    private async Task SeedRolesAsync(CancellationToken cancellationToken)
    {
        var groupIds = await identityDbContext.PermissionGroups
            .ToDictionaryAsync(g => g.Name, g => g.Id, StringComparer.Ordinal, cancellationToken);

        foreach (var (roleName, groups) in DefaultRoleGroups.Map)
        {
            var role = await roleManager.FindByNameAsync(roleName);
            if (role is null)
            {
                role = new ApplicationRole(roleName);
                var created = await roleManager.CreateAsync(role);
                if (!created.Succeeded)
                {
                    logger.LogError("Failed to create role {Role}: {Errors}", roleName, Describe(created));
                    continue;
                }

                logger.LogInformation("Created role {Role}", roleName);
            }

            var target = groups.Select(g => groupIds[g]).ToHashSet();
            await SetRoleGroupsAsync(role.Id, target, cancellationToken);
        }

        await identityDbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Carries a pre-groups installation across: any role whose permission claims are not yet
    /// explained by its groups gets a private group holding exactly what it had.
    /// </summary>
    /// <remarks>
    /// Roles are composed of groups and nothing else, so an upgrade that simply dropped the old
    /// per-role claims would silently strip every custom role. Adopting them into a named group
    /// keeps the access and makes it visible: "Editor — převzatá oprávnění" says where it came from
    /// and can be edited or replaced like any other group.
    /// </remarks>
    private async Task AdoptLegacyRolePermissionsAsync(CancellationToken cancellationToken)
    {
        var roles = await roleManager.Roles
            .Where(r => r.Name != null && !DefaultRoleGroups.Map.Keys.Contains(r.Name))
            .ToListAsync(cancellationToken);

        foreach (var role in roles)
        {
            var alreadyComposed = await identityDbContext.RolePermissionGroups
                .AnyAsync(l => l.RoleId == role.Id, cancellationToken);
            if (alreadyComposed)
            {
                continue;
            }

            var claimed = (await roleManager.GetClaimsAsync(role))
                .Where(c => c.Type == D3ParkingClaimTypes.Permission && Permissions.IsKnown(c.Value))
                .Select(c => c.Value)
                .ToArray();
            if (claimed.Length == 0)
            {
                continue;
            }

            // The seeder has no UI culture, so the marker stays a short neutral tag rather than a
            // sentence in one language. It is meant to be reviewed and renamed, not kept forever.
            var name = $"{role.Name} (legacy)";
            var group = await identityDbContext.PermissionGroups
                .FirstOrDefaultAsync(g => g.Name == name, cancellationToken);

            if (group is null)
            {
                group = new PermissionGroup(name, "Adopted from the role's own permissions on upgrade.", claimed);
                identityDbContext.PermissionGroups.Add(group);
                await identityDbContext.SaveChangesAsync(cancellationToken);
            }

            identityDbContext.RolePermissionGroups.Add(new RolePermissionGroup(role.Id, group.Id));
            logger.LogInformation("Adopted {Count} permissions of role {Role} into group {Group}",
                claimed.Length, role.Name, name);
        }

        await identityDbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Recomputes every role's claims from its groups, so the derived table is correct.</summary>
    private async Task MaterializeAllRolesAsync(CancellationToken cancellationToken)
    {
        var roleIds = await roleManager.Roles.Select(r => r.Id).ToListAsync(cancellationToken);
        foreach (var roleId in roleIds)
        {
            if (await materializer.MaterializeAsync(roleId, cancellationToken))
            {
                await materializer.RefreshMembersAsync(roleId, cancellationToken);
            }
        }
    }

    private async Task SetRoleGroupsAsync(Guid roleId, IReadOnlySet<Guid> target, CancellationToken cancellationToken)
    {
        var current = await identityDbContext.RolePermissionGroups
            .Where(l => l.RoleId == roleId)
            .ToListAsync(cancellationToken);

        identityDbContext.RolePermissionGroups.RemoveRange(
            current.Where(l => !target.Contains(l.PermissionGroupId)));

        var held = current.Select(l => l.PermissionGroupId).ToHashSet();
        foreach (var groupId in target.Where(id => !held.Contains(id)))
        {
            identityDbContext.RolePermissionGroups.Add(new RolePermissionGroup(roleId, groupId));
        }
    }

    private async Task SeedAdminAsync()
    {
        var seed = options.Value;
        if (string.IsNullOrWhiteSpace(seed.AdminEmail) || string.IsNullOrWhiteSpace(seed.AdminPassword))
        {
            // Deliberate: there is no baked-in default credential. Production must supply its own
            // IdentitySeed:AdminEmail/AdminPassword (environment variables or secrets) — only the
            // Development config carries the well-known local pair.
            logger.LogWarning("No admin seed configured; skipping admin account creation. "
                + "Set IdentitySeed:AdminEmail and IdentitySeed:AdminPassword to create one.");
            return;
        }

        var admin = await userManager.FindByEmailAsync(seed.AdminEmail);
        if (admin is null)
        {
            admin = new ApplicationUser
            {
                UserName = seed.AdminEmail,
                Email = seed.AdminEmail,
                EmailConfirmed = true,
                DisplayName = seed.AdminDisplayName,
                Status = AccountStatus.Active,
            };

            var created = await userManager.CreateAsync(admin, seed.AdminPassword);
            if (!created.Succeeded)
            {
                logger.LogError("Failed to create admin {Email}: {Errors}", seed.AdminEmail, Describe(created));
                return;
            }

            logger.LogInformation("Created admin account {Email}", seed.AdminEmail);
        }
        else if (admin.Status != AccountStatus.Active)
        {
            admin.Status = AccountStatus.Active;
            await userManager.UpdateAsync(admin);
        }

        if (!await userManager.IsInRoleAsync(admin, Roles.Administrator))
        {
            await userManager.AddToRoleAsync(admin, Roles.Administrator);
        }
    }

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));
}
