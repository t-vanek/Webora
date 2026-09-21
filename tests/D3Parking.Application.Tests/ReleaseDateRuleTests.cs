using D3Parking.Domain.Common;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public class ReleaseDateRuleTests
{
    [TestCase(false, -1, "Parking_Error_PastDate")]
    [TestCase(true, -1, "Parking_Error_PastDate")]
    [TestCase(false, 0, "Parking_Error_SameDayReleaseNotAllowed")]
    [TestCase(true, 0, null)]
    [TestCase(false, 1, null)]
    [TestCase(true, 1, null)]
    public void Release_rules_are_independent_of_new_booking_permission(bool allowed, int offset, string? error)
    {
        var today = new DateOnly(2026, 9, 21);
        var policy = new IncentivePolicy { SameDayReleasesAllowed = allowed, SameDayReservationsAllowed = false };
        Assert.That(policy.ValidateResidentRelease(today.AddDays(offset), SiteTime.At(today, new TimeOnly(12, 0), TimeZoneInfo.Utc), TimeZoneInfo.Utc), Is.EqualTo(error));
    }

    [Test]
    public void Release_day_uses_the_lot_midnight_instead_of_UTC_midnight()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
        var now = new DateTimeOffset(2026, 9, 21, 22, 30, 0, TimeSpan.Zero);
        var start = new DateTimeOffset(2026, 9, 22, 8, 0, 0, TimeSpan.Zero);
        var policy = new IncentivePolicy { SameDayReleasesAllowed = false };
        Assert.That(policy.ValidateRelease(start, now, zone),
            Is.EqualTo("Parking_Error_SameDayReleaseNotAllowed"));
    }

    [Test]
    public void Stored_rule_reaches_the_runtime_policy_and_defaults_preserve_existing_behavior()
    {
        var settings = ParkingSettings.CreateDefault();
        Assert.That(settings.ToPolicy().SameDayReleasesAllowed, Is.True);
        settings.SetSameDayReleasesAllowed(false);
        Assert.That(settings.ToPolicy().SameDayReleasesAllowed, Is.False);
    }

    [TestCase(ReservationReleaseMode.PreviousDay, -1, false)]
    [TestCase(ReservationReleaseMode.BeforeStart, -1, true)]
    [TestCase(ReservationReleaseMode.BeforeStart, 0, false)]
    [TestCase(ReservationReleaseMode.BeforeStart, 1, false)]
    [TestCase(ReservationReleaseMode.DuringReservation, -1, true)]
    [TestCase(ReservationReleaseMode.DuringReservation, 0, true)]
    [TestCase(ReservationReleaseMode.DuringReservation, 1, true)]
    public void Mode_distinguishes_same_day_cancellation_from_ending_a_started_booking(ReservationReleaseMode mode, int minutes, bool allowed)
    {
        var start = new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);
        var policy = new IncentivePolicy { ReleaseMode = mode };
        Assert.That(policy.ValidateRelease(start, start.AddMinutes(minutes), TimeZoneInfo.Utc) is null, Is.EqualTo(allowed));
    }

    [TestCase("2026-03-29")]
    [TestCase("2026-10-25")]
    public void Previous_day_deadline_uses_local_wall_time_across_clock_changes(string value)
    {
        var day = DateOnly.Parse(value);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
        var start = SiteTime.At(day, new TimeOnly(9, 0), zone);
        var deadline = SiteTime.At(day.AddDays(-1), new TimeOnly(18, 0), zone);
        var policy = new IncentivePolicy { ReleaseMode = ReservationReleaseMode.DuringReservation,
            ReleaseDeadline = ReleaseDeadlineMode.PreviousDayAtTime, ReleasePreviousDayTime = new(18, 0) };
        Assert.That(policy.ReleaseAllowedUntil(start, zone), Is.EqualTo(deadline));
        Assert.That(policy.ValidateRelease(start, deadline.AddTicks(-1), zone), Is.Null);
        Assert.That(policy.ValidateRelease(start, deadline, zone), Is.EqualTo("Parking_Error_ReleaseDeadlinePassed"));
    }

    [Test]
    public void Earlier_boundary_wins_and_credit_refund_cutoff_does_not_control_release()
    {
        var start = new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);
        var policy = new IncentivePolicy { ReleaseMode = ReservationReleaseMode.PreviousDay,
            ReleaseDeadline = ReleaseDeadlineMode.MinutesBeforeStart, ReleaseLeadMinutes = 120,
            ReleaseCutoff = TimeSpan.FromDays(5) };
        Assert.That(policy.ReleaseAllowedUntil(start, TimeZoneInfo.Utc), Is.EqualTo(start.AddHours(-10)));
        Assert.That(policy.ValidateRelease(start, start.AddHours(-11), TimeZoneInfo.Utc), Is.Null);
        policy = policy with { ReleaseMode = ReservationReleaseMode.DuringReservation };
        Assert.That(policy.ReleaseAllowedUntil(start, TimeZoneInfo.Utc), Is.EqualTo(start.AddHours(-2)));
        Assert.That(policy.ValidateRelease(start, start.AddHours(-2), TimeZoneInfo.Utc), Is.EqualTo("Parking_Error_ReleaseDeadlinePassed"));
    }

    [Test]
    public void Resident_day_starts_at_midnight_and_explicit_mode_overrides_legacy_switch()
    {
        var today = new DateOnly(2026, 9, 22);
        var midnight = SiteTime.At(today, TimeOnly.MinValue, TimeZoneInfo.Utc);
        var policy = new IncentivePolicy { SameDayReleasesAllowed = true, ReleaseMode = ReservationReleaseMode.BeforeStart };
        Assert.That(policy.ValidateResidentRelease(today, midnight, TimeZoneInfo.Utc), Is.EqualTo("Parking_Error_ReleaseAlreadyStarted"));
        Assert.That(policy.ValidateResidentRelease(today.AddDays(1), midnight, TimeZoneInfo.Utc), Is.Null);
    }
}
