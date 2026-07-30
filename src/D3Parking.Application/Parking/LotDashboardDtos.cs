using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

/// <summary>
/// What a spot is doing on the chosen day, in the order the board resolves it: an inactive spot is
/// out of the lot whatever else is recorded, an arrived driver outranks a mere booking, and a
/// resident spot only counts as free once it is actually shared.
/// </summary>
public enum SpotBoardState
{
    /// <summary>Bookable and nothing on it.</summary>
    Free,

    /// <summary>Booked for part of the day; nobody has arrived yet.</summary>
    Booked,

    /// <summary>Someone checked in — there is a car on it now.</summary>
    Occupied,

    /// <summary>A resident's spot, still held for them (not released, nobody booked it).</summary>
    ResidentHeld,

    /// <summary>A resident's spot that is in the shared pool for the day and still free.</summary>
    ResidentShared,

    /// <summary>A visitor spot with a reception booking on the day.</summary>
    VisitorBooked,

    /// <summary>Deactivated — out of the lot entirely.</summary>
    Inactive,
}

/// <summary>Lot-level counts for the chosen day, plus what is happening right now.</summary>
public sealed record LotOverviewDto(
    int TotalSpots,
    int ActiveSpots,
    int InactiveSpots,
    int ResidentSpots,
    int PoolSpots,
    int VisitorSpots,
    // "Now" figures are only meaningful for today; for another day they describe that day's bookings.
    int Occupied,
    int Booked,
    int Free,
    int OccupancyPercent,
    int QueueWaiting,
    int VisitorBookings,
    int OpenMismatches,
    /// <summary>Shared days in the recent window that nobody booked — capacity given away for nothing.</summary>
    int UnusedSharedDays);

/// <summary>One spot as a row on the board.</summary>
public sealed record SpotTileDto(
    Guid SpotId,
    string Code,
    /// <summary>The section the code starts with ("A-12" → "A"); empty when the code has no prefix.</summary>
    string Section,
    ParkingSpotType Type,
    bool IsActive,
    Guid? OwnerId,
    string? OwnerName,
    SpotBoardState State,
    /// <summary>Who is on it (reservation holder or visitor name), when the day has a booking.</summary>
    string? HolderName,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    /// <summary>Blocked-spot reports on this spot in the recent window — the "check this one" marker.</summary>
    int MismatchCount);

/// <summary>
/// The whole board for one day: the lot summary plus every spot, ordered by section and then by code
/// read as a number, which is the order the table lands in before the manager sorts it themselves.
/// </summary>
public sealed record LotBoardDto(
    DateOnly Date,
    bool IsToday,
    LotOverviewDto Overview,
    IReadOnlyList<SpotTileDto> Spots);

/// <summary>What kind of thing occupies a slot in a spot's calendar.</summary>
public enum SpotCalendarKind
{
    Reservation,
    VisitorBooking,

    /// <summary>A resident released the day into the pool (no booking on it yet).</summary>
    ResidentRelease,
}

/// <summary>One entry in a spot's calendar.</summary>
public sealed record SpotCalendarEntryDto(
    SpotCalendarKind Kind,
    DateOnly Date,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string? HolderName,
    Guid? ReservationId,
    ReservationStatus? Status,
    int CreditsCharged,
    /// <summary>Whether an admin may still cancel this booking (only a not-yet-arrived reservation).</summary>
    bool CanCancel,
    /// <summary>Whether an admin may still move this booking to another spot.</summary>
    bool CanMove);

/// <summary>A blocked-spot report on the spot, for the manager's follow-up.</summary>
public sealed record SpotMismatchSummaryDto(
    DateTimeOffset ReportedAtUtc,
    string ReporterName,
    string? BlockerPlate,
    bool Relocated);

