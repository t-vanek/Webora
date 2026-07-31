using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using D3Parking.Application.Administration;
using D3Parking.Domain.Accounts;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Administration;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Pins the paged read behind the members panel on the role detail screen. The detail used to carry
/// every holder along with the role, which is fine for a role held by three people and a whole
/// department otherwise — and a list that long cannot be searched by eye, which is exactly when
/// somebody asks "who holds this".
/// Requires ConnectionStrings__SqlServer (skipped without it).
/// </summary>
[TestFixture]
[NonParallelizable]
public class RoleMemberPagingTests
{
    private const string RoleName = "Recepce";

    private ServiceProvider _provider = null!;
    private DbContextOptions<D3ParkingDbContext> _options = null!;
    private Guid _roleId;
    private Guid _emptyRoleId;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the role member paging tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_RoleMemberPagingTests",
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
        services.AddDataProtection();
        services.AddIdentityCore<ApplicationUser>(o => o.User.RequireUniqueEmail = true)
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<D3ParkingDbContext>()
            .AddDefaultTokenProviders();
        _provider = services.BuildServiceProvider();

        // Seven holders with predictably sorting emails, plus one account that holds neither role —
        // a members query that forgets its WHERE would sweep it up.
        _roleId = await CreateRoleAsync(RoleName);
        _emptyRoleId = await CreateRoleAsync("Nikdo");
        await SeedMembersAsync(7);
        await CreateUserAsync("outsider@d3.local", "Cizí Účet", role: null);
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
    public async Task A_page_carries_the_total_and_walks_the_members_by_email()
    {
        var first = await ListAsync(null, pageIndex: 0, pageSize: 5);
        var second = await ListAsync(null, pageIndex: 1, pageSize: 5);

        Assert.Multiple(() =>
        {
            Assert.That(first.TotalCount, Is.EqualTo(7), "The total is what lets the pager exist at all.");
            Assert.That(first.Items, Has.Count.EqualTo(5));
            Assert.That(second.Items, Has.Count.EqualTo(2), "The tail page is short, not padded.");
        });

        Assert.That(first.Items.Select(m => m.Email),
            Is.EqualTo(new[] { "m0@d3.local", "m1@d3.local", "m2@d3.local", "m3@d3.local", "m4@d3.local" }));
        Assert.That(second.Items[0].Email, Is.EqualTo("m5@d3.local"), "The pages join up where the previous one stopped.");
    }

    [Test]
    public async Task Only_the_role_being_looked_at_is_listed()
    {
        var members = await ListAsync(null, 0, 100);
        var empty = await ListAsync(null, 0, 100, _emptyRoleId);

        Assert.Multiple(() =>
        {
            Assert.That(members.Items.Select(m => m.Email), Has.No.Member("outsider@d3.local"));
            Assert.That(empty.TotalCount, Is.Zero, "A role nobody holds has no members, not everybody.");
            Assert.That(empty.Items, Is.Empty);
        });
    }

    [Test]
    public async Task The_search_narrows_the_total_the_pager_is_built_from()
    {
        var byEmail = await ListAsync("m3@", 0, 100);
        var byName = await ListAsync("Člen 05", 0, 100);
        var nothing = await ListAsync("nikdo-takový", 0, 100);

        Assert.Multiple(() =>
        {
            Assert.That(byEmail.TotalCount, Is.EqualTo(1));
            Assert.That(byName.TotalCount, Is.EqualTo(1), "The display name is searchable, not just the email.");
            Assert.That(byName.Items[0].Email, Is.EqualTo("m5@d3.local"));
            Assert.That(nothing.TotalCount, Is.Zero);
            Assert.That(nothing.Items, Is.Empty);
        });
    }

    [Test]
    public async Task A_page_past_the_end_walks_back_to_the_last_real_page()
    {
        var beyond = await ListAsync(null, pageIndex: 99, pageSize: 5);

        Assert.Multiple(() =>
        {
            Assert.That(beyond.PageIndex, Is.EqualTo(1),
                "Asking past the end must land on the last page, not render an empty table.");
            Assert.That(beyond.Items, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task The_page_size_is_clamped_server_side()
    {
        var huge = await ListAsync(null, 0, 10_000);
        var zero = await ListAsync(null, 0, 0);

        Assert.Multiple(() =>
        {
            Assert.That(huge.PageSize, Is.EqualTo(100), "A caller must not be able to ask for the whole table.");
            Assert.That(zero.PageSize, Is.EqualTo(1), "Nor for nothing at all.");
        });
    }

    [Test]
    public async Task The_detail_counts_the_holders_it_no_longer_carries()
    {
        await using var scope = _provider.CreateAsyncScope();
        var detail = await CreateService(scope).GetAsync(_roleId);

        Assert.That(detail, Is.Not.Null);
        // The count is what the delete guard and the panel heading both read; it has to survive the
        // members leaving the detail.
        Assert.That(detail!.MemberCount, Is.EqualTo(7));
    }

    private async Task<PagedResult<RoleMember>> ListAsync(
        string? search,
        int pageIndex,
        int pageSize,
        Guid? roleId = null)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await CreateService(scope).ListMembersPageAsync(roleId ?? _roleId, search, pageIndex, pageSize);
    }

    private RoleAdminService CreateService(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>(),
        scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>(),
        new TestDbContextFactory(_options),
        new RolePermissionMaterializer(
            scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>(),
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>(),
            NullLogger<RolePermissionMaterializer>.Instance),
        new PassthroughLocalizer<AccountMessages>(),
        TimeProvider.System,
        NullLogger<RoleAdminService>.Instance);

    private async Task<Guid> CreateRoleAsync(string name)
    {
        await using var scope = _provider.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var role = new ApplicationRole(name);
        var created = await roleManager.CreateAsync(role);
        Assert.That(created.Succeeded, Is.True, string.Join("; ", created.Errors.Select(e => e.Description)));
        return role.Id;
    }

    private async Task SeedMembersAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await CreateUserAsync($"m{i}@d3.local", $"Člen {i:00}", RoleName);
        }
    }

    private async Task CreateUserAsync(string email, string displayName, string? role)
    {
        await using var scope = _provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            EmailConfirmed = true,
            Status = AccountStatus.Active,
        };

        var created = await userManager.CreateAsync(user, "Str0ng!Password");
        Assert.That(created.Succeeded, Is.True, string.Join("; ", created.Errors.Select(e => e.Description)));

        if (role is not null)
        {
            var assigned = await userManager.AddToRoleAsync(user, role);
            Assert.That(assigned.Succeeded, Is.True, string.Join("; ", assigned.Errors.Select(e => e.Description)));
        }
    }
}
