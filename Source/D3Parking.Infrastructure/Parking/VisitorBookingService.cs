using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Application.Settings;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Authorization;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Identity;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Parking;

public sealed class VisitorBookingService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IParkingSettingsService parkingSettings,
    ISiteSettingsService siteSettings,
    INotificationService notifications,
    IStringLocalizer<ParkingMessages> messages,
    TimeProvider timeProvider,
    ILogger<VisitorBookingService>? logger = null) : IVisitorBookingService
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
        var policy = await parkingSettings.GetCurrentPolicyAsync(cancellationToken);
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        if (VisitorBookingWindowRules.Validate(startUtc, endUtc, policy, timeProvider.GetUtcNow(), timeZone) is not null)
        {
            return [];
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var taken = dbContext.VisitorBookings
            .Where(b => b.Status == VisitorBookingStatus.Booked && b.StartUtc < endUtc && b.EndUtc > startUtc)
            .Select(b => b.SpotId);

        var free = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.IsActive && s.Type == ParkingSpotType.Visitor && !taken.Contains(s.Id))
            .Select(s => new ParkingSpotDto(s.Id, s.Code, s.Type, s.IsActive, s.Notes, s.OwnerId, null))
            .ToListAsync(cancellationToken);
        return free.OrderBy(s => s.Code, SpotCodeComparer.Instance).ToList();
    }

    public async Task<ParkingResult> BookAsync(Guid createdById, Guid spotId, DateTimeOffset startUtc, DateTimeOffset endUtc,
        string visitorName, string? company, string? licensePlate, Guid? hostUserId, CancellationToken cancellationToken = default)
    {
        BookingOutcome outcome;
        try
        {
            outcome = await OptimisticConcurrency.RetryAsync(
                () => BookCoreAsync(createdById, spotId, startUtc, endUtc, visitorName, company, licensePlate,
                    hostUserId, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex.GetBaseException() is SqlException { Number: 1205 })
        {
            return ParkingResult.Failure("Parking_Error_ConcurrentChange");
        }

        // Notification delivery is outside the transaction and retry boundary. A delivery failure
        // must never turn a committed booking into an apparent failure that invites another booking.
        if (outcome.Booking is { HostUserId: { } host } booking)
        {
            try
            {
                await notifications.NotifyAsync(host, NotificationCategory.SelfService, NotificationLevel.Info,
                    messages["Parking_Notify_VisitorBooked_Title"],
                    messages["Parking_Notify_VisitorBooked_Body", booking.VisitorName, outcome.SpotCode!], cancellationToken);
            }
            catch (Exception)
            {
                // Exception messages can contain the visitor name or address. Log only the booking id.
                logger?.LogWarning("Host notification failed for visitor booking {BookingId}; booking remains saved.", booking.Id);
            }
        }

        return outcome.Result;
    }

    private async Task<BookingOutcome> BookCoreAsync(Guid createdById, Guid spotId,
        DateTimeOffset startUtc, DateTimeOffset endUtc, string visitorName, string? company,
        string? licensePlate, Guid? hostUserId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // The settings read, authorization, overlap check and insert are protected together. SQL
        // Server's key-range locks coordinate independent application processes, including a
        // concurrent calendar-settings change; the optional policy cache is never used for writes.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (!await EffectivePermissions.HasActiveUserPermissionAsync(
                dbContext, createdById, Permissions.Parking.ManageVisitors, cancellationToken))
        {
            return BookingOutcome.Failure("Parking_Error_AccessDenied");
        }

        var now = timeProvider.GetUtcNow();
        if (string.IsNullOrWhiteSpace(visitorName))
        {
            return BookingOutcome.Failure("Parking_Visitor_Error_NameRequired");
        }
        visitorName = visitorName.Trim();
        company = string.IsNullOrWhiteSpace(company) ? null : company.Trim();
        licensePlate = string.IsNullOrWhiteSpace(licensePlate) ? null : licensePlate.Trim().ToUpperInvariant();
        if (visitorName.Length > 128 || company?.Length > 128 || licensePlate?.Length > 16)
        {
            return BookingOutcome.Failure("Parking_Visitor_Error_DetailsTooLong");
        }

        // The host may have departed after the form loaded. Keep this read in the booking
        // transaction so departure either prevents the booking or cleans it up after it commits.
        if (hostUserId is { } host && !await dbContext.Users.AsNoTracking()
                .AnyAsync(u => u.Id == host && u.Status == AccountStatus.Active, cancellationToken))
        {
            return BookingOutcome.Failure("Parking_Visitor_Error_HostUnavailable");
        }

        var settings = await dbContext.ParkingSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == ParkingSettings.SingletonId, cancellationToken);
        var policy = (settings ?? ParkingSettings.CreateDefault()).ToPolicy();
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        if (VisitorBookingWindowRules.Validate(startUtc, endUtc, policy, now, timeZone) is { } calendarError)
        {
            return BookingOutcome.Failure(calendarError);
        }

        var spot = await dbContext.ParkingSpots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == spotId, cancellationToken);
        if (spot is null || !spot.IsActive)
        {
            return BookingOutcome.Failure("Parking_Error_SpotNotFound");
        }

        if (spot.Type != ParkingSpotType.Visitor)
        {
            return BookingOutcome.Failure("Parking_Visitor_Error_NotVisitorSpot");
        }

        var taken = await dbContext.VisitorBookings.AnyAsync(b => b.SpotId == spotId
            && b.Status == VisitorBookingStatus.Booked
            && b.StartUtc < endUtc && b.EndUtc > startUtc, cancellationToken);
        if (taken)
        {
            return BookingOutcome.Failure("Parking_Error_SpotConflict");
        }

        var booking = new VisitorBooking(
            spotId,
            visitorName, company, licensePlate,
            hostUserId,
            startUtc, endUtc, createdById, now);
        dbContext.VisitorBookings.Add(booking);
        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(createdById,
            AccountAuditEventType.ReservationOverridden, $"admin:{createdById}",
            $"Visitor booking {booking.Id}: created; spot={spotId}; start={startUtc:O}; end={endUtc:O}.", now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BookingOutcome(ParkingResult.Success, booking, spot.Code);
    }

    private sealed record BookingOutcome(ParkingResult Result, VisitorBooking? Booking = null, string? SpotCode = null)
    {
        public static BookingOutcome Failure(string error) => new(ParkingResult.Failure(error));
    }

    public Task<ParkingResult> CancelAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        Task.FromResult(ParkingResult.Failure("Parking_Error_AccessDenied"));

    public async Task<ParkingResult> CancelCheckedAsync(Guid bookingId, Guid actingUserId,
        CancellationToken cancellationToken = default)
    {
        BookingOutcome outcome;
        try
        {
            outcome = await OptimisticConcurrency.RetryAsync(
                () => CancelCoreAsync(bookingId, actingUserId, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex.GetBaseException() is SqlException { Number: 1205 })
        {
            return ParkingResult.Failure("Parking_Error_ConcurrentChange");
        }

        if (outcome.Booking is { HostUserId: { } host } booking)
        {
            try
            {
                await notifications.NotifyAsync(host, NotificationCategory.SelfService, NotificationLevel.Info,
                    messages["Parking_Notify_VisitorCancelled_Title"],
                    outcome.SpotCode is { } spotCode
                        ? messages["Parking_Notify_VisitorCancelled_Body", booking.VisitorName, spotCode]
                        : messages["Parking_Notify_VisitorCancelled_BodyWithoutSpot", booking.VisitorName], cancellationToken);
            }
            catch (Exception)
            {
                logger?.LogWarning("Host notification failed for cancelled visitor booking {BookingId}; cancellation remains saved.", booking.Id);
            }
        }

        return outcome.Result;
    }

    private async Task<BookingOutcome> CancelCoreAsync(Guid bookingId, Guid actingUserId,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        // Visitor bookings currently have no edit/reschedule operation. The only state transition,
        // Booked -> Cancelled, is read and written under a SQL Server transaction so concurrent
        // callers cannot both succeed or produce duplicate audit/notification events.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (!await EffectivePermissions.HasActiveUserPermissionAsync(
                dbContext, actingUserId, Permissions.Parking.ManageVisitors, cancellationToken))
        {
            return BookingOutcome.Failure("Parking_Error_AccessDenied");
        }

        var booking = await dbContext.VisitorBookings.FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (booking is null)
        {
            return BookingOutcome.Failure("Parking_Error_ReservationNotFound");
        }
        if (booking.Status != VisitorBookingStatus.Booked)
        {
            return BookingOutcome.Failure("Parking_Visitor_Error_AlreadyCancelled");
        }

        var now = timeProvider.GetUtcNow();
        if (booking.EndUtc <= now)
        {
            return BookingOutcome.Failure("Parking_Visitor_Error_Ended");
        }

        var spotCode = await dbContext.ParkingSpots.Where(s => s.Id == booking.SpotId)
            .Select(s => s.Code).SingleOrDefaultAsync(cancellationToken);
        booking.Cancel();
        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(actingUserId,
            AccountAuditEventType.ReservationOverridden, $"admin:{actingUserId}",
            $"Visitor booking {booking.Id}: cancelled; spot={booking.SpotId}; start={booking.StartUtc:O}; end={booking.EndUtc:O}.", now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BookingOutcome(ParkingResult.Success, booking, spotCode);
    }
}
