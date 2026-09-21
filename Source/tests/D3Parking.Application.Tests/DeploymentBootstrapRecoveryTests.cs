using System.Text.Json;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;
using D3Parking.Web.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture, NonParallelizable]
public class DeploymentBootstrapRecoveryTests
{
    private const string Email = "bootstrap@test.local";
    private const string Password = "Recovery-Password-938!";
    private string root = null!;
    private IConfiguration config = null!;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), $"d3parking-bootstrap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "state"));
        config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["deployment-recovery"] = "true",
            ["IdentitySeed:AdminEmail"] = Email,
            ["IdentitySeed:AdminPassword"] = Password,
        }).Build();
        WriteJournal("database upgrade");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(root, recursive: true); // Only this test's GUID directory.

    [TestCase(null)]
    [TestCase("false")]
    [TestCase("invalid")]
    public async Task Ordinary_preflight_cannot_use_the_bootstrap_exception(string? flag)
    {
        config["deployment-recovery"] = flag;
        await AssertRejectedWithoutDatabaseAsync();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("Short-1!")]
    [TestCase("all-lowercase-123!")]
    [TestCase("ALL-UPPERCASE-123!")]
    [TestCase("Missing-Digits!!")]
    [TestCase("MissingSymbols123")]
    public async Task Missing_or_invalid_password_cannot_allow_recovery(string? password)
    {
        config["IdentitySeed:AdminPassword"] = password;
        await AssertRejectedWithoutDatabaseAsync();
    }

    [TestCase(null)]
    [TestCase("invalid")]
    [TestCase("Bootstrap <bootstrap@test.local>")]
    public async Task Seed_email_must_be_usable_as_an_identity_user_name(string? email)
    {
        config["IdentitySeed:AdminEmail"] = email;
        await AssertRejectedWithoutDatabaseAsync();
    }

    [TestCase("{}")]
    [TestCase("null")]
    [TestCase("{")]
    [TestCase("{\"previous\":null,\"phase\":\"database upgrade\"}")]
    public async Task Incomplete_or_corrupt_journal_cannot_allow_recovery(string journal)
    {
        File.WriteAllText(Path.Combine(root, "state", "in-progress.json"), journal);
        await AssertRejectedWithoutDatabaseAsync();
    }

    [Test]
    public async Task Missing_journal_cannot_allow_recovery()
    {
        File.Delete(Path.Combine(root, "state", "in-progress.json"));
        await AssertRejectedWithoutDatabaseAsync();
    }

    [Test]
    public async Task Mismatched_release_or_an_update_journal_cannot_allow_recovery()
    {
        WriteJournal("database upgrade", target: "a-different-release");
        await AssertRejectedWithoutDatabaseAsync();
        WriteJournal("database upgrade", previous: "previous-release");
        await AssertRejectedWithoutDatabaseAsync();
        WriteJournal("prepared");
        await AssertRejectedWithoutDatabaseAsync();
        File.WriteAllText(Path.Combine(root, "state", "in-progress.json"), JsonSerializer.Serialize(new
        { target = ReleaseInformation.Read("Development").Version, phase = "recovery start" }));
        await AssertRejectedWithoutDatabaseAsync(); // An omitted previous is not an explicit null.
    }

    [Test]
    public async Task Existing_installation_state_prevents_bootstrap_even_if_unreadable_as_json()
    {
        File.WriteAllText(Path.Combine(root, "state", "installation.json"), "{");
        await AssertRejectedWithoutDatabaseAsync();
    }

    [Test]
    public async Task Interrupted_first_install_requires_complete_schema_and_no_users_and_seed_is_repeatable()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured)) Assert.Ignore("Requires SQL Server; creates a unique temporary database.");
        var connection = new SqlConnectionStringBuilder(configured)
        { InitialCatalog = $"D3Parking_BootstrapRecovery_{Guid.NewGuid():N}" }.ConnectionString;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<D3ParkingDbContext>(o => o.UseSqlServer(connection));
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<D3ParkingDbContext>();
        services.AddScoped<RolePermissionMaterializer>();
        services.AddScoped<IdentitySeeder>();
        services.Configure<IdentitySeedOptions>(o => { o.AdminEmail = Email; o.AdminPassword = Password; });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>();
        try
        {
            var migrations = db.Database.GetMigrations().ToArray();
            await db.GetService<IMigrator>().MigrateAsync(migrations[^2]);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await EligibleAsync(db));
            await db.Database.MigrateAsync();
            foreach (var phase in new[] { "database upgrade", "starting target", "recovery start" })
            {
                WriteJournal(phase);
                Assert.That(await EligibleAsync(db), Is.True, phase);
                Assert.That(await EligibleAsync(db), Is.True, "The recovery check must remain read-only and repeatable.");
            }
            Assert.That(await db.Users.CountAsync(), Is.Zero);

            var seeder = scope.ServiceProvider.GetRequiredService<IdentitySeeder>();
            await seeder.SeedAsync();
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = (await manager.FindByEmailAsync(Email))!;
            Assert.That(await manager.IsInRoleAsync(admin, Roles.Administrator), Is.True);
            Assert.That(await manager.CheckPasswordAsync(admin, Password), Is.True);
            Assert.That(await EligibleAsync(db), Is.False, "Completed bootstrap must close the exception.");
            var originalHash = admin.PasswordHash;
            await seeder.SeedAsync();
            Assert.That(await db.Users.CountAsync(), Is.EqualTo(1));
            Assert.That((await manager.FindByEmailAsync(Email))!.PasswordHash, Is.EqualTo(originalHash));

            Assert.That((await manager.RemoveFromRoleAsync(admin, Roles.Administrator)).Succeeded, Is.True);
            foreach (var status in new[] { AccountStatus.Active, AccountStatus.Deactivated, AccountStatus.Blocked })
            {
                admin.Status = status;
                Assert.That((await manager.UpdateAsync(admin)).Succeeded, Is.True);
                Assert.That(await EligibleAsync(db), Is.False, $"Existing {status} non-administrator must never be bootstrapped again.");
            }
            await seeder.SeedAsync();
            db.ChangeTracker.Clear();
            admin = (await manager.FindByEmailAsync(Email))!;
            Assert.That(admin.Status, Is.EqualTo(AccountStatus.Blocked));
            Assert.That(await manager.IsInRoleAsync(admin, Roles.Administrator), Is.False);
        }
        finally { await db.Database.EnsureDeletedAsync(); } // Only the GUID-named test database.
    }

    private Task<bool> EligibleAsync(D3ParkingDbContext db) =>
        DeploymentBootstrapRecovery.IsEligibleAsync(db, config, root, "Development", CancellationToken.None);

    private async Task AssertRejectedWithoutDatabaseAsync()
    {
        await using var db = new D3ParkingDbContext(new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=Unavailable;Integrated Security=true;Connect Timeout=1;ConnectRetryCount=0").Options);
        Assert.That(await EligibleAsync(db), Is.False);
    }

    private void WriteJournal(string phase, string? target = null, string? previous = null) =>
        File.WriteAllText(Path.Combine(root, "state", "in-progress.json"), JsonSerializer.Serialize(new
        { previous, target = target ?? ReleaseInformation.Read("Development").Version, phase }));
}
