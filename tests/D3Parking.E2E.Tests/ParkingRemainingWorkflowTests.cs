using D3Parking.Domain.Common;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture, NonParallelizable]
public sealed class ParkingRemainingWorkflowTests : AdminTest
{
    private DbContextOptions<D3ParkingDbContext>? _options;
    private readonly Dictionary<string, object?> _settings = [];
    private Guid _user;
    private TimeZoneInfo _zone = TimeZoneInfo.Utc;
    private DateOnly _day;
    private readonly List<Guid> _spots = [];

    [OneTimeSetUp]
    public async Task Prepare()
    {
        if (WebAppFixture.IsolatedSqlConnection is not { } connection)
            Assert.Ignore("These UI mutations require the disposable local SQL E2E application.");
        else _options = new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(connection, sql => sql.EnableRetryOnFailure()).Options;
        await using var db = new D3ParkingDbContext(_options!);
        _user = await db.Users.Where(u => u.Email == Admin.Email).Select(u => u.Id).SingleAsync();
        var zoneId = await db.SiteSettings.Select(s => s.DefaultTimeZoneId).SingleAsync();
        _zone = string.IsNullOrWhiteSpace(zoneId) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        _day = SiteTime.Today(DateTimeOffset.UtcNow, _zone).AddDays(1);
        var settings = await db.ParkingSettings.SingleAsync();
        foreach (var (key, value) in new Dictionary<string, object?>
        {
            [nameof(ParkingSettings.ReservationTimeMode)] = ReservationTimeMode.AllDay,
            [nameof(ParkingSettings.ResidentAlternativeBookingPolicy)] = ResidentAlternativeBookingPolicy.ConfirmRelease,
            [nameof(ParkingSettings.SameDayReservationsAllowed)] = true,
            [nameof(ParkingSettings.PublicHolidayReservationsAllowed)] = true,
            [nameof(ParkingSettings.AllowedReservationWeekdays)] = Weekday.Everyday,
            [nameof(ParkingSettings.BaseReservationCost)] = 0,
            [nameof(ParkingSettings.WeeklyReservationLimitEnabled)] = false,
        })
        {
            _settings[key] = db.Entry(settings).Property(key).CurrentValue;
            db.Entry(settings).Property(key).CurrentValue = value;
        }
        await db.SaveChangesAsync();
        await Task.Delay(TimeSpan.FromSeconds(31));
    }

    [TearDown]
    public async Task ClearTestData()
    {
        if (_options is null) return;
        if (!Page.IsClosed) await Page.GotoAsync("about:blank");
        await using var db = new D3ParkingDbContext(_options);
        await db.QueueEntries.Where(q => q.UserId == _user).ExecuteDeleteAsync();
        await db.Reservations.Where(r => _spots.Contains(r.SpotId)).ExecuteDeleteAsync();
        await db.SpotReleases.Where(r => _spots.Contains(r.SpotId)).ExecuteDeleteAsync();
        await db.OccupancyMismatches.Where(r => _spots.Contains(r.SpotId)).ExecuteDeleteAsync();
        await db.ParkingSpots.Where(s => _spots.Contains(s.Id)).ExecuteDeleteAsync();
        _spots.Clear();
    }

    [OneTimeTearDown]
    public async Task Restore()
    {
        if (_options is null) return;
        await using var db = new D3ParkingDbContext(_options);
        var settings = await db.ParkingSettings.SingleAsync();
        foreach (var (key, value) in _settings) db.Entry(settings).Property(key).CurrentValue = value;
        await db.SaveChangesAsync();
        await Task.Delay(TimeSpan.FromSeconds(31));
    }

