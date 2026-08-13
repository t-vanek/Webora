using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using D3Parking.Application.Accounts;
using D3Parking.Application.Administration;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Administration;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Pins the paged read behind the account administration list. The unpaged read stops at 500 accounts
/// and says nothing about it, so on a directory any larger the tail had no way to be reached — and a
/// full page of results looks the same whether it is the whole answer or the first slice of one. The
/// capped read stays for the pickers that genuinely want the whole directory.
/// Requires ConnectionStrings__SqlServer (skipped without it).
/// </summary>
[TestFixture]
[NonParallelizable]
public class UserListPagingTests
{
    private ServiceProvider _provider = null!;
    private DbContextOptions<D3ParkingDbContext> _options = null!;
    private UserAdminService _users = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the user paging tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_UserListPagingTests",
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

        // Twelve accounts whose emails sort predictably, so a page can be checked against a slice.
        await SeedAsync(12);

        _users = CreateService();
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
    public async Task A_page_carries_the_total_and_walks_the_directory_by_email()
    {
        var first = await _users.ListPageAsync(null, pageIndex: 0, pageSize: 5);
        var second = await _users.ListPageAsync(null, pageIndex: 1, pageSize: 5);
        var last = await _users.ListPageAsync(null, pageIndex: 2, pageSize: 5);

        Assert.Multiple(() =>
        {
            Assert.That(first.TotalCount, Is.EqualTo(12), "The total is what lets the pager exist at all.");
            Assert.That(first.Items, Has.Count.EqualTo(5));
            Assert.That(last.Items, Has.Count.EqualTo(2), "The tail page is short, not padded.");
        });

        // Emails were seeded zero-padded, so the expected slice is a plain string sort.
        Assert.That(first.Items.Select(u => u.Email),
            Is.EqualTo(new[] { "u00@d3.local", "u01@d3.local", "u02@d3.local", "u03@d3.local", "u04@d3.local" }));
        Assert.That(second.Items[0].Email, Is.EqualTo("u05@d3.local"), "The pages join up where the previous one stopped.");

        var seen = first.Items.Concat(second.Items).Concat(last.Items).Select(u => u.Id).ToList();
        Assert.That(seen.Distinct().Count(), Is.EqualTo(12), "Every account appears exactly once across the pages.");
    }

    [Test]
    public async Task The_search_narrows_the_total_the_pager_is_built_from()
    {
        var byEmail = await _users.ListPageAsync("u1", 0, 100);
        var byName = await _users.ListPageAsync("Uživatel 03", 0, 100);
        var nothing = await _users.ListPageAsync("nikdo-takový", 0, 100);

        Assert.Multiple(() =>
        {
            // u10, u11 — the total must count the matches, not the directory.
            Assert.That(byEmail.TotalCount, Is.EqualTo(2));
            Assert.That(byName.TotalCount, Is.EqualTo(1), "The display name is searchable, not just the email.");
            Assert.That(byName.Items[0].Email, Is.EqualTo("u03@d3.local"));
            Assert.That(nothing.TotalCount, Is.Zero);
            Assert.That(nothing.Items, Is.Empty);
        });
    }

