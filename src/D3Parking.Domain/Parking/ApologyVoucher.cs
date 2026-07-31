using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>
/// An apology for a blocked reserved spot: one reservation free of charge, dynamic price and peak
/// surcharge included. The voucher is born pending — a spot manager reviews the report's photo
/// proof and approves or rejects it, so value only ever flows from a human-confirmed mismatch.
/// A user holds at most one pending-or-approved unredeemed voucher at a time and it expires, so
/// faking mismatch reports cannot stockpile value. A timely cancel/release of the voucher-paid
/// booking restores the voucher on the same terms as a credit refund.
/// </summary>
public class ApologyVoucher : Entity
{
    public Guid UserId { get; private set; }

    /// <summary>The occupancy mismatch this voucher apologizes for.</summary>
    public Guid SourceMismatchId { get; private set; }

    public ApologyVoucherStatus Status { get; private set; }

    public DateTimeOffset GrantedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    /// <summary>When a spot manager approved or rejected the voucher.</summary>
    public DateTimeOffset? ReviewedAtUtc { get; private set; }

    /// <summary>The spot manager who approved or rejected the voucher.</summary>
    public Guid? ReviewedById { get; private set; }

    public DateTimeOffset? RedeemedAtUtc { get; private set; }

    public Guid? RedeemedReservationId { get; private set; }

    /// <summary>The dynamic price the voucher absorbed, kept for economy stats.</summary>
    public int WaivedCredits { get; private set; }

    private ApologyVoucher() { }

    public ApologyVoucher(Guid userId, Guid sourceMismatchId, DateTimeOffset grantedAtUtc, DateTimeOffset expiresAtUtc)
    {
        UserId = userId;
        SourceMismatchId = sourceMismatchId;
        Status = ApologyVoucherStatus.PendingApproval;
        GrantedAtUtc = grantedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>
    /// The spot manager confirmed the report. Validity restarts from the approval, so a slow
    /// review never eats into the 30 days the driver was promised.
    /// </summary>
    public void Approve(Guid reviewerId, DateTimeOffset at, TimeSpan validity)
    {
        Status = ApologyVoucherStatus.Approved;
        ReviewedById = reviewerId;
        ReviewedAtUtc = at;
        ExpiresAtUtc = at + validity;
    }

    /// <summary>The spot manager judged the report unfounded; the voucher is dead for good.</summary>
    public void Reject(Guid reviewerId, DateTimeOffset at)
    {
        Status = ApologyVoucherStatus.Rejected;
        ReviewedById = reviewerId;
        ReviewedAtUtc = at;
    }

    /// <summary>
    /// The holder withdrew the report this apologises for, so the voucher goes with it. Not
    /// <see cref="Reject"/>: nobody ruled on anything, which is why no reviewer is recorded. Safe
    /// in the holder's own hands precisely because it only ever moves value away from them — the
    /// guard that stops somebody approving their own apology has nothing to defend here.
    /// </summary>
    public void Relinquish(DateTimeOffset at)
    {
        Status = ApologyVoucherStatus.Rejected;
        ReviewedAtUtc = at;
    }

    public void Redeem(Guid reservationId, int waivedCredits, DateTimeOffset at)
    {
        RedeemedAtUtc = at;
        RedeemedReservationId = reservationId;
        WaivedCredits = waivedCredits;
    }

    /// <summary>A timely cancel/release returns the voucher — the free reservation is not lost to a change of plans.</summary>
    public void Restore()
    {
        RedeemedAtUtc = null;
        RedeemedReservationId = null;
        WaivedCredits = 0;
    }

    /// <summary>
    /// Re-points a redeemed voucher at the replacement reservation when a blocked booking is
    /// relocated: the promise that a timely cancel/release restores the voucher must follow the
    /// booking the user actually holds, not the voided one.
    /// </summary>
    public void TransferRedemption(Guid replacementReservationId) =>
        RedeemedReservationId = replacementReservationId;
}
