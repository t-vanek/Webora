using System.Globalization;
using D3Parking.Domain.Common;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace D3Parking.E2E.Tests;

[TestFixture]
[NonParallelizable]
public class NonResidentBookingTests : AdminTest
{
    private DbContextOptions<D3ParkingDbContext>? _options;
    private readonly Dictionary<string, object?> _originalSettings = [];
    private TimeZoneInfo _timeZone = TimeZoneInfo.Utc;

    [OneTimeSetUp]
    public async Task PrepareNonResidentCalendarAsync()
    {
        // Global policy changes are allowed only in the disposable app started by the fixture.
        if (WebAppFixture.IsolatedSqlConnection is not { } connection)
            Assert.Ignore("Nonresident calendar tests require the isolated E2E application.");
        else
            _options = new DbContextOptionsBuilder<D3ParkingDbContext>().UseSqlServer(connection).Options;

        await using var db = new D3ParkingDbContext(_options!);
        var actor = await db.Users.Where(u => u.Email == Admin.Email).Select(u => u.Id).SingleAsync();
        Assert.That(await db.ParkingSpotResidents.AnyAsync(r => r.UserId == actor && r.RemovedAtUtc == null), Is.False);
        Assert.That(await db.ParkingSpots.AnyAsync(s => s.OwnerId == actor), Is.False);
        var timeZoneId = await db.SiteSettings.Select(s => s.DefaultTimeZoneId).SingleAsync();
        _timeZone = string.IsNullOrWhiteSpace(timeZoneId) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var settings = await db.ParkingSettings.SingleAsync();
        foreach (var name in new[]
        {
            nameof(ParkingSettings.ReservationTimeMode), nameof(ParkingSettings.ReservationHorizonDays),
            nameof(ParkingSettings.SameDayReservationsAllowed), nameof(ParkingSettings.AllowedReservationWeekdays),
            nameof(ParkingSettings.PublicHolidayReservationsAllowed), nameof(ParkingSettings.HolidayCalendarRegion),
        })
            _originalSettings[name] = db.Entry(settings).Property(name).CurrentValue;
    }

    [OneTimeTearDown]
    public async Task RestoreCalendarAsync()
    {
        if (_options is null || _originalSettings.Count == 0) return;
        await using var db = new D3ParkingDbContext(_options);
        var settings = await db.ParkingSettings.SingleAsync();
        foreach (var (name, value) in _originalSettings)
            db.Entry(settings).Property(name).CurrentValue = value;
        await db.SaveChangesAsync();
        // Other fixtures must not inherit this fixture's cached policy.
        await Task.Delay(TimeSpan.FromSeconds(31));
    }

    [TestCase(ReservationTimeMode.AllDay)]
    [TestCase(ReservationTimeMode.TimeWindow)]
    public async Task A_nonresident_can_change_a_forbidden_date_and_search_an_allowed_day(ReservationTimeMode mode)
    {
        await using (var db = new D3ParkingDbContext(_options!))
        {
            await db.ParkingSettings.ExecuteUpdateAsync(s => s
                .SetProperty(p => p.ReservationTimeMode, mode)
                .SetProperty(p => p.ReservationHorizonDays, 366)
                .SetProperty(p => p.SameDayReservationsAllowed, false)
                .SetProperty(p => p.AllowedReservationWeekdays, Weekday.Workdays)
                .SetProperty(p => p.PublicHolidayReservationsAllowed, false)
                .SetProperty(p => p.HolidayCalendarRegion, HolidayCalendarRegion.CzechRepublic));
        }
        await Task.Delay(TimeSpan.FromSeconds(31));
        await Pages.GotoInteractiveAsync(Page, "/parking");

        var panel = Page.Locator("#booking-bar");
        var date = panel.Locator("input[type=date]");
        var find = panel.Locator(".booking-bar__submit button");
        var today = SiteTime.Today(DateTimeOffset.UtcNow, _timeZone);
        await Expect(Page.Locator(".parking-planner")).ToHaveCountAsync(0);
        await Expect(date).ToBeVisibleAsync();
        await Expect(date).ToHaveValueAsync(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await Expect(find).ToBeDisabledAsync();
        await Expect(panel).ToContainTextAsync("Rezervace na dnešní den nejsou povolené.");
        await Expect(panel.Locator("input[type=time]")).ToHaveCountAsync(mode == ReservationTimeMode.AllDay ? 0 : 2);

        var futureDays = Enumerable.Range(1, 366).Select(today.AddDays).ToList();
        var allowed = futureDays.First(d => Weekday.Workdays.Includes(d)
            && !HolidayCalendar.IsPublicHoliday(d, HolidayCalendarRegion.CzechRepublic));
        var weekend = futureDays.First(d => d.DayOfWeek == DayOfWeek.Saturday
            && !HolidayCalendar.IsPublicHoliday(d, HolidayCalendarRegion.CzechRepublic));
        var holiday = futureDays.First(d => Weekday.Workdays.Includes(d)
            && HolidayCalendar.IsPublicHoliday(d, HolidayCalendarRegion.CzechRepublic));

        // Recover from the initially forbidden today, then from forbidden user selections.
        foreach (var forbidden in new[] { today, weekend, holiday })
        {
            await date.FillAsync(forbidden.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            await date.BlurAsync();
            await Expect(date).ToBeVisibleAsync();
            await Expect(find).ToBeDisabledAsync();

            await date.FillAsync(allowed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            await date.BlurAsync();
            await Expect(find).ToBeEnabledAsync();
            await find.ClickAsync();
            await Expect(Page.Locator(".availability-toolbar")).ToBeVisibleAsync();
            await Expect(Page.GetByText("E2E-BASE-01", new() { Exact = true })).ToBeVisibleAsync();
        }
    }
}
