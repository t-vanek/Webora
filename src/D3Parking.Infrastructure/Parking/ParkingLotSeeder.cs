using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Parking;

/// <summary>
/// Idempotently creates the parking spaces that belong to the Seyfor lot. Existing spaces are
/// deliberately left untouched so an administrator's later changes are never reverted on restart.
/// </summary>
public sealed class ParkingLotSeeder(
    D3ParkingDbContext dbContext,
    ILogger<ParkingLotSeeder> logger)
{
    internal const string LotName = "Seyfor";

    internal static IReadOnlyList<ParkingSpotSeed> Spots { get; } =
    [
        new("5", ParkingSpotType.Standard),
        new("11", ParkingSpotType.Standard),
        new("12", ParkingSpotType.Standard),
        new("418", ParkingSpotType.Standard),
        new("422", ParkingSpotType.Standard),
        new("423", ParkingSpotType.Standard),
        new("424", ParkingSpotType.Standard),
        new("425", ParkingSpotType.Standard),
        new("426", ParkingSpotType.Standard),
        new("428", ParkingSpotType.Standard),
        new("429", ParkingSpotType.Standard),
        new("430", ParkingSpotType.Standard),
        new("431", ParkingSpotType.Standard),
        new("432", ParkingSpotType.Standard),
        new("433", ParkingSpotType.Standard),
        new("434", ParkingSpotType.Standard),
        new("435", ParkingSpotType.Standard),
        new("436", ParkingSpotType.Standard),
        new("437", ParkingSpotType.Standard),
        new("438", ParkingSpotType.Standard),
        new("439", ParkingSpotType.Standard),
        new("440", ParkingSpotType.Standard),
        new("441", ParkingSpotType.Standard),
        new("451", ParkingSpotType.Standard),
        new("456", ParkingSpotType.Standard),
        new("459", ParkingSpotType.Standard),
        new("H9", ParkingSpotType.Disabled),
        new("H14", ParkingSpotType.Disabled),
    ];

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        // Startup can overlap during a rolling deployment. The database lock makes the
        // check-then-insert sequence safe across processes, while the unique Code index remains
        // the final invariant.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            DECLARE @r int;
            EXEC @r = sp_getapplock @Resource = N'D3Parking:SeyforParkingSeed', @LockMode = 'Exclusive',
                @LockOwner = 'Transaction', @LockTimeout = 60000;
            IF @r < 0 THROW 51000, 'Could not acquire the Seyfor parking seeding lock.', 1;
            """,
            cancellationToken);

        var targetCodes = Spots.Select(spot => spot.Code).ToArray();
        var existingCodes = await dbContext.ParkingSpots
            .Where(spot => targetCodes.Contains(spot.Code))
            .Select(spot => spot.Code)
            .ToListAsync(cancellationToken);
        var existing = existingCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = Spots.Where(spot => !existing.Contains(spot.Code)).ToArray();
        foreach (var spot in missing)
        {
            dbContext.ParkingSpots.Add(new ParkingSpot(spot.Code, spot.Type, LotName));
        }

        if (missing.Length > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Created {Count} missing parking spaces for {Lot}",
                missing.Length,
                LotName);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    internal readonly record struct ParkingSpotSeed(string Code, ParkingSpotType Type);
}
