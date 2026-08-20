using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

/// <summary>Today's state of a resident's reserved spot.</summary>
public enum OwnedSpotDayState
{
    /// <summary>Still held for the resident (before the cutoff, not yet claimed or released).</summary>
    Held,

    /// <summary>Shared to the pool (released or past the cutoff) and still free.</summary>
    SharedFree,

    /// <summary>Shared to the pool and already taken by someone else.</summary>
    SharedTaken,
}

/// <summary>A day the resident released, and whether taking it back will displace a guest plan.</summary>
public sealed record ReleasedDayDto(DateOnly Date, bool TakenByGuest);

/// <summary>A resident's view of their reserved spot, with today's state and sharing controls.</summary>
public sealed record OwnedSpotDto(
    Guid SpotId,
    string Code,
    ParkingSpotType Type,
    OwnedSpotDayState TodayState,
    bool ReleasedToday,
    int PotentialReleasePointsToday,
    // Today-or-later released days. Every one can be reclaimed; TakenByGuest warns that doing so
    // cancels another user's plan with a full refund and notification.
    IReadOnlyList<ReleasedDayDto> UpcomingReleases,
    // The standing usage plan: the weekdays the resident needs the spot, whether the rest are
    // released ahead of time, and how far ahead that reaches.
    Weekday PlannedUseDays,
    bool AutoReleaseUnplannedDays,
    int PlanHorizonDays);
