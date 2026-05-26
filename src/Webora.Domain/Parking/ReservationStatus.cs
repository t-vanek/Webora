namespace Webora.Domain.Parking;

public enum ReservationStatus
{
    /// <summary>Booked but not yet used.</summary>
    Reserved,

    /// <summary>The holder has arrived and occupies the spot.</summary>
    CheckedIn,

    /// <summary>The visit finished normally after check-in.</summary>
    Completed,

    /// <summary>Given up ahead of time, freeing the spot for others (rewarded).</summary>
    Released,

    /// <summary>The holder never checked in and never released (penalized).</summary>
    NoShow,

    /// <summary>Called off without qualifying as an early release (e.g. too late, or by an admin).</summary>
    Cancelled,
}
