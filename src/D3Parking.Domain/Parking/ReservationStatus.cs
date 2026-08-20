namespace D3Parking.Domain.Parking;

public enum ReservationStatus
{
    /// <summary>A planned time window. It becomes history by time, without a presence action.</summary>
    Reserved,

    /// <summary>Legacy status retained so reservations created before the planner migration remain readable.</summary>
    CheckedIn,

    /// <summary>Legacy status retained so reservations created before the planner migration remain readable.</summary>
    Completed,

    /// <summary>Given up ahead of time, freeing the spot for others (rewarded).</summary>
    Released,

    /// <summary>Legacy status retained for historical reports; the planner never creates it.</summary>
    NoShow,

    /// <summary>Called off without qualifying as an early release (e.g. too late, or by an admin).</summary>
    Cancelled,
}
