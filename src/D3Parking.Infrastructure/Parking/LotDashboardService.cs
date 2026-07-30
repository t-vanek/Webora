using System.Data;
using Microsoft.EntityFrameworkCore;
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
public sealed class LotDashboardService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    IParkingSettingsService parkingSettings,
    ISiteSettingsService siteSettings,
    TimeProvider timeProvider,
    INotificationService notifications,
    IStringLocalizer<ParkingMessages> messages) : ILotDashboardService
{
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
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
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

        var signalFrom = now.AddDays(-SignalWindowDays);
        var mismatchesPerSpot = (await dbContext.OccupancyMismatches.AsNoTracking()
                .Where(m => m.ReportedAtUtc >= signalFrom)
                .Select(m => m.SpotId)
                .ToListAsync(cancellationToken))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        // Auto-share only applies to today and only past the cutoff: a resident spot on a future day
        // is still theirs, so the board must not paint it as pool capacity.
        var autoShareActive = isToday && policy.IsResidentAutoShareActive(date, now, timeZone);

        var tiles = new List<SpotTileDto>(spots.Count);
        foreach (var spot in spots)
        {
            var reservation = bookings
                .Where(b => b.SpotId == spot.Id)
                // Checked-in first, then whatever starts earliest: the tile should show the car that
                // is standing there over a booking later in the day.
                .OrderByDescending(b => b.Status == ReservationStatus.CheckedIn)
                .ThenBy(b => b.StartUtc)
                .FirstOrDefault();
            var visitor = visitors.Where(v => v.SpotId == spot.Id).OrderBy(v => v.StartUtc).FirstOrDefault();

            var state = ResolveState(spot.IsActive, reservation?.Status, visitor is not null, spot.OwnerId is not null,
                releasedSpotIds.Contains(spot.Id) || autoShareActive);
            var holder = reservation?.HolderName ?? visitor?.VisitorName;
            var from = reservation?.StartUtc ?? visitor?.StartUtc;
            var to = reservation?.EndUtc ?? visitor?.EndUtc;

            tiles.Add(new SpotTileDto(spot.Id, spot.Code, SectionOf(spot.Code), spot.Type, spot.IsActive,
                spot.OwnerId, spot.OwnerName, state, holder, from, to,
                mismatchesPerSpot.GetValueOrDefault(spot.Id)));
        }

        var queueWaiting = await dbContext.QueueEntries.AsNoTracking()
            .CountAsync(q => (q.Status == QueueEntryStatus.Waiting || q.Status == QueueEntryStatus.Offered)
                && q.StartUtc < dayEnd && q.EndUtc > dayStart, cancellationToken);
        var openMismatches = await dbContext.OccupancyMismatches.AsNoTracking()
            .CountAsync(m => m.ReportedAtUtc >= signalFrom, cancellationToken);
        var unusedSharedDays = await CountUnusedSharedDaysAsync(
            dbContext, timeZone, today.AddDays(-(SignalWindowDays - 1)), today, cancellationToken);

        var occupied = tiles.Count(t => t.State == SpotBoardState.Occupied);
        var booked = tiles.Count(t => t.State is SpotBoardState.Booked or SpotBoardState.VisitorBooked);
        var activeSpots = tiles.Count(t => t.IsActive);
        var free = tiles.Count(t => t.State is SpotBoardState.Free or SpotBoardState.ResidentShared);
        var held = tiles.Count(t => t.State == SpotBoardState.ResidentHeld);
        // Held resident spots are not "taken" but not offerable either; counting them out of the
        // denominator would read as 100 % full on a quiet day with many residents.
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
            Free: free,
            OccupancyPercent: bookable <= 0 ? 0 : (int)Math.Round((occupied + booked) * 100.0 / bookable),
            QueueWaiting: queueWaiting,
            VisitorBookings: visitors.Count,
            OpenMismatches: openMismatches,
            UnusedSharedDays: unusedSharedDays);

        // Section first, then the code read as a number, so D3-2 precedes D3-10 and the unnamed
        // section (codes with no letter prefix) sorts ahead of the named ones.
        var ordered = tiles
            .OrderBy(t => t.Section, SpotCodeComparer.Instance)
            .ThenBy(t => t.Code, SpotCodeComparer.Instance)
            .ToList();

        var trends = await ComputeSummaryTrendsAsync(dbContext, timeZone, today, cancellationToken);
        return new LotBoardDto(date, isToday, overview, trends, ordered);
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
        var spotIds = await dbContext.ParkingSpots.AsNoTracking().Select(s => s.Id).ToListAsync(cancellationToken);
        var occupancy = await ComputeDailyOccupancyAsync(dbContext, timeZone, from, today, spotIds, cancellationToken);

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
            .Where(r => r.Date >= from && r.Date < today)
            .Select(r => new { r.SpotId, r.Date })
            .ToListAsync(cancellationToken);
        var honoured = await HonouredDaysAsync(dbContext, timeZone, from, today, spotIds, cancellationToken);
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
        bool hasOwner, bool sharedForTheDay) =>
        (isActive, liveBooking, hasVisitorBooking, hasOwner) switch
        {
            (false, _, _, _) => SpotBoardState.Inactive,
            (_, ReservationStatus.CheckedIn, _, _) => SpotBoardState.Occupied,
            (_, not null, _, _) => SpotBoardState.Booked,
            (_, _, true, _) => SpotBoardState.VisitorBooked,
            (_, _, _, true) => sharedForTheDay ? SpotBoardState.ResidentShared : SpotBoardState.ResidentHeld,
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
                CanCancel: reservation.Status == ReservationStatus.Reserved,
                CanMove: live));
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
        var stats = (await ComputeUtilizationAsync(dbContext, timeZone, today.AddDays(-(SignalWindowDays - 1)), today, today,
                [spotId], cancellationToken))
            .FirstOrDefault()
            ?? new SpotUtilizationDto(spot.Id, spot.Code, spot.Type, ownerName, 0, SignalWindowDays, 0, 0, 0, 0, 0);

        // The headline state describes today, resolved through the same precedence the board uses.
        var policy = await parkingSettings.GetPolicyAsync(cancellationToken);
        var (todayStart, todayEnd) = SiteTime.Day(today, timeZone);
        var liveToday = reservations
            .Where(r => r.Status is ReservationStatus.Reserved or ReservationStatus.CheckedIn
                && r.StartUtc < todayEnd && r.EndUtc > todayStart)
            .OrderByDescending(r => r.Status == ReservationStatus.CheckedIn)
            .Select(r => (ReservationStatus?)r.Status)
            .FirstOrDefault();
        var state = ResolveState(spot.IsActive, liveToday,
            visitorBookings.Any(v => v.StartUtc < todayEnd && v.EndUtc > todayStart),
            spot.OwnerId is not null,
            releases.Contains(today) || policy.IsResidentAutoShareActive(today, now, timeZone));

        var trend = await ComputeSpotTrendAsync(dbContext, timeZone,
            today.AddDays(-(SignalWindowDays - 1)), today, spotId, cancellationToken);

        return new SpotDetailDto(spot.Id, spot.Code, spot.Type, spot.IsActive, spot.Notes, spot.OwnerId, ownerName,
            spot.MonthlyShareAllowance, spot.PlannedUseDays, spot.AutoReleaseUnplannedDays, state,
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

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var spotIds = await dbContext.ParkingSpots.AsNoTracking().Select(s => s.Id).ToListAsync(cancellationToken);
        var today = SiteTime.Today(timeProvider.GetUtcNow(), timeZone);
        var spots = await ComputeUtilizationAsync(dbContext, timeZone, from, to, today, spotIds, cancellationToken);

        var (rangeStart, _) = SiteTime.Day(from, timeZone);
        var (_, rangeEnd) = SiteTime.Day(to.AddDays(1), timeZone);
        var windows = await dbContext.Reservations.AsNoTracking()
            .Where(r => r.StartUtc < rangeEnd && r.EndUtc > rangeStart)
            .Select(r => new { r.StartUtc, r.EndUtc, r.Status })
            .ToListAsync(cancellationToken);

        // Demand is measured in occupied spot-hours, not bookings: an 8-hour booking presses on the
        // lot for eight hours, and a count of bookings would flatten that into one tick at its start.
        var demand = new Dictionary<(DayOfWeek Day, int Hour), int>();
        foreach (var window in windows.Where(w => w.Status is not ReservationStatus.Cancelled))
        {
            var localStart = TimeZoneInfo.ConvertTime(window.StartUtc, timeZone);
            var localEnd = TimeZoneInfo.ConvertTime(window.EndUtc, timeZone);
            for (var hour = localStart; hour < localEnd; hour = hour.AddHours(1))
            {
                var key = (hour.DayOfWeek, hour.Hour);
                demand[key] = demand.GetValueOrDefault(key) + 1;
            }
        }

        var peak = demand.Count == 0 ? null : (KeyValuePair<(DayOfWeek Day, int Hour), int>?)demand.MaxBy(cell => cell.Value);
        var completedOrLive = windows.Count(w => w.Status is not ReservationStatus.Cancelled);
        var noShows = windows.Count(w => w.Status == ReservationStatus.NoShow);

        return new LotAnalyticsDto(
            From: from,
            To: to,
            WindowDays: windowDays,
            Spots: spots,
            Daily: await ComputeDailyOccupancyAsync(dbContext, timeZone, from, to, spotIds, cancellationToken),
            Demand: demand.Select(cell => new DemandCellDto(cell.Key.Day, cell.Key.Hour, cell.Value))
                .OrderBy(cell => cell.DayOfWeek).ThenBy(cell => cell.Hour).ToList(),
            PeakHour: peak?.Key.Hour,
            PeakDayOfWeek: peak?.Key.Day,
            TotalReservations: completedOrLive,
            NoShowPercent: completedOrLive == 0 ? 0 : (int)Math.Round(noShows * 100.0 / completedOrLive),
            AverageOccupancyPercent: spots.Count == 0 ? 0 : (int)Math.Round(spots.Average(s => s.UtilizationPercent)));
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
    /// How many spots each day of the window carried, oldest first. The denominator is today's
    /// bookable capacity — see <see cref="DailyOccupancyDto"/> for why there cannot be a per-day one.
    /// </summary>
    private static async Task<List<DailyOccupancyDto>> ComputeDailyOccupancyAsync(
        D3ParkingDbContext dbContext, TimeZoneInfo timeZone, DateOnly from, DateOnly to,
        IReadOnlyList<Guid> spotIds, CancellationToken cancellationToken)
    {
        var honoured = await HonouredDaysAsync(dbContext, timeZone, from, to, spotIds, cancellationToken);
        var capacity = await dbContext.ParkingSpots.AsNoTracking()
            .CountAsync(s => s.IsActive && s.Type != ParkingSpotType.Visitor, cancellationToken);
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
        IReadOnlyList<Guid> spotIds, CancellationToken cancellationToken)
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

        var honoured = await HonouredDaysAsync(dbContext, timeZone, from, to, spotIds, cancellationToken);

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

        var result = new List<SpotUtilizationDto>(spots.Count);
        foreach (var spot in spots)
        {
            var busyDays = honoured.Where(x => x.SpotId == spot.Id).Select(x => x.Date).ToHashSet();
            var spotReleases = releases.Where(r => r.SpotId == spot.Id).ToList();

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
                SharedDays: spotReleases.Count,
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
    /// release's reward state: a day released past the cutoff or over the monthly quota carries no
    /// points to reverse, so the reconciliation never touches it, yet it was just as wasted.
    /// </summary>
    private static async Task<int> CountUnusedSharedDaysAsync(
        D3ParkingDbContext dbContext, TimeZoneInfo timeZone, DateOnly from, DateOnly toExclusive,
        CancellationToken cancellationToken)
    {
        var releases = await dbContext.SpotReleases.AsNoTracking()
            .Where(r => r.Date >= from && r.Date < toExclusive)
            .Select(r => new { r.SpotId, r.Date })
            .ToListAsync(cancellationToken);
        if (releases.Count == 0)
        {
            return 0;
        }

        var (rangeStart, _) = SiteTime.Day(from, timeZone);
        var (_, rangeEnd) = SiteTime.Day(toExclusive, timeZone);
        var honoured = (await dbContext.Reservations.AsNoTracking()
                .Where(r => r.Status != ReservationStatus.Cancelled
                    && r.StartUtc < rangeEnd && r.EndUtc > rangeStart)
                .Select(r => new { r.SpotId, r.StartUtc, r.EndUtc })
                .ToListAsync(cancellationToken))
            .SelectMany(r => LocalDaysOf(r.StartUtc, r.EndUtc, timeZone).Select(day => (r.SpotId, Date: day)))
            .ToHashSet();

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
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var reservation = await dbContext.Reservations.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);
        if (reservation is null || reservation.Status is not (ReservationStatus.Reserved or ReservationStatus.CheckedIn))
        {
            return [];
        }

        return await FreeSpotsForWindowAsync(dbContext, reservation.SpotId, reservation.StartUtc, reservation.EndUtc,
            timeProvider.GetUtcNow(), cancellationToken);
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

        await notifications.NotifyAsync(reservation.UserId, NotificationCategory.Administrative, NotificationLevel.Warning,
            messages["Parking_Notify_AdminCancelled_Title"],
            messages["Parking_Notify_AdminCancelled_Body", spotCode, reservation.CreditsCharged],
            cancellationToken);

        return ParkingResult.Success;
    }

    public Task<ParkingResult> MoveReservationAsync(Guid reservationId, Guid targetSpotId, Guid actingUserId, CancellationToken cancellationToken = default) =>
        OptimisticConcurrency.RetryAsync(() => MoveReservationCoreAsync(reservationId, targetSpotId, actingUserId, cancellationToken), cancellationToken);

    private async Task<ParkingResult> MoveReservationCoreAsync(Guid reservationId, Guid targetSpotId, Guid actingUserId, CancellationToken cancellationToken)
    {
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

        await notifications.NotifyAsync(reservation.UserId, NotificationCategory.Administrative, NotificationLevel.Warning,
            messages["Parking_Notify_AdminMoved_Title"],
            messages["Parking_Notify_AdminMoved_Body", fromCode, target.Code],
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
