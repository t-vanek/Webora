using D3Parking.Domain.Common;
using D3Parking.Domain.Parking;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public class ReservationWindowRulesTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    [Test]
    public void A_calendar_day_matches_only_the_all_day_mode()
    {
        var window = SiteTime.Day(new DateOnly(2026, 8, 21), Prague);

        Assert.That(ReservationWindowRules.MatchesMode(
            window.Start, window.End, ReservationTimeMode.AllDay, Prague), Is.True);
        Assert.That(ReservationWindowRules.MatchesMode(
            window.Start, window.End, ReservationTimeMode.TimeWindow, Prague), Is.False);
    }

    [Test]
    public void A_same_day_time_window_matches_only_the_time_window_mode()
    {
        var date = new DateOnly(2026, 8, 21);
        var start = SiteTime.At(date, new TimeOnly(8, 0), Prague);
        var end = SiteTime.At(date, new TimeOnly(17, 0), Prague);

        Assert.That(ReservationWindowRules.MatchesMode(
            start, end, ReservationTimeMode.TimeWindow, Prague), Is.True);
        Assert.That(ReservationWindowRules.MatchesMode(
            start, end, ReservationTimeMode.AllDay, Prague), Is.False);
    }

    [Test]
    public void An_all_day_window_follows_the_local_day_across_daylight_saving()
    {
        var springForward = SiteTime.Day(new DateOnly(2026, 3, 29), Prague);
        var fallBack = SiteTime.Day(new DateOnly(2026, 10, 25), Prague);

        Assert.That(springForward.End - springForward.Start, Is.EqualTo(TimeSpan.FromHours(23)));
        Assert.That(fallBack.End - fallBack.Start, Is.EqualTo(TimeSpan.FromHours(25)));
        Assert.That(ReservationWindowRules.IsFullLocalDay(
            springForward.Start, springForward.End, Prague), Is.True);
        Assert.That(ReservationWindowRules.IsFullLocalDay(
            fallBack.Start, fallBack.End, Prague), Is.True);
    }

    [Test]
    public void A_time_window_may_not_cross_into_another_local_day()
    {
        var date = new DateOnly(2026, 8, 21);
        var start = SiteTime.At(date, new TimeOnly(23, 0), Prague);
        var end = SiteTime.At(date.AddDays(1), new TimeOnly(1, 0), Prague);

        Assert.That(ReservationWindowRules.MatchesMode(
            start, end, ReservationTimeMode.TimeWindow, Prague), Is.False);
    }

    [Test]
    public void Planner_horizon_is_evaluated_in_the_lots_local_calendar()
    {
        var policy = new D3Parking.Domain.Parking.Incentives.IncentivePolicy
        {
            ReservationHorizonDays = 14,
        };
        var now = SiteTime.At(new DateOnly(2026, 8, 20), new TimeOnly(23, 30), Prague);

        Assert.That(policy.IsWithinReservationHorizon(
            SiteTime.At(new DateOnly(2026, 9, 3), new TimeOnly(8, 0), Prague), now, Prague), Is.True);
        Assert.That(policy.IsWithinReservationHorizon(
            SiteTime.At(new DateOnly(2026, 9, 4), new TimeOnly(8, 0), Prague), now, Prague), Is.False);
    }

    [Test]
    public void Planner_week_runs_from_monday_to_monday()
    {
        var (start, end) = D3Parking.Domain.Parking.Incentives.IncentivePolicy.WeekOf(
            new DateOnly(2026, 8, 23)); // Sunday

        Assert.That(start, Is.EqualTo(new DateOnly(2026, 8, 17)));
        Assert.That(end, Is.EqualTo(new DateOnly(2026, 8, 24)));
    }

    [Test]
    public void Last_minute_capacity_opens_inside_the_configured_window()
    {
        var policy = new D3Parking.Domain.Parking.Incentives.IncentivePolicy
        {
            LastMinuteUnlimitedHours = 24,
        };
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);

        Assert.That(policy.IsLastMinute(now.AddHours(24), now), Is.True);
        Assert.That(policy.IsLastMinute(now.AddHours(24).AddTicks(1), now), Is.False);
    }

    [Test]
    public void Planner_allows_only_configured_local_weekdays()
    {
        var policy = new D3Parking.Domain.Parking.Incentives.IncentivePolicy
        {
            AllowedReservationWeekdays = Weekday.Monday | Weekday.Wednesday,
        };

        Assert.That(policy.IsReservationWeekdayAllowed(
            SiteTime.At(new DateOnly(2026, 8, 24), new TimeOnly(8, 0), Prague), Prague), Is.True);
        Assert.That(policy.IsReservationWeekdayAllowed(
            SiteTime.At(new DateOnly(2026, 8, 25), new TimeOnly(8, 0), Prague), Prague), Is.False);
    }
}
