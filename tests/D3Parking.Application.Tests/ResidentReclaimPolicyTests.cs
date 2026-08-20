using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public class ResidentReclaimPolicyTests
{
    [Test]
    public void New_installations_start_with_the_balanced_human_friendly_policy()
    {
        var policy = ParkingSettings.CreateDefault().ToPolicy();

        Assert.Multiple(() =>
        {
            Assert.That(policy.ResidentReclaimPolicy, Is.EqualTo(ResidentReclaimPolicy.AdvanceOrReplacement));
            Assert.That(policy.ManualReleasesAreBinding, Is.True);
            Assert.That(policy.ResidentProtectionDeadlineMode, Is.EqualTo(ResidentProtectionDeadlineMode.PreviousDayAtTime));
            Assert.That(policy.ResidentProtectionPreviousDayTime, Is.EqualTo(new TimeOnly(18, 0)));
            Assert.That(policy.ResidentNoReplacementAction, Is.EqualTo(ResidentNoReplacementAction.CancelAndQueue));
        });
    }

    [Test]
    public void Previous_day_deadline_uses_the_sites_local_calendar()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
        var policy = new IncentivePolicy
        {
            ResidentProtectionDeadlineMode = ResidentProtectionDeadlineMode.PreviousDayAtTime,
            ResidentProtectionPreviousDayTime = new TimeOnly(18, 0),
        };
        var summerStart = new DateTimeOffset(2026, 9, 16, 6, 0, 0, TimeSpan.Zero);

        Assert.That(policy.ResidentProtectionDeadline(summerStart, zone),
            Is.EqualTo(new DateTimeOffset(2026, 9, 15, 18, 0, 0, TimeSpan.FromHours(2))));
    }

    [Test]
    public void Hour_deadline_is_measured_from_the_actual_start_instant()
    {
        var policy = new IncentivePolicy
        {
            ResidentProtectionDeadlineMode = ResidentProtectionDeadlineMode.HoursBeforeStart,
            ResidentProtectionLeadHours = 24,
        };
        var start = new DateTimeOffset(2026, 9, 16, 7, 30, 0, TimeSpan.Zero);

        Assert.That(policy.ResidentProtectionDeadline(start, TimeZoneInfo.Utc), Is.EqualTo(start.AddHours(-24)));
    }
}
