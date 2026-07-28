using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

/// <summary>Today's state of a resident's reserved spot.</summary>
public enum OwnedSpotDayState
{
    /// <summary>Still held for the resident (before the cutoff, not yet claimed or released).</summary>
    Held,

    /// <summary>The resident confirmed arrival and holds it for the day.</summary>
    Claimed,

    /// <summary>Shared to the pool (released or past the cutoff) and still free.</summary>
    SharedFree,

    /// <summary>Shared to the pool and already taken by someone else.</summary>
    SharedTaken,
}

/// <summary>A resident's view of their reserved spot, with today's state and sharing controls.</summary>
public sealed record OwnedSpotDto(
    Guid SpotId,
    string Code,
    ParkingSpotType Type,
    int MonthlyShareAllowance,
    int MaxShareAllowance,
    OwnedSpotDayState TodayState,
    bool ReleasedToday,
    int PotentialReleasePointsToday);
