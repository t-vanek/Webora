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
/// Pins the release-reward preview to what ReleaseAsync then actually credits. Every newly released
/// day is rewarded from its advance notice; there is no monthly allowance or quota. Requires
/// ConnectionStrings__SqlServer (skipped without it).
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
    public async Task A_legacy_zero_allowance_does_not_limit_release_rewards()
    {
        var owner = Guid.NewGuid();
        await CreateOwnedSpotAsync("PV-01", owner, legacyAllowance: 0);
        var residents = CreateResidentService(BeforeCutoff);

        var preview = await residents.PreviewReleaseRewardAsync(owner, Tomorrow, Tomorrow.AddDays(3));
        Assert.That(preview, Is.GreaterThan(0),
            "The retired allowance column must not suppress any release reward.");

        Assert.That((await residents.ReleaseAsync(owner, Tomorrow, Tomorrow.AddDays(3))).Succeeded, Is.True);
        await using var dbContext = new D3ParkingDbContext(_options);
        var awarded = await dbContext.SpotReleases.Where(r => r.OwnerId == owner).SumAsync(r => r.AwardedPoints);
        Assert.That(awarded, Is.EqualTo(preview), "The promise and the payout must be the same number.");
    }

    [Test]
    public async Task The_preview_rewards_every_newly_released_day()
    {
        var owner = Guid.NewGuid();
        await CreateOwnedSpotAsync("PV-02", owner);
        var residents = CreateResidentService(BeforeCutoff);

        // Four future days in one month: every day is independent now that the quota is gone.
        var policy = new IncentivePolicy();
        var expected = Enumerable.Range(0, 4)
            .Sum(offset => policy.ComputeShareReward(
                policy.ResidentShareCutoff(Tomorrow.AddDays(offset), TimeZoneInfo.Utc), BeforeCutoff));
        var preview = await residents.PreviewReleaseRewardAsync(owner, Tomorrow, Tomorrow.AddDays(3));
        Assert.That(preview, Is.EqualTo(expected));

        Assert.That((await residents.ReleaseAsync(owner, Tomorrow, Tomorrow.AddDays(3))).Succeeded, Is.True);
        await using var dbContext = new D3ParkingDbContext(_options);
        var awarded = await dbContext.SpotReleases.Where(r => r.OwnerId == owner).SumAsync(r => r.AwardedPoints);
        Assert.That(awarded, Is.EqualTo(preview), "The promise and the payout must be the same number.");
        Assert.That(await dbContext.SpotReleases.CountAsync(r => r.OwnerId == owner && r.AwardedPoints > 0),
            Is.EqualTo(4), "Every newly shared day may carry its own reward.");
    }

    [Test]
    public async Task Already_released_days_are_skipped_and_invalid_ranges_preview_as_zero()
    {
        var owner = Guid.NewGuid();
        await CreateOwnedSpotAsync("PV-03", owner);
        var residents = CreateResidentService(BeforeCutoff);

        Assert.That((await residents.ReleaseAsync(owner, Tomorrow, Tomorrow)).Succeeded, Is.True,
            "The first day is released so the preview must skip it.");

        Assert.That(await residents.PreviewReleaseRewardAsync(owner, Tomorrow, Tomorrow.AddDays(1)), Is.GreaterThan(0),
            "The already released day is skipped, but the next new day is still rewarded.");
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
        await CreateOwnedSpotAsync("PV-04", owner);
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

    private async Task<Guid> CreateOwnedSpotAsync(string code, Guid ownerId, int? legacyAllowance = null)
    {
        await using var dbContext = new D3ParkingDbContext(_options);
        var spot = new ParkingSpot(code, ParkingSpotType.Standard);
        spot.AssignOwner(ownerId);
        if (legacyAllowance is not null)
            spot.SetShareAllowance(legacyAllowance.Value);
        dbContext.ParkingSpots.Add(spot);
        await dbContext.SaveChangesAsync();
        return spot.Id;
    }
}
