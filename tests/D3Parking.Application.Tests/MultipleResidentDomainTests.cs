using D3Parking.Domain.Parking;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public class MultipleResidentDomainTests
{
    [Test]
    public void A_spot_has_one_resident_place_by_default_and_accepts_a_configured_capacity()
    {
        var spot = new ParkingSpot("MR-1", ParkingSpotType.Standard);

        Assert.That(spot.ResidentCapacity, Is.EqualTo(1));
        spot.SetResidentCapacity(4);
        Assert.That(spot.ResidentCapacity, Is.EqualTo(4));
    }

    [TestCase(0)]
    [TestCase(21)]
    public void Resident_capacity_outside_the_supported_range_is_rejected(int capacity)
    {
        var spot = new ParkingSpot("MR-2", ParkingSpotType.Standard);

        Assert.Throws<ArgumentOutOfRangeException>(() => spot.SetResidentCapacity(capacity));
    }

    [Test]
    public void Removing_and_reactivating_one_membership_does_not_replace_its_identity()
    {
        var membership = new ParkingSpotResident(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        var id = membership.Id;

        membership.Remove(DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.That(membership.IsActive, Is.False);

        membership.Reactivate(DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.Multiple(() =>
        {
            Assert.That(membership.IsActive, Is.True);
            Assert.That(membership.Id, Is.EqualTo(id));
        });
    }

    [Test]
    public void A_day_assignment_names_exactly_one_membership_and_day()
    {
        var spotId = Guid.NewGuid();
        var residentId = Guid.NewGuid();
        var date = new DateOnly(2026, 8, 21);

        var assignment = new SpotDayAssignment(spotId, residentId, date, DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(assignment.SpotId, Is.EqualTo(spotId));
            Assert.That(assignment.ResidentId, Is.EqualTo(residentId));
            Assert.That(assignment.Date, Is.EqualTo(date));
        });
    }
}
