using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Webora.Application.Notifications;
using Webora.Application.Parking;
using Webora.Domain.Notifications;
using Webora.Domain.Parking;
using Webora.Domain.Parking.Incentives;
using Webora.Infrastructure.Persistence;

namespace Webora.Infrastructure.Parking;

public sealed class ReservationService(
    IDbContextFactory<WeboraDbContext> dbContextFactory,
    IParkingSettingsService parkingSettings,
    TimeProvider timeProvider,
    INotificationService notifications,
    IStringLocalizer<ParkingMessages> messages) : IReservationService
{
    /// <summary>Most releases a user can be rewarded for in a day; bounds reserve/release farming.</summary>
    private const int MaxRewardedReleasesPerDay = 2;

    public async Task<IReadOnlyList<ParkingSpotDto>> GetAvailableSpotsAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var requestDate = DateOnly.FromDateTime(startUtc.Date);
        var autoShareActive = policy.IsResidentAutoShareActive(requestDate, now);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var blocked = dbContext.Reservations
            .Where(r => (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                        && r.StartUtc < endUtc && r.EndUtc > startUtc)
            .Select(r => r.SpotId);

        var released = dbContext.SpotReleases.Where(r => r.Date == requestDate).Select(r => r.SpotId);

        // Owned spots are hidden from the pool unless released for that day, or it is today and the
        // hold cutoff has passed (auto-share). Once a guest books one, the block above excludes it.
        return await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.IsActive && !blocked.Contains(s.Id)
                && (s.OwnerId == null || autoShareActive || released.Contains(s.Id)))
            .OrderBy(s => s.Code)
            .Select(s => new ParkingSpotDto(s.Id, s.Code, s.Type, s.IsActive, s.Notes, s.OwnerId, null, s.MonthlyShareAllowance))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReservationDto>> GetMyReservationsAsync(Guid userId, bool upcomingOnly = false, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        var query = from r in dbContext.Reservations.AsNoTracking()
                    join s in dbContext.ParkingSpots on r.SpotId equals s.Id
                    where r.UserId == userId
                    select new { r, s.Code, s.Type };

        if (upcomingOnly)
        {
            query = query.Where(x => x.r.EndUtc >= now
                && (x.r.Status == ReservationStatus.Reserved || x.r.Status == ReservationStatus.CheckedIn));
        }

        return await query
            .OrderByDescending(x => x.r.StartUtc)
            .Take(200)
            .Select(x => new ReservationDto(
                x.r.Id, x.r.SpotId, x.Code, x.Type, x.r.UserId,
                x.r.StartUtc, x.r.EndUtc, x.r.Status, x.r.IsOffPeak, x.r.CreatedAtUtc,
                x.r.CheckedInAtUtc, x.r.ReleasedAtUtc, x.r.CompletedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<ParkingResult> ReserveAsync(Guid userId, Guid spotId, DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken = default)
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

        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.Id == spotId, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_SpotNotFound");
        }

        if (!spot.IsActive)
        {
            return ParkingResult.Failure("Parking_Error_SpotInactive");
        }

        // A reserved (owned) spot can only be booked by a non-owner once it is shared for that day.
        if (spot.OwnerId is { } owner && owner != userId)
        {
            var requestDate = DateOnly.FromDateTime(startUtc.Date);
            var released = await dbContext.SpotReleases.AnyAsync(r => r.SpotId == spotId && r.Date == requestDate, cancellationToken);
            if (!released && !policy.IsResidentAutoShareActive(requestDate, now))
            {
                return ParkingResult.Failure("Parking_Error_SpotReserved");
            }
        }

        var spotTaken = await dbContext.Reservations.AnyAsync(r => r.SpotId == spotId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.StartUtc < endUtc && r.EndUtc > startUtc, cancellationToken);
        if (spotTaken)
        {
            return ParkingResult.Failure("Parking_Error_SpotConflict");
        }

        var ownConflict = await dbContext.Reservations.AnyAsync(r => r.UserId == userId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.StartUtc < endUtc && r.EndUtc > startUtc, cancellationToken);
        if (ownConflict)
        {
            return ParkingResult.Failure("Parking_Error_OwnConflict");
        }

        // Off-peak and distance rewards are granted on completion (actual use), not at booking, so a
        // reserve/release loop earns nothing. IsOffPeak is captured now for use at completion.
        var isOffPeak = policy.IsOffPeak(startUtc);
        var reservation = new Reservation(spotId, userId, startUtc, endUtc, isOffPeak, now);
        dbContext.Reservations.Add(reservation);

        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<ParkingResult> CheckInAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reservation = await FindOwnedAsync(dbContext, userId, reservationId, cancellationToken);
        if (reservation is null)
        {
            return ParkingResult.Failure("Parking_Error_ReservationNotFound");
        }

        if (reservation.Status != ReservationStatus.Reserved)
        {
            return ParkingResult.Failure("Parking_Error_InvalidState");
        }

        reservation.CheckIn(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<ParkingResult> CompleteAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reservation = await FindOwnedAsync(dbContext, userId, reservationId, cancellationToken);
        if (reservation is null)
        {
            return ParkingResult.Failure("Parking_Error_ReservationNotFound");
        }

        if (reservation.Status != ReservationStatus.CheckedIn)
        {
            return ParkingResult.Failure("Parking_Error_InvalidState");
        }

        var now = timeProvider.GetUtcNow();
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        reservation.Complete(now);

        var score = await GetOrCreateScoreAsync(dbContext, userId, cancellationToken);
        score.RecordCompletion(now);
        dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
            userId, IncentiveReason.ReservationCompleted, 0, reservation.Id, now));

        // Reward off-peak use only once the spot was actually used.
        if (reservation.IsOffPeak)
        {
            score.RewardOffPeak(policy.OffPeakBonusPoints, now);
            dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                userId, IncentiveReason.OffPeakBonus, policy.OffPeakBonusPoints, reservation.Id, now));
        }

        // Reward taking a shared reserved spot, scaled by commute distance — also only on real use.
        var spot = await dbContext.ParkingSpots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == reservation.SpotId, cancellationToken);
        if (spot is { OwnerId: { } owner } && owner != userId)
        {
            // Only a verified home address counts, so a spoofed far address earns nothing.
            var home = await dbContext.Users.Where(u => u.Id == userId)
                .Select(u => new { u.CommuteDistanceKm, u.HomeVerified })
                .FirstOrDefaultAsync(cancellationToken);
            var distanceKm = home is { HomeVerified: true } ? home.CommuteDistanceKm : null;
            var takenPoints = policy.ComputeSharedTakenReward(distanceKm);
            if (takenPoints > 0)
            {
                score.RewardSharedSpotTaken(takenPoints, now);
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    userId, IncentiveReason.SharedSpotTaken, takenPoints, reservation.Id, now, spot.Code));
            }
        }

        await ReevaluateBadgesAsync(dbContext, score, now, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<ParkingResult> ReleaseAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reservation = await FindOwnedAsync(dbContext, userId, reservationId, cancellationToken);
        if (reservation is null)
        {
            return ParkingResult.Failure("Parking_Error_ReservationNotFound");
        }

        if (reservation.Status != ReservationStatus.Reserved)
        {
            return ParkingResult.Failure("Parking_Error_InvalidState");
        }

        var now = timeProvider.GetUtcNow();
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        reservation.Release(now);

        // Releasing always frees the spot. The reward applies when it happens early enough AND the
        // user hasn't already hit the daily cap on rewarded releases — otherwise a reserve/release
        // loop could farm points without ever parking.
        var todayStart = new DateTimeOffset(DateOnly.FromDateTime(now.Date).ToDateTime(TimeOnly.MinValue), now.Offset);
        var rewardedToday = await dbContext.PointsLedgerEntries.CountAsync(
            e => e.UserId == userId && e.Reason == IncentiveReason.ReleasedReservation && e.OccurredAtUtc >= todayStart,
            cancellationToken);

        if (policy.QualifiesForReleaseReward(reservation.StartUtc, now) && rewardedToday < MaxRewardedReleasesPerDay)
        {
            var score = await GetOrCreateScoreAsync(dbContext, userId, cancellationToken);
            score.RewardRelease(policy.ReleasePoints, now);
            dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                userId, IncentiveReason.ReleasedReservation, policy.ReleasePoints, reservation.Id, now));
            await ReevaluateBadgesAsync(dbContext, score, now, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<ParkingResult> CancelAsync(Guid userId, Guid reservationId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reservation = await FindOwnedAsync(dbContext, userId, reservationId, cancellationToken);
        if (reservation is null)
        {
            return ParkingResult.Failure("Parking_Error_ReservationNotFound");
        }

        if (reservation.Status != ReservationStatus.Reserved)
        {
            return ParkingResult.Failure("Parking_Error_InvalidState");
        }

        reservation.Cancel();
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<int> SweepNoShowsAsync(CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var threshold = now - policy.NoShowGracePeriod;

        var due = await dbContext.Reservations
            .Where(r => r.Status == ReservationStatus.Reserved && r.StartUtc <= threshold)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return 0;
        }

        var spotIds = due.Select(r => r.SpotId).Distinct().ToList();
        var spots = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => spotIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, cancellationToken);

        // Residents whose shared spot a guest wasted: notify them and claw back part of the reward.
        var ownerNotices = new List<(Guid OwnerId, string Code, int Clawback)>();

        foreach (var reservation in due)
        {
            reservation.MarkNoShow();
            var score = await GetOrCreateScoreAsync(dbContext, reservation.UserId, cancellationToken);
            score.PenalizeNoShow(policy.NoShowPenaltyPoints, now);
            dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                reservation.UserId, IncentiveReason.NoShowPenalty, -policy.NoShowPenaltyPoints, reservation.Id, now));

            if (spots.TryGetValue(reservation.SpotId, out var spot) && spot.OwnerId is { } ownerId && ownerId != reservation.UserId)
            {
                var date = DateOnly.FromDateTime(reservation.StartUtc.Date);
                var release = await dbContext.SpotReleases.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.SpotId == reservation.SpotId && r.Date == date, cancellationToken);
                var clawback = policy.ComputeShareClawback(release?.AwardedPoints ?? 0);
                if (clawback > 0)
                {
                    var ownerScore = await GetOrCreateScoreAsync(dbContext, ownerId, cancellationToken);
                    ownerScore.RevokeSharePoints(clawback, now);
                    dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                        ownerId, IncentiveReason.ResidentShareWasted, -clawback, reservation.Id, now, spot.Code));
                }

                ownerNotices.Add((ownerId, spot.Code, clawback));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var reservation in due)
        {
            var code = spots.TryGetValue(reservation.SpotId, out var s) ? s.Code : string.Empty;
            await notifications.NotifyAsync(reservation.UserId, NotificationCategory.Administrative, NotificationLevel.Warning,
                messages["Parking_Notify_NoShow_Title"],
                messages["Parking_Notify_NoShow_Body", code, policy.NoShowPenaltyPoints],
                cancellationToken);
        }

        foreach (var (ownerId, code, clawback) in ownerNotices)
        {
            var body = clawback > 0
                ? messages["Parking_Notify_ShareWasted_Body", code, clawback]
                : messages["Parking_Notify_ShareWastedNoPenalty_Body", code];
            await notifications.NotifyAsync(ownerId, NotificationCategory.Administrative, NotificationLevel.Warning,
                messages["Parking_Notify_ShareWasted_Title"], body, cancellationToken);
        }

        return due.Count;
    }

    public async Task<int> SendDueRemindersAsync(CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        // Remind once when the start is near (within the lead time) and the holder can still act
        // (before the no-show deadline at start + grace).
        var remindFrom = now - policy.NoShowGracePeriod;
        var remindTo = now + policy.ReminderLeadTime;

        var due = await dbContext.Reservations
            .Where(r => r.Status == ReservationStatus.Reserved && r.ReminderSentAtUtc == null
                && r.StartUtc > remindFrom && r.StartUtc <= remindTo)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
        {
            return 0;
        }

        var spotCodes = await GetSpotCodesAsync(dbContext, due, cancellationToken);

        foreach (var reservation in due)
        {
            reservation.MarkReminderSent(now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var reservation in due)
        {
            var code = spotCodes.GetValueOrDefault(reservation.SpotId, string.Empty);
            await notifications.NotifyAsync(reservation.UserId, NotificationCategory.SelfService, NotificationLevel.Warning,
                messages["Parking_Notify_Reminder_Title"],
                messages["Parking_Notify_Reminder_Body", code],
                cancellationToken);
        }

        return due.Count;
    }

    private static async Task<Dictionary<Guid, string>> GetSpotCodesAsync(WeboraDbContext dbContext, IReadOnlyList<Reservation> reservations, CancellationToken cancellationToken)
    {
        var spotIds = reservations.Select(r => r.SpotId).Distinct().ToList();
        return await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => spotIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.Code, cancellationToken);
    }

    private static Task<Reservation?> FindOwnedAsync(WeboraDbContext dbContext, Guid userId, Guid reservationId, CancellationToken cancellationToken) =>
        dbContext.Reservations.FirstOrDefaultAsync(r => r.Id == reservationId && r.UserId == userId, cancellationToken);

    private static async Task<ParkerScore> GetOrCreateScoreAsync(WeboraDbContext dbContext, Guid userId, CancellationToken cancellationToken)
    {
        var score = await dbContext.ParkerScores.FindAsync([userId], cancellationToken);
        if (score is null)
        {
            score = new ParkerScore(userId);
            dbContext.ParkerScores.Add(score);
        }

        return score;
    }

    private static async Task ReevaluateBadgesAsync(WeboraDbContext dbContext, ParkerScore score, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var earned = ParkingBadgeRules.Earned(score).ToHashSet();
        if (earned.Count == 0)
        {
            return;
        }

        var existing = await dbContext.UserBadges
            .Where(b => b.UserId == score.UserId)
            .Select(b => b.Badge)
            .ToListAsync(cancellationToken);

        foreach (var badge in earned)
        {
            if (!existing.Contains(badge))
            {
                dbContext.UserBadges.Add(new UserBadge(score.UserId, badge, now));
            }
        }
    }
}
