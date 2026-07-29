using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Parking;

public sealed class VisitorBookingService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    INotificationService notifications,
    IStringLocalizer<ParkingMessages> messages,
    TimeProvider timeProvider) : IVisitorBookingService
{
    public async Task<IReadOnlyList<VisitorBookingDto>> ListUpcomingAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        return await (from b in dbContext.VisitorBookings.AsNoTracking()
                      join s in dbContext.ParkingSpots on b.SpotId equals s.Id
                      join hu in dbContext.Users on b.HostUserId equals hu.Id into hosts
                      from host in hosts.DefaultIfEmpty()
                      join cu in dbContext.Users on b.CreatedById equals cu.Id into creators
                      from creator in creators.DefaultIfEmpty()
                      where b.Status == VisitorBookingStatus.Booked && b.EndUtc > now
                      orderby b.StartUtc
                      select new VisitorBookingDto(
                          b.Id, s.Code, b.VisitorName, b.Company, b.LicensePlate,
                          host != null ? (host.DisplayName ?? host.Email) : null,
                          b.StartUtc, b.EndUtc,
                          creator != null ? (creator.DisplayName ?? creator.Email ?? string.Empty) : string.Empty))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ParkingSpotDto>> GetFreeSpotsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var taken = dbContext.VisitorBookings
            .Where(b => b.Status == VisitorBookingStatus.Booked && b.StartUtc < endUtc && b.EndUtc > startUtc)
            .Select(b => b.SpotId);

        var free = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.IsActive && s.Type == ParkingSpotType.Visitor && !taken.Contains(s.Id))
            .Select(s => new ParkingSpotDto(s.Id, s.Code, s.Type, s.IsActive, s.Notes, s.OwnerId, null, s.MonthlyShareAllowance))
            .ToListAsync(cancellationToken);
        return free.OrderBy(s => s.Code, SpotCodeComparer.Instance).ToList();
    }

    public async Task<ParkingResult> BookAsync(Guid createdById, Guid spotId, DateTimeOffset startUtc, DateTimeOffset endUtc,
        string visitorName, string? company, string? licensePlate, Guid? hostUserId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        if (endUtc <= startUtc)
        {
            return ParkingResult.Failure("Parking_Error_InvalidWindow");
        }

        if (endUtc <= now)
        {
            return ParkingResult.Failure("Parking_Error_PastWindow");
        }

        if (string.IsNullOrWhiteSpace(visitorName))
        {
            return ParkingResult.Failure("Parking_Visitor_Error_NameRequired");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Same rationale as ReserveCoreAsync: the conflict check and the insert must be one atomic
        // step, or two receptionists could book the last visitor spot for the same window.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var spot = await dbContext.ParkingSpots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == spotId, cancellationToken);
        if (spot is null || !spot.IsActive)
        {
            return ParkingResult.Failure("Parking_Error_SpotNotFound");
        }

        if (spot.Type != ParkingSpotType.Visitor)
        {
            return ParkingResult.Failure("Parking_Visitor_Error_NotVisitorSpot");
        }

        var taken = await dbContext.VisitorBookings.AnyAsync(b => b.SpotId == spotId
            && b.Status == VisitorBookingStatus.Booked
            && b.StartUtc < endUtc && b.EndUtc > startUtc, cancellationToken);
        if (taken)
        {
            return ParkingResult.Failure("Parking_Error_SpotConflict");
        }

        var booking = new VisitorBooking(
            spotId,
            visitorName.Trim(),
            string.IsNullOrWhiteSpace(company) ? null : company.Trim(),
            string.IsNullOrWhiteSpace(licensePlate) ? null : licensePlate.Trim().ToUpperInvariant(),
            hostUserId,
            startUtc, endUtc, createdById, now);
        dbContext.VisitorBookings.Add(booking);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // The visited employee learns where their guest parks without asking the reception.
        if (hostUserId is { } host)
        {
            await notifications.NotifyAsync(host, NotificationCategory.SelfService, NotificationLevel.Info,
                messages["Parking_Notify_VisitorBooked_Title"],
                messages["Parking_Notify_VisitorBooked_Body", booking.VisitorName, spot.Code], cancellationToken);
        }

        return ParkingResult.Success;
    }

    public async Task<ParkingResult> CancelAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var booking = await dbContext.VisitorBookings.FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (booking is null || booking.Status != VisitorBookingStatus.Booked)
        {
            return ParkingResult.Failure("Parking_Error_ReservationNotFound");
        }

        booking.Cancel();
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }
}
