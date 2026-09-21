namespace D3Parking.Domain.Parking;

/// <summary>Resident priority before the colleague's booking starts. Started bookings are always protected.</summary>
public enum ResidentReclaimPolicy
{
    /// <summary>A confirmed booking is never changed by resident self-service.</summary>
    ConfirmedBookingProtected,

    /// <summary>The resident may reclaim only before the configured protection deadline.</summary>
    AdvancePriority,

    /// <summary>Before the booking starts, reclaim is allowed only with a replacement.</summary>
    ReplacementOnly,

    /// <summary>Before the deadline the resident has priority; afterwards a replacement is required.</summary>
    AdvanceOrReplacement,

    /// <summary>Before the booking starts, priority ignores the earlier deadline; the fallback still applies.</summary>
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
