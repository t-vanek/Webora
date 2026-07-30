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

    /// <summary>
    /// Held for a waitlist claim: free of bookings, but promised to whoever is holding the offer, so
    /// it is not capacity anybody else can be given.
    /// </summary>
    OfferedFromQueue,

    /// <summary>Deactivated — out of the lot entirely.</summary>
    Inactive,
}

/// <summary>
/// Lot-level counts for the chosen day, plus what is happening right now.
///
/// The spot counts are one partition of the lot and nothing else, so the manager can add them up and
/// check them: <c>TotalSpots = ActiveSpots + InactiveSpots</c>, and every active spot lands in exactly
/// one of <see cref="Occupied"/>, <see cref="Booked"/>, <see cref="OfferedFromQueue"/>,
/// <see cref="Free"/> and <see cref="ResidentHeld"/>. The kind counts (resident / pool / visitor) are
/// a second, independent partition of the whole lot. Anything that is not a count of spots — bookings,
/// queue entries, reports — is named for what it counts.
/// </summary>
public sealed record LotOverviewDto(
    int TotalSpots,
    int ActiveSpots,
    int InactiveSpots,
    int ResidentSpots,
    int PoolSpots,
    int VisitorSpots,
    // "Now" figures are only meaningful for today; for another day they describe that day's bookings.
    int Occupied,
    /// <summary>Spots with a booking nobody has arrived on — employee bookings and visitor bookings alike.</summary>
    int Booked,
    /// <summary>Spots held for a waitlist offer: not taken, but not on offer to anyone else either.</summary>
    int OfferedFromQueue,
    int Free,
    /// <summary>A resident's spot, still theirs for the day — neither taken nor offerable.</summary>
    int ResidentHeld,
    /// <summary>Taken or spoken for, against the spots that could be offered at all (active minus held).</summary>
    int OccupancyPercent,
    int QueueWaiting,
    int QueueOffered,
    /// <summary>Visitor <em>bookings</em> touching the day — rows, not spots; a spot can carry several.</summary>
    int VisitorBookings,
    /// <summary>Blocked-spot reports filed in the recent window. There is no triage state to be "open".</summary>
    int ReportedMismatches,
    /// <summary>Of those, the ones resolved by putting the driver on another spot.</summary>
    int RelocatedMismatches,
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
/// active spot count <em>today</em>, not on that day: spots get created, retired and reassigned, and
/// there is no history of that, so a per-day denominator would be invented. The count is the honest
/// figure; the percentage is against today's capacity and is only meant for the recent past.
///
/// Both sides count the same population — every active spot, visitor spots included, because a
/// visitor booking occupies asphalt exactly like an employee one. Leaving visitor spots out of the
/// denominator while their bookings stayed in the numerator was how this could read past 100 %.
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

/// <summary>
/// Where the lot's capacity went over the window, as a partition of spot-days that adds up:
/// <c>CapacityDays = UsedDays + OfferedUnusedDays + ResidentHeldDays + IdleDays</c>. Each spot-day of
/// an active spot falls in exactly one bucket, and the four answer four different questions — the lot
/// worked, a resident offered it and nobody came, a resident sat on it, or nobody wanted it at all.
/// </summary>
/// <remarks>
/// <see cref="ResidentHeldDays"/> reads high wherever auto-share has been doing the work: past the
/// hold cutoff an unclaimed resident spot becomes pool capacity without leaving a record, so those
/// days look held here when they were in fact on offer. Recording auto-shares would need a write in
/// the resident sweep; until then this bucket is an upper bound.
/// </remarks>
public sealed record CapacityUseDto(
    int ActiveSpots,
    int WindowDays,
    int CapacityDays,
    /// <summary>Spot-days that carried a booking somebody stood by.</summary>
    int UsedDays,
    /// <summary>Days a resident released that no booking ever took (days already past).</summary>
    int OfferedUnusedDays,
    /// <summary>Days a resident's spot stayed theirs — not released, not booked by anyone.</summary>
    int ResidentHeldDays,
    /// <summary>Bookable spot-days nobody asked for.</summary>
    int IdleDays,
    /// <summary>UsedDays against CapacityDays — the same figure the daily curve averages to.</summary>
    int UsePercent,
    /// <summary>Active spots that carried nothing at all in the window.</summary>
    int DeadSpots);

/// <summary>
/// Demand the lot turned away. A waitlist entry is only ever created for a window with nothing free
/// in it, so each one is a recorded refusal — the single hard measurement that capacity ran out. The
/// counts are of entries <em>created</em> in the window; <see cref="Refused"/> spreads each entry
/// over the hours it asked for, so it overlays the demand map.
/// </summary>
public sealed record QueueDemandDto(
    int Entries,
    /// <summary>Entries that ended up on a spot.</summary>
    int Claimed,
    /// <summary>Entries whose window passed without one.</summary>
    int Expired,
    int Cancelled,
    /// <summary>Still waiting or holding an offer — no outcome yet, so outside the conversion.</summary>
    int Open,
    /// <summary>Claimed against the entries that reached an outcome.</summary>
    int ConversionPercent,
    IReadOnlyList<DemandCellDto> Refused);

/// <summary>
/// What became of the bookings that ran out in the window, as shares of one whole. Only bookings whose
/// window has ended are here: a booking still ahead of us has not had the chance to be honoured, and
/// counting it would quietly dilute every rate. Cancellations are reported beside the three rather
/// than inside them — a booking called off in time occupied nothing and failed nobody.
/// </summary>
public sealed record ReliabilityDto(
    int Resolved,
    int Completed,
    /// <summary>Given up early enough for somebody else to take the spot.</summary>
    int Released,
    int NoShows,
    int Cancelled,
    int NoShowPercent,
    /// <summary>Median hours between booking and arrival — how far ahead the lot is planned.</summary>
    int? MedianLeadHours);

/// <summary>
/// The busiest stretch the window actually shows, against the peak window the pricing is configured
/// with. Same length as the configured one so the two are comparable; null when the window holds no
/// demand at all.
/// </summary>
public sealed record PeakWindowDto(
    int? SuggestedStartHour,
    int? SuggestedEndHour,
    TimeOnly ConfiguredStart,
    TimeOnly ConfiguredEnd,
    bool Matches);

/// <summary>
/// What the window cost and how the price levers were hit. Over the same resolved bookings as
/// <see cref="ReliabilityDto"/>, so the shares below and the funnel above have one denominator.
/// </summary>
public sealed record LotEconomyDto(
    int CreditsCharged,
    /// <summary>Credits handed back — late-cancel refunds and manager overrides.</summary>
    int CreditsRefunded,
    /// <summary>Share of bookings that arrived outside the peak window, where the discount lives.</summary>
    int OffPeakPercent,
    /// <summary>Share of bookings that came from the waitlist rather than straight off the board.</summary>
    int FromQueuePercent);

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
    CapacityUseDto Capacity,
    QueueDemandDto Queue,
    ReliabilityDto Reliability,
    PeakWindowDto Peak,
    LotEconomyDto Economy);

/// <summary>A spot a booking could be moved to (free for the same window).</summary>
public sealed record MoveTargetDto(Guid SpotId, string Code, ParkingSpotType Type);
