using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Webora.Application.Notifications;
using Webora.Application.Parking;
using Webora.Domain.Notifications;
using Webora.Domain.Parking;
using Webora.Infrastructure.Persistence;

namespace Webora.Infrastructure.Parking;

public sealed class ParkingSpotService(
    IDbContextFactory<WeboraDbContext> dbContextFactory,
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

        return await query
            .OrderBy(s => s.Code)
            .Select(s => new ParkingSpotDto(s.Id, s.Code, s.Type, s.IsActive, s.Notes, s.OwnerId,
                s.OwnerId == null
                    ? null
                    : dbContext.Users.Where(u => u.Id == s.OwnerId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault(),
                s.MonthlyShareAllowance))
            .ToListAsync(cancellationToken);
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
                    messages["Parking_Notify_SpotDeactivated_Body", spot.Code], cancellationToken);
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
