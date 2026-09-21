namespace D3Parking.Domain.Parking.Incentives;

/// <summary>Recognitions earned for behaviour that improves how well the lot is used.</summary>
public enum ParkingBadge
{
    /// <summary>Repeatedly freed reserved spots for colleagues.</summary>
    ConsiderateColleague,

    /// <summary>Regularly parks outside the high-demand peak.</summary>
    OffPeakChampion,

    /// <summary>A long run of used reservations with no no-shows.</summary>
    ReliableParker,

    /// <summary>Reached a major points milestone.</summary>
    CenturyClub,

    /// <summary>Highly trusted in the sharing network (top trust-graph standing).</summary>
    Trusted,

    // Positive-only planning achievements. Unlike the legacy reputation badges these are
    // permanent acknowledgements of a concrete contribution; they never reduce access, budget or
    // queue position and are never revoked.
    PlanningStarted,
    ActivePlanner,
    PlaceForColleague,
    ParkingHelper,
    BigHelper,
    FreeSpotHero,
    QueueHelper,
    ShortensWaiting,
    SharesWhenPossible,
    GenerousResident,
}
