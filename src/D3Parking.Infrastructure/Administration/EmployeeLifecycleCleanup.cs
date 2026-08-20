using Microsoft.EntityFrameworkCore;
using D3Parking.Application.Administration;
using D3Parking.Domain.Oversight;
using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Administration;

/// <summary>
/// The application-level cascade for an employee departure. Parking history deliberately has no
/// foreign key to Identity: once the identity row is gone, its Guid is an anonymous historical key.
/// Live resources are closed here so an absent employee cannot keep capacity or work assigned.
/// </summary>
internal static class EmployeeLifecycleCleanup
{
    public static async Task<EmployeeDeletionImpact> PreviewAsync(
        D3ParkingDbContext dbContext,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var ownedSpots = await dbContext.ParkingSpotResidents.CountAsync(
            r => r.UserId == userId && r.RemovedAtUtc == null, cancellationToken);
        if (ownedSpots == 0)
        {
            ownedSpots = await dbContext.ParkingSpots.CountAsync(s => s.OwnerId == userId, cancellationToken);
        }
        var pairedVehicles = await dbContext.CompanyVehicles.CountAsync(v => v.PairedUserId == userId, cancellationToken);
        var activeReservations = await dbContext.Reservations.CountAsync(r => r.UserId == userId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn), cancellationToken);
        var activeQueue = await dbContext.QueueEntries.CountAsync(q => q.UserId == userId
            && (q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered), cancellationToken);
        var visitors = await dbContext.VisitorBookings.CountAsync(v => v.Status == VisitorBookingStatus.Booked
            && v.EndUtc > now && (v.HostUserId == userId || v.CreatedById == userId), cancellationToken);
        var personalMessages = await dbContext.Notifications.CountAsync(n => n.UserId == userId, cancellationToken)
            + await dbContext.NotificationEmailDeliveries.CountAsync(d => d.UserId == userId, cancellationToken)
            + await dbContext.PushSubscriptions.CountAsync(p => p.UserId == userId, cancellationToken)
            + await dbContext.NotificationPreferences.CountAsync(p => p.UserId == userId, cancellationToken)
            + await dbContext.CalendarSubscriptions.CountAsync(p => p.UserId == userId, cancellationToken);
        var assignedCases = await dbContext.OversightCases.CountAsync(c => c.AssigneeId == userId
            && c.Status != OversightCaseStatus.Resolved, cancellationToken);

        // These rows remain useful for capacity, economy and incident reports. Without an Identity
        // row their user id cannot be resolved to a name, e-mail, address or plate.
        var history = await dbContext.Reservations.CountAsync(r => r.UserId == userId, cancellationToken)
            + await dbContext.AccountAuditEvents.CountAsync(a => a.UserId == userId, cancellationToken)
            + await dbContext.PointsLedgerEntries.CountAsync(p => p.UserId == userId, cancellationToken)
            + await dbContext.UserBadges.CountAsync(b => b.UserId == userId, cancellationToken)
            + await dbContext.ParkerScores.CountAsync(s => s.UserId == userId, cancellationToken)
            + await dbContext.ApologyVouchers.CountAsync(v => v.UserId == userId, cancellationToken)
            + await dbContext.OccupancyMismatches.CountAsync(m => m.ReporterId == userId, cancellationToken)
            + await dbContext.SpotDefectReports.CountAsync(d => d.ReporterId == userId, cancellationToken)
            + await dbContext.OversightCases.CountAsync(c => c.ReporterId == userId, cancellationToken)
            + await dbContext.OversightCaseEvents.CountAsync(e => e.ActorUserId == userId, cancellationToken)
            + await dbContext.CollusionFlags.CountAsync(f => f.UserA == userId || f.UserB == userId, cancellationToken);

