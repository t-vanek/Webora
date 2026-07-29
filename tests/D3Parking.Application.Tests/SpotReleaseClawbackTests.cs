using D3Parking.Domain.Parking;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public class SpotReleaseClawbackTests
{
    private static SpotRelease Release(int awardedPoints) =>
        new(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 7, 29), DateTimeOffset.UtcNow, awardedPoints);

    [Test]
    public void First_clawback_applies_in_full()
    {
        var release = Release(awardedPoints: 20);

        Assert.That(release.RegisterClawback(5), Is.EqualTo(5));
        Assert.That(release.ClawedBackPoints, Is.EqualTo(5));
    }

    [Test]
    public void Repeated_clawbacks_stop_at_the_awarded_reward()
    {
        // Each no-show requests its own percentage of the award; the day's total must never
        // exceed what the share earned, no matter how many guests failed to show up.
        var release = Release(awardedPoints: 20);

        Assert.That(release.RegisterClawback(8), Is.EqualTo(8));
        Assert.That(release.RegisterClawback(8), Is.EqualTo(8));
        Assert.That(release.RegisterClawback(8), Is.EqualTo(4), "Only the remainder up to the award applies.");
        Assert.That(release.RegisterClawback(8), Is.EqualTo(0), "A fully clawed-back day yields nothing more.");
        Assert.That(release.ClawedBackPoints, Is.EqualTo(20));
    }

    [Test]
    public void Unrewarded_day_never_claws_anything()
    {
        var release = Release(awardedPoints: 0);

        Assert.That(release.RegisterClawback(5), Is.EqualTo(0));
        Assert.That(release.ClawedBackPoints, Is.EqualTo(0));
    }

    [Test]
    public void Negative_request_is_ignored()
    {
        var release = Release(awardedPoints: 20);

        Assert.That(release.RegisterClawback(-3), Is.EqualTo(0));
        Assert.That(release.ClawedBackPoints, Is.EqualTo(0));
    }
}
