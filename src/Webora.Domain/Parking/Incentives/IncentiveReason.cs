namespace Webora.Domain.Parking.Incentives;

/// <summary>Why a points ledger entry was created. Drives both scoring and the per-user counters.</summary>
public enum IncentiveReason
{
    /// <summary>A reservation was given up early enough to free the spot for others.</summary>
    ReleasedReservation,

    /// <summary>A reservation was booked outside the high-demand peak window.</summary>
    OffPeakBonus,

    /// <summary>A reservation went unused without being released.</summary>
    NoShowPenalty,

    /// <summary>A reservation was used as booked (check-in through completion).</summary>
    ReservationCompleted,

    /// <summary>A manual correction made by an administrator.</summary>
    ManualAdjustment,
}
