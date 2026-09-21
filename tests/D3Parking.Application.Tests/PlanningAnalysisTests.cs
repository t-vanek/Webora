using D3Parking.Application.Parking;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

public class PlanningAnalysisTests
{
    private static readonly DateOnly Monday = new(2026, 9, 21);

    [Test]
    public void Closed_days_are_excluded_from_average_but_existing_bookings_remain_visible()
    {
        var data = new PlanningAnalysisDto(10, [
            new(Monday, 8, 2), new(Monday.AddDays(1), 2, 0)])
        {
            Today = Monday,
            Policy = IncentivePolicy.Default with { SameDayReservationsAllowed = false },
        };
        Assert.That(data.Availability(Monday), Is.EqualTo(ReservationDateAvailability.SameDayNotAllowed));
        Assert.That(data.AverageBookedSpots, Is.EqualTo(2));
        Assert.That(data.Days[0].BookedSpots, Is.EqualTo(8));
    }

    [Test]
    public void Horizon_is_inclusive_and_excludes_later_days_from_average()
    {
        var data = new PlanningAnalysisDto(10, [
            new(Monday.AddDays(1), 2, 0), new(Monday.AddDays(2), 8, 0)])
        {
            Today = Monday,
            Policy = IncentivePolicy.Default with { ReservationHorizonDays = 1 },
        };
        Assert.That(data.LastBookableDate, Is.EqualTo(Monday.AddDays(1)));
        Assert.That(data.Availability(Monday.AddDays(2)), Is.EqualTo(ReservationDateAvailability.OutsideReservationHorizon));
        Assert.That(data.AverageBookedSpots, Is.EqualTo(2));
    }

    [Test]
    public void No_allowed_days_produces_no_average_rather_than_zero_demand()
    {
        var data = new PlanningAnalysisDto(10, [new(Monday, 0, 0)])
        {
            Today = Monday,
            Policy = IncentivePolicy.Default with { SameDayReservationsAllowed = false },
        };
        Assert.That(data.AverageBookedSpots, Is.Null);
    }

    [Test]
    public void History_does_not_apply_current_calendar_retroactively()
    {
        var data = new PlanningAnalysisDto(10, [new(Monday.AddDays(-1), 4, 0)])
        {
            Today = Monday,
            Policy = IncentivePolicy.Default with { AllowedReservationWeekdays = Weekday.Monday },
        };
        Assert.That(data.IsHistory, Is.True);
        Assert.That(data.AverageBookedSpots, Is.EqualTo(4));
    }

    [Test]
    public void Czech_holiday_respects_configuration()
    {
        var holiday = new DateOnly(2026, 9, 28);
        var data = new PlanningAnalysisDto(10, [new(holiday, 4, 0)]) { Today = Monday };
        Assert.That(data.Availability(holiday), Is.EqualTo(ReservationDateAvailability.PublicHolidayNotAllowed));
        var enabled = data with { Policy = data.Policy with { PublicHolidayReservationsAllowed = true } };
        Assert.That(enabled.AverageBookedSpots, Is.EqualTo(4));
    }

    [TestCase(0, 0, 0)]
    [TestCase(0, 28, 0)]
    [TestCase(7, 28, 25)]
    [TestCase(28, 28, 100)]
    public void Booking_percentage_uses_active_capacity(int booked, int capacity, int expected)
    {
        var day = new PlanningDayDto(new DateOnly(2026, 9, 21), booked, 0);
        Assert.That(day.BookingPercent(capacity), Is.EqualTo(expected));
    }
}
