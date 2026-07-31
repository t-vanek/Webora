using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using D3Parking.Application.Accounts;
using D3Parking.Application.Mapping;
using D3Parking.Domain.Accounts;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Accounts;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Pins the paged reads of the account audit trail — the cross-account one behind /admin/audit and
/// the per-account one on the user detail screen. Both used to hand the UI a flat list: the first
/// stopped at 200 rows without saying so, the second had no ceiling at all. On an append-only trail
/// a silent cap is the worst possible failure — the questions the trail exists for are asked of old
/// rows, and a full page of results looks identical whether or not it is the whole answer.
/// Requires ConnectionStrings__SqlServer (skipped without it).
/// </summary>
[TestFixture]
[NonParallelizable]
public class AuditPagingTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

    private ServiceProvider _provider = null!;
    private DbContextOptions<D3ParkingDbContext> _options = null!;
    private AuditService _audit = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the audit paging tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_AuditPagingTests",
        };

        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            await dbContext.Database.EnsureDeletedAsync();
            await dbContext.Database.EnsureCreatedAsync();
        }

        // The per-account read lives on AccountService, which needs the real Identity stack to be
        // constructed at all — the same container the account security fixture builds.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<D3ParkingDbContext>(o => o.UseSqlServer(builder.ConnectionString));
        services.AddDataProtection();
        services.AddIdentityCore<ApplicationUser>(o => o.User.RequireUniqueEmail = true)
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<D3ParkingDbContext>()
            .AddDefaultTokenProviders();
        _provider = services.BuildServiceProvider();

        _audit = new AuditService(new TestDbContextFactory(_options));
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

    [SetUp]
    public async Task ResetAsync()
    {
        if (_options is null)
        {
            return;
        }

        await using var dbContext = new D3ParkingDbContext(_options);
        dbContext.AccountAuditEvents.RemoveRange(dbContext.AccountAuditEvents);
        await dbContext.SaveChangesAsync();
    }

    [Test]
    public async Task A_page_of_the_trail_carries_the_total_and_walks_newest_first()
    {
        var user = await SeedUserAsync();
        await SeedEventsAsync(user, count: 25);

        var first = await _audit.SearchAsync(null, null, pageIndex: 0, pageSize: 10);
        var last = await _audit.SearchAsync(null, null, pageIndex: 2, pageSize: 10);

        Assert.Multiple(() =>
        {
            Assert.That(first.TotalCount, Is.EqualTo(25), "The total is what lets the pager exist at all.");
            Assert.That(first.Items, Has.Count.EqualTo(10));
            Assert.That(last.Items, Has.Count.EqualTo(5), "The tail page is short, not padded.");
            Assert.That(first.Items[0].OccurredAtUtc, Is.GreaterThan(first.Items[^1].OccurredAtUtc));
        });

        var second = await _audit.SearchAsync(null, null, pageIndex: 1, pageSize: 10);
        var seen = first.Items.Concat(second.Items).Concat(last.Items).Select(e => e.Id).ToList();
        Assert.That(seen.Distinct().Count(), Is.EqualTo(25), "Every event appears exactly once across the pages.");
    }

    /// <summary>
    /// One operation writes several events on the same tick, and ordering by the instant alone is
    /// then not a total order — SQL Server is free to return a tie in one order for a plain read and
    /// another for an OFFSET/FETCH one, which loses rows off one page and repeats them on the next.
    /// The order the tie-break imposes cannot be asserted from here (ORDER BY on uniqueidentifier
    /// uses SQL Server's own byte order, which is not Guid.CompareTo), so what is pinned is the
    /// property that matters: walking the trail in pages must agree with reading it whole.
    /// </summary>
    [Test]
    public async Task Paging_a_tie_on_the_instant_agrees_with_reading_it_whole()
    {
        var user = await SeedUserAsync();
        await SeedEventsAsync(user, count: 20, sameInstant: true);

        var whole = await _audit.SearchAsync(null, null, pageIndex: 0, pageSize: 20);
        var walked = new List<AuditLogEntry>();
        for (var page = 0; page < 4; page++)
        {
            walked.AddRange((await _audit.SearchAsync(null, null, page, pageSize: 5)).Items);
        }

        Assert.Multiple(() =>
        {
            Assert.That(walked.Select(e => e.Id), Is.EqualTo(whole.Items.Select(e => e.Id)),
                "Four pages of five must be the same twenty rows, in the same order, as one page of twenty.");
            Assert.That(walked.Select(e => e.Id).Distinct().Count(), Is.EqualTo(20),
                "No row may be lost off one page and repeated on the next.");
        });
    }

    [Test]
    public async Task The_filters_narrow_the_total_the_pager_is_built_from()
    {
        var user = await SeedUserAsync();
        await SeedEventsAsync(user, count: 12, type: AccountAuditEventType.Blocked);
        await SeedEventsAsync(user, count: 5, type: AccountAuditEventType.Unblocked, detail: "kvůli auditu");

        var blocked = await _audit.SearchAsync(null, AccountAuditEventType.Blocked, 0, 100);
        var byDetail = await _audit.SearchAsync("kvůli", null, 0, 100);

        Assert.Multiple(() =>
        {
            Assert.That(blocked.TotalCount, Is.EqualTo(12), "The total must count the matches, not the trail.");
            Assert.That(blocked.Items.Select(e => e.Type), Has.All.EqualTo(AccountAuditEventType.Blocked));
            Assert.That(byDetail.TotalCount, Is.EqualTo(5));
        });
    }

    [Test]
    public async Task A_page_past_the_end_walks_back_and_an_empty_trail_is_page_one()
    {
        var user = await SeedUserAsync();
        await SeedEventsAsync(user, count: 12);

        var beyond = await _audit.SearchAsync(null, null, pageIndex: 99, pageSize: 10);
        var nothing = await _audit.SearchAsync("nikdy-nenapsáno", null, pageIndex: 3, pageSize: 10);

        Assert.Multiple(() =>
        {
            Assert.That(beyond.PageIndex, Is.EqualTo(1), "Asking past the end lands on the last real page.");
            Assert.That(beyond.Items, Has.Count.EqualTo(2));
            Assert.That(nothing.TotalCount, Is.Zero);
            Assert.That(nothing.PageIndex, Is.Zero, "Nothing to page through means page one.");
        });
    }

    [Test]
    public async Task The_page_size_is_clamped_server_side()
    {
        var user = await SeedUserAsync();
        await SeedEventsAsync(user, count: 5);

        var huge = await _audit.SearchAsync(null, null, 0, 10_000);
        var zero = await _audit.SearchAsync(null, null, 0, 0);

        Assert.Multiple(() =>
        {
            Assert.That(huge.PageSize, Is.EqualTo(100), "A caller must not be able to ask for the whole table.");
            Assert.That(zero.PageSize, Is.EqualTo(1), "Nor for nothing at all.");
        });
    }

    [Test]
    public async Task One_accounts_trail_is_paged_and_holds_only_that_account()
    {
        var mine = await SeedUserAsync();
        var theirs = await SeedUserAsync();
        await SeedEventsAsync(mine, count: 30);
        await SeedEventsAsync(theirs, count: 7);

        await using var scope = _provider.CreateAsyncScope();
        var accounts = CreateAccountService(scope);

        var first = await accounts.GetAuditTrailAsync(mine, pageIndex: 0, pageSize: 20);
        var second = await accounts.GetAuditTrailAsync(mine, pageIndex: 1, pageSize: 20);

        Assert.Multiple(() =>
        {
            Assert.That(first.TotalCount, Is.EqualTo(30), "The other account's events are not this account's history.");
            Assert.That(first.Items, Has.Count.EqualTo(20));
            Assert.That(second.Items, Has.Count.EqualTo(10));
            Assert.That(first.Items.Concat(second.Items).Select(e => e.UserId), Has.All.EqualTo(mine));
        });

        Assert.That(first.Items[0].OccurredAtUtc, Is.GreaterThan(first.Items[^1].OccurredAtUtc),
            "Newest first — the screen reads the recent past, not the sign-up.");
    }

    [Test]
    public async Task An_account_with_no_history_gets_an_empty_first_page()
    {
        var user = await SeedUserAsync();

        await using var scope = _provider.CreateAsyncScope();
        var page = await CreateAccountService(scope).GetAuditTrailAsync(user, pageIndex: 4, pageSize: 20);

        Assert.Multiple(() =>
        {
            Assert.That(page.TotalCount, Is.Zero);
            Assert.That(page.Items, Is.Empty);
            Assert.That(page.PageIndex, Is.Zero);
        });
    }

    private async Task<Guid> SeedUserAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            UserName = $"audit-{Guid.NewGuid():N}@d3parking.local",
            Email = $"audit-{Guid.NewGuid():N}@d3parking.local",
            EmailConfirmed = true,
        };

        var created = await userManager.CreateAsync(user, "Str0ng!Password");
        Assert.That(created.Succeeded, Is.True, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user.Id;
    }

    /// <summary>Audit events for one account, one per hour going back — or all on one tick.</summary>
    private async Task SeedEventsAsync(
        Guid userId,
        int count,
        AccountAuditEventType type = AccountAuditEventType.Blocked,
        string? detail = null,
        bool sameInstant = false)
    {
        await using var dbContext = new D3ParkingDbContext(_options);
        for (var i = 0; i < count; i++)
        {
            var when = sameInstant ? Now : Now.AddHours(-i);
            dbContext.AccountAuditEvents.Add(new AccountAuditEvent(userId, type, "admin:test", detail, when));
        }

        await dbContext.SaveChangesAsync();
    }

    private AccountService CreateAccountService(AsyncServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
        scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>(),
        scope.ServiceProvider.GetRequiredService<D3ParkingDbContext>(),
        new TestDbContextFactory(_options),
        new NullEmailSender(),
        new AccountAuditMapper(),
        new NullNotificationService(),
        new NullFleetService(),
        new FakeSiteSettings(),
        new PassthroughLocalizer<AccountMessages>(),
        Options.Create(new AccountOptions()),
        new FixedTimeProvider(Now),
        NullLogger<AccountService>.Instance);
}
