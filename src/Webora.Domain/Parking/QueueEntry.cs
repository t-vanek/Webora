using Webora.Domain.Common;

namespace Webora.Domain.Parking;

/// <summary>
/// A user's place in the waitlist for a time window when the lot is full. When a spot frees up it is
/// held for (offered to) the earliest waiting entry for a short claim window; if unclaimed, the offer
/// is withdrawn and the spot is offered to the next in line.
/// </summary>
public class QueueEntry : Entity
{
    public Guid UserId { get; private set; }

    public DateTimeOffset StartUtc { get; private set; }

    public DateTimeOffset EndUtc { get; private set; }

    public QueueEntryStatus Status { get; private set; } = QueueEntryStatus.Waiting;

    /// <summary>Join time; the queue is served first-in, first-out by this.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>The spot currently held for this entry while <see cref="Status"/> is Offered.</summary>
    public Guid? OfferedSpotId { get; private set; }

    /// <summary>When the current offer's claim window closes; after this the hold lapses.</summary>
    public DateTimeOffset? OfferExpiresAtUtc { get; private set; }

    private QueueEntry() { }

    public QueueEntry(Guid userId, DateTimeOffset startUtc, DateTimeOffset endUtc, DateTimeOffset createdAtUtc)
    {
        if (endUtc <= startUtc)
            throw new ArgumentException("Queue window end must be after its start.", nameof(endUtc));

        UserId = userId;
        StartUtc = startUtc;
        EndUtc = endUtc;
        CreatedAtUtc = createdAtUtc;
    }

    public bool IsActive => Status is QueueEntryStatus.Waiting or QueueEntryStatus.Offered;

    public bool Overlaps(DateTimeOffset startUtc, DateTimeOffset endUtc) =>
        startUtc < EndUtc && endUtc > StartUtc;

    /// <summary>Holds a freed spot for this entry until the claim window closes.</summary>
    public void Offer(Guid spotId, DateTimeOffset expiresAtUtc)
    {
        Status = QueueEntryStatus.Offered;
        OfferedSpotId = spotId;
        OfferExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Drops an unclaimed offer back to waiting so the spot can go to the next in line.</summary>
    public void WithdrawOffer()
    {
        Status = QueueEntryStatus.Waiting;
        OfferedSpotId = null;
        OfferExpiresAtUtc = null;
    }

    public void Claim() => Status = QueueEntryStatus.Claimed;

    public void Cancel() => Status = QueueEntryStatus.Cancelled;

    public void Expire() => Status = QueueEntryStatus.Expired;
}
