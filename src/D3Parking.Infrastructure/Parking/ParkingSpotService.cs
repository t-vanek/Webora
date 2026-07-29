using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Parking;

public sealed class ParkingSpotService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    INotificationService notifications,
    TimeProvider timeProvider,
    IStringLocalizer<ParkingMessages> messages) : IParkingSpotService
{
    public async Task<IReadOnlyList<ParkingSpotDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = dbContext.ParkingSpots.AsNoTracking();
        if (!includeInactive)
        {
            query = query.Where(s => s.IsActive);
        }

        // Natural code order (D3-2 before D3-10) needs the comparer, so sort in memory —
        // the whole lot fits in one page anyway.
        var spots = await query
            .Select(s => new ParkingSpotDto(s.Id, s.Code, s.Type, s.IsActive, s.Notes, s.OwnerId,
                s.OwnerId == null
                    ? null
                    : dbContext.Users.Where(u => u.Id == s.OwnerId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault(),
                s.MonthlyShareAllowance))
            .ToListAsync(cancellationToken);
        return spots.OrderBy(s => s.Code, SpotCodeComparer.Instance).ToList();
    }

    public async Task<ParkingSpotDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new ParkingSpotDto(s.Id, s.Code, s.Type, s.IsActive, s.Notes, s.OwnerId,
                s.OwnerId == null
                    ? null
                    : dbContext.Users.Where(u => u.Id == s.OwnerId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault(),
                s.MonthlyShareAllowance))
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

    // Mirrors the ParkingSpots.Code column config in D3ParkingDbContext.
    private const int MaxCodeLength = 32;

    public async Task<SpotBatchPlan> PreviewBatchAsync(IReadOnlyList<string> codes, CancellationToken cancellationToken = default)
    {
        var (valid, invalid) = NormalizeBatch(codes);
        if (valid.Count == 0)
        {
            return new SpotBatchPlan([], [], invalid);
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await ExistingCodesAsync(dbContext, valid, cancellationToken);
        return new SpotBatchPlan(
            valid.Where(c => !existing.Contains(c)).ToList(),
            valid.Where(existing.Contains).ToList(),
            invalid);
    }

    public async Task<SpotBatchResult> CreateBatchAsync(IReadOnlyList<string> codes, ParkingSpotType type, string? notes, CancellationToken cancellationToken = default)
    {
        var (valid, invalid) = NormalizeBatch(codes);
        if (invalid.Count > 0)
        {
            // Refuse rather than silently drop: the admin sees the offending codes in the preview.
            return SpotBatchResult.Failure("Parking_Error_SeriesCodeTooLong");
        }

        if (valid.Count == 0)
        {
            return SpotBatchResult.Failure("Parking_Error_CodeRequired");
        }

        if (valid.Count > SpotCodeSeries.MaxBatchSize)
        {
            return SpotBatchResult.Failure("Parking_Error_SeriesTooLarge");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await ExistingCodesAsync(dbContext, valid, cancellationToken);
        var toCreate = valid.Where(c => !existing.Contains(c)).ToList();
        var skipped = valid.Where(existing.Contains).ToList();

        if (toCreate.Count == 0)
        {
            return SpotBatchResult.Failure("Parking_Error_SeriesAllExist");
        }

        dbContext.ParkingSpots.AddRange(toCreate.Select(c => new ParkingSpot(c, type, notes)));
        try
        {
            // One SaveChanges = one transaction: the batch lands whole or not at all.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Somebody created a colliding code between the duplicate check and the save;
            // the unique index on Code is the last line of defence. Safe to just retry.
            return SpotBatchResult.Failure("Parking_Error_SeriesConflict");
        }

        return SpotBatchResult.Success(toCreate.Count, skipped);
    }

    /// <summary>Trims, drops empties, de-duplicates case-insensitively and splits off over-long codes.</summary>
    private static (List<string> Valid, List<string> Invalid) NormalizeBatch(IReadOnlyList<string> codes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valid = new List<string>();
        var invalid = new List<string>();
        foreach (var raw in codes)
        {
            var code = raw?.Trim();
            if (string.IsNullOrEmpty(code) || !seen.Add(code))
            {
                continue;
            }

            (code.Length > MaxCodeLength ? invalid : valid).Add(code);
        }

        return (valid, invalid);
    }

    private static async Task<HashSet<string>> ExistingCodesAsync(D3ParkingDbContext dbContext, List<string> codes, CancellationToken cancellationToken)
    {
        // Same case-insensitive comparison as the single-spot duplicate check above.
        var lowered = codes.Select(c => c.ToLower()).ToList();
        var existing = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => lowered.Contains(s.Code.ToLower()))
            .Select(s => s.Code)
            .ToListAsync(cancellationToken);
        return new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
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

        // Deactivating a spot leaves its upcoming reservations stranded, so warn the holders to re-book.
        if (!active)
        {
            var now = timeProvider.GetUtcNow();
            var affected = await dbContext.Reservations.AsNoTracking()
                .Where(r => r.SpotId == id && r.EndUtc >= now
                    && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn))
                .Select(r => r.UserId)
                .Distinct()
                .ToListAsync(cancellationToken);

            foreach (var holderId in affected)
            {
                await notifications.NotifyAsync(holderId, NotificationCategory.Administrative, NotificationLevel.Warning,
                    messages["Parking_Notify_SpotDeactivated_Title"],
                    messages["Parking_Notify_SpotDeactivated_Body", spot.Code], email: true, cancellationToken);
            }
        }

        return ParkingResult.Success;
    }

    public async Task<ParkingResult> AssignOwnerAsync(Guid id, Guid? ownerId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_SpotNotFound");
        }

        if (ownerId is { } owner && !await dbContext.Users.AnyAsync(u => u.Id == owner, cancellationToken))
        {
            return ParkingResult.Failure("Parking_Error_UserNotFound");
        }

        var previousOwner = spot.OwnerId;
        spot.AssignOwner(ownerId);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Tell the affected residents: a removed/replaced owner loses the spot, a new owner gains one.
        if (previousOwner != ownerId)
        {
            if (previousOwner is { } prev)
            {
                await notifications.NotifyAsync(prev, NotificationCategory.Administrative, NotificationLevel.Info,
                    messages["Parking_Notify_ResidentUnassigned_Title"],
                    messages["Parking_Notify_ResidentUnassigned_Body", spot.Code], cancellationToken);
            }

            if (ownerId is { } newOwner)
            {
                await notifications.NotifyAsync(newOwner, NotificationCategory.Administrative, NotificationLevel.Info,
                    messages["Parking_Notify_ResidentAssigned_Title"],
                    messages["Parking_Notify_ResidentAssigned_Body", spot.Code], cancellationToken);
            }
        }

        return ParkingResult.Success;
    }
}
