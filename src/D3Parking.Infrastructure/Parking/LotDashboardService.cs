using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
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
/// The spot manager's dashboard. Read paths are deliberately snapshot reads (AsNoTracking, no
/// transaction): the board is a picture of a moment and a stale tile costs nothing, whereas the two
/// override operations at the bottom write and take the same protection the booking paths do.
/// </summary>
/// <remarks>
/// Only the backward-looking aggregates are cached — the analytics window and the summary trends.
/// Both scan every booking in a range to answer a question about the past, and both are read again
/// on every visit to the page. The board is never cached: it answers "who is standing on which spot
/// right now", which is exactly what the manager acts on. Both overrides evict the aggregates, so a
/// cancel or a move is never followed by numbers that still count the booking.
/// </remarks>
public sealed class LotDashboardService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IParkingSettingsService parkingSettings,
    ISiteSettingsService siteSettings,
    TimeProvider timeProvider,
    INotificationService notifications,
    IMemoryCache cache,
    IStringLocalizer<ParkingMessages> messages) : ILotDashboardService
{
    /// <summary>
    /// How long a computed aggregate is reused. Short on purpose: long enough that stepping between
    /// tabs or re-entering the page is free, short enough that the manager is never looking at
    /// yesterday's answer without noticing.
    /// </summary>
    private static readonly TimeSpan AggregateCacheTtl = TimeSpan.FromSeconds(60);

    private const string CachePrefix = "d3parking:lot-dashboard:";
    /// <summary>
    /// How far back the "needs a look" signals reach (mismatches, wasted shares, a spot's own
    /// utilization). Inclusive of today, so it lines up with the analytics windows the page offers —
    /// a spot reading "1/31" here and "1/30" in the table for the same period is just confusing.
    /// </summary>
    private const int SignalWindowDays = 30;

    /// <summary>Longest analytics window; bounds the per-day scan behind the utilization table.</summary>
    private const int MaxAnalyticsDays = 366;

    public async Task<LotBoardDto> GetBoardAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var today = SiteTime.Today(now, timeZone);
        var isToday = date == today;
        var (dayStart, dayEnd) = SiteTime.Day(date, timeZone);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var spots = await dbContext.ParkingSpots.AsNoTracking()
            .Select(s => new
            {
                s.Id,
                s.Code,
                s.Type,
                s.IsActive,
                s.OwnerId,
                OwnerName = s.OwnerId == null
                    ? null
                    : dbContext.Users.Where(u => u.Id == s.OwnerId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        // Live bookings touching the day, with the holder's name resolved in the same query — a tile
        // without a name would send the manager to another screen to answer "who is that".
        var bookings = await dbContext.Reservations.AsNoTracking()
            .Where(r => (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < dayEnd && r.EndUtc > dayStart)
            .Select(r => new
            {
                r.SpotId,
                r.Status,
                r.StartUtc,
                r.EndUtc,
                HolderName = dbContext.Users.Where(u => u.Id == r.UserId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var visitors = await dbContext.VisitorBookings.AsNoTracking()
            .Where(v => v.Status == VisitorBookingStatus.Booked && v.StartUtc < dayEnd && v.EndUtc > dayStart)
            .Select(v => new { v.SpotId, v.VisitorName, v.StartUtc, v.EndUtc })
            .ToListAsync(cancellationToken);

        var releasedSpotIds = (await dbContext.SpotReleases.AsNoTracking()
                .Where(r => r.Date == date)
                .Select(r => r.SpotId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        // Spots promised to a waitlist claim. The booking path already refuses to hand these out
        // (see FreeSpotsForWindowAsync), so painting them free was the board telling the manager
        // about capacity nobody could actually be given.
        var offeredSpotIds = (await dbContext.QueueEntries.AsNoTracking()
                .Where(q => q.Status == QueueEntryStatus.Offered && q.OfferedSpotId != null
                    && q.OfferExpiresAtUtc > now && q.StartUtc < dayEnd && q.EndUtc > dayStart)
                .Select(q => q.OfferedSpotId!.Value)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var signalFrom = now.AddDays(-SignalWindowDays);
        var mismatchesPerSpot = (await dbContext.OccupancyMismatches.AsNoTracking()
                .Where(m => m.ReportedAtUtc >= signalFrom)
                .Select(m => m.SpotId)
                .ToListAsync(cancellationToken))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());
        var reservationBySpot = bookings
            .GroupBy(b => b.SpotId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.Status == ReservationStatus.CheckedIn)
                .ThenBy(b => b.StartUtc)
                .First());
        var visitorBySpot = visitors
            .GroupBy(v => v.SpotId)
            .ToDictionary(g => g.Key, g => g.OrderBy(v => v.StartUtc).First());

        var tiles = new List<SpotTileDto>(spots.Count);
        foreach (var spot in spots)
        {
            // Checked-in first, then whatever starts earliest: the tile should show the car that
            // is standing there over a booking later in the day. These lookups are deliberately
            // prepared once rather than scanning every booking again for every tile.
            reservationBySpot.TryGetValue(spot.Id, out var reservation);
            visitorBySpot.TryGetValue(spot.Id, out var visitor);

            var state = ResolveState(spot.IsActive, reservation?.Status, visitor is not null,
                offeredSpotIds.Contains(spot.Id), spot.OwnerId is not null,
                releasedSpotIds.Contains(spot.Id));
            var holder = reservation?.HolderName ?? visitor?.VisitorName;
            var from = reservation?.StartUtc ?? visitor?.StartUtc;
            var to = reservation?.EndUtc ?? visitor?.EndUtc;

            tiles.Add(new SpotTileDto(spot.Id, spot.Code, SectionOf(spot.Code), spot.Type, spot.IsActive,
                spot.OwnerId, spot.OwnerName, state, holder, from, to,
                mismatchesPerSpot.GetValueOrDefault(spot.Id)));
        }

        var queueWaiting = await dbContext.QueueEntries.AsNoTracking()
            .CountAsync(q => q.Status == QueueEntryStatus.Waiting
                && q.StartUtc < dayEnd && q.EndUtc > dayStart, cancellationToken);
        var queueOffered = await dbContext.QueueEntries.AsNoTracking()
            .CountAsync(q => q.Status == QueueEntryStatus.Offered
                && q.StartUtc < dayEnd && q.EndUtc > dayStart, cancellationToken);
        // Reported, not "open": a blocked-spot report has no triage state to be closed. What it does
        // carry is whether the driver was put somewhere else, which is the only resolution the
        // system knows about — so that is the second number rather than an invented one.
        var reportedMismatches = await dbContext.OccupancyMismatches.AsNoTracking()
            .CountAsync(m => m.ReportedAtUtc >= signalFrom, cancellationToken);
        var relocatedMismatches = await dbContext.OccupancyMismatches.AsNoTracking()
            .CountAsync(m => m.ReportedAtUtc >= signalFrom && m.RelocatedToSpotId != null, cancellationToken);

        var signalWindowStart = today.AddDays(-(SignalWindowDays - 1));
        var activeSpotIds = spots.Where(s => s.IsActive).Select(s => s.Id).ToList();
        var signalHonoured = await HonouredDaysAsync(dbContext, timeZone, signalWindowStart, today, activeSpotIds, cancellationToken);
        var unusedSharedDays = await CountUnusedSharedDaysAsync(
            dbContext, signalWindowStart, today, activeSpotIds, signalHonoured, cancellationToken);

        var occupied = tiles.Count(t => t.State == SpotBoardState.Occupied);
        var booked = tiles.Count(t => t.State is SpotBoardState.Booked or SpotBoardState.VisitorBooked);
        var offered = tiles.Count(t => t.State == SpotBoardState.OfferedFromQueue);
        var activeSpots = tiles.Count(t => t.IsActive);
        var free = tiles.Count(t => t.State is SpotBoardState.Free or SpotBoardState.ResidentShared);
        var held = tiles.Count(t => t.State == SpotBoardState.ResidentHeld);
        // Held resident spots are not "taken" but not offerable either; counting them out of the
        // denominator would read as 100 % full on a quiet day with many residents. Everything else
        // adds up on purpose: occupied + booked + offered + free + held == activeSpots, because
        // Inactive is the one remaining state and it can only fall to an inactive spot.
        var bookable = activeSpots - held;

        var overview = new LotOverviewDto(
            TotalSpots: tiles.Count,
            ActiveSpots: activeSpots,
            InactiveSpots: tiles.Count - activeSpots,
            ResidentSpots: tiles.Count(t => t.OwnerId is not null),
            PoolSpots: tiles.Count(t => t.OwnerId is null && t.Type != ParkingSpotType.Visitor),
            VisitorSpots: tiles.Count(t => t.Type == ParkingSpotType.Visitor),
            Occupied: occupied,
            Booked: booked,
            OfferedFromQueue: offered,
            Free: free,
            ResidentHeld: held,
            OccupancyPercent: bookable <= 0 ? 0 : (int)Math.Round((occupied + booked + offered) * 100.0 / bookable),
            QueueWaiting: queueWaiting,
            QueueOffered: queueOffered,
            VisitorBookings: visitors.Count,
            ReportedMismatches: reportedMismatches,
            RelocatedMismatches: relocatedMismatches,
            UnusedSharedDays: unusedSharedDays);

        // Section first, then the code read as a number, so D3-2 precedes D3-10 and the unnamed
        // section (codes with no letter prefix) sorts ahead of the named ones.
        var ordered = tiles
            .OrderBy(t => t.Section, SpotCodeComparer.Instance)
            .ThenBy(t => t.Code, SpotCodeComparer.Instance)
            .ToList();

        return new LotBoardDto(date, isToday, overview, ordered);
    }

    public async Task<LotSummaryTrendsDto> GetSummaryTrendsAsync(CancellationToken cancellationToken = default)
    {
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var today = SiteTime.Today(timeProvider.GetUtcNow(), timeZone);
        var key = $"{CachePrefix}trends:{CacheGeneration}:{today:yyyy-MM-dd}";
        if (cache.TryGetValue(key, out var cached) && cached is LotSummaryTrendsDto hit)
        {
            return hit;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var trends = await ComputeSummaryTrendsAsync(dbContext, timeZone, today, cancellationToken);

        using var entry = cache.CreateEntry(key);
        entry.Value = trends;
        entry.AbsoluteExpirationRelativeToNow = AggregateCacheTtl;
        return trends;
    }

    /// <summary>
    /// The fixed-length daily series the summary table draws next to each number it can. Deliberately
    /// independent of the analytics window: the summary is the "how are we doing lately" header and
    /// must not change shape when someone picks a different window on the analytics tab.
    /// </summary>
    private static async Task<LotSummaryTrendsDto> ComputeSummaryTrendsAsync(
        D3ParkingDbContext dbContext, TimeZoneInfo timeZone, DateOnly today, CancellationToken cancellationToken)
    {
        var from = today.AddDays(-(LotBoard.SummaryTrendDays - 1));
        var spotIds = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.IsActive).Select(s => s.Id).ToListAsync(cancellationToken);
        var honoured = await HonouredDaysAsync(dbContext, timeZone, from, today, spotIds, cancellationToken);
        var occupancy = BuildDailyOccupancy(honoured, spotIds.Count, from, today);

        var (rangeStart, _) = SiteTime.Day(from, timeZone);
        var (_, rangeEnd) = SiteTime.Day(today, timeZone);
        var reportedPerDay = (await dbContext.OccupancyMismatches.AsNoTracking()
                .Where(m => m.ReportedAtUtc >= rangeStart && m.ReportedAtUtc < rangeEnd)
                .Select(m => m.ReportedAtUtc)
                .ToListAsync(cancellationToken))
            .GroupBy(at => SiteTime.Today(at, timeZone))
            .ToDictionary(g => g.Key, g => g.Count());

        // Shared days nobody took, per day — the same rule as CountUnusedSharedDaysAsync, just kept
        // per day instead of summed, and only for days that have already passed.
        var releases = await dbContext.SpotReleases.AsNoTracking()
            .Where(r => spotIds.Contains(r.SpotId) && r.Date >= from && r.Date < today)
            .Select(r => new { r.SpotId, r.Date })
            .ToListAsync(cancellationToken);
        var wastedPerDay = releases
            .Where(r => !honoured.Contains((r.SpotId, r.Date)))
            .GroupBy(r => r.Date)
            .ToDictionary(g => g.Key, g => g.Count());

        var mismatches = new List<DailyCountDto>(LotBoard.SummaryTrendDays);
        var wasted = new List<DailyCountDto>(LotBoard.SummaryTrendDays);
        for (var date = from; date <= today; date = date.AddDays(1))
        {
            mismatches.Add(new DailyCountDto(date, reportedPerDay.GetValueOrDefault(date)));
            wasted.Add(new DailyCountDto(date, wastedPerDay.GetValueOrDefault(date)));
        }

        return new LotSummaryTrendsDto(occupancy, mismatches, wasted);
    }

    /// <summary>
    /// The one place the tile precedence lives, so the board and a spot's detail can never disagree:
    /// an inactive spot is out of the lot whatever is booked on it, an arrived driver outranks a mere
    /// booking, and a resident spot is only pool capacity once it is actually shared.
    /// </summary>
    private static SpotBoardState ResolveState(bool isActive, ReservationStatus? liveBooking, bool hasVisitorBooking,
        bool offeredFromQueue, bool hasOwner, bool sharedForTheDay) =>
        (isActive, liveBooking, hasVisitorBooking, offeredFromQueue, hasOwner) switch
        {
            (false, _, _, _, _) => SpotBoardState.Inactive,
            (_, ReservationStatus.CheckedIn, _, _, _) => SpotBoardState.Occupied,
            (_, not null, _, _, _) => SpotBoardState.Booked,
            (_, _, true, _, _) => SpotBoardState.VisitorBooked,
            // A waitlist offer only ever lands on a spot that is otherwise free or already shared, so
            // it can outrank the resident branch without ever hiding a spot that is being held.
            (_, _, _, true, _) => SpotBoardState.OfferedFromQueue,
            (_, _, _, _, true) => sharedForTheDay ? SpotBoardState.ResidentShared : SpotBoardState.ResidentHeld,
            _ => SpotBoardState.Free,
        };

    /// <summary>
    /// The section of a code: its leading non-digit run ("P2-08" → "P2"), which is the convention the
    /// spot generator already writes. A code that starts with a digit, or has no separator, has no
    /// section rather than being a section of its own.
    /// </summary>
    private static string SectionOf(string code)
    {
        var separator = code.IndexOfAny(['-', '_', '/', ' ', '.']);
        var head = separator > 0 ? code[..separator] : code;
        // A purely numeric head is a spot number, not a section name.
        return head.Length > 0 && !head.All(char.IsAsciiDigit) ? head.Trim() : string.Empty;
    }

    public async Task<SpotDetailDto?> GetSpotDetailAsync(Guid spotId, DateOnly from, int days, CancellationToken cancellationToken = default)
    {
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var span = Math.Clamp(days, 1, 92);
        var to = from.AddDays(span - 1);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spot = await dbContext.ParkingSpots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == spotId, cancellationToken);
        if (spot is null)
        {
            return null;
        }

        var ownerName = spot.OwnerId is null
            ? null
            : await dbContext.Users.AsNoTracking().Where(u => u.Id == spot.OwnerId)
                .Select(u => u.DisplayName ?? u.Email).FirstOrDefaultAsync(cancellationToken);

        var (rangeStart, _) = SiteTime.Day(from, timeZone);
        var (_, rangeEnd) = SiteTime.Day(to, timeZone);

        var reservations = await dbContext.Reservations.AsNoTracking()
            .Where(r => r.SpotId == spotId && r.StartUtc < rangeEnd && r.EndUtc > rangeStart)
            .Select(r => new
            {
                r.Id,
                r.Status,
                r.StartUtc,
                r.EndUtc,
                r.CreditsCharged,
                HolderName = dbContext.Users.Where(u => u.Id == r.UserId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var visitorBookings = await dbContext.VisitorBookings.AsNoTracking()
            .Where(v => v.SpotId == spotId && v.Status == VisitorBookingStatus.Booked
                && v.StartUtc < rangeEnd && v.EndUtc > rangeStart)
            .Select(v => new { v.VisitorName, v.Company, v.StartUtc, v.EndUtc })
            .ToListAsync(cancellationToken);

        var releases = await dbContext.SpotReleases.AsNoTracking()
            .Where(r => r.SpotId == spotId && r.Date >= from && r.Date <= to)
            .Select(r => r.Date)
            .ToListAsync(cancellationToken);

        var calendar = new List<SpotCalendarEntryDto>();
        foreach (var reservation in reservations)
        {
            // A live booking is the manager's to override; a finished one is history.
            var live = reservation.Status is ReservationStatus.Reserved or ReservationStatus.CheckedIn;
            calendar.Add(new SpotCalendarEntryDto(
                SpotCalendarKind.Reservation, SiteTime.Today(reservation.StartUtc, timeZone),
                reservation.StartUtc, reservation.EndUtc, reservation.HolderName, reservation.Id,
                reservation.Status, reservation.CreditsCharged,
                // Cancelling is only a legal move on a booking nobody has arrived on; once checked in,
                // moving it is the honest intervention (see the ILotDashboardService docs).
                CanCancel: reservation.Status == ReservationStatus.Reserved && reservation.EndUtc > now,
                CanMove: live && reservation.EndUtc > now));
        }

        foreach (var visitor in visitorBookings)
        {
            var label = string.IsNullOrWhiteSpace(visitor.Company)
                ? visitor.VisitorName
                : $"{visitor.VisitorName} ({visitor.Company})";
            calendar.Add(new SpotCalendarEntryDto(
                SpotCalendarKind.VisitorBooking, SiteTime.Today(visitor.StartUtc, timeZone),
                visitor.StartUtc, visitor.EndUtc, label, null, null, 0, false, false));
        }

        // A released day with no booking on it is the third thing a manager needs to see on the
        // calendar: capacity that is on offer and going unclaimed.
        var bookedDates = reservations
            .Where(r => r.Status is not ReservationStatus.Cancelled)
            .SelectMany(r => LocalDaysOf(r.StartUtc, r.EndUtc, timeZone))
            .ToHashSet();
        foreach (var date in releases.Where(d => !bookedDates.Contains(d)))
        {
            var (dayStart, dayEnd) = SiteTime.Day(date, timeZone);
            calendar.Add(new SpotCalendarEntryDto(
                SpotCalendarKind.ResidentRelease, date, dayStart, dayEnd, null, null, null, 0, false, false));
        }

        var mismatches = await dbContext.OccupancyMismatches.AsNoTracking()
            .Where(m => m.SpotId == spotId)
            .OrderByDescending(m => m.ReportedAtUtc)
            .Take(20)
            .Select(m => new SpotMismatchSummaryDto(
                m.ReportedAtUtc,
                dbContext.Users.Where(u => u.Id == m.ReporterId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault() ?? string.Empty,
                m.BlockerPlate,
                m.RelocatedToSpotId != null))
            .ToListAsync(cancellationToken);

        // Stats look backwards over the signal window; the calendar above looks forwards. Mixing the
        // two would make "utilization" mean "how much of the future is already booked".
        var today = SiteTime.Today(now, timeZone);
        var statisticsFrom = today.AddDays(-(SignalWindowDays - 1));
        var statisticsHonoured = await HonouredDaysAsync(dbContext, timeZone, statisticsFrom, today, [spotId], cancellationToken);
        var stats = (await ComputeUtilizationAsync(dbContext, timeZone, statisticsFrom, today, today,
                [spotId], statisticsHonoured, cancellationToken))
            .FirstOrDefault()
            ?? new SpotUtilizationDto(spot.Id, spot.Code, spot.Type, ownerName, 0, SignalWindowDays, 0, 0, 0, 0, 0);

        // The headline state describes today, resolved through the same precedence the board uses.
        var (todayStart, todayEnd) = SiteTime.Day(today, timeZone);
        var liveToday = reservations
            .Where(r => r.Status is ReservationStatus.Reserved or ReservationStatus.CheckedIn
                && r.StartUtc < todayEnd && r.EndUtc > todayStart)
            .OrderByDescending(r => r.Status == ReservationStatus.CheckedIn)
            .Select(r => (ReservationStatus?)r.Status)
            .FirstOrDefault();
        var offeredFromQueue = await dbContext.QueueEntries.AsNoTracking()
            .AnyAsync(q => q.Status == QueueEntryStatus.Offered && q.OfferedSpotId == spotId
                && q.OfferExpiresAtUtc > now && q.StartUtc < todayEnd && q.EndUtc > todayStart, cancellationToken);
        var state = ResolveState(spot.IsActive, liveToday,
            visitorBookings.Any(v => v.StartUtc < todayEnd && v.EndUtc > todayStart),
            offeredFromQueue,
            spot.OwnerId is not null,
            releases.Contains(today));

        var trend = await ComputeSpotTrendAsync(dbContext, timeZone,
            today.AddDays(-(SignalWindowDays - 1)), today, spotId, cancellationToken);

        return new SpotDetailDto(spot.Id, spot.Code, spot.Type, spot.IsActive, spot.Notes, spot.OwnerId, ownerName,
            spot.PlannedUseDays, spot.AutoReleaseUnplannedDays, state,
            calendar.OrderBy(e => e.StartUtc).ToList(), mismatches, stats, trend);
    }

    public async Task<LotAnalyticsDto> GetAnalyticsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var timeZone = await siteSettings.GetTimeZoneAsync(cancellationToken);
        if (to < from)
        {
            (from, to) = (to, from);
        }

        if (to.DayNumber - from.DayNumber >= MaxAnalyticsDays)
        {
            from = to.AddDays(-(MaxAnalyticsDays - 1));
        }

        var windowDays = to.DayNumber - from.DayNumber + 1;

        // Keyed on the clamped window, so the three window choices the page offers each get their own
        // entry rather than fighting over one.
        var key = $"{CachePrefix}analytics:{CacheGeneration}:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}";
        if (cache.TryGetValue(key, out var cached) && cached is LotAnalyticsDto hit)
        {
            return hit;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var allSpots = await dbContext.ParkingSpots.AsNoTracking()
            .Select(s => new { s.Id, s.IsActive, s.OwnerId })
            .ToListAsync(cancellationToken);
        // The per-spot table lists every spot, including retired ones, because their history is still
        // worth reading. Everything that divides by capacity counts active spots only, on both sides
        // of the fraction.
        var spotIds = allSpots.Select(s => s.Id).ToList();
        var activeSpotIds = allSpots.Where(s => s.IsActive).Select(s => s.Id).ToList();
        var today = SiteTime.Today(timeProvider.GetUtcNow(), timeZone);
        // Utilization includes retired spots, whereas lot-wide capacity deliberately does not.
        // Load the broad set once and derive the active-only view below instead of issuing the
        // same reservations/visitor-bookings query twice for a cache miss.
        var honouredAll = await HonouredDaysAsync(dbContext, timeZone, from, to, spotIds, cancellationToken);
        var spots = await ComputeUtilizationAsync(dbContext, timeZone, from, to, today, spotIds, honouredAll, cancellationToken);

        var (rangeStart, _) = SiteTime.Day(from, timeZone);
        var (_, rangeEnd) = SiteTime.Day(to, timeZone);
        // Containment on the end, not overlap: a booking that ran out inside the window is one the
        // window can be held responsible for. Overlap pulled in bookings that merely brushed the
        // boundary — and, through a stray AddDays(1), the whole day after the window as well.
        var resolved = await dbContext.Reservations.AsNoTracking()
            .Where(r => r.EndUtc > rangeStart && r.EndUtc <= rangeEnd)
            .Select(r => new { r.Status, r.CreatedAtUtc, r.StartUtc, r.CreditsCharged, r.IsOffPeak, r.FromQueue })
            .ToListAsync(cancellationToken);

        var demandWindows = await dbContext.Reservations.AsNoTracking()
            .Where(r => r.Status != ReservationStatus.Cancelled && r.StartUtc < rangeEnd && r.EndUtc > rangeStart)
            .Select(r => new { r.StartUtc, r.EndUtc })
            .ToListAsync(cancellationToken);

        // Demand is measured in occupied spot-hours, not bookings: an 8-hour booking presses on the
        // lot for eight hours, and a count of bookings would flatten that into one tick at its start.
        // No-shows are in here on purpose — the spot was asked for and withheld from everyone else,
        // which is what demand means. Utilization is the other question and answers it separately.
        var demand = new Dictionary<(DayOfWeek Day, int Hour), int>();
        foreach (var window in demandWindows)
        {
            AddHours(demand, window.StartUtc, window.EndUtc, timeZone);
        }

        var peak = demand.Count == 0 ? null : (KeyValuePair<(DayOfWeek Day, int Hour), int>?)demand.MaxBy(cell => cell.Value);
        var activeSpotIdSet = activeSpotIds.ToHashSet();
        var honoured = honouredAll.Where(day => activeSpotIdSet.Contains(day.SpotId)).ToHashSet();
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);

        var analytics = new LotAnalyticsDto(
            From: from,
            To: to,
            WindowDays: windowDays,
            Spots: spots,
            Daily: BuildDailyOccupancy(honoured, activeSpotIds.Count, from, to),
            Demand: demand.Select(cell => new DemandCellDto(cell.Key.Day, cell.Key.Hour, cell.Value))
                .OrderBy(cell => cell.DayOfWeek).ThenBy(cell => cell.Hour).ToList(),
            PeakHour: peak?.Key.Hour,
            PeakDayOfWeek: peak?.Key.Day,
            Capacity: await ComputeCapacityUseAsync(dbContext, honoured,
                allSpots.Where(s => s.IsActive).Select(s => (s.Id, s.OwnerId)).ToList(), from, to, today, cancellationToken),
            Queue: await ComputeQueueDemandAsync(dbContext, timeZone, rangeStart, rangeEnd, cancellationToken),
            Reliability: BuildReliability(resolved.Select(r => (r.Status, r.CreatedAtUtc, r.StartUtc))),
            Peak: BuildPeakWindow(demand, policy.PeakStart, policy.PeakEnd),
            Economy: await BuildEconomyAsync(dbContext, rangeStart, rangeEnd,
                resolved.Select(r => (r.Status, r.CreditsCharged, r.IsOffPeak, r.FromQueue)), cancellationToken));

        using var entry = cache.CreateEntry(key);
        entry.Value = analytics;
        entry.AbsoluteExpirationRelativeToNow = AggregateCacheTtl;
        return analytics;
    }

    /// <summary>Adds one tick per local hour a window covers, into a weekday × hour tally.</summary>
    private static void AddHours(Dictionary<(DayOfWeek Day, int Hour), int> tally,
        DateTimeOffset startUtc, DateTimeOffset endUtc, TimeZoneInfo timeZone)
    {
        var hour = TimeZoneInfo.ConvertTime(startUtc, timeZone);
        var localEnd = TimeZoneInfo.ConvertTime(endUtc, timeZone);
        // A weekday × hour map saturates after a week; anything longer only repeats itself, and a
        // pathological window would otherwise spin for as many hours as it spans.
        for (var counted = 0; hour < localEnd && counted < 24 * 7; hour = hour.AddHours(1), counted++)
        {
            var cell = (hour.DayOfWeek, hour.Hour);
            tally[cell] = tally.GetValueOrDefault(cell) + 1;
        }
    }

    /// <summary>
    /// Where the window's capacity went, as a partition of the active spot-days. Every spot-day lands
    /// in exactly one bucket, so the four add back up to capacity and the manager can check them —
    /// which is the whole point of splitting them out instead of reporting one utilization figure.
    /// </summary>
    private static async Task<CapacityUseDto> ComputeCapacityUseAsync(
        D3ParkingDbContext dbContext, HashSet<(Guid SpotId, DateOnly Date)> honoured,
        IReadOnlyList<(Guid Id, Guid? OwnerId)> activeSpots, DateOnly from, DateOnly to, DateOnly today,
        CancellationToken cancellationToken)
    {
        var activeIds = activeSpots.Select(s => s.Id).ToList();
        var released = (await dbContext.SpotReleases.AsNoTracking()
                .Where(r => activeIds.Contains(r.SpotId) && r.Date >= from && r.Date <= to)
                .Select(r => new { r.SpotId, r.Date })
                .ToListAsync(cancellationToken))
            .Select(r => (r.SpotId, r.Date))
            .ToHashSet();

        var windowDays = to.DayNumber - from.DayNumber + 1;
        int used = 0, offeredUnused = 0, heldDays = 0, idle = 0;
        var deadSpots = 0;

        foreach (var (spotId, ownerId) in activeSpots)
        {
            var usedHere = 0;
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                if (honoured.Contains((spotId, date)))
                {
                    used++;
                    usedHere++;
                }
                else if (released.Contains((spotId, date)) && date < today)
                {
                    // Offered and the day has passed with nobody on it. A release still ahead of us
                    // is an open offer, not waste, and falls through to idle with the rest.
                    offeredUnused++;
                }
                else if (ownerId is not null && !released.Contains((spotId, date)))
                {
                    heldDays++;
                }
                else
                {
                    idle++;
                }
            }

            if (usedHere == 0)
            {
                deadSpots++;
            }
        }

        var capacityDays = activeSpots.Count * windowDays;
        return new CapacityUseDto(
            ActiveSpots: activeSpots.Count,
            WindowDays: windowDays,
            CapacityDays: capacityDays,
            UsedDays: used,
            OfferedUnusedDays: offeredUnused,
            ResidentHeldDays: heldDays,
            IdleDays: idle,
            UsePercent: capacityDays == 0 ? 0 : (int)Math.Round(used * 100.0 / capacityDays),
            DeadSpots: deadSpots);
    }

    /// <summary>
    /// Demand the lot turned away, from the waitlist. Entries are counted by when they were created,
    /// because that is when somebody was refused; the hours they asked for are mapped separately so
    /// the refusals can be read against the demand that did get in.
    /// </summary>
    private static async Task<QueueDemandDto> ComputeQueueDemandAsync(
        D3ParkingDbContext dbContext, TimeZoneInfo timeZone, DateTimeOffset rangeStart, DateTimeOffset rangeEnd,
        CancellationToken cancellationToken)
    {
        var entries = await dbContext.QueueEntries.AsNoTracking()
            .Where(q => q.CreatedAtUtc >= rangeStart && q.CreatedAtUtc < rangeEnd)
            .Select(q => new { q.Status, q.StartUtc, q.EndUtc })
            .ToListAsync(cancellationToken);

        var refused = new Dictionary<(DayOfWeek Day, int Hour), int>();
        foreach (var entry in entries)
        {
            AddHours(refused, entry.StartUtc, entry.EndUtc, timeZone);
        }

        var claimed = entries.Count(e => e.Status == QueueEntryStatus.Claimed);
        var expired = entries.Count(e => e.Status == QueueEntryStatus.Expired);
        var cancelled = entries.Count(e => e.Status == QueueEntryStatus.Cancelled);
        var open = entries.Count(e => e.Status is QueueEntryStatus.Waiting or QueueEntryStatus.Offered);
        var settled = claimed + expired + cancelled;

        return new QueueDemandDto(
            Entries: entries.Count,
            Claimed: claimed,
            Expired: expired,
            Cancelled: cancelled,
            Open: open,
            // Against the entries that reached an outcome: one still waiting has not failed yet, and
            // counting it as a miss would make a busy morning look like a broken waitlist.
            ConversionPercent: settled == 0 ? 0 : (int)Math.Round(claimed * 100.0 / settled),
            Refused: refused.Select(cell => new DemandCellDto(cell.Key.Day, cell.Key.Hour, cell.Value))
                .OrderBy(cell => cell.DayOfWeek).ThenBy(cell => cell.Hour).ToList());
    }

    /// <summary>
    /// What became of planned bookings whose window ran out inside the period. A still-reserved row
    /// is an honoured plan: the planner deliberately does not ask for presence confirmation.
    /// </summary>
    private static ReliabilityDto BuildReliability(
        IEnumerable<(ReservationStatus Status, DateTimeOffset CreatedAtUtc, DateTimeOffset StartUtc)> ended)
    {
        var all = ended.ToList();
        var completed = all.Count(r => r.Status is ReservationStatus.Reserved
            or ReservationStatus.Completed or ReservationStatus.CheckedIn);
        var released = all.Count(r => r.Status == ReservationStatus.Released);
        var noShows = 0;
        var cancelled = all.Count(r => r.Status == ReservationStatus.Cancelled);
        var total = completed + released;

        // How far ahead the lot gets planned. Median rather than mean: one booking made three months
        // out would drag an average somewhere no actual booking sits. Over the same three outcomes the
        // percentages are built on, so the whole strip of tiles describes one population.
        var leads = all.Where(r => IsResolved(r.Status))
            .Select(r => (r.StartUtc - r.CreatedAtUtc).TotalHours)
            .Where(hours => hours >= 0)
            .OrderBy(hours => hours)
            .ToList();

        return new ReliabilityDto(
            Resolved: total,
            Completed: completed,
            Released: released,
            NoShows: noShows,
            Cancelled: cancelled,
            NoShowPercent: total == 0 ? 0 : (int)Math.Round(noShows * 100.0 / total),
            MedianLeadHours: leads.Count == 0 ? null : (int)Math.Round(leads[leads.Count / 2]));
    }

    /// <summary>
    /// The busiest stretch of the day the window actually shows, cut to the same length as the peak
    /// window the pricing is configured with so the two are comparable at a glance. Hours are summed
    /// across weekdays: the price setting is one window for every day, so the evidence for it has to
    /// be too.
    /// </summary>
    private static PeakWindowDto BuildPeakWindow(
        Dictionary<(DayOfWeek Day, int Hour), int> demand, TimeOnly configuredStart, TimeOnly configuredEnd)
    {
        // Rounded up, not to nearest: the default peak is 07:30–10:00, and to-nearest turns two and a
        // half hours into two, so the suggestion would quietly cover less ground than the window it is
        // being compared with. Erring long keeps it an honest "a window your size belongs here".
        var configuredHours = Math.Clamp((int)Math.Ceiling((configuredEnd - configuredStart).TotalHours), 1, 24);
        if (demand.Count == 0)
        {
            return new PeakWindowDto(null, null, configuredStart, configuredEnd, Matches: false);
        }

        var perHour = new int[24];
        foreach (var (cell, count) in demand)
        {
            perHour[cell.Hour] += count;
        }

        var bestStart = 0;
        var bestTotal = -1;
        // Wrapping on purpose: a lot whose pressure lands late evening to early morning has a real
        // peak that a non-wrapping scan would cut in half and miss.
        for (var start = 0; start < 24; start++)
        {
            var total = 0;
            for (var offset = 0; offset < configuredHours; offset++)
            {
                total += perHour[(start + offset) % 24];
            }

            if (total > bestTotal)
            {
                bestTotal = total;
                bestStart = start;
            }
        }

        var suggestedEnd = (bestStart + configuredHours) % 24;
        return new PeakWindowDto(bestStart, suggestedEnd, configuredStart, configuredEnd,
            // The suggestion is whole hours, so a configured 7:30 is "the same" as a suggested 7:00
            // only if nobody minds the half hour — hence the comparison is on the hour it starts.
            Matches: bestStart == configuredStart.Hour);
    }

    /// <summary>
    /// A booking that reached one of the planner outcomes. Everything
    /// that describes "the bookings this window is responsible for" runs off this one predicate, so
    /// the funnel, the lead time and the economy shares cannot end up describing different sets.
    /// A cancelled booking is not one of them: it was called off, refunded, and cost nobody a spot.
    /// Reserved is the normal final planner outcome once its time window has ended.
    /// </summary>
    private static bool IsResolved(ReservationStatus status) =>
        status is ReservationStatus.Reserved or ReservationStatus.Completed
            or ReservationStatus.CheckedIn or ReservationStatus.Released;

    /// <summary>
    /// What the window cost. Charges come from the same resolved bookings the funnel counts; refunds
    /// come from the ledger, because a refund is its own event and the booking it reversed may well
    /// have been made before the window started.
    /// </summary>
    private static async Task<LotEconomyDto> BuildEconomyAsync(
        D3ParkingDbContext dbContext, DateTimeOffset rangeStart, DateTimeOffset rangeEnd,
        IEnumerable<(ReservationStatus Status, int CreditsCharged, bool IsOffPeak, bool FromQueue)> ended,
        CancellationToken cancellationToken)
    {
        // A cancelled booking was refunded and is counted on the refund side; leaving it in the
        // charges would make the lot look like it earned twice what it did.
        var kept = ended.Where(r => IsResolved(r.Status)).ToList();
        var refunded = await dbContext.PointsLedgerEntries.AsNoTracking()
            .Where(e => e.Reason == IncentiveReason.ReservationRefund
                && e.OccurredAtUtc >= rangeStart && e.OccurredAtUtc < rangeEnd)
            .SumAsync(e => e.Points, cancellationToken);

        return new LotEconomyDto(
            CreditsCharged: kept.Sum(r => r.CreditsCharged),
            CreditsRefunded: refunded,
            OffPeakPercent: kept.Count == 0 ? 0 : (int)Math.Round(kept.Count(r => r.IsOffPeak) * 100.0 / kept.Count),
            FromQueuePercent: kept.Count == 0 ? 0 : (int)Math.Round(kept.Count(r => r.FromQueue) * 100.0 / kept.Count));
    }

    /// <summary>
    /// Every aggregate key carries this number, so invalidating is one write instead of a list of keys
    /// to remove. Enumerating keys would mean knowing every window the UI might ask for — miss one and
    /// it silently serves a stale answer. Orphaned entries are simply unreachable and expire on their
    /// own TTL. A lost race on the increment costs at most one TTL of staleness, not correctness.
    /// </summary>
    private long CacheGeneration =>
        cache.TryGetValue($"{CachePrefix}generation", out var value) && value is long generation ? generation : 0;

    /// <summary>
    /// Makes the cached aggregates unreachable. Called after an override: the booking the manager just
    /// cancelled or moved is counted in those numbers, and showing them unchanged would read as the
    /// intervention not having happened.
    /// </summary>
    private void InvalidateAggregates()
    {
        using var entry = cache.CreateEntry($"{CachePrefix}generation");
        entry.Value = CacheGeneration + 1;
    }

    /// <summary>
    /// The days a single spot carried a booking somebody stood by, oldest first — the spot's own load
    /// over time. Same "honoured, not merely booked" rule as the utilization figures.
    /// </summary>
    private static async Task<List<SpotDayDto>> ComputeSpotTrendAsync(
        D3ParkingDbContext dbContext, TimeZoneInfo timeZone, DateOnly from, DateOnly to, Guid spotId,
        CancellationToken cancellationToken)
    {
        var busy = await HonouredDaysAsync(dbContext, timeZone, from, to, [spotId], cancellationToken);
        var days = new List<SpotDayDto>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            days.Add(new SpotDayDto(date, busy.Contains((spotId, date))));
        }

        return days;
    }

    /// <summary>
    /// Every (spot, local day) pair that carried a booking somebody stood by in the window — live,
    /// arrived or completed reservations plus reception visitor bookings. A cancelled booking occupied
    /// nothing and a no-show occupied the spot only on paper, so neither is here. The single source of
    /// "this spot worked that day" behind the utilization figures, the daily curve and a spot's trend.
    /// </summary>
    private static async Task<HashSet<(Guid SpotId, DateOnly Date)>> HonouredDaysAsync(
        D3ParkingDbContext dbContext, TimeZoneInfo timeZone, DateOnly from, DateOnly to,
        IReadOnlyList<Guid> spotIds, CancellationToken cancellationToken)
    {
        var (rangeStart, _) = SiteTime.Day(from, timeZone);
        var (_, rangeEnd) = SiteTime.Day(to, timeZone);

        var reservations = await dbContext.Reservations.AsNoTracking()
            .Where(r => spotIds.Contains(r.SpotId) && r.StartUtc < rangeEnd && r.EndUtc > rangeStart
                && (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn
                    || r.Status == ReservationStatus.Completed))
            .Select(r => new { r.SpotId, r.StartUtc, r.EndUtc })
            .ToListAsync(cancellationToken);

        var visitorBookings = await dbContext.VisitorBookings.AsNoTracking()
            .Where(v => spotIds.Contains(v.SpotId) && v.Status == VisitorBookingStatus.Booked
                && v.StartUtc < rangeEnd && v.EndUtc > rangeStart)
            .Select(v => new { v.SpotId, v.StartUtc, v.EndUtc })
            .ToListAsync(cancellationToken);

        return reservations.Select(r => (r.SpotId, r.StartUtc, r.EndUtc))
            .Concat(visitorBookings.Select(v => (v.SpotId, v.StartUtc, v.EndUtc)))
            .SelectMany(x => LocalDaysOf(x.StartUtc, x.EndUtc, timeZone)
                .Where(day => day >= from && day <= to)
                .Select(day => (x.SpotId, Date: day)))
            .ToHashSet();
    }

    /// <summary>
    /// How many spots each day of the window carried, oldest first. The denominator is today's active
    /// spot count — see <see cref="DailyOccupancyDto"/> for why there cannot be a per-day one, and why
    /// both sides of the fraction have to count the same spots.
    /// </summary>
    private static List<DailyOccupancyDto> BuildDailyOccupancy(
        HashSet<(Guid SpotId, DateOnly Date)> honoured, int capacity, DateOnly from, DateOnly to)
    {
        var perDay = honoured.GroupBy(x => x.Date).ToDictionary(g => g.Key, g => g.Count());

        var daily = new List<DailyOccupancyDto>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var occupied = perDay.GetValueOrDefault(date);
            daily.Add(new DailyOccupancyDto(date, occupied, capacity,
                capacity == 0 ? 0 : (int)Math.Round(occupied * 100.0 / capacity)));
        }

        return daily;
    }

    /// <summary>
    /// Utilization per spot over an inclusive local-day window: the share of days that carried a
    /// booking somebody stood by, plus the signals that say a spot is being wasted (no-shows,
    /// blocked-spot reports, resident days shared into nobody's hands). Busiest first.
    /// </summary>
    private static async Task<List<SpotUtilizationDto>> ComputeUtilizationAsync(
        D3ParkingDbContext dbContext, TimeZoneInfo timeZone, DateOnly from, DateOnly to, DateOnly today,
        IReadOnlyList<Guid> spotIds, HashSet<(Guid SpotId, DateOnly Date)> honoured, CancellationToken cancellationToken)
    {
        var windowDays = to.DayNumber - from.DayNumber + 1;
        var (rangeStart, _) = SiteTime.Day(from, timeZone);
        var (_, rangeEnd) = SiteTime.Day(to, timeZone);

        var spots = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => spotIds.Contains(s.Id))
            .Select(s => new
            {
                s.Id,
                s.Code,
                s.Type,
                s.OwnerId,
                OwnerName = s.OwnerId == null
                    ? null
                    : dbContext.Users.Where(u => u.Id == s.OwnerId).Select(u => u.DisplayName ?? u.Email).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        // Only the no-show tally needs the statuses the honoured set deliberately leaves out.
        var noShows = (await dbContext.Reservations.AsNoTracking()
                .Where(r => spotIds.Contains(r.SpotId) && r.Status == ReservationStatus.NoShow
                    && r.StartUtc < rangeEnd && r.EndUtc > rangeStart)
                .Select(r => r.SpotId)
                .ToListAsync(cancellationToken))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        var mismatches = (await dbContext.OccupancyMismatches.AsNoTracking()
                .Where(m => spotIds.Contains(m.SpotId) && m.StartUtc < rangeEnd && m.EndUtc > rangeStart)
                .Select(m => m.SpotId)
                .ToListAsync(cancellationToken))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        var releases = await dbContext.SpotReleases.AsNoTracking()
            .Where(r => spotIds.Contains(r.SpotId) && r.Date >= from && r.Date <= to)
            .Select(r => new { r.SpotId, r.Date, r.ReconciledAtUtc, r.AwardedPoints, r.ClawedBackPoints })
            .ToListAsync(cancellationToken);

        var busyDaysBySpot = honoured
            .ToLookup(day => day.SpotId, day => day.Date);
        var releasesBySpot = releases.ToLookup(release => release.SpotId);
        var result = new List<SpotUtilizationDto>(spots.Count);
        foreach (var spot in spots)
        {
            var busyDays = busyDaysBySpot[spot.Id].ToHashSet();
            var spotReleases = releasesBySpot[spot.Id];

            result.Add(new SpotUtilizationDto(
                SpotId: spot.Id,
                Code: spot.Code,
                Type: spot.Type,
                OwnerName: spot.OwnerName,
                BookedDays: busyDays.Count,
                WindowDays: windowDays,
                UtilizationPercent: windowDays == 0 ? 0 : (int)Math.Round(busyDays.Count * 100.0 / windowDays),
                NoShows: noShows.GetValueOrDefault(spot.Id),
                Mismatches: mismatches.GetValueOrDefault(spot.Id),
                SharedDays: spotReleases.Count(),
                // Same rule as CountUnusedSharedDaysAsync: shared and nobody took it. Only days that
                // have already passed count — a shared day still ahead of us is an offer, not waste.
                UnusedSharedDays: spotReleases.Count(r => r.Date < today && !busyDays.Contains(r.Date))));
        }

        return result
            .OrderByDescending(s => s.UtilizationPercent)
            .ThenByDescending(s => s.BookedDays)
            .ThenBy(s => s.Code, SpotCodeComparer.Instance)
            .ToList();
    }

    /// <summary>
    /// Resident days put into the pool in [from, toExclusive) that no booking ever honoured — capacity
    /// a resident gave up and nobody took. Deliberately derived from bookings rather than from the
    /// release's reward state: a day released past the reward cutoff carries no points to reverse,
    /// so the reconciliation never touches it, yet it was just as unused.
    /// </summary>
    /// <remarks>
    /// Takes the honoured set rather than deriving its own. It used to run its own query with its own
    /// rule — any non-cancelled reservation, visitor bookings ignored — which made a released day the
    /// taker no-showed on "not wasted" here and "wasted" in the per-spot table beside it. One rule,
    /// one answer.
    /// </remarks>
    private static async Task<int> CountUnusedSharedDaysAsync(
        D3ParkingDbContext dbContext, DateOnly from, DateOnly toExclusive, IReadOnlyList<Guid> activeSpotIds,
        HashSet<(Guid SpotId, DateOnly Date)> honoured, CancellationToken cancellationToken)
    {
        // Scoped to the same spots the honoured set covers. A release on a spot that has since been
        // deactivated has no honoured days to be checked against, so counting it would conjure waste.
        var releases = await dbContext.SpotReleases.AsNoTracking()
            .Where(r => activeSpotIds.Contains(r.SpotId) && r.Date >= from && r.Date < toExclusive)
            .Select(r => new { r.SpotId, r.Date })
            .ToListAsync(cancellationToken);

        return releases.Count(r => !honoured.Contains((r.SpotId, r.Date)));
    }

    /// <summary>Every local calendar day a UTC window touches; a window can span more than one.</summary>
    private static IEnumerable<DateOnly> LocalDaysOf(DateTimeOffset startUtc, DateTimeOffset endUtc, TimeZoneInfo timeZone)
    {
        var first = SiteTime.Today(startUtc, timeZone);
        // The end is exclusive: a booking ending at midnight belongs to the day before it.
        var last = SiteTime.Today(endUtc.AddTicks(-1), timeZone);
        return Enumerable.Range(0, Math.Max(0, last.DayNumber - first.DayNumber) + 1).Select(first.AddDays);
    }

    public async Task<IReadOnlyList<MoveTargetDto>> GetMoveTargetsAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reservation = await dbContext.Reservations.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);
        if (reservation is null || reservation.Status is not (ReservationStatus.Reserved or ReservationStatus.CheckedIn)
            || reservation.EndUtc <= now)
        {
            return [];
        }

        return await FreeSpotsForWindowAsync(dbContext, reservation.SpotId, reservation.StartUtc, reservation.EndUtc,
            now, cancellationToken);
    }

    /// <summary>
    /// Active non-visitor spots with nothing on them for the whole window, excluding the spot the
    /// booking already sits on. Unlike the booking path this ignores residency: the manager is
    /// resolving a physical conflict and may put a car on a resident spot deliberately — the tile
    /// tells them whose it is.
    /// </summary>
    private static async Task<List<MoveTargetDto>> FreeSpotsForWindowAsync(
        D3ParkingDbContext dbContext, Guid excludeSpotId, DateTimeOffset startUtc, DateTimeOffset endUtc,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var taken = dbContext.Reservations
            .Where(r => (r.Status == ReservationStatus.Reserved || r.Status == ReservationStatus.CheckedIn)
                && r.StartUtc < endUtc && r.EndUtc > startUtc)
            .Select(r => r.SpotId);
        var visitorTaken = dbContext.VisitorBookings
            .Where(v => v.Status == VisitorBookingStatus.Booked && v.StartUtc < endUtc && v.EndUtc > startUtc)
            .Select(v => v.SpotId);
        // A spot held for a waitlist offer is promised to someone; dropping a moved car on it would
        // make their claim land on a taken spot.
        var held = dbContext.QueueEntries
            .Where(q => q.Status == QueueEntryStatus.Offered && q.OfferedSpotId != null
                && q.OfferExpiresAtUtc > now && q.StartUtc < endUtc && q.EndUtc > startUtc)
            .Select(q => q.OfferedSpotId!.Value);

        var free = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.IsActive && s.Id != excludeSpotId && s.Type != ParkingSpotType.Visitor
                && !taken.Contains(s.Id) && !visitorTaken.Contains(s.Id) && !held.Contains(s.Id))
            .Select(s => new MoveTargetDto(s.Id, s.Code, s.Type))
            .ToListAsync(cancellationToken);

        // Sorted in memory, not in SQL: a database string sort offers D3-10 before D3-2, and the
        // manager picking a target from a dropdown reads codes as numbers like everywhere else.
        return free.OrderBy(s => s.Code, SpotCodeComparer.Instance).ToList();
    }

    public Task<ParkingResult> CancelReservationAsync(Guid reservationId, Guid actingUserId, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => CancelReservationCoreAsync(reservationId, actingUserId, cancellationToken), cancellationToken);

    // No explicit transaction, for the same reason as the holder's own cancel: one SaveChanges is
    // atomic and the reservation's rowversion turns a concurrent cancel/sweep into a retry that
    // lands on the InvalidState guard rather than refunding twice.
    private async Task<ParkingResult> CancelReservationCoreAsync(Guid reservationId, Guid actingUserId, CancellationToken cancellationToken)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reservation = await dbContext.Reservations.FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);
        if (reservation is null)
        {
            return ParkingResult.Failure("Parking_Error_ReservationNotFound");
        }

        if (reservation.Status != ReservationStatus.Reserved)
        {
            return ParkingResult.Failure("Parking_Error_InvalidState");
        }

        if (reservation.EndUtc <= now)
        {
            return ParkingResult.Failure("Parking_Error_PastWindow");
        }

        var spotCode = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.Id == reservation.SpotId).Select(s => s.Code).FirstAsync(cancellationToken);

        reservation.Cancel();

        // Full refund however late, and the voucher back: unlike the holder's own late cancel this is
        // the lot being wrong, not the driver changing their mind — the same no-fault rule the
        // blocked-spot report follows.
        if (reservation.CreditsCharged > 0)
        {
            var score = await GetOrCreateScoreAsync(dbContext, reservation.UserId, cancellationToken);
            score.RefundCredits(reservation.CreditsCharged, now);
            dbContext.PointsLedgerEntries.Add(new PointsLedgerEntry(
                reservation.UserId, IncentiveReason.ReservationRefund, reservation.CreditsCharged, reservation.Id, now));
        }

        await RestoreVoucherAsync(dbContext, reservation.Id, now, cancellationToken);

        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
            reservation.UserId, AccountAuditEventType.ReservationOverridden, $"admin:{actingUserId}",
            $"Cancelled reservation {reservation.Id} on {spotCode} ({reservation.StartUtc:u}–{reservation.EndUtc:u}), refunded {reservation.CreditsCharged} credits.",
            now));

        await dbContext.SaveChangesAsync(cancellationToken);

        InvalidateAggregates();

        await notifications.NotifyAsync(reservation.UserId, NotificationCategory.Administrative, NotificationLevel.Warning,
            messages["Parking_Notify_AdminCancelled_Title"],
            messages.ForEconomy(policy, "Parking_Notify_AdminCancelled_Body", spotCode, reservation.CreditsCharged),
            cancellationToken);

        return ParkingResult.Success;
    }

    public Task<ParkingResult> MoveReservationAsync(Guid reservationId, Guid targetSpotId, Guid actingUserId, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => MoveReservationCoreAsync(reservationId, targetSpotId, actingUserId, cancellationToken), cancellationToken);

    private async Task<ParkingResult> MoveReservationCoreAsync(Guid reservationId, Guid targetSpotId, Guid actingUserId, CancellationToken cancellationToken)
    {
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // The "is the target free" check and the re-point must be one atomic step, exactly as when a
        // booking is created: at read-committed a concurrent booking of the target would pass its own
        // check and both cars would end up on one spot.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var reservation = await dbContext.Reservations.FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);
        if (reservation is null)
        {
            return ParkingResult.Failure("Parking_Error_ReservationNotFound");
        }

        if (reservation.Status is not (ReservationStatus.Reserved or ReservationStatus.CheckedIn))
        {
            return ParkingResult.Failure("Parking_Error_InvalidState");
        }

        if (reservation.EndUtc <= now)
        {
            return ParkingResult.Failure("Parking_Error_PastWindow");
        }

        if (reservation.SpotId == targetSpotId)
        {
            return ParkingResult.Failure("Parking_Error_SameSpot");
        }

        var target = await dbContext.ParkingSpots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == targetSpotId, cancellationToken);
        if (target is null || !target.IsActive || target.Type == ParkingSpotType.Visitor)
        {
            return ParkingResult.Failure("Parking_Error_SpotInactive");
        }

        var free = await FreeSpotsForWindowAsync(dbContext, reservation.SpotId, reservation.StartUtc, reservation.EndUtc, now, cancellationToken);
        if (free.All(s => s.SpotId != targetSpotId))
        {
            return ParkingResult.Failure("Parking_Error_SpotTaken");
        }

        var fromCode = await dbContext.ParkingSpots.AsNoTracking()
            .Where(s => s.Id == reservation.SpotId).Select(s => s.Code).FirstAsync(cancellationToken);

        // Re-pointed, not re-made: the price, the check-in and any voucher stay attached to the same
        // booking, so nothing moves in the wallet and the holder keeps their history.
        reservation.MoveTo(targetSpotId);

        dbContext.AccountAuditEvents.Add(new AccountAuditEvent(
            reservation.UserId, AccountAuditEventType.ReservationOverridden, $"admin:{actingUserId}",
            $"Moved reservation {reservation.Id} from {fromCode} to {target.Code} ({reservation.StartUtc:u}–{reservation.EndUtc:u}).",
            now));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        InvalidateAggregates();

        await notifications.NotifyAsync(reservation.UserId, NotificationCategory.Administrative, NotificationLevel.Warning,
            messages["Parking_Notify_AdminMoved_Title"],
            messages.ForEconomy(policy, "Parking_Notify_AdminMoved_Body", fromCode, target.Code),
            cancellationToken);

        return ParkingResult.Success;
    }

    // Mirrors ReservationService.RestoreVoucherAsync, including its cap check: an override must not
    // become the one path that re-arms a voucher past the one-unredeemed-voucher limit, or
    // redeem→report→override cycles could stockpile the value that cap exists to bound.
    private static async Task RestoreVoucherAsync(D3ParkingDbContext dbContext, Guid reservationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var voucher = await dbContext.ApologyVouchers
            .FirstOrDefaultAsync(v => v.RedeemedReservationId == reservationId, cancellationToken);
        if (voucher is null)
        {
            return;
        }

        var holdsAnotherUsable = await dbContext.ApologyVouchers.AnyAsync(v =>
            v.UserId == voucher.UserId && v.Id != voucher.Id
            && v.RedeemedAtUtc == null && v.ExpiresAtUtc > now
            && (v.Status == ApologyVoucherStatus.PendingApproval || v.Status == ApologyVoucherStatus.Approved),
            cancellationToken);
        if (!holdsAnotherUsable)
        {
            voucher.Restore();
        }
    }

    private static async Task<ParkerScore> GetOrCreateScoreAsync(D3ParkingDbContext dbContext, Guid userId, CancellationToken cancellationToken)
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
