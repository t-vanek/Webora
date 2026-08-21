using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Parking;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public class AvailabilityCampaignRulesTests
{
    [Test]
    public void Low_occupancy_requires_a_consecutive_bookable_stretch_below_the_threshold()
    {
        var forecast = new[]
        {
            Day(1, true, 49),
            Day(2, false, 0),
            Day(3, true, 49),
            Day(4, true, 50),
            Day(5, true, 40),
            Day(6, true, 45),
        };

        var stretch = AvailabilityCampaignService.FindStretch(
            forecast, AvailabilityCampaignKind.LowOccupancy, thresholdPercent: 50, minimumDays: 2);

        Assert.That(stretch, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(stretch!.Start, Is.EqualTo(new DateOnly(2026, 8, 5)));
            Assert.That(stretch.End, Is.EqualTo(new DateOnly(2026, 8, 6)));
            Assert.That(stretch.OccupancyPercent, Is.EqualTo(42));
        });
    }

    [Test]
    public void High_occupancy_includes_the_threshold_and_only_its_reservation_holders()
    {
        var firstHolder = Guid.NewGuid();
        var secondHolder = Guid.NewGuid();
        var outsideHolder = Guid.NewGuid();
        var forecast = new[]
        {
            Day(1, true, 84, outsideHolder),
            Day(2, true, 85, firstHolder),
            Day(3, true, 95, firstHolder, secondHolder),
            Day(4, true, 70, outsideHolder),
        };

        var stretch = AvailabilityCampaignService.FindStretch(
            forecast, AvailabilityCampaignKind.HighOccupancy, thresholdPercent: 85, minimumDays: 2);

        Assert.That(stretch, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(stretch!.Start, Is.EqualTo(new DateOnly(2026, 8, 2)));
            Assert.That(stretch.End, Is.EqualTo(new DateOnly(2026, 8, 3)));
            Assert.That(stretch.OccupancyPercent, Is.EqualTo(90));
            Assert.That(stretch.OccupantUserIds, Is.EquivalentTo(new[] { firstHolder, secondHolder }));
            Assert.That(stretch.OccupantUserIds, Does.Not.Contain(outsideHolder));
        });
    }

    private static AvailabilityCampaignService.OccupancyDay Day(
        int day,
        bool isBookable,
        int occupancy,
        params Guid[] occupants) =>
        new(new DateOnly(2026, 8, day), isBookable, occupancy, occupants.ToHashSet());
}
