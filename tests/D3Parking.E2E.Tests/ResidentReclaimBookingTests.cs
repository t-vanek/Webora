using D3Parking.Domain.Common;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture]
[NonParallelizable]
public class ResidentReclaimBookingTests : AdminTest
{
    private DbContextOptions<D3ParkingDbContext>? _options;
    private Guid _ownSpotId;
    private Guid _alternativeSpotId;
    private Guid _reservationId;
    private DateOnly _day;

    [OneTimeSetUp]
    public async Task PrepareResidentAsync()
    {
        if (WebAppFixture.IsolatedSqlConnection is not { } connection)
            Assert.Ignore("Resident reclaim UI tests require the isolated E2E application.");
        else
            _options = new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(connection).Options;

        await using var db = new D3ParkingDbContext(_options!);
        var owner = await db.Users.Where(u => u.Email == Admin.Email).Select(u => u.Id).SingleAsync();
        Assert.That(await db.ParkingSpotResidents.AnyAsync(r => r.UserId == owner && r.RemovedAtUtc == null), Is.False);
        Assert.That(await db.ParkingSpots.AnyAsync(s => s.OwnerId == owner), Is.False);
        var timeZoneId = await db.SiteSettings.Select(s => s.DefaultTimeZoneId).SingleAsync();
        var timeZone = string.IsNullOrWhiteSpace(timeZoneId) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var now = DateTimeOffset.UtcNow;
        var today = SiteTime.Today(now, timeZone);
        var policy = (await db.ParkingSettings.SingleAsync()).ToPolicy();
        _day = Enumerable.Range(1, policy.ReservationHorizonDays).Select(today.AddDays)
            .First(date => policy.GetReservationDateAvailability(date, today) == ReservationDateAvailability.Allowed);
        var code = Guid.NewGuid().ToString("N")[..10];
        var own = new ParkingSpot($"E2E-R-{code}", ParkingSpotType.Standard);
        var alternative = new ParkingSpot($"E2E-A-{code}", ParkingSpotType.Standard);
        own.AssignOwner(owner);
        _ownSpotId = own.Id;
        _alternativeSpotId = alternative.Id;
        var (start, end) = SiteTime.Day(_day, timeZone);
        var reservation = new Reservation(alternative.Id, owner, start, end, false, now);
        _reservationId = reservation.Id;
        db.ParkingSpots.AddRange(own, alternative);
        db.ParkingSpotResidents.Add(new ParkingSpotResident(own.Id, owner, now.AddDays(-1)));
        db.SpotReleases.Add(new SpotRelease(own.Id, owner, _day, now, 0, SpotReleaseSource.AlternativeBooking));
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();
    }

    [OneTimeTearDown]
    public async Task RemoveResidentFixtureAsync()
    {
        if (_options is null || _ownSpotId == Guid.Empty) return;
        await using var db = new D3ParkingDbContext(_options);
        await db.Reservations.Where(r => r.Id == _reservationId).ExecuteDeleteAsync();
        await db.SpotReleases.Where(r => r.SpotId == _ownSpotId).ExecuteDeleteAsync();
        await db.ParkingSpotResidents.Where(r => r.SpotId == _ownSpotId).ExecuteDeleteAsync();
        await db.ParkingSpots.Where(s => s.Id == _ownSpotId || s.Id == _alternativeSpotId).ExecuteDeleteAsync();
    }

    [Test]
    public async Task A_resident_with_an_alternative_sees_why_their_own_day_cannot_be_taken_back()
    {
        await Pages.GotoInteractiveAsync(Page, "/parking");
        await Expect(Page.Locator(".parking-planner")).ToBeVisibleAsync();
        var day = Page.Locator($"#parking-planner-detail-{_day:yyyyMMdd}");
        for (var week = 0; week < 3 && await day.CountAsync() == 0; week++)
        {
            var selected = Page.Locator(".parking-planner__day.is-selected");
            var previous = await selected.GetAttributeAsync("id");
            await Page.GetByRole(AriaRole.Button, new() { Name = "Následující týden" }).ClickAsync();
            await Expect(selected).Not.ToHaveAttributeAsync("id", previous!);
        }
        await day.ClickAsync();
        var dialog = Page.Locator(".parking-day-dialog");
        await Expect(dialog).ToContainTextAsync("Vracené dny se překrývají s tvojí rezervací jiného místa.");
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Vzít místo zpět", Exact = true })).ToHaveCountAsync(0);

        await using var db = new D3ParkingDbContext(_options!);
        Assert.That(await db.SpotReleases.AnyAsync(r => r.SpotId == _ownSpotId && r.Date == _day), Is.True);
        Assert.That((await db.Reservations.SingleAsync(r => r.Id == _reservationId)).Status, Is.EqualTo(ReservationStatus.Reserved));
    }
}
