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
/// Pins the release-reward preview to what ReleaseAsync then actually credits: the same monthly
/// share-allowance cap and the same skipping of already-released days. Guards against the UI
/// promising points (say "+40") that the release pays out as zero — an allowance-0 owner or an
/// exhausted month must be shown 0 up front. Requires ConnectionStrings__SqlServer (skipped without it).
/// </summary>
[TestFixture]
[NonParallelizable]
public class ReleaseRewardPreviewTests
{
    private DbContextOptions<D3ParkingDbContext> _options = null!;

    // The tests pick their own "now"; the shared FakeSiteSettings pins the site zone to UTC.
    private static readonly DateOnly Today = new(2026, 9, 15);
    private static readonly DateOnly Tomorrow = Today.AddDays(1);
    private static readonly DateTimeOffset BeforeCutoff = new(2026, 9, 15, 6, 0, 0, TimeSpan.Zero);

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; the release preview tests need a real SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_ReleaseRewardPreviewTests",
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
    public async Task A_zero_allowance_owner_is_promised_zero_and_the_release_credits_zero()
    {
        var owner = Guid.NewGuid();
        await CreateOwnedSpotAsync("PV-01", owner, allowance: 0);
        var residents = CreateResidentService(BeforeCutoff);

        var preview = await residents.PreviewReleaseRewardAsync(owner, Tomorrow, Tomorrow.AddDays(3));
        Assert.That(preview, Is.Zero,
            "With a zero share allowance no day can be rewarded, so no points may be promised.");

        Assert.That((await residents.ReleaseAsync(owner, Tomorrow, Tomorrow.AddDays(3))).Succeeded, Is.True);
        await using var dbContext = new D3ParkingDbContext(_options);
        var awarded = await dbContext.SpotReleases.Where(r => r.OwnerId == owner).SumAsync(r => r.AwardedPoints);
        Assert.That(awarded, Is.EqualTo(preview), "The promise and the payout must be the same number.");
    }

    [Test]
    public async Task The_preview_rewards_only_the_days_left_in_the_monthly_quota()
    {
        var owner = Guid.NewGuid();
        await CreateOwnedSpotAsync("PV-02", owner, allowance: 2);
        var residents = CreateResidentService(BeforeCutoff);

        // Four future days in one month against a quota of two: only the first two count.
        var policy = new IncentivePolicy();
        var expected = policy.ComputeShareReward(policy.ResidentShareCutoff(Tomorrow, TimeZoneInfo.Utc), BeforeCutoff, 2)
            + policy.ComputeShareReward(policy.ResidentShareCutoff(Tomorrow.AddDays(1), TimeZoneInfo.Utc), BeforeCutoff, 2);
        var preview = await residents.PreviewReleaseRewardAsync(owner, Tomorrow, Tomorrow.AddDays(3));
        Assert.That(preview, Is.EqualTo(expected));

        Assert.That((await residents.ReleaseAsync(owner, Tomorrow, Tomorrow.AddDays(3))).Succeeded, Is.True);
        await using var dbContext = new D3ParkingDbContext(_options);
        var awarded = await dbContext.SpotReleases.Where(r => r.OwnerId == owner).SumAsync(r => r.AwardedPoints);
        Assert.That(awarded, Is.EqualTo(preview), "The promise and the payout must be the same number.");
        Assert.That(await dbContext.SpotReleases.CountAsync(r => r.OwnerId == owner && r.AwardedPoints > 0),
            Is.EqualTo(2), "Only the quota's worth of days may carry a reward.");
    }

    [Test]
    public async Task An_exhausted_quota_released_days_and_rejected_ranges_all_preview_as_zero()
    {
        var owner = Guid.NewGuid();
        await CreateOwnedSpotAsync("PV-03", owner, allowance: 1);
        var residents = CreateResidentService(BeforeCutoff);

        Assert.That((await residents.ReleaseAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True,
            "The single-day release must succeed to use up the month's quota.");

        Assert.That(await residents.PreviewReleaseRewardAsync(owner, Tomorrow, Tomorrow.AddDays(1)), Is.Zero,
            "An already released day is skipped and the quota is spent — nothing left to promise.");
        Assert.That(await residents.PreviewReleaseRewardAsync(owner, Tomorrow.AddDays(1), Tomorrow), Is.Zero,
            "An inverted range would be rejected by the release, so it previews as zero.");
        Assert.That(await residents.PreviewReleaseRewardAsync(owner, Today.AddDays(-1), Today), Is.Zero,
            "A range starting in the past would be rejected by the release, so it previews as zero.");
        Assert.That(await residents.PreviewReleaseRewardAsync(Guid.NewGuid(), Tomorrow, Tomorrow), Is.Zero,
            "A caller without an owned spot has nothing to release.");
    }

    [Test]
    public async Task Releasing_today_zeroes_the_owned_spot_cards_potential()
    {
        var owner = Guid.NewGuid();
        await CreateOwnedSpotAsync("PV-04", owner, allowance: 5);
        var residents = CreateResidentService(BeforeCutoff);

        var before = (await residents.GetMyOwnedSpotAsync(owner))!.PotentialReleasePointsToday;
        Assert.That(before, Is.GreaterThan(0), "Before the cutoff an unreleased day has a reward to offer.");

        Assert.That((await residents.ReleaseAsync(owner, Today, Today)).Succeeded, Is.True);

        var after = (await residents.GetMyOwnedSpotAsync(owner))!.PotentialReleasePointsToday;
        Assert.That(after, Is.Zero,
            "Releasing today again would credit nothing — the card must not promise otherwise.");
    }

    private ResidentSpotService CreateResidentService(DateTimeOffset now) =>
        new(new TestDbContextFactory(_options),
            new FakeParkingSettings(),
            new FakeSiteSettings(),
            new FixedTimeProvider(now),
            new NullNotificationService(),
            new PassthroughLocalizer<ParkingMessages>());

    private async Task<Guid> CreateOwnedSpotAsync(string code, Guid ownerId, int allowance)
    {
        await using var dbContext = new D3ParkingDbContext(_options);
        var spot = new ParkingSpot(code, ParkingSpotType.Standard);
        spot.AssignOwner(ownerId);
        spot.SetShareAllowance(allowance);
        dbContext.ParkingSpots.Add(spot);
        await dbContext.SaveChangesAsync();
        return spot.Id;
    }
}
