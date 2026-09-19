using D3Parking.Domain.Common;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture]
[NonParallelizable]
public class StartedBookingProtectionTests : AdminTest
{
    private DbContextOptions<D3ParkingDbContext>? _options;
    private readonly Dictionary<string, object?> _settings = [];
    private Guid _ownSpotId;
    private Guid _alternativeId;
    private Guid _guestBookingId;
    private Guid _ownBookingId;

    [OneTimeSetUp]
    public async Task PrepareAsync()
    {
        if (WebAppFixture.IsolatedSqlConnection is not { } connection)
            Assert.Ignore("Started booking UI tests require the isolated E2E application.");
        else
            _options = new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(connection).Options;
        await using var db = new D3ParkingDbContext(_options!);
        var settings = await db.ParkingSettings.SingleAsync();
        foreach (var (name, value) in new Dictionary<string, object?>
        {
            [nameof(ParkingSettings.ReservationTimeMode)] = ReservationTimeMode.AllDay,
            [nameof(ParkingSettings.SameDayReservationsAllowed)] = true,
            [nameof(ParkingSettings.AllowedReservationWeekdays)] = Weekday.Everyday,
            [nameof(ParkingSettings.PublicHolidayReservationsAllowed)] = true,
        })
        {
            _settings[name] = db.Entry(settings).Property(name).CurrentValue;
            db.Entry(settings).Property(name).CurrentValue = value;
        }
        var now = DateTimeOffset.UtcNow;
        var zoneId = await db.SiteSettings.Select(s => s.DefaultTimeZoneId).SingleAsync();
        var zone = string.IsNullOrWhiteSpace(zoneId) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var today = SiteTime.Today(now, zone);
        var (start, end) = SiteTime.Day(today, zone);
        var owner = await db.Users.Where(u => u.Email == Admin.Email).Select(u => u.Id).SingleAsync();
        var code = Guid.NewGuid().ToString("N")[..10];
        var own = new ParkingSpot($"LOCK-{code}", ParkingSpotType.Standard);
        var alternative = new ParkingSpot($"ALT-{code}", ParkingSpotType.Standard);
        own.AssignOwner(owner);
        var guestBooking = new Reservation(own.Id, Guid.NewGuid(), start, end, false, now);
        var ownBooking = new Reservation(alternative.Id, owner, start, end, false, now);
        _ownSpotId = own.Id;
        _alternativeId = alternative.Id;
        _guestBookingId = guestBooking.Id;
        _ownBookingId = ownBooking.Id;
        db.ParkingSpots.AddRange(own, alternative);
        db.ParkingSpotResidents.Add(new ParkingSpotResident(own.Id, owner, now.AddDays(-1)));
        db.SpotReleases.Add(new SpotRelease(own.Id, owner, today, now.AddDays(-1), 0, SpotReleaseSource.AlternativeBooking));
        db.Reservations.AddRange(guestBooking, ownBooking);
        await db.SaveChangesAsync();
        await Task.Delay(TimeSpan.FromSeconds(31));
    }

    [OneTimeTearDown]
    public async Task RestoreAsync()
    {
        if (_options is null) return;
        await using var db = new D3ParkingDbContext(_options);
        var settings = await db.ParkingSettings.SingleAsync();
        foreach (var (name, value) in _settings) db.Entry(settings).Property(name).CurrentValue = value;
        await db.Reservations.Where(r => r.Id == _guestBookingId || r.Id == _ownBookingId).ExecuteDeleteAsync();
        await db.SpotReleases.Where(r => r.SpotId == _ownSpotId).ExecuteDeleteAsync();
        await db.ParkingSpotResidents.Where(r => r.SpotId == _ownSpotId).ExecuteDeleteAsync();
        await db.ParkingSpots.Where(s => s.Id == _ownSpotId || s.Id == _alternativeId).ExecuteDeleteAsync();
        await db.SaveChangesAsync();
        await Task.Delay(TimeSpan.FromSeconds(31));
    }

    [TestCase(false), TestCase(true)]
    public async Task The_resident_cannot_reclaim_todays_guest_booking_but_can_release_their_own_booking(bool checkedIn)
    {
        // Keep the historical checked-in state usable after check-in stopped being required.
        await using (var arrange = new D3ParkingDbContext(_options!))
        {
            await arrange.Reservations.Where(r => r.Id == _ownBookingId).ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Status, checkedIn ? ReservationStatus.CheckedIn : ReservationStatus.Reserved));
        }
        await Pages.GotoInteractiveAsync(Page, "/parking");
        await Page.Locator(".parking-planner__day.is-today").ClickAsync();
        var dialog = Page.Locator(".parking-day-dialog");
        await Expect(dialog).ToContainTextAsync("Rezervace už začala a místo je až do jejího konce chráněné.");
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Požádat o vrácení", Exact = true })).ToHaveCountAsync(0);
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Vzít místo zpět", Exact = true })).ToHaveCountAsync(0);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Zavřít", Exact = true }).ClickAsync();

        var release = Page.GetByRole(AriaRole.Button, new() { Name = "Ukončit dříve", Exact = true }).First;
        await Expect(release).ToBeEnabledAsync();
        await release.ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Potvrdit ukončení", Exact = true }).ClickAsync();
        await Expect(Page.Locator(".parking-confirmation")).ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Ukončit dříve", Exact = true })).ToHaveCountAsync(0);
        await using var db = new D3ParkingDbContext(_options!);
        Assert.That((await db.Reservations.SingleAsync(r => r.Id == _ownBookingId)).Status,
            Is.EqualTo(checkedIn ? ReservationStatus.Completed : ReservationStatus.Released));
        var guest = await db.Reservations.SingleAsync(r => r.Id == _guestBookingId);
        Assert.That(guest.Status, Is.EqualTo(ReservationStatus.Reserved));
        Assert.That(guest.SpotId, Is.EqualTo(_ownSpotId));
    }
}
