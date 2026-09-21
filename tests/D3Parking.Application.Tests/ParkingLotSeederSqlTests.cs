using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Parking;
using D3Parking.Infrastructure.Persistence;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>Real SQL Server only; proves the startup seed is repeatable against its production provider.</summary>
[TestFixture]
[NonParallelizable]
public class ParkingLotSeederSqlTests
{
    private DbContextOptions<D3ParkingDbContext>? _options;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        var configured = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServer");
        if (string.IsNullOrWhiteSpace(configured))
        {
            Assert.Ignore("ConnectionStrings__SqlServer is not set; parking seed consistency requires real SQL Server.");
        }

        var connection = new SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = $"D3Parking_ParkingLotSeedTests_{Guid.NewGuid():N}",
        };
        _options = new DbContextOptionsBuilder<D3ParkingDbContext>()
            .UseSqlServer(connection.ConnectionString)
            .Options;

        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureCreatedAsync();
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        if (_options is null)
        {
            return;
        }

        await using var db = new D3ParkingDbContext(_options);
        await db.Database.EnsureDeletedAsync();
    }

    [Test]
    public async Task Repeated_seed_adds_only_missing_spots_and_preserves_existing_details()
    {
        await using (var setup = new D3ParkingDbContext(_options!))
        {
            setup.ParkingSpots.Add(new ParkingSpot("5", ParkingSpotType.Motorcycle, "Ruční nastavení"));
            await setup.SaveChangesAsync();
        }

        await SeedAsync();
        await SeedAsync();

        await using var verify = new D3ParkingDbContext(_options!);
        var targetCodes = ParkingLotSeeder.Spots.Select(spot => spot.Code).ToArray();
        var spots = await verify.ParkingSpots
            .Where(spot => targetCodes.Contains(spot.Code))
            .ToListAsync();
        var existing = spots.Single(spot => spot.Code == "5");
        var accessible = spots.Single(spot => spot.Code == "H9");

        Assert.Multiple(() =>
        {
            Assert.That(spots, Has.Count.EqualTo(28));
            Assert.That(spots.Select(spot => spot.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Is.EqualTo(28));
            Assert.That(existing.Type, Is.EqualTo(ParkingSpotType.Motorcycle));
            Assert.That(existing.Notes, Is.EqualTo("Ruční nastavení"));
            Assert.That(accessible.Type, Is.EqualTo(ParkingSpotType.Disabled));
            Assert.That(accessible.IsActive, Is.True);
            Assert.That(accessible.OwnerId, Is.Null);
            Assert.That(accessible.Notes, Is.EqualTo(ParkingLotSeeder.LotName));
        });
    }

    private async Task SeedAsync()
    {
        await using var db = new D3ParkingDbContext(_options!);
        var seeder = new ParkingLotSeeder(db, NullLogger<ParkingLotSeeder>.Instance);
        await seeder.SeedAsync();
    }
}
