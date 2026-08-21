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
/// Verifies that planning achievements are positive, outcome-based and announced through both the
/// in-app notification path and its requested email mirror.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class PositiveAchievementTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 8, 0, 0, TimeSpan.Zero);

    private DbContextOptions<D3ParkingDbContext> _options = null!;
    private TestDbContextFactory _factory = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; achievement tests need SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = "D3Parking_PositiveAchievementTests",
        };
        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .Options;

        await using var dbContext = new D3ParkingDbContext(_options);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();
        _factory = new TestDbContextFactory(_options);
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
    public async Task First_plan_unlocks_a_permanent_achievement_and_requests_email()
    {
        var userId = Guid.NewGuid();
        var spot = new ParkingSpot("PA-01", ParkingSpotType.Standard);
        await SeedAsync(db => db.ParkingSpots.Add(spot));
        var notifications = new RecordingNotificationService();
        var service = CreateService(notifications);

        var result = await service.ReserveAsync(userId, spot.Id, Now.AddDays(1), Now.AddDays(1).AddHours(8));

        Assert.That(result.Succeeded, Is.True);
        await using var db = new D3ParkingDbContext(_options);
        Assert.That(await db.UserBadges.AnyAsync(b => b.UserId == userId
            && b.Badge == ParkingBadge.PlanningStarted), Is.True);
        Assert.That(notifications.Sent, Does.Contain((userId, "Parking_Notify_Achievement_Title")));
        Assert.That(notifications.EmailRequested, Does.Contain((userId, "Parking_Notify_Achievement_Title")));
    }

    [Test]
    public async Task Colleague_using_a_released_space_thanks_the_releaser_exactly_once()
    {
        var releaserId = Guid.NewGuid();
        var colleagueId = Guid.NewGuid();
        var spot = new ParkingSpot("PA-02", ParkingSpotType.Standard);
        var released = new Reservation(
            spot.Id, releaserId, Now.AddDays(2), Now.AddDays(2).AddHours(8), false, Now.AddDays(-1));
        released.Release(Now);
        await SeedAsync(db =>
        {
            db.ParkingSpots.Add(spot);
            db.Reservations.Add(released);
        });
        var notifications = new RecordingNotificationService();
        var service = CreateService(notifications);

        var result = await service.ReserveAsync(
            colleagueId, spot.Id, released.StartUtc, released.EndUtc);

        Assert.That(result.Succeeded, Is.True);
        await using var db = new D3ParkingDbContext(_options);
        Assert.That(await db.ParkingContributions.CountAsync(c => c.UserId == releaserId
            && c.Kind == ParkingContributionKind.UsefulRelease
            && c.SourceId == released.Id), Is.EqualTo(1));
        Assert.That(await db.UserBadges.AnyAsync(b => b.UserId == releaserId
            && b.Badge == ParkingBadge.PlaceForColleague), Is.True);
        Assert.That(notifications.EmailRequested.Count(n => n.UserId == releaserId
            && n.Title == "Parking_Notify_Achievement_Title"), Is.EqualTo(1));
    }

    [Test]
    public async Task Claimed_queue_offer_celebrates_the_person_who_freed_the_space()
    {
        var releaserId = Guid.NewGuid();
        var colleagueId = Guid.NewGuid();
        var spot = new ParkingSpot("PA-03", ParkingSpotType.Standard);
        var released = new Reservation(
            spot.Id, releaserId, Now.AddDays(3), Now.AddDays(3).AddHours(8), false, Now.AddDays(-1));
        released.Release(Now);
        var queue = new QueueEntry(colleagueId, released.StartUtc, released.EndUtc, Now.AddHours(-1));
        queue.Offer(spot.Id, Now.AddMinutes(30));
        await SeedAsync(db =>
        {
            db.ParkingSpots.Add(spot);
            db.Reservations.Add(released);
            db.QueueEntries.Add(queue);
        });
        var notifications = new RecordingNotificationService();
        var service = CreateService(notifications);

        var result = await service.ClaimQueueOfferAsync(colleagueId, queue.Id);

        Assert.That(result.Succeeded, Is.True);
        await using var db = new D3ParkingDbContext(_options);
        Assert.That(await db.ParkingContributions.AnyAsync(c => c.UserId == releaserId
            && c.Kind == ParkingContributionKind.QueueHelped
            && c.SourceId == released.Id), Is.True);
        Assert.That(await db.UserBadges.AnyAsync(b => b.UserId == releaserId
            && b.Badge == ParkingBadge.QueueHelper), Is.True);
        Assert.That(notifications.EmailRequested.Count(n => n.UserId == releaserId
            && n.Title == "Parking_Notify_Achievement_Title"), Is.EqualTo(2),
            "The first useful release and first queue help are two distinct reasons to say thank you.");
    }

    [Test]
    public async Task Used_resident_day_celebrates_the_resident_without_upfront_points()
    {
        var residentId = Guid.NewGuid();
        var colleagueId = Guid.NewGuid();
        var spot = new ParkingSpot("PA-04", ParkingSpotType.Standard);
        spot.AssignOwner(residentId);
        var date = DateOnly.FromDateTime(Now.AddDays(4).UtcDateTime);
        var release = new SpotRelease(spot.Id, residentId, date, Now, awardedPoints: 0);
        await SeedAsync(db =>
        {
            db.ParkingSpots.Add(spot);
            db.SpotReleases.Add(release);
        });
        var notifications = new RecordingNotificationService();
        var service = CreateService(notifications);

        var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero);
        var result = await service.ReserveAsync(colleagueId, spot.Id, start, start.AddHours(8));

        Assert.That(result.Succeeded, Is.True);
        await using var db = new D3ParkingDbContext(_options);
        Assert.That(await db.ParkingContributions.AnyAsync(c => c.UserId == residentId
            && c.Kind == ParkingContributionKind.ResidentShareUsed), Is.True);
        Assert.That(await db.UserBadges.AnyAsync(b => b.UserId == residentId
            && b.Badge == ParkingBadge.SharesWhenPossible), Is.True);
        Assert.That(notifications.EmailRequested, Does.Contain((residentId, "Parking_Notify_Achievement_Title")));
    }

    [Test]
    public void Contribution_model_rejects_self_praise()
    {
        var userId = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new ParkingContribution(
            userId, ParkingContributionKind.UsefulRelease, Guid.NewGuid(), userId, Now));
    }

    private ReservationService CreateService(RecordingNotificationService notifications) =>
        new(_factory, new FakeParkingSettings(), new FakeSiteSettings(), new FixedTimeProvider(Now),
            notifications, new PassthroughLocalizer<ParkingMessages>());

    private async Task SeedAsync(Action<D3ParkingDbContext> seed)
    {
        await using var db = new D3ParkingDbContext(_options);
        seed(db);
        await db.SaveChangesAsync();
    }
}
