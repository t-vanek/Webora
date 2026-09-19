using D3Parking.Application.Parking;
using D3Parking.Domain.Common;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public class VisitorBookingInitialWindowTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
    private static readonly IncentivePolicy Policy = IncentivePolicy.Default with
    {
        SameDayReservationsAllowed = true,
        AllowedReservationWeekdays = Weekday.Everyday,
        PublicHolidayReservationsAllowed = true,
        ReservationTimeMode = ReservationTimeMode.TimeWindow,
    };

    [TestCase(6, 0, 8, 0, 17, 0)]
    [TestCase(12, 30, 12, 31, 17, 0)]
    [TestCase(19, 10, 19, 11, 20, 11)]
    [TestCase(23, 30, 23, 31, 0, 0)]
    public void Today_starts_in_the_future_and_ends_no_later_than_midnight(
        int hour, int minute, int fromHour, int fromMinute, int toHour, int toMinute)
    {
        var day = new DateOnly(2026, 9, 19);
        var now = SiteTime.At(day, new TimeOnly(hour, minute), Prague);
        var window = VisitorBookingWindowRules.FindInitialWindow(Policy, now, Prague)!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(SiteTime.Today(window.Start, Prague), Is.EqualTo(day));
            Assert.That(SiteTime.TimeOfDay(window.Start, Prague), Is.EqualTo(new TimeOnly(fromHour, fromMinute)));
            Assert.That(SiteTime.TimeOfDay(window.End, Prague), Is.EqualTo(new TimeOnly(toHour, toMinute)));
            Assert.That(window.Start, Is.GreaterThan(now));
            Assert.That(VisitorBookingWindowRules.Validate(window.Start, window.End, Policy, now, Prague), Is.Null);
        });
    }

    [Test]
    public void Final_minute_moves_to_the_next_permitted_day()
    {
        var day = new DateOnly(2026, 9, 19);
        var now = SiteTime.At(day, new TimeOnly(23, 59, 30), Prague);
        var window = VisitorBookingWindowRules.FindInitialWindow(Policy, now, Prague)!.Value;
        Assert.That(window.Start, Is.EqualTo(SiteTime.At(day.AddDays(1), new TimeOnly(8, 0), Prague)));
    }

    [Test]
    public void Same_day_weekend_and_public_holiday_rules_all_apply_to_the_initial_date()
    {
        var policy = Policy with
        {
            SameDayReservationsAllowed = false,
            AllowedReservationWeekdays = Weekday.Workdays,
            PublicHolidayReservationsAllowed = false,
            HolidayCalendarRegion = HolidayCalendarRegion.CzechRepublic,
        };
        // Friday, then weekend and the Czech public holiday on Monday 28 September.
        var now = SiteTime.At(new DateOnly(2026, 9, 25), new TimeOnly(9, 0), Prague);
        var window = VisitorBookingWindowRules.FindInitialWindow(policy, now, Prague)!.Value;
        Assert.That(SiteTime.Today(window.Start, Prague), Is.EqualTo(new DateOnly(2026, 9, 29)));
        Assert.That(VisitorBookingWindowRules.FindInitialWindow(
            policy with { ReservationHorizonDays = 2 }, now, Prague), Is.Null,
            "Finding an allowed weekday must not extend the configured horizon.");
    }

    [TestCase(2026, 3, 29, 23)]
    [TestCase(2026, 10, 25, 25)]
    public void All_day_uses_the_entire_site_day_including_daylight_saving(int year, int month, int day, int hours)
    {
        var date = new DateOnly(year, month, day);
        var policy = Policy with { ReservationTimeMode = ReservationTimeMode.AllDay };
        var now = SiteTime.At(date, new TimeOnly(19, 0), Prague);
        var window = VisitorBookingWindowRules.FindInitialWindow(policy, now, Prague)!.Value;
        Assert.That(window, Is.EqualTo(SiteTime.Day(date, Prague)));
        Assert.That(window.End - window.Start, Is.EqualTo(TimeSpan.FromHours(hours)));
    }

    [Test]
    public void Initial_date_uses_the_site_time_zone_instead_of_the_servers_date()
    {
        var now = new DateTimeOffset(2026, 9, 19, 23, 10, 0, TimeSpan.Zero);
        var window = VisitorBookingWindowRules.FindInitialWindow(Policy, now, Prague)!.Value;
        Assert.That(window.Start, Is.EqualTo(SiteTime.At(new DateOnly(2026, 9, 20), new TimeOnly(8, 0), Prague)));
    }
}