    [Test]
    public async Task A_page_past_the_end_walks_back_to_the_last_real_page()
    {
        var beyond = await _users.ListPageAsync(null, pageIndex: 99, pageSize: 5);

        Assert.That(beyond.PageIndex, Is.EqualTo(2),
            "Asking past the end must land on the last page, not render an empty table.");
        Assert.That(beyond.Items, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task A_search_that_matches_nothing_is_page_one()
    {
        var page = await _users.ListPageAsync("nikdo-takový", pageIndex: 4, pageSize: 5);

        Assert.Multiple(() =>
        {
            Assert.That(page.TotalCount, Is.Zero);
            Assert.That(page.PageIndex, Is.Zero, "Nothing to page through means page one.");
        });
    }

    [Test]
    public async Task The_page_size_is_clamped_server_side()
    {
        var huge = await _users.ListPageAsync(null, 0, 10_000);
        var zero = await _users.ListPageAsync(null, 0, 0);

        Assert.Multiple(() =>
        {
            Assert.That(huge.PageSize, Is.EqualTo(100), "A caller must not be able to ask for the whole table.");
            Assert.That(zero.PageSize, Is.EqualTo(1), "Nor for nothing at all.");
        });
    }

    [Test]
    public async Task A_page_carries_the_same_row_the_capped_list_does()
    {
        // The two reads project the same shape; a page must not quietly lose the roles column.
        var listed = (await _users.ListAsync("u07")).Single();
        var paged = (await _users.ListPageAsync("u07", 0, 25)).Items.Single();

        Assert.Multiple(() =>
        {
            Assert.That(paged.Id, Is.EqualTo(listed.Id));
            Assert.That(paged.Email, Is.EqualTo(listed.Email));
            Assert.That(paged.DisplayName, Is.EqualTo(listed.DisplayName));
            Assert.That(paged.Status, Is.EqualTo(listed.Status));
            Assert.That(paged.Roles, Is.EqualTo(listed.Roles));
            Assert.That(paged.EmailConfirmed, Is.EqualTo(listed.EmailConfirmed));
            Assert.That(paged.ExternalProvider, Is.EqualTo(listed.ExternalProvider));
        });
    }

    [Test]
    public async Task Structured_filters_are_applied_before_the_page_is_cut()
    {
        var active = await _users.ListFilteredPageAsync(
            new UserListQuery(Status: AccountStatus.Active), 0, 5);
        var employee = await _users.ListFilteredPageAsync(
            new UserListQuery(Role: Roles.Employee), 0, 5);
        var federated = await _users.ListFilteredPageAsync(
            new UserListQuery(Source: UserAccountSourceFilter.Federated), 0, 5);
        var summary = await _users.GetSummaryAsync();

        Assert.Multiple(() =>
        {
            Assert.That(active.TotalCount, Is.EqualTo(8));
            Assert.That(active.Items, Has.All.Property(nameof(UserSummary.Status)).EqualTo(AccountStatus.Active));
            Assert.That(employee.TotalCount, Is.EqualTo(1));
            Assert.That(employee.Items.Single().Email, Is.EqualTo("u00@d3.local"));
            Assert.That(federated.TotalCount, Is.EqualTo(1));
            Assert.That(federated.Items.Single().ExternalProvider, Is.EqualTo("Entra ID"));
            Assert.That(summary, Is.EqualTo(new UserDirectorySummary(12, 8, 3, 1)));
        });
    }

    private UserAdminService CreateService()
    {
        var scope = _provider.CreateScope();
        return new UserAdminService(
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>(),
            new TestDbContextFactory(_options),
            new PassthroughLocalizer<AccountMessages>(),
            TimeProvider.System,
            NullLogger<UserAdminService>.Instance);
    }

    private async Task SeedAsync(int count)
    {
        await using var scope = _provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var roleCreated = await roleManager.CreateAsync(new ApplicationRole(Roles.Employee));
        Assert.That(roleCreated.Succeeded, Is.True, string.Join("; ", roleCreated.Errors.Select(e => e.Description)));

        for (var i = 0; i < count; i++)
        {
            var user = new ApplicationUser
            {
                UserName = $"u{i:00}@d3.local",
                Email = $"u{i:00}@d3.local",
                DisplayName = $"Uživatel {i:00}",
                EmailConfirmed = true,
                Status = i < 8 ? AccountStatus.Active : i == 8 ? AccountStatus.Blocked : AccountStatus.PendingActivation,
                ExternalProvider = i == 9 ? "Entra ID" : null,
            };

            var created = await userManager.CreateAsync(user, "Str0ng!Password");
            Assert.That(created.Succeeded, Is.True, string.Join("; ", created.Errors.Select(e => e.Description)));
            if (i == 0)
            {
                var assigned = await userManager.AddToRoleAsync(user, Roles.Employee);
                Assert.That(assigned.Succeeded, Is.True, string.Join("; ", assigned.Errors.Select(e => e.Description)));
            }
        }
    }
}
