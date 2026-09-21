using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Parking;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

[TestFixture]
public class ParkingLotSeederTests
{
    [Test]
    public void Definition_matches_the_confirmed_Seyfor_parking_map()
    {
        var spots = ParkingLotSeeder.Spots;

        Assert.Multiple(() =>
        {
            Assert.That(ParkingLotSeeder.LotName, Is.EqualTo("Seyfor"));
            Assert.That(spots, Has.Count.EqualTo(28));
            Assert.That(spots.Select(spot => spot.Code), Is.EqualTo(new[]
            {
                "5", "11", "12", "418", "422", "423", "424", "425", "426",
                "428", "429", "430", "431", "432", "433", "434", "435", "436",
                "437", "438", "439", "440", "441", "451", "456", "459", "H9", "H14",
            }));
            Assert.That(spots.Select(spot => spot.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Is.EqualTo(28));
            Assert.That(spots.Where(spot => spot.Type == ParkingSpotType.Disabled).Select(spot => spot.Code),
                Is.EqualTo(new[] { "H9", "H14" }));
            Assert.That(spots.Count(spot => spot.Type == ParkingSpotType.Standard), Is.EqualTo(26));
        });
    }

    [Test]
    public void Seeded_spots_start_active_unassigned_and_labelled_for_Seyfor()
    {
        var spots = ParkingLotSeeder.Spots
            .Select(seed => new ParkingSpot(seed.Code, seed.Type, ParkingLotSeeder.LotName))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(spots, Has.All.Property(nameof(ParkingSpot.IsActive)).True);
            Assert.That(spots, Has.All.Property(nameof(ParkingSpot.OwnerId)).Null);
            Assert.That(spots, Has.All.Property(nameof(ParkingSpot.Notes)).EqualTo("Seyfor"));
        });
    }
}
