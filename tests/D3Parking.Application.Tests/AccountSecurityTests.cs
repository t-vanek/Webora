using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Identity;
using D3Parking.Application.Abstractions.Email;
using D3Parking.Application.Accounts;
using D3Parking.Application.Mapping;
using D3Parking.Application.Settings;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Accounts;
using D3Parking.Infrastructure.Administration;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Exercises the account-security invariants against SQL Server with the real Identity stack:
/// disabling an account rotates its security stamp (so live cookies/circuits stop validating),
/// the last administrator can be disabled by no path, and concurrent role edits cannot strip the
/// two last administrators at once. Requires ConnectionStrings__SqlServer (skipped without it).
/// </summary>
[TestFixture]
[NonParallelizable]
public class AccountSecurityTests
{
    private ServiceProvider _provider = null!;
    private DbContextOptions<D3ParkingDbContext> _options = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the account security tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_AccountSecurityTests",
        };

        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            await dbContext.Database.EnsureDeletedAsync();
            await dbContext.Database.EnsureCreatedAsync();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<D3ParkingDbContext>(o => o.UseSqlServer(builder.ConnectionString));
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 8;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<D3ParkingDbContext>();
        _provider = services.BuildServiceProvider();

        // The admin role must exist before the role-membership tests.
        await using var scope = _provider.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        await roleManager.CreateAsync(new ApplicationRole(Roles.Administrator));
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        if (_options is not null)
        {
            await using var dbContext = new D3ParkingDbContext(_options);
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    [Test]
    public async Task Blocking_an_account_rotates_its_security_stamp()
    {
        await using var scope = _provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var accounts = CreateAccountService(scope);

        var user = await CreateUserAsync(userManager, "block-target");
        var stampBefore = await userManager.GetSecurityStampAsync(user);

        var result = await accounts.BlockAsync(user.Id, Guid.NewGuid(), "abuse", CancellationToken.None);

        Assert.That(result.Succeeded, Is.True);
        var reloaded = await userManager.FindByIdAsync(user.Id.ToString());
        Assert.That(reloaded!.Status, Is.EqualTo(AccountStatus.Blocked));
        Assert.That(await userManager.GetSecurityStampAsync(reloaded), Is.Not.EqualTo(stampBefore),
            "Blocking must rotate the security stamp so existing sessions stop validating.");
    }

    [Test]
    public async Task Blocking_the_last_administrator_is_refused()
    {
        await using var scope = _provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var accounts = CreateAccountService(scope);

        var admin = await CreateUserAsync(userManager, "last-admin");
        await userManager.AddToRoleAsync(admin, Roles.Administrator);

        var result = await accounts.BlockAsync(admin.Id, Guid.NewGuid(), null, CancellationToken.None);

        Assert.That(result.Succeeded, Is.False, "The only administrator must not be blockable.");
        var reloaded = await userManager.FindByIdAsync(admin.Id.ToString());
        Assert.That(reloaded!.Status, Is.EqualTo(AccountStatus.Active));

        // Cleanup so later tests see no leftover administrator from this one.
        await userManager.RemoveFromRoleAsync(reloaded, Roles.Administrator);
    }

    [Test]
    public async Task Concurrent_role_strips_keep_at_least_one_administrator()
    {
        Guid adminA, adminB;
        await using (var seedScope = _provider.CreateAsyncScope())
        {
            var userManager = seedScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var a = await CreateUserAsync(userManager, "admin-a");
            var b = await CreateUserAsync(userManager, "admin-b");
            await userManager.AddToRoleAsync(a, Roles.Administrator);
            await userManager.AddToRoleAsync(b, Roles.Administrator);
            (adminA, adminB) = (a.Id, b.Id);
        }

        // Two independent scopes = two contexts = a real race: each admin strips the other.
        await using var scopeA = _provider.CreateAsyncScope();
        await using var scopeB = _provider.CreateAsyncScope();
        var results = await Task.WhenAll(
            RunTolerantAsync(() => CreateUserAdminService(scopeA).SetRolesAsync(adminB, [], adminA)),
            RunTolerantAsync(() => CreateUserAdminService(scopeB).SetRolesAsync(adminA, [], adminB)));

        await using var verifyScope = _provider.CreateAsyncScope();
        var verifyManager = verifyScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var remaining = await verifyManager.GetUsersInRoleAsync(Roles.Administrator);

        Assert.That(remaining, Is.Not.Empty,
            "Concurrently stripping the two last administrators must leave at least one in place.");
        Assert.That(results.Count(r => r is { Succeeded: true }), Is.LessThanOrEqualTo(1));

        // Cleanup for any test added later.
        foreach (var admin in remaining)
        {
            await verifyManager.RemoveFromRoleAsync(admin, Roles.Administrator);
        }
    }

    private static async Task<ApplicationUser> CreateUserAsync(UserManager<ApplicationUser> userManager, string prefix)
    {
        var user = new ApplicationUser
        {
            UserName = $"{prefix}-{Guid.NewGuid():N}@test.local",
            Email = $"{prefix}-{Guid.NewGuid():N}@test.local",
            EmailConfirmed = true,
            Status = AccountStatus.Active,
        };
        var created = await userManager.CreateAsync(user, "Str0ng!Password");
        Assert.That(created.Succeeded, Is.True, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user;
    }

    private AccountService CreateAccountService(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
        scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>(),
        scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>(),
        new TestDbContextFactory(_options),
        new NullEmailSender(),
        new AccountAuditMapper(),
        new NullNotificationService(),
        new FakeSiteSettings(),
        new PassthroughLocalizer<AccountMessages>(),
        Options.Create(new AccountOptions()),
        TimeProvider.System,
        NullLogger<AccountService>.Instance);

    private UserAdminService CreateUserAdminService(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
        scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>(),
        new TestDbContextFactory(_options),
        new PassthroughLocalizer<AccountMessages>(),
        TimeProvider.System,
        NullLogger<UserAdminService>.Instance);

    private static async Task<AccountResult?> RunTolerantAsync(Func<Task<AccountResult>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (ex is DbUpdateException || ex.GetBaseException() is SqlException)
        {
            // A lost deadlock between the two serializable guards rolls the loser back whole —
            // which preserves the invariant the test asserts.
            return null;
        }
    }

    private sealed class NullEmailSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