/// <summary>How hard a single spot has been working over the analysed window.</summary>
public sealed record SpotUtilizationDto(
    Guid SpotId,
    string Code,
    ParkingSpotType Type,
    string? OwnerName,
    /// <summary>Days in the window with at least one booking that was honoured or is still live.</summary>
    int BookedDays,
    int WindowDays,
    int UtilizationPercent,
    int NoShows,
    int Mismatches,
    /// <summary>Resident days put into the pool (0 for a pool spot).</summary>
    int SharedDays,
    /// <summary>Of those, the ones nobody booked.</summary>
    int UnusedSharedDays);

/// <summary>Everything the manager needs about one spot, including its calendar.</summary>
public sealed record SpotDetailDto(
    Guid SpotId,
    string Code,
    ParkingSpotType Type,
    bool IsActive,
    string? Notes,
    Guid? OwnerId,
    string? OwnerName,
    int MonthlyShareAllowance,
    Weekday PlannedUseDays,
    bool AutoReleaseUnplannedDays,
    SpotBoardState State,
    IReadOnlyList<SpotCalendarEntryDto> Calendar,
    IReadOnlyList<SpotMismatchSummaryDto> Mismatches,
    SpotUtilizationDto Stats,
    /// <summary>This spot's recent days, oldest first — its own load over time, not the lot's.</summary>
    IReadOnlyList<SpotDayDto> Trend);

/// <summary>One cell of the weekday × hour demand heatmap: how many bookings covered that hour.</summary>
public sealed record DemandCellDto(DayOfWeek DayOfWeek, int Hour, int Count);

/// <summary>
/// How many spots one day of the window actually carried. <paramref name="Capacity"/> is the lot's
/// bookable capacity <em>today</em>, not on that day: spots get created, retired and reassigned, and
/// there is no history of that, so a per-day denominator would be invented. The count is the honest
/// figure; the percentage is against today's capacity and is only meant for the recent past.
/// </summary>
public sealed record DailyOccupancyDto(DateOnly Date, int Occupied, int Capacity, int OccupancyPercent);

/// <summary>One day of a single spot's history: did it carry a booking somebody stood by.</summary>
public sealed record SpotDayDto(DateOnly Date, bool Busy);

/// <summary>One day of a plain daily count (mismatches reported, shared days nobody took, …).</summary>
public sealed record DailyCountDto(DateOnly Date, int Count);

/// <summary>
/// The daily series behind the summary table's sparklines, oldest first. Only the metrics that have a
/// real day-by-day history are here — a trend line for "how many spots are residents'" would be
/// invented, so those rows show their number and nothing else.
/// </summary>
public sealed record LotSummaryTrendsDto(
    IReadOnlyList<DailyOccupancyDto> Occupancy,
    IReadOnlyList<DailyCountDto> Mismatches,
    IReadOnlyList<DailyCountDto> WastedShares);

/// <summary>How many days back the summary table's sparklines reach.</summary>
public static class LotBoard
{
    public const int SummaryTrendDays = 14;
}

/// <summary>The lot's analytics over a window: which spots work hardest, and when demand lands.</summary>
public sealed record LotAnalyticsDto(
    DateOnly From,
    DateOnly To,
    int WindowDays,
    /// <summary>Every spot, busiest first — the head is the bottleneck, the tail is dead capacity.</summary>
    IReadOnlyList<SpotUtilizationDto> Spots,
    /// <summary>One entry per day of the window, oldest first — the lot's load over time.</summary>
    IReadOnlyList<DailyOccupancyDto> Daily,
    IReadOnlyList<DemandCellDto> Demand,
    /// <summary>The busiest hour and weekday overall, or null when the window holds no bookings.</summary>
    int? PeakHour,
    DayOfWeek? PeakDayOfWeek,
    int TotalReservations,
    int NoShowPercent,
    int AverageOccupancyPercent);

/// <summary>A spot a booking could be moved to (free for the same window).</summary>
public sealed record MoveTargetDto(Guid SpotId, string Code, ParkingSpotType Type);
