namespace D3Parking.Domain.Parking;

/// <summary>How strongly a resident may reclaim a day which a colleague has already booked.</summary>
public enum ResidentReclaimPolicy
{
    /// <summary>A confirmed booking is never changed by resident self-service.</summary>
    ConfirmedBookingProtected,

    /// <summary>The resident may reclaim only before the configured protection deadline.</summary>
    AdvancePriority,

    /// <summary>The resident may reclaim at any time, but only when the guest can be moved.</summary>
    ReplacementOnly,

    /// <summary>Before the deadline the resident has priority; afterwards a replacement is required.</summary>
    AdvanceOrReplacement,

    /// <summary>The resident has priority at any time; the no-replacement action decides the fallback.</summary>
    AbsolutePriority,
}

public enum ResidentProtectionDeadlineMode
{
    HoursBeforeStart,
    PreviousDayAtTime,
}

/// <summary>What self-service does when priority applies but no safe replacement exists.</summary>
public enum ResidentNoReplacementAction
{
    Deny,
    ManagerOnly,
    CancelAndQueue,
    CancelAndNotify,
}

/// <summary>What happens to an assigned resident spot when its resident books another spot.</summary>
public enum ResidentAlternativeBookingPolicy
{
    /// <summary>The assigned resident spot is shared in the same transaction as the alternative booking.</summary>
    AutoRelease,

    /// <summary>The booking succeeds only after the resident explicitly accepts releasing their assigned spot.</summary>
    ConfirmRelease,

    /// <summary>A resident must use or release their assigned spot before booking another one.</summary>
    Deny,
}

/// <summary>Distinguishes an explicit promise to colleagues from capacity opened by an automatic plan.</summary>
public enum SpotReleaseSource
{
    Manual,
    UsagePlan,
    AlternativeBooking,
}