    [TestCase("/parking"), TestCase("/")]
    public async Task Queue_claim_confirms_the_release_of_the_same_resident_day(string route)
    {
        var (own, alternative, queue) = await PrepareOffer();
        await Pages.GotoInteractiveAsync(Page, route);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Převzít místo", Exact = true }).ClickAsync();
        var confirmation = Page.Locator(".parking-confirmation");
        await Expect(confirmation).ToContainTextAsync(alternative.Code);
        await Expect(confirmation.GetByRole(AriaRole.Button, new() { Name = "Uvolnit a rezervovat", Exact = true })).ToBeVisibleAsync();
        await using (var before = new D3ParkingDbContext(_options!))
        {
            Assert.That(await before.SpotReleases.AnyAsync(r => r.SpotId == own.Id), Is.False);
            Assert.That((await before.QueueEntries.FindAsync(queue.Id))!.Status, Is.EqualTo(QueueEntryStatus.Offered));
        }
        await confirmation.GetByRole(AriaRole.Button, new() { Name = "Uvolnit a rezervovat", Exact = true }).ClickAsync();
        await Expect(confirmation).ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Převzít místo", Exact = true })).ToHaveCountAsync(0);
        await using var db = new D3ParkingDbContext(_options!);
        Assert.That(await db.SpotReleases.AnyAsync(r => r.SpotId == own.Id && r.Date == _day), Is.True);
        Assert.That(await db.Reservations.AnyAsync(r => r.SpotId == alternative.Id && r.UserId == _user && r.Status == ReservationStatus.Reserved), Is.True);
        Assert.That((await db.QueueEntries.FindAsync(queue.Id))!.Status, Is.EqualTo(QueueEntryStatus.Claimed));
    }

    [Test]
    public async Task A_co_resident_can_find_and_book_the_same_physical_spot_on_the_other_members_released_day()
    {
        var spot = NewSpot("CO");
        var now = DateTimeOffset.UtcNow;
        var other = Guid.NewGuid();
        var mine = new ParkingSpotResident(spot.Id, _user, now.AddDays(-2));
        var theirs = new ParkingSpotResident(spot.Id, other, now.AddDays(-1));
        await using (var db = new D3ParkingDbContext(_options!))
        {
            db.ParkingSpots.Add(spot);
            db.ParkingSpotResidents.AddRange(mine, theirs);
            db.SpotDayAssignments.Add(new SpotDayAssignment(spot.Id, theirs.Id, _day, now));
            db.SpotReleases.Add(new SpotRelease(spot.Id, other, _day, now, 0));
            await db.SaveChangesAsync();
        }
        await Pages.GotoInteractiveAsync(Page, "/parking");
        await SelectDay();
        var dialog = Page.Locator(".parking-day-dialog");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Najít místo", Exact = true }).ClickAsync();
        var find = Page.Locator(".booking-bar__submit button");
        await Expect(find).ToBeEnabledAsync();
        await Expect(Page.Locator(".booking-bar input[type=date]")).ToHaveValueAsync($"{_day:yyyy-MM-dd}");
        await find.ClickAsync();
        await Expect(Page.Locator(".availability-toolbar")).ToBeVisibleAsync(new() { Timeout = 15000 });
        var row = Page.Locator(".spot-card, .recommended-spot", new() { HasText = spot.Code });
        await Expect(row).ToBeVisibleAsync();
        await row.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^Rezervovat") }).ClickAsync();
        await Expect(Page.GetByText("Místo bylo rezervováno.", new() { Exact = true })).ToBeVisibleAsync();
        await using var check = new D3ParkingDbContext(_options!);
        var booking = await check.Reservations.SingleAsync(r => r.SpotId == spot.Id && r.UserId == _user);
        Assert.That(booking.CountsTowardWeeklyLimit, Is.True);
    }

