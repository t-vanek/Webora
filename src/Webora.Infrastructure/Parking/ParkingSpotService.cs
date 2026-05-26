using Microsoft.EntityFrameworkCore;
using Webora.Application.Parking;
using Webora.Domain.Parking;
using Webora.Infrastructure.Persistence;

namespace Webora.Infrastructure.Parking;

public sealed class ParkingSpotService(IDbContextFactory<WeboraDbContext> dbContextFactory) : IParkingSpotService
{
    public async Task<IReadOnlyList<ParkingSpotDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = dbContext.ParkingSpots.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(s => s.IsActive);
        }

        return await query
            .OrderBy(s => s.Code)
            .Select(s => new ParkingSpotDto(s.Id, s.Code, s.Type, s.IsActive, s.Notes))
            .ToListAsync(cancellationToken);
    }

    public async Task<ParkingSpotDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new ParkingSpotDto(s.Id, s.Code, s.Type, s.IsActive, s.Notes))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<ParkingResult> CreateAsync(string code, ParkingSpotType type, string? notes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return ParkingResult.Failure("Parking_Error_CodeRequired");
        }

        code = code.Trim();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await dbContext.ParkingSpots.AnyAsync(s => s.Code.ToLower() == code.ToLower(), cancellationToken))
        {
            return ParkingResult.Failure("Parking_Error_DuplicateCode");
        }

        dbContext.ParkingSpots.Add(new ParkingSpot(code, type, notes));
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<ParkingResult> UpdateAsync(Guid id, string code, ParkingSpotType type, string? notes, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return ParkingResult.Failure("Parking_Error_CodeRequired");
        }

        code = code.Trim();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_SpotNotFound");
        }

        if (await dbContext.ParkingSpots.AnyAsync(s => s.Id != id && s.Code.ToLower() == code.ToLower(), cancellationToken))
        {
            return ParkingResult.Failure("Parking_Error_DuplicateCode");
        }

        spot.Rename(code);
        spot.ChangeType(type);
        spot.UpdateNotes(notes);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<ParkingResult> SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_SpotNotFound");
        }

        if (active)
        {
            spot.Activate();
        }
        else
        {
            spot.Deactivate();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }
}
