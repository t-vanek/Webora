using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

/// <summary>Today's state of a resident's reserved spot.</summary>
public enum OwnedSpotDayState
{
    /// <summary>The schedule could not resolve the day. UI must not infer availability or ownership.</summary>
    Unknown,

    /// <summary>The shared-residency schedule assigns the physical spot to another resident.</summary>
    NotAssigned,

    /// <summary>Assigned to the current resident and not released for another user.</summary>
    Held,

    /// <summary>Shared to the pool (released or past the cutoff) and still free.</summary>
    SharedFree,

    /// <summary>Released or handed over and reserved by one or more users.</summary>
    SharedTaken,
}

/// <summary>Who currently controls the resident capacity for a local day.</summary>
public enum ResidentAllocationState
{
    Unknown,
    AssignedToCurrentUser,
    AssignedToOtherResident,
    Released,
}

/// <summary>Reservation activity on the resident spot, kept separate from its allocation.</summary>
public enum ResidentBookingState
{
    None,
    ReservedByCurrentUser,
    ReservedByOtherUser,
    MultipleReservations,
}

/// <summary>A day the resident released, and whether taking it back will displace a guest plan.</summary>
public sealed record ReleasedDayDto(DateOnly Date, bool TakenByGuest, bool DirectHandoff = false);

/// <summary>A person shown in the shared resident schedule.</summary>
public sealed record ParkingUserLabelDto(Guid UserId, string? DisplayName, string? Email)
{
    /// <summary>The best non-localized label available to the application layer.</summary>
    public string? Name => string.IsNullOrWhiteSpace(DisplayName) ? Email : DisplayName;
}

/// <summary>A live booking occupying part or all of a released resident day.</summary>
public sealed record ResidentSpotBookingDto(
    Guid ReservationId,
    ParkingUserLabelDto User,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool DirectHandoff = false);

/// <summary>
/// The complete resident-facing state of one local day. The assigned resident and every live
/// booking are named because several residents can rotate one physical spot and a released day can
/// contain more than one non-overlapping time-window booking.
/// </summary>
public sealed record ResidentSpotDayDto(
    DateOnly Date,
    OwnedSpotDayState State,
    ResidentAllocationState AllocationState,
    ResidentBookingState BookingState,
    ParkingUserLabelDto? AssignedResident,
    ParkingUserLabelDto? ReleasedByResident,
    IReadOnlyList<ResidentSpotBookingDto> Bookings,
    bool IsAssignedToCurrentUser,
    bool CanReclaim);

/// <summary>A resident's view of their reserved spot, with today's state and sharing controls.</summary>
public sealed record OwnedSpotDto(
    Guid SpotId,
    string Code,
    ParkingSpotType Type,
    OwnedSpotDayState TodayState,
    bool ReleasedToday,
    // Today-or-later released days. Every one can be reclaimed; TakenByGuest warns that doing so
    // cancels another user's plan with a full refund and notification.
    IReadOnlyList<ReleasedDayDto> UpcomingReleases,
    // The standing usage plan: the weekdays the resident needs the spot, whether the rest are
    // released ahead of time, and how far ahead that reaches.
    Weekday PlannedUseDays,
    bool AutoReleaseUnplannedDays,
    int PlanHorizonDays,
    IReadOnlyList<DateOnly>? AssignedDates = null,
    IReadOnlyList<ResidentSpotDayDto>? Days = null)
{
    public IReadOnlyList<DateOnly> ResidentAssignedDates => AssignedDates ?? [];

    public IReadOnlyList<ResidentSpotDayDto> DaySchedule => Days ?? [];
}
