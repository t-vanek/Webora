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

/// <summary>Distinguishes an explicit promise to colleagues from capacity opened by an automatic plan.</summary>
public enum SpotReleaseSource
{
    Manual,
    UsagePlan,
}
