using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Identity;

/// <summary>Idempotently ensures the default roles, their permission claims, and the admin account exist.</summary>
public class IdentitySeeder(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    // The scoped context the managers write through; the seeding transaction and its app lock
    // must open on this instance so the managers' saves ride inside it.
    D3ParkingDbContext identityDbContext,
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

        await SeedRolesAsync();
        await SeedAdminAsync();

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SeedRolesAsync()
    {
        foreach (var (roleName, permissions) in DefaultRolePermissions.Map)
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

            var existing = (await roleManager.GetClaimsAsync(role))
                .Where(c => c.Type == D3ParkingClaimTypes.Permission)
                .Select(c => c.Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var permission in permissions)
            {
                if (existing.Add(permission))
                {
                    await roleManager.AddClaimAsync(role, new Claim(D3ParkingClaimTypes.Permission, permission));
                }
            }
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
