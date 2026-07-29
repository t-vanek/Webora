using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using D3Parking.Application.Notifications;
using D3Parking.Application.Parking;
using D3Parking.Application.Settings;
using D3Parking.Domain.Accounts;
using D3Parking.Domain.Common;
using D3Parking.Domain.Notifications;
using D3Parking.Domain.Parking;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Parking;

/// <summary>
/// Sends the proactive "the lot is wide open" tips. Design rules (see the availability-campaign
/// analysis): only stable aggregate capacity well ahead, never momentary per-spot availability;
/// bell + push only (the Availability category has its own per-user opt-out and is never
/// mirrored to email); one campaign per period and at most one evaluation per local day.
/// </summary>
public sealed class AvailabilityCampaignService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IParkingSettingsService parkingSettings,
    ISiteSettingsService siteSettings,
    INotificationService notifications,
    IStringLocalizer<ParkingMessages> messages,
    TimeProvider timeProvider,
    ILogger<AvailabilityCampaignService> logger) : IAvailabilityCampaignService
{
    public async Task<int> RunDueCampaignsAsync(CancellationToken cancellationToken = default)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        if (!policy.AvailabilityCampaignsEnabled)
        {
            return 0;
        }

        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);

        // Send in a predictable morning slot on workdays — people plan their week then, and a
        // fixed hour keeps the tip from feeling like random noise.
        if (localNow.Hour != policy.AvailabilitySendHourLocal
            || localNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return 0;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var today = SiteTime.Today(now, timeZone);

        // At most one evaluation per local day (the maintenance loop calls this every few
        // minutes within the send hour).
        var lastCampaignAtUtc = await dbContext.AvailabilityCampaigns
            .OrderByDescending(c => c.CreatedAtUtc)
            .Select(c => (DateTimeOffset?)c.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (lastCampaignAtUtc is { } lastAt && SiteTime.Today(lastAt, timeZone) == today)
        {
            return 0;
        }

        var stretch = await FindWideOpenStretchAsync(dbContext, policy, timeZone, today, cancellationToken);
        if (stretch is not var (start, end, occupancyPercent))
        {
            return 0;
        }

        // One campaign per period: an overlap with anything already sent means people were told.
        var alreadyCovered = await dbContext.AvailabilityCampaigns
            .AnyAsync(c => c.PeriodStart <= end && c.PeriodEnd >= start, cancellationToken);
        if (alreadyCovered)
        {
            return 0;
        }

        var (windowStart, _) = SiteTime.Day(start, timeZone);
        var (_, windowEnd) = SiteTime.Day(end, timeZone);

        // Anyone waiting in the queue for the stretch means it is not truly "wide open" — a tip
        // would contradict the scarcity the queue is handling.
        var queueOverlap = await dbContext.QueueEntries.AnyAsync(q =>
            (q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered)
            && q.StartUtc < windowEnd && q.EndUtc > windowStart, cancellationToken);
        if (queueOverlap)
        {
            return 0;
        }

        // Everyone active without a booking in the stretch. Preference gating (the Availability
        // opt-out, muting) happens inside NotifyAsync per recipient.
        var bookedUserIds = await dbContext.Reservations
            .Where(r => (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < windowEnd && r.EndUtc > windowStart)
            .Select(r => r.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var recipients = await dbContext.Users
            .Where(u => u.Status == AccountStatus.Active && !bookedUserIds.Contains(u.Id))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        var title = messages["Parking_Notify_Availability_Title"].Value;
        var body = messages["Parking_Notify_Availability_Body",
            start.ToString("d.M."), end.ToString("d.M."), occupancyPercent].Value;
        foreach (var userId in recipients)
        {
            await notifications.NotifyAsync(userId, NotificationCategory.Availability, NotificationLevel.Info,
                title, body, cancellationToken);
        }

        dbContext.AvailabilityCampaigns.Add(new AvailabilityCampaign(start, end, occupancyPercent, recipients.Count, now));
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Availability campaign sent: {Start}–{End} at ~{Occupancy}% occupancy, {Recipients} recipients.",
            start, end, occupancyPercent, recipients.Count);
        return recipients.Count;
    }

    /// <summary>
    /// The earliest run of at least MinConsecutiveDays days within the lookahead whose projected
    /// occupancy stays under the threshold. Capacity counts only what is actually bookable that
    /// far ahead: unowned active spots plus explicitly released owned days — auto-share cannot be
    /// assumed for the future, so counting held resident spots would oversell the offer.
    /// </summary>
    private static async Task<(DateOnly Start, DateOnly End, int OccupancyPercent)?> FindWideOpenStretchAsync(
        D3ParkingDbContext dbContext, Domain.Parking.Incentives.IncentivePolicy policy, TimeZoneInfo timeZone,
        DateOnly today, CancellationToken cancellationToken)
    {
        var firstDay = today.AddDays(1);
        var lastDay = today.AddDays(policy.AvailabilityLookaheadDays);

        var unownedActive = await dbContext.ParkingSpots.CountAsync(
            s => s.IsActive && s.OwnerId == null && s.Type != ParkingSpotType.Visitor, cancellationToken);
        var releasedPerDay = (await dbContext.SpotReleases
                .Where(r => r.Date >= firstDay && r.Date <= lastDay)
                .Join(dbContext.ParkingSpots.Where(s => s.IsActive && s.OwnerId != null),
                    r => r.SpotId, s => s.Id, (r, _) => r.Date)
                .ToListAsync(cancellationToken))
            .GroupBy(d => d)
            .ToDictionary(g => g.Key, g => g.Count());

        var (rangeStart, _) = SiteTime.Day(firstDay, timeZone);
        var (_, rangeEnd) = SiteTime.Day(lastDay, timeZone);
        var activeReservations = await dbContext.Reservations.AsNoTracking()
            .Where(r => (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < rangeEnd && r.EndUtc > rangeStart)
            .Select(r => new { r.SpotId, r.StartUtc, r.EndUtc })
            .ToListAsync(cancellationToken);

        DateOnly? runStart = null;
        var occupancies = new List<int>();
        for (var date = firstDay; date <= lastDay; date = date.AddDays(1))
        {
            var bookable = unownedActive + releasedPerDay.GetValueOrDefault(date);
            var (dayStart, dayEnd) = SiteTime.Day(date, timeZone);
            var occupied = activeReservations
                .Where(r => r.StartUtc < dayEnd && r.EndUtc > dayStart)
                .Select(r => r.SpotId)
                .Distinct()
                .Count();
            var percent = bookable == 0 ? 100 : (int)Math.Round(occupied * 100.0 / bookable);

            if (percent < policy.AvailabilityFreeThresholdPercent)
            {
                runStart ??= date;
                occupancies.Add(percent);
                continue;
            }

            if (runStart is { } startedAt && occupancies.Count >= policy.AvailabilityMinConsecutiveDays)
            {
                return (startedAt, date.AddDays(-1), (int)Math.Round(occupancies.Average()));
            }

            runStart = null;
            occupancies.Clear();
        }

        return runStart is { } from && occupancies.Count >= policy.AvailabilityMinConsecutiveDays
            ? (from, lastDay, (int)Math.Round(occupancies.Average()))
            : null;
    }
}
