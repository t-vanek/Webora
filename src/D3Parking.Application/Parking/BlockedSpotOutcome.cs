namespace D3Parking.Application.Parking;

/// <summary>
/// Outcome of a blocked-spot report ("I can't park"): whether it went through, and where the
/// driver goes now. A null <see cref="RelocatedToSpotCode"/> on success means the state was
/// recorded and the reservation voided, but no replacement spot was booked.
/// </summary>
public sealed record BlockedSpotOutcome(bool Succeeded, string? Error, string? RelocatedToSpotCode)
{
    public static readonly BlockedSpotOutcome Recorded = new(true, null, null);

    public static BlockedSpotOutcome Relocated(string spotCode) => new(true, null, spotCode);

    public static BlockedSpotOutcome Failure(string error) => new(false, error, null);
}
