namespace D3Parking.Domain.Parking.Incentives;

/// <summary>
/// A positive, objectively observable result of somebody's planning. There are deliberately no
/// negative kinds: doing nothing never damages a person, and these records are never consulted by
/// reservation, pricing, allowance or queue decisions.
/// </summary>
public enum ParkingContributionKind
{
    /// <summary>A colleague booked capacity freed by the user's released reservation.</summary>
    UsefulRelease,

    /// <summary>A released reservation ultimately supplied a claimed waitlist offer.</summary>
    QueueHelped,

    /// <summary>A colleague booked a day made available by a resident.</summary>
    ResidentShareUsed,
}
