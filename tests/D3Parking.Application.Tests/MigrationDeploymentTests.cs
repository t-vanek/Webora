using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;
using D3Parking.Web.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture, NonParallelizable]
public class MigrationDeploymentTests
{
    [Test]
    public async Task Full_migration_chain_and_repeatable_seed_preserve_a_disabled_bootstrap_account()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured)) Assert.Ignore("Requires SQL Server; creates a unique temporary database.");
        var connection = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = $"D3Parking_MigrationAudit_{Guid.NewGuid():N}",
        }.ConnectionString;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<D3ParkingDbContext>(o => o.UseSqlServer(connection));
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<D3ParkingDbContext>();
        services.AddScoped<RolePermissionMaterializer>();
        services.AddScoped<IdentitySeeder>();
        services.Configure<IdentitySeedOptions>(o => { o.AdminEmail = "bootstrap@test.local"; o.AdminPassword = "Audit-Password-938!"; });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>();
        try
        {
            await db.Database.MigrateAsync();
            var schema = await DeploymentDatabase.InspectAsync(db, CancellationToken.None);
            Assert.That(schema.Pending, Is.Empty);
            Assert.That(schema.Applied.Length, Is.GreaterThan(30));
            await DeploymentDatabase.RequireCurrentSchemaAsync(db, CancellationToken.None);
            // A second apply is a no-op; most existing DB tests only use EnsureCreated.
            await db.Database.MigrateAsync();
            var seeder = scope.ServiceProvider.GetRequiredService<IdentitySeeder>();
            await seeder.SeedAsync();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = (await users.FindByEmailAsync("bootstrap@test.local"))!;
            Assert.That(await users.IsInRoleAsync(admin, Roles.Administrator), Is.True);
            admin.Status = AccountStatus.Blocked;
            Assert.That((await users.UpdateAsync(admin)).Succeeded, Is.True);
            Assert.That((await users.RemoveFromRoleAsync(admin, Roles.Administrator)).Succeeded, Is.True);
            await seeder.SeedAsync();
            db.ChangeTracker.Clear();
            admin = (await users.FindByEmailAsync("bootstrap@test.local"))!;
            Assert.That(admin.Status, Is.EqualTo(AccountStatus.Blocked));
            Assert.That(await users.IsInRoleAsync(admin, Roles.Administrator), Is.False);
        }
        finally
        {
            // Only the GUID-named database created above can be removed.
            await db.Database.EnsureDeletedAsync();
        }
    }
}
