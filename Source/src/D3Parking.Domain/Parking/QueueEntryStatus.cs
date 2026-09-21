namespace D3Parking.Domain.Parking;

/// <summary>Lifecycle of a waitlist entry for a full lot.</summary>
public enum QueueEntryStatus
{
    /// <summary>Waiting in line; no spot offered yet.</summary>
    Waiting,

    /// <summary>A freed spot is being held for this entry until the claim window passes.</summary>
    Offered,

    /// <summary>The user reserved the offered spot.</summary>
    Claimed,

    /// <summary>The user left the queue.</summary>
    Cancelled,

    /// <summary>The requested window passed before a spot could be claimed.</summary>
    Expired,
}
