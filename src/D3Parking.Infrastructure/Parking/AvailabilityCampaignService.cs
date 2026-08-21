using Microsoft.Data.SqlClient;
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
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Parking;

/// <summary>
/// Balances planned demand from both sides. Low-occupancy campaigns invite active users without a
/// booking; high-occupancy campaigns ask the holders consuming scarce shared capacity to release
/// it if their plans changed. Both use stable future stretches, never momentary spot state.
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
        if (!policy.AvailabilityCampaignsEnabled && !policy.HighOccupancyCampaignsEnabled)
        {
            return 0;
        }

        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        if (localNow.Hour != policy.AvailabilitySendHourLocal
            || localNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return 0;
        }

        var today = SiteTime.Today(now, timeZone);
        if (!policy.IsReservationDateAllowed(today))
        {
            return 0;
        }

        await using var forecastContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var forecast = await LoadForecastAsync(forecastContext, policy, timeZone, today, cancellationToken);
        var notifiedToday = new HashSet<Guid>();
        var delivered = 0;

        // Scarcity wins when one person could qualify for both messages on different future dates.
        // They receive the actionable release request, not a second invitation to create demand.
        if (policy.HighOccupancyCampaignsEnabled
            && FindStretch(forecast, AvailabilityCampaignKind.HighOccupancy,
                policy.AvailabilityBusyThresholdPercent, policy.AvailabilityMinConsecutiveDays) is { } high)
        {
            var result = await SendCampaignAsync(
                AvailabilityCampaignKind.HighOccupancy, high, today, now, policy, timeZone,
                excludedRecipients: new HashSet<Guid>(), cancellationToken);
            delivered += result.Delivered;
            notifiedToday.UnionWith(result.Recipients);
        }

        if (policy.AvailabilityCampaignsEnabled
            && FindStretch(forecast, AvailabilityCampaignKind.LowOccupancy,
                policy.AvailabilityFreeThresholdPercent, policy.AvailabilityMinConsecutiveDays) is { } low)
        {
            var result = await SendCampaignAsync(
                AvailabilityCampaignKind.LowOccupancy, low, today, now, policy, timeZone,
                notifiedToday, cancellationToken);
            delivered += result.Delivered;
        }

        return delivered;
    }

    private async Task<CampaignResult> SendCampaignAsync(
        AvailabilityCampaignKind kind,
        OccupancyStretch stretch,
        DateOnly today,
        DateTimeOffset now,
        IncentivePolicy policy,
        TimeZoneInfo timeZone,
        IReadOnlySet<Guid> excludedRecipients,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        if (await dbContext.AvailabilityCampaigns.AnyAsync(
                c => c.Kind == kind && c.CampaignDate == today, cancellationToken)
            || await dbContext.AvailabilityCampaigns.AnyAsync(
                c => c.Kind == kind && c.PeriodStart <= stretch.End && c.PeriodEnd >= stretch.Start,
                cancellationToken))
        {
            return CampaignResult.Empty;
        }

        var (windowStart, _) = SiteTime.Day(stretch.Start, timeZone);
        var (_, windowEnd) = SiteTime.Day(stretch.End, timeZone);

        if (kind == AvailabilityCampaignKind.LowOccupancy)
        {
            // An active queue contradicts a message saying that capacity is freely available.
            var queueOverlap = await dbContext.QueueEntries.AnyAsync(q =>
                (q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered)
                && q.StartUtc < windowEnd && q.EndUtc > windowStart, cancellationToken);
            if (queueOverlap)
            {
                return CampaignResult.Empty;
            }
        }

        IReadOnlyList<Guid> recipients;
        if (kind == AvailabilityCampaignKind.HighOccupancy)
        {
            var occupantUserIds = stretch.OccupantUserIds.ToList();
            var excludedUserIds = excludedRecipients.ToList();
            recipients = await dbContext.Users
                .Where(u => u.Status == AccountStatus.Active
                    && occupantUserIds.Contains(u.Id)
                    && !excludedUserIds.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);
        }
        else
        {
            var bookedUserIds = await dbContext.Reservations
                .Where(r => (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                    && r.StartUtc < windowEnd && r.EndUtc > windowStart)
                .Select(r => r.UserId)
                .Distinct()
                .ToListAsync(cancellationToken);
            var excludedUserIds = excludedRecipients.ToList();
            recipients = await dbContext.Users
                .Where(u => u.Status == AccountStatus.Active
                    && !bookedUserIds.Contains(u.Id)
                    && !excludedUserIds.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);
        }

        dbContext.AvailabilityCampaigns.Add(new AvailabilityCampaign(
            kind, today, stretch.Start, stretch.End, stretch.OccupancyPercent, recipients.Count, now));
        try
        {
            // The unique kind/day index is the cross-process guard. The row lands before fan-out,
            // so a partial notification failure cannot make the next maintenance tick resend it.
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueCampaignConflict(exception))
        {
            logger.LogInformation(
                "Skipping concurrent {Kind} occupancy campaign for {CampaignDate}; another instance won.",
                kind, today);
            return CampaignResult.Empty;
        }

        var titleKey = kind == AvailabilityCampaignKind.HighOccupancy
            ? "Parking_Notify_HighOccupancy_Title"
            : "Parking_Notify_Availability_Title";
        var body = kind == AvailabilityCampaignKind.HighOccupancy
            ? messages["Parking_Notify_HighOccupancy_Body",
                stretch.Start.ToString("d.M."), stretch.End.ToString("d.M."), stretch.OccupancyPercent].Value
            : messages.ForEconomy(policy, "Parking_Notify_Availability_Body",
                stretch.Start.ToString("d.M."), stretch.End.ToString("d.M."), stretch.OccupancyPercent).Value;
        var level = kind == AvailabilityCampaignKind.HighOccupancy
            ? NotificationLevel.Warning
            : NotificationLevel.Info;
        var delivered = await notifications.NotifyManyAsync(
            recipients.Select(userId => new NotificationRequest(
                userId, NotificationCategory.Availability, level, messages[titleKey].Value, body)).ToArray(),
            cancellationToken);

        logger.LogInformation(
            "{Kind} occupancy campaign sent: {Start}–{End} at ~{Occupancy}% occupancy, {Delivered}/{Recipients} recipients.",
            kind, stretch.Start, stretch.End, stretch.OccupancyPercent, delivered, recipients.Count);
        return new CampaignResult(delivered, recipients.ToHashSet());
    }

    private static bool IsUniqueCampaignConflict(DbUpdateException exception) =>
        exception.GetBaseException() is SqlException { Number: 2601 or 2627 };

    internal static OccupancyStretch? FindStretch(
        IReadOnlyList<OccupancyDay> forecast,
        AvailabilityCampaignKind kind,
        int thresholdPercent,
        int minimumDays)
    {
        DateOnly? runStart = null;
        var occupancies = new List<int>();
        var occupantUserIds = new HashSet<Guid>();

        foreach (var day in forecast)
        {
            var matches = day.IsBookable && (kind == AvailabilityCampaignKind.LowOccupancy
                ? day.OccupancyPercent < thresholdPercent
                : day.OccupancyPercent >= thresholdPercent);
            if (matches)
            {
                runStart ??= day.Date;
                occupancies.Add(day.OccupancyPercent);
                occupantUserIds.UnionWith(day.OccupantUserIds);
                continue;
            }

            if (runStart is { } startedAt && occupancies.Count >= minimumDays)
            {
                return new OccupancyStretch(
                    startedAt, day.Date.AddDays(-1),
                    (int)Math.Round(occupancies.Average()), occupantUserIds.ToHashSet());
            }

            runStart = null;
            occupancies.Clear();
            occupantUserIds.Clear();
        }

        return runStart is { } from && occupancies.Count >= minimumDays
            ? new OccupancyStretch(
                from, forecast[^1].Date, (int)Math.Round(occupancies.Average()), occupantUserIds.ToHashSet())
            : null;
    }

    /// <summary>
    /// Projects only genuinely bookable shared capacity: unowned active spots plus released owned
    /// spots. Zero-capacity and calendar-blocked dates break a stretch instead of reading as 100 %.
    /// </summary>
    private static async Task<IReadOnlyList<OccupancyDay>> LoadForecastAsync(
        D3ParkingDbContext dbContext,
        IncentivePolicy policy,
        TimeZoneInfo timeZone,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var firstDay = today.AddDays(1);
        var lastDay = today.AddDays(Math.Min(
            policy.AvailabilityLookaheadDays,
            Math.Clamp(policy.ReservationHorizonDays, 1, 366)));

        var unownedActiveIds = (await dbContext.ParkingSpots
                .Where(s => s.IsActive && s.OwnerId == null && s.Type != ParkingSpotType.Visitor)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var releasedPerDay = (await dbContext.SpotReleases
                .Where(r => r.Date >= firstDay && r.Date <= lastDay)
                .Join(dbContext.ParkingSpots.Where(s => s.IsActive && s.OwnerId != null),
                    r => r.SpotId, s => s.Id, (r, _) => new { r.Date, r.SpotId })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.Date)
            .ToDictionary(g => g.Key, g => g.Select(x => x.SpotId).ToHashSet());

        var (rangeStart, _) = SiteTime.Day(firstDay, timeZone);
        var (_, rangeEnd) = SiteTime.Day(lastDay, timeZone);
        var activeReservations = await dbContext.Reservations.AsNoTracking()
            .Where(r => (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < rangeEnd && r.EndUtc > rangeStart)
            .Select(r => new { r.SpotId, r.UserId, r.StartUtc, r.EndUtc })
            .ToListAsync(cancellationToken);

        var result = new List<OccupancyDay>();
        for (var date = firstDay; date <= lastDay; date = date.AddDays(1))
        {
            if (!policy.IsReservationDateAllowed(date))
            {
                result.Add(new OccupancyDay(date, false, 0, new HashSet<Guid>()));
                continue;
            }

            var releasedIds = releasedPerDay.GetValueOrDefault(date);
            var bookable = unownedActiveIds.Count + (releasedIds?.Count ?? 0);
            if (bookable == 0)
            {
                result.Add(new OccupancyDay(date, false, 0, new HashSet<Guid>()));
                continue;
            }

            var (dayStart, dayEnd) = SiteTime.Day(date, timeZone);
            var occupants = activeReservations
                .Where(r => r.StartUtc < dayEnd && r.EndUtc > dayStart
                    && (unownedActiveIds.Contains(r.SpotId) || (releasedIds?.Contains(r.SpotId) ?? false)))
                .GroupBy(r => r.SpotId)
                .Select(group => group.First())
                .ToList();
            var percent = (int)Math.Round(occupants.Count * 100.0 / bookable);
            result.Add(new OccupancyDay(date, true, percent,
                occupants.Select(r => r.UserId).ToHashSet()));
        }

        return result;
    }

    internal sealed record OccupancyDay(
        DateOnly Date,
        bool IsBookable,
        int OccupancyPercent,
        IReadOnlySet<Guid> OccupantUserIds);

    internal sealed record OccupancyStretch(
        DateOnly Start,
        DateOnly End,
        int OccupancyPercent,
        IReadOnlySet<Guid> OccupantUserIds);

    private sealed record CampaignResult(int Delivered, IReadOnlySet<Guid> Recipients)
    {
        public static CampaignResult Empty { get; } = new(0, new HashSet<Guid>());
    }
}
