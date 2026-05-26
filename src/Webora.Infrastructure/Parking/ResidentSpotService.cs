using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Webora.Application.Notifications;
using Webora.Application.Parking;
using Webora.Domain.Notifications;
using Webora.Domain.Parking;
using Webora.Domain.Parking.Incentives;
using Webora.Infrastructure.Persistence;

namespace Webora.Infrastructure.Parking;

public sealed class ResidentSpotService(
    IDbContextFactory<WeboraDbContext> dbContextFactory,
    IParkingSettingsService parkingSettings,
    TimeProvider timeProvider,
    INotificationService notifications,
    IStringLocalizer<ParkingMessages> messages) : IResidentSpotService
{
    public async Task<OwnedSpotDto?> GetMyOwnedSpotAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(now.Date);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spot = await dbContext.ParkingSpots.AsNoTracking().FirstOrDefaultAsync(s => s.OwnerId == userId, cancellationToken);
        if (spot is null)
        {
            return null;
        }

        var (dayStart, dayEnd) = DayWindow(today, now.Offset);
        var claimed = await dbContext.Reservations.AnyAsync(r => r.SpotId == spot.Id && r.UserId == userId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.StartUtc < dayEnd && r.EndUtc > dayStart, cancellationToken);
        var releasedToday = await dbContext.SpotReleases.AnyAsync(r => r.SpotId == spot.Id && r.Date == today, cancellationToken);
        var takenByOther = await dbContext.Reservations.AnyAsync(r => r.SpotId == spot.Id && r.UserId != userId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.StartUtc < dayEnd && r.EndUtc > dayStart, cancellationToken);

        OwnedSpotDayState state;
        if (takenByOther)
        {
            state = OwnedSpotDayState.SharedTaken;
        }
        else if (claimed)
        {
            state = OwnedSpotDayState.Claimed;
        }
        else if (releasedToday || policy.IsResidentAutoShareActive(today, now))
        {
            state = OwnedSpotDayState.SharedFree;
        }
        else
        {
            state = OwnedSpotDayState.Held;
        }

        var potential = policy.ComputeShareReward(policy.ResidentShareCutoff(today, now.Offset), now, spot.MonthlyShareAllowance);
        return new OwnedSpotDto(spot.Id, spot.Code, spot.Type, spot.MonthlyShareAllowance,
            policy.ResidentMaxShareAllowance, state, releasedToday, potential);
    }

    public async Task<ParkingResult> ConfirmArrivalAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(now.Date);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.OwnerId == userId, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_NoOwnedSpot");
        }

        var (dayStart, dayEnd) = DayWindow(today, now.Offset);

        var alreadyClaimed = await dbContext.Reservations.AnyAsync(r => r.SpotId == spot.Id && r.UserId == userId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.StartUtc < dayEnd && r.EndUtc > dayStart, cancellationToken);
        if (alreadyClaimed)
        {
            return ParkingResult.Success;
        }

        if (await dbContext.SpotReleases.AnyAsync(r => r.SpotId == spot.Id && r.Date == today, cancellationToken))
        {
            return ParkingResult.Failure("Parking_Error_AlreadyReleased");
        }

        var takenByOther = await dbContext.Reservations.AnyAsync(r => r.SpotId == spot.Id && r.UserId != userId
            && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
            && r.StartUtc < dayEnd && r.EndUtc > dayStart, cancellationToken);
        if (takenByOther)
        {
            return ParkingResult.Failure("Parking_Error_SpotTakenToday");
        }

        var reservation = new Reservation(spot.Id, userId, dayStart, dayEnd, false, now);
        reservation.CheckIn(now);
        dbContext.Reservations.Add(reservation);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    private const int MaxReleaseRangeDays = 92;

    public async Task<ParkingResult> ReleaseAsync(Guid userId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(now.Date);

        if (toDate < fromDate)
        {
            return ParkingResult.Failure("Parking_Error_InvalidRange");
        }

        if (fromDate < today)
        {
            return ParkingResult.Failure("Parking_Error_PastDate");
        }

        if (toDate.DayNumber - fromDate.DayNumber >= MaxReleaseRangeDays)
        {
            return ParkingResult.Failure("Parking_Error_RangeTooLong");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.OwnerId == userId, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_NoOwnedSpot");
        }

        var alreadyReleased = (await dbContext.SpotReleases
            .Where(r => r.SpotId == spot.Id && r.Date >= fromDate && r.Date <= toDate)
            .Select(r => r.Date)
            .ToListAsync(cancellationToken)).ToHashSet();

        var rangeStart = new DateTimeOffset(fromDate.ToDateTime(TimeOnly.MinValue), now.Offset);
        var rangeEnd = new DateTimeOffset(toDate.AddDays(1).ToDateTime(TimeOnly.MinValue), now.Offset);
        var claimedDays = (await dbContext.Reservations
            .Where(r => r.SpotId == spot.Id && r.UserId == userId && r.Status == ReservationStatus.CheckedIn
                && r.StartUtc < rangeEnd && r.EndUtc > rangeStart)
            .Select(r => r.StartUtc)
            .ToListAsync(cancellationToken))
            .Select(start => DateOnly.FromDateTime(start.Date))
            .ToHashSet();

        // The monthly share allowance is a hard cap on how many days a month a resident is rewarded
        // for sharing (not just a multiplier), so releasing a long future range can't farm points.
        var allowance = spot.MonthlyShareAllowance;
        var monthFloor = new DateOnly(fromDate.Year, fromDate.Month, 1);
        var rewardedPerMonth = (await dbContext.SpotReleases
                .Where(r => r.OwnerId == userId && r.AwardedPoints > 0 && r.Date >= monthFloor && r.Date <= toDate)
                .Select(r => r.Date)
                .ToListAsync(cancellationToken))
            .GroupBy(d => (d.Year, d.Month))
            .ToDictionary(g => g.Key, g => g.Count());

        ParkerScore? score = null;
        var releasedCount = 0;
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            if (alreadyReleased.Contains(date) || claimedDays.Contains(date))
            {
                continue;
            }

            var monthKey = (date.Year, date.Month);
            var points = rewardedPerMonth.GetValueOrDefault(monthKey) < allowance
                ? policy.ComputeShareReward(policy.ResidentShareCutoff(date, now.Offset), now, allowance)
                : 0;
            dbContext.SpotReleases.Add(new SpotRelease(spot.Id, userId, date, now, points));
            releasedCount++;

            if (points > 0)
            {
                rewardedPerMonth[monthKey] = rewardedPerMonth.GetValueOrDefault(monthKey) + 1;
                score ??= await GetOrCreateScoreAsync(dbContext, userId, cancellationToken);
                score.RewardSharing(points, now);
                dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                    userId, IncentiveReason.ResidentSpotShared, points, null, now, $"{spot.Code} {date:yyyy-MM-dd}"));
            }
        }

        if (releasedCount == 0)
        {
            return ParkingResult.Failure("Parking_Error_NothingToRelease");
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<ParkingResult> SetShareAllowanceAsync(Guid userId, int allowance, CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spot = await dbContext.ParkingSpots.FirstOrDefaultAsync(s => s.OwnerId == userId, cancellationToken);
        if (spot is null)
        {
            return ParkingResult.Failure("Parking_Error_NoOwnedSpot");
        }

        spot.SetShareAllowance(Math.Clamp(allowance, 0, policy.ResidentMaxShareAllowance));
        await dbContext.SaveChangesAsync(cancellationToken);
        return ParkingResult.Success;
    }

    public async Task<int> SendDueHoldRemindersAsync(CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(now.Date);
        var cutoff = policy.ResidentShareCutoff(today, now.Offset);
        var remindFrom = cutoff - policy.ReminderLeadTime;

        // Only inside the lead window just before the cutoff, while the resident can still act.
        if (now < remindFrom || now >= cutoff)
        {
            return 0;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var (dayStart, dayEnd) = DayWindow(today, now.Offset);

        var candidates = await dbContext.ParkingSpots
            .Where(s => s.OwnerId != null && s.LastResidentReminderDate != today)
            .ToListAsync(cancellationToken);

        var toNotify = new List<(Guid OwnerId, string Code)>();
        foreach (var spot in candidates)
        {
            var releasedToday = await dbContext.SpotReleases.AnyAsync(r => r.SpotId == spot.Id && r.Date == today, cancellationToken);
            if (releasedToday)
            {
                continue;
            }

            var claimed = await dbContext.Reservations.AnyAsync(r => r.SpotId == spot.Id && r.UserId == spot.OwnerId
                && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < dayEnd && r.EndUtc > dayStart, cancellationToken);
            if (claimed)
            {
                continue;
            }

            spot.MarkResidentReminded(today);
            toNotify.Add((spot.OwnerId!.Value, spot.Code));
        }

        if (toNotify.Count == 0)
        {
            return 0;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var (ownerId, code) in toNotify)
        {
            await notifications.NotifyAsync(ownerId, NotificationCategory.SelfService, NotificationLevel.Warning,
                messages["Parking_Notify_HoldReminder_Title"],
                messages["Parking_Notify_HoldReminder_Body", code],
                cancellationToken);
        }

        return toNotify.Count;
    }

    private static (DateTimeOffset start, DateTimeOffset end) DayWindow(DateOnly date, TimeSpan offset)
    {
        var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), offset);
        return (start, start.AddDays(1));
    }

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
}