        return new EmployeeDeletionImpact(ownedSpots, pairedVehicles, activeReservations, activeQueue,
            visitors, personalMessages, assignedCases, history);
    }

    /// <summary>
    /// Closes live work and removes personal delivery data. Safe to retry: every transition is
    /// selected by its live state and every delete/update is idempotent.
    /// </summary>
    public static async Task CleanOperationalAsync(
        D3ParkingDbContext dbContext,
        Guid userId,
        string? email,
        Guid? actingUserId,
        DateTimeOffset now,
        bool revokeAccess,
        CancellationToken cancellationToken)
    {
        var todayUtc = DateOnly.FromDateTime(now.UtcDateTime.Date);

        var memberships = await dbContext.ParkingSpotResidents
            .Where(r => r.UserId == userId && r.RemovedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var membership in memberships)
        {
            membership.Remove(now);
        }

        var affectedSpotIds = memberships.Select(r => r.SpotId).Distinct().ToList();
        var assignments = await dbContext.SpotDayAssignments
            .Where(a => affectedSpotIds.Contains(a.SpotId))
            .ToListAsync(cancellationToken);
        dbContext.SpotDayAssignments.RemoveRange(assignments);

        var primarySpots = await dbContext.ParkingSpots
            .Where(s => s.OwnerId == userId)
            .ToListAsync(cancellationToken);
        foreach (var spot in primarySpots)
        {
            var next = await dbContext.ParkingSpotResidents
                .Where(r => r.SpotId == spot.Id && r.UserId != userId && r.RemovedAtUtc == null)
                .OrderBy(r => r.AssignedAtUtc)
                .Select(r => (Guid?)r.UserId)
                .FirstOrDefaultAsync(cancellationToken);
            spot.AssignOwner(next);
        }

        await dbContext.SpotReleases
            .Where(r => r.OwnerId == userId && r.Date >= todayUtc)
            .ExecuteDeleteAsync(cancellationToken);

        await dbContext.CompanyVehicles
            .Where(v => v.PairedUserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.PairedUserId, (Guid?)null)
                .SetProperty(v => v.PairedAtUtc, (DateTimeOffset?)null)
                .SetProperty(v => v.PairingCodeSentAtUtc, (DateTimeOffset?)null)
                .SetProperty(v => v.PairingAttempts, 0)
                .SetProperty(v => v.PairingAttemptsWindowStartUtc, (DateTimeOffset?)null), cancellationToken);

        if (!string.IsNullOrWhiteSpace(email))
        {
            var normalizedEmail = email.Trim().ToUpperInvariant();
            await dbContext.CompanyVehicles
                .Where(v => v.DriverEmail != null && v.DriverEmail.ToUpper() == normalizedEmail)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.DriverEmail, (string?)null), cancellationToken);
        }

        var reservations = await dbContext.Reservations
            .Where(r => r.UserId == userId
                && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn))
            .ToListAsync(cancellationToken);

        var refundable = reservations.Where(r => r.Status == ReservationStatus.Reserved && r.CreditsCharged > 0).ToList();
        if (refundable.Count > 0)
        {
            var score = await dbContext.ParkerScores.FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken)
                ?? new ParkerScore(userId);
            if (dbContext.Entry(score).State == EntityState.Detached)
            {
                dbContext.ParkerScores.Add(score);
            }

            foreach (var reservation in refundable)
            {
                score.RefundCredits(reservation.CreditsCharged, now);
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    userId, IncentiveReason.ReservationRefund, reservation.CreditsCharged,
                    reservation.Id, now, "Employee lifecycle cancellation"));
            }
        }

        foreach (var reservation in reservations)
        {
            if (reservation.Status == ReservationStatus.Reserved)
            {
                reservation.Cancel(now);
            }
            else
            {
                // A car already on the lot is usage history, not a future capacity claim.
                reservation.Complete(now);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await dbContext.QueueEntries
            .Where(q => q.UserId == userId
                && (q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered))
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Status, QueueEntryStatus.Cancelled)
                .SetProperty(q => q.OfferedSpotId, (Guid?)null)
                .SetProperty(q => q.OfferExpiresAtUtc, (DateTimeOffset?)null), cancellationToken);

        await dbContext.VisitorBookings
            .Where(v => v.Status == VisitorBookingStatus.Booked && v.EndUtc > now
                && (v.HostUserId == userId || v.CreatedById == userId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status, VisitorBookingStatus.Cancelled)
                .SetProperty(v => v.HostUserId, (Guid?)null), cancellationToken);

        await dbContext.ApologyVouchers
            .Where(v => v.UserId == userId
                && (v.Status == ApologyVoucherStatus.PendingApproval || v.Status == ApologyVoucherStatus.Approved))
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status, ApologyVoucherStatus.Rejected)
                .SetProperty(v => v.ReviewedAtUtc, now)
                .SetProperty(v => v.ReviewedById, actingUserId), cancellationToken);

        await dbContext.OversightCases
            .Where(c => c.AssigneeId == userId && c.Status != OversightCaseStatus.Resolved)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.AssigneeId, (Guid?)null)
                .SetProperty(c => c.Status, OversightCaseStatus.New)
                .SetProperty(c => c.AwaitingSinceUtc, (DateTimeOffset?)null)
                .SetProperty(c => c.UpdatedAtUtc, now), cancellationToken);

        await dbContext.PushSubscriptions.Where(p => p.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.NotificationEmailDeliveries.Where(d => d.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.Notifications.Where(n => n.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.NotificationPreferences.Where(p => p.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.CalendarSubscriptions.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);

        if (revokeAccess)
        {
            await dbContext.ExternalRoleAssignments.Where(a => a.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await dbContext.UserRoles.Where(r => r.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