    [Test]
    public async Task An_open_page_refreshes_a_new_queue_offer_without_losing_unsaved_plan_edits()
    {
        var (own, alternative, queue) = await PrepareOffer(waiting: true);
        await Pages.GotoInteractiveAsync(Page, "/parking");
        var plan = Page.Locator("details.owned-section").Filter(new() { Has = Page.Locator(".owned-plan-actions") });
        await plan.Locator("summary").ClickAsync();
        var dayButton = plan.Locator(".plan-days fluent-button").First;
        var oldAppearance = await dayButton.GetAttributeAsync("appearance");
        await dayButton.ClickAsync();
        var editedAppearance = oldAppearance == "accent" ? "outline" : "accent";
        await Expect(dayButton).ToHaveAttributeAsync("appearance", editedAppearance);
        await using (var db = new D3ParkingDbContext(_options!))
        {
            (await db.QueueEntries.FindAsync(queue.Id))!.Offer(alternative.Id, DateTimeOffset.UtcNow.AddMinutes(30));
            await db.SaveChangesAsync();
        }
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Převzít místo", Exact = true })).ToBeVisibleAsync(new() { Timeout = 25000 });
        await Expect(dayButton).ToHaveAttributeAsync("appearance", editedAppearance);
    }

    private ParkingSpot NewSpot(string prefix)
    {
        var spot = new ParkingSpot($"{prefix}-{Guid.NewGuid():N}"[..18], ParkingSpotType.Standard);
        _spots.Add(spot.Id);
        return spot;
    }

    [Test]
    public async Task A_manager_sees_and_clears_a_temporary_physical_block()
    {
        var spot = NewSpot("BLOCK");
        var now = DateTimeOffset.UtcNow;
        var (start, end) = SiteTime.Day(SiteTime.Today(now, _zone), _zone);
        await using (var db = new D3ParkingDbContext(_options!))
        {
            db.ParkingSpots.Add(spot);
            db.OccupancyMismatches.Add(new OccupancyMismatch(spot.Id, Guid.NewGuid(), _user, start, end, now));
            await db.SaveChangesAsync();
        }
        await Pages.GotoInteractiveAsync(Page, "/admin/parking/dashboard");
        await Page.Locator("button.lot-tile, button.lot-list__spot").Filter(new() { HasText = spot.Code }).ClickAsync();
        var detail = Page.Locator(".lot-split__side");
        await Expect(detail).ToContainTextAsync("Dočasně blokované");
        var clear = detail.GetByRole(AriaRole.Button, new() { Name = "Místo je zkontrolované a volné", Exact = true });
        await clear.ClickAsync();
        await Expect(clear).ToHaveCountAsync(0);
        await Expect(detail).Not.ToContainTextAsync("Dočasně blokované");
        await using var check = new D3ParkingDbContext(_options!);
        Assert.That((await check.OccupancyMismatches.SingleAsync(m => m.SpotId == spot.Id)).ResolvedAtUtc, Is.Not.Null);
    }
    private async Task<(ParkingSpot, ParkingSpot, QueueEntry)> PrepareOffer(bool waiting = false)
    {
        var own = NewSpot("OWN");
        own.AssignOwner(_user);
        var alternative = NewSpot("OFFER");
        var now = DateTimeOffset.UtcNow;
        var (start, end) = SiteTime.Day(_day, _zone);
        var queue = new QueueEntry(_user, start, end, now);
        if (!waiting) queue.Offer(alternative.Id, now.AddMinutes(30));
        await using var db = new D3ParkingDbContext(_options!);
        db.ParkingSpots.AddRange(own, alternative);
        db.ParkingSpotResidents.Add(new ParkingSpotResident(own.Id, _user, now.AddDays(-1)));
        db.QueueEntries.Add(queue);
        await db.SaveChangesAsync();
        return (own, alternative, queue);
    }
    private async Task SelectDay()
    {
        var day = Page.Locator($"#parking-planner-detail-{_day:yyyyMMdd}");
        for (var i = 0; i < 3 && await day.CountAsync() == 0; i++)
            await Page.GetByRole(AriaRole.Button, new() { Name = "Následující týden", Exact = true }).ClickAsync();
        await day.ClickAsync();
    }
}
