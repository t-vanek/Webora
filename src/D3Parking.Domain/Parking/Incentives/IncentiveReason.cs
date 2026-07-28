namespace D3Parking.Domain.Parking.Incentives;

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

    /// <summary>A resident proactively released their reserved spot into the shared pool.</summary>
    ResidentSpotShared,

    /// <summary>Part of a share reward clawed back because the guest no-showed on the shared spot.</summary>
    ResidentShareWasted,

    /// <summary>A share reward fully reversed because nobody booked the released day (no demand).</summary>
    ResidentShareUnused,

    /// <summary>Awarded for taking a shared reserved spot, scaled by the taker's commute distance.</summary>
    SharedSpotTaken,

    /// <summary>The recurring monthly credit allowance added to a user's wallet.</summary>
    MonthlyCreditGrant,

    /// <summary>Credits charged for booking a spot (dynamic peak/occupancy price).</summary>
    ReservationCharge,

    /// <summary>Credits returned when a booking is given up early enough to re-let the spot.</summary>
    ReservationRefund,

    /// <summary>Reputation penalty for a no-show on a spot claimed from the waitlist (harsher than a normal no-show).</summary>
    QueueNoShowPenalty,

    /// <summary>Extra credit fine for a no-show on a spot claimed from the waitlist.</summary>
    QueueNoShowFine,

    /// <summary>A reliability bonus for an unbroken run of completed reservations.</summary>
    StreakBonus,

    /// <summary>Periodic decay of reputation toward zero so the score reflects recent behaviour.</summary>
    ReputationDecay,

    /// <summary>A manual correction made by an administrator.</summary>
    ManualAdjustment,
}
