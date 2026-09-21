namespace D3Parking.Domain.Oversight;

/// <summary>
/// How a case ended. This is the answer the queue is there to produce, so it is recorded as a
/// value rather than left to a free-text note: "unfounded" is what stops the nightly scan from
/// raising the same pair again, and it is what a later report about the same spot is counted
/// against.
/// </summary>
public enum OversightResolution
{
    /// <summary>The signal was real and whatever it called for has happened.</summary>
    Founded,

    /// <summary>A false alarm — the report or the flag did not hold up.</summary>
    Unfounded,

    /// <summary>Real, but nothing needed doing (the spot was already fixed, the pair explained itself).</summary>
    NoActionNeeded,
}
