using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;

namespace D3Parking.Infrastructure.Identity;

/// <summary>Idempotently ensures the default roles, their permission claims, and the admin account exist.</summary>
public class IdentitySeeder(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IOptions<IdentitySeedOptions> options,
    ILogger<IdentitySeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await SeedRolesAsync();
        await SeedAdminAsync();
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
            logger.LogInformation("No admin seed configured; skipping admin account creation.");
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
