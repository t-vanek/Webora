using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Pins the resident usage plan: the days outside the plan are released ahead of time (and rewarded
/// exactly like a manual release), the days inside it stay held, today is never touched, and each
/// day is decided once — so a five-minute maintenance loop cannot re-share a day the resident took
/// back. Requires ConnectionStrings__SqlServer (skipped without it).
/// </summary>
[TestFixture]
[NonParallelizable]
public class ResidentUsagePlanTests
{
    private DbContextOptions<D3ParkingDbContext> _options = null!;

    // 2026-09-15 is a Tuesday; the shared FakeSiteSettings pins the site zone to UTC.
    private static readonly DateOnly Today = new(2026, 9, 15);
    private static readonly DateTimeOffset Morning = new(2026, 9, 15, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NextMorning = Morning.AddDays(1);
    private static readonly IncentivePolicy Policy = new();

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the usage plan tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_ResidentUsagePlanTests",
        };

        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        await using var dbContext = new D3ParkingDbContext(_options);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (_options is not null)
        {
            await using var dbContext = new D3ParkingDbContext(_options);
            await dbContext.Database.EnsureDeletedAsync();
        }
    }

    [Test]
    public async Task A_workday_plan_releases_the_weekends_in_the_horizon_and_keeps_the_workdays()
    {
        var owner = Guid.NewGuid();
        await CreateOwnedSpotAsync("UP-01", owner);
        var residents = CreateResidentService(Morning);
        Assert.That((await residents.SetUsagePlanAsync(owner, Weekday.Workdays, true)).Succeeded, Is.True);

        var released = await residents.ApplyDuePlanReleasesAsync();

        var expected = UnplannedDaysInHorizon(Weekday.Workdays);
        Assert.That(released, Is.EqualTo(expected.Count));
        Assert.That(await ReleasedDatesAsync(owner), Is.EqualTo(expected),
            "Exactly the days the plan does not claim may be shared — the workdays stay held.");
    }

    [Test]
    public async Task The_plan_is_authoritative_from_today_and_covers_the_whole_horizon()
    {
        var owner = Guid.NewGuid();
        var spotId = await CreateOwnedSpotAsync("UP-02", owner);
        var residents = CreateResidentService(Morning);
        // Nothing planned: every day in the horizon is a candidate, which is what makes today's
        // absence meaningful rather than an accident of the weekday.
        Assert.That((await residents.SetUsagePlanAsync(owner, Weekday.None, true)).Succeeded, Is.True);

        await residents.ApplyDuePlanReleasesAsync();

        var dates = await ReleasedDatesAsync(owner);
        Assert.That(dates, Does.Contain(Today),
            "An unplanned day is released by the schedule itself; no arrival-confirmation flow remains.");
        Assert.That(dates, Is.EqualTo(UnplannedDaysInHorizon(Weekday.None)));
        Assert.That(await AppliedThroughAsync(spotId), Is.EqualTo(Policy.ResidentPlanHorizonEnd(Today)));
    }

    [Test]
    public async Task Running_the_planner_again_the_same_day_releases_nothing_more()
    {
        var owner = Guid.NewGuid();
        await CreateOwnedSpotAsync("UP-03", owner);
        var residents = CreateResidentService(Morning);
        Assert.That((await residents.SetUsagePlanAsync(owner, Weekday.Workdays, true)).Succeeded, Is.True);

        var first = await residents.ApplyDuePlanReleasesAsync();
        Assert.That(first, Is.GreaterThan(0), "The first run has the whole horizon to release.");

        Assert.That(await residents.ApplyDuePlanReleasesAsync(), Is.Zero,
            "The maintenance loop runs every few minutes; the same days must not be re-decided.");
        Assert.That(await ReleasedDatesAsync(owner), Has.Count.EqualTo(first));
    }

    [Test]
    public async Task A_day_taken_back_by_hand_is_not_released_again_when_the_horizon_moves_on()
    {
        var owner = Guid.NewGuid();
        await CreateOwnedSpotAsync("UP-04", owner);
        var residents = CreateResidentService(Morning);
        Assert.That((await residents.SetUsagePlanAsync(owner, Weekday.Workdays, true)).Succeeded, Is.True);
        await residents.ApplyDuePlanReleasesAsync();

        var takenBack = (await ReleasedDatesAsync(owner))[0];
        Assert.That((await residents.ReclaimAsync(owner, takenBack, takenBack)).Succeeded, Is.True);

        // The next day the horizon has moved one day forward, so the planner runs again.
        var tomorrowsRun = CreateResidentService(NextMorning);
        await tomorrowsRun.ApplyDuePlanReleasesAsync();

        Assert.That(await ReleasedDatesAsync(owner), Does.Not.Contain(takenBack),
            "The plan is a standing instruction, not a veto on the resident's own right of first refusal.");
    }

    [Test]
    public async Task The_horizon_moving_forward_releases_the_newly_visible_day_only()
    {
        var owner = Guid.NewGuid();
        await CreateOwnedSpotAsync("UP-05", owner);
        Assert.That((await CreateResidentService(Morning).SetUsagePlanAsync(owner, Weekday.None, true)).Succeeded, Is.True);
        await CreateResidentService(Morning).ApplyDuePlanReleasesAsync();
        var beforeCount = (await ReleasedDatesAsync(owner)).Count;

        var released = await CreateResidentService(NextMorning).ApplyDuePlanReleasesAsync();

        Assert.That(released, Is.EqualTo(1), "One day came into view, so one day is released.");
        Assert.That(await ReleasedDatesAsync(owner), Has.Count.EqualTo(beforeCount + 1));
        Assert.That((await ReleasedDatesAsync(owner))[^1],
            Is.EqualTo(Policy.ResidentPlanHorizonEnd(Today.AddDays(1))));
    }

    [Test]
    public async Task Every_planned_release_can_be_rewarded_without_a_monthly_limit()
    {
        var owner = Guid.NewGuid();
        await CreateOwnedSpotAsync("UP-06", owner);
        var residents = CreateResidentService(Morning);
        Assert.That((await residents.SetUsagePlanAsync(owner, Weekday.Workdays, true)).Succeeded, Is.True);

        var released = await residents.ApplyDuePlanReleasesAsync();

        await using var dbContext = new D3ParkingDbContext(_options);
        var rewarded = await dbContext.SpotReleases.CountAsync(r => r.OwnerId == owner && r.AwardedPoints > 0);
        Assert.That(rewarded, Is.EqualTo(released),
            "Every day released with advance notice is independently rewardable.");
        var awarded = await dbContext.SpotReleases.Where(r => r.OwnerId == owner).SumAsync(r => r.AwardedPoints);
        var score = await dbContext.ParkerScores.FindAsync(owner);
        Assert.That(score!.Points, Is.EqualTo(awarded), "Every awarded point must land on the resident's score.");
        Assert.That(await dbContext.PointsLedgerEntries.CountAsync(e => e.UserId == owner), Is.EqualTo(rewarded),
            "A rewarded planned day is ledgered exactly like a hand-picked one.");
    }

    [Test]
    public async Task A_plan_with_auto_release_off_shares_nothing()
    {
        var owner = Guid.NewGuid();
        var spotId = await CreateOwnedSpotAsync("UP-07", owner);
        var residents = CreateResidentService(Morning);
        Assert.That((await residents.SetUsagePlanAsync(owner, Weekday.Workdays, false)).Succeeded, Is.True);

        Assert.That(await residents.ApplyDuePlanReleasesAsync(), Is.Zero);
        Assert.That(await ReleasedDatesAsync(owner), Is.Empty,
            "Advance release is opt-in; a plan alone must not start sharing the spot.");
        Assert.That(await AppliedThroughAsync(spotId), Is.Null,
            "An unplanned spot is never watermarked, so switching the plan on later still covers the horizon.");
    }

    [Test]
    public async Task An_inactive_spot_is_left_alone()
    {
        var owner = Guid.NewGuid();
        var spotId = await CreateOwnedSpotAsync("UP-08", owner);
        var residents = CreateResidentService(Morning);
        Assert.That((await residents.SetUsagePlanAsync(owner, Weekday.None, true)).Succeeded, Is.True);

        await using (var dbContext = new D3ParkingDbContext(_options))
        {
            var spot = await dbContext.ParkingSpots.FindAsync(spotId);
            spot!.Deactivate();
            await dbContext.SaveChangesAsync();
        }

        Assert.That(await residents.ApplyDuePlanReleasesAsync(), Is.Zero,
            "A deactivated spot never reaches the pool, so release rows for it would be a lie.");
    }

    [Test]
    public async Task Saving_a_plan_needs_an_owned_spot()
    {
        var result = await CreateResidentService(Morning).SetUsagePlanAsync(Guid.NewGuid(), Weekday.Workdays, true);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Errors, Is.EqualTo(new[] { "Parking_Error_NoOwnedSpot" }));
    }

    /// <summary>The days a plan releases: today through the horizon, minus the planned ones.</summary>
    private static List<DateOnly> UnplannedDaysInHorizon(Weekday planned)
    {
        var days = new List<DateOnly>();
        for (var date = Today; date <= Policy.ResidentPlanHorizonEnd(Today); date = date.AddDays(1))
        {
            if (!planned.Includes(date))
            {
                days.Add(date);
            }
        }

        return days;
    }

    private async Task<List<DateOnly>> ReleasedDatesAsync(Guid ownerId)
    {
        await using var dbContext = new D3ParkingDbContext(_options);
        return await dbContext.SpotReleases
            .Where(r => r.OwnerId == ownerId)
            .OrderBy(r => r.Date)
            .Select(r => r.Date)
            .ToListAsync();
    }

    private async Task<DateOnly?> AppliedThroughAsync(Guid spotId)
    {
        await using var dbContext = new D3ParkingDbContext(_options);
        return await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.Id == spotId)
            .Select(s => s.PlanAppliedThrough)
            .FirstAsync();
    }

    private ResidentSpotService CreateResidentService(DateTimeOffset now) =>
        new(new TestDbContextFactory(_options),
            new FakeParkingSettings(),
            new FakeSiteSettings(),
            new FixedTimeProvider(now),
            new NullNotificationService(),
            new PassthroughLocalizer<ParkingMessages>());

    private async Task<Guid> CreateOwnedSpotAsync(string code, Guid ownerId)
    {
        await using var dbContext = new D3ParkingDbContext(_options);
        var spot = new ParkingSpot(code, ParkingSpotType.Standard);
        spot.AssignOwner(ownerId);
        dbContext.ParkingSpots.Add(spot);
        await dbContext.SaveChangesAsync();
        return spot.Id;
    }
}
