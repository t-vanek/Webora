using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking.Incentives;

/// <summary>An immutable record of a single points award or deduction for a user.</summary>
public class PointsLedgerEntry : Entity
{
    public Guid UserId { get; private set; }

    public IncentiveReason Reason { get; private set; }

    /// <summary>Signed point delta: positive for rewards, negative for penalties.</summary>
    public int Points { get; private set; }

    /// <summary>The reservation that triggered the entry, when applicable.</summary>
    public Guid? ReservationId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string? Detail { get; private set; }

    private PointsLedgerEntry() { }

    public PointsLedgerEntry(Guid userId, IncentiveReason reason, int points, Guid? reservationId, DateTimeOffset occurredAtUtc, string? detail = null)
    {
        UserId = userId;
        Reason = reason;
        Points = points;
        ReservationId = reservationId;
        OccurredAtUtc = occurredAtUtc;
        Detail = detail;
    }
}
