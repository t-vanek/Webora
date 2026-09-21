using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking.Incentives;

/// <summary>
/// Immutable evidence that a user's planning helped somebody else. <see cref="SourceId"/> is the
/// released reservation for release/queue contributions and the consuming reservation for a
/// resident share. The unique database key makes each positive outcome idempotent.
/// </summary>
public sealed class ParkingContribution : Entity
{
    public Guid UserId { get; private set; }

    public ParkingContributionKind Kind { get; private set; }

    public Guid SourceId { get; private set; }

    public Guid BeneficiaryUserId { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public string? Detail { get; private set; }

    private ParkingContribution() { }

    public ParkingContribution(Guid userId, ParkingContributionKind kind, Guid sourceId,
        Guid beneficiaryUserId, DateTimeOffset occurredAtUtc, string? detail = null)
    {
        if (userId == beneficiaryUserId)
        {
            throw new ArgumentException("A contribution must help another user.", nameof(beneficiaryUserId));
        }

        UserId = userId;
        Kind = kind;
        SourceId = sourceId;
        BeneficiaryUserId = beneficiaryUserId;
        OccurredAtUtc = occurredAtUtc;
        Detail = detail;
    }
}
