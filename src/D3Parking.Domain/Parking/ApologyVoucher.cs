using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>
/// An apology for a blocked reserved spot: one reservation free of charge, dynamic price and peak
/// surcharge included. A user holds at most one unredeemed voucher at a time and it expires, so
/// faking mismatch reports cannot stockpile value. A timely cancel/release of the voucher-paid
/// booking restores the voucher on the same terms as a credit refund.
/// </summary>
public class ApologyVoucher : Entity
{
    public Guid UserId { get; private set; }

    /// <summary>The occupancy mismatch this voucher apologizes for.</summary>
    public Guid SourceMismatchId { get; private set; }

    public DateTimeOffset GrantedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? RedeemedAtUtc { get; private set; }

    public Guid? RedeemedReservationId { get; private set; }

    /// <summary>The dynamic price the voucher absorbed, kept for economy stats.</summary>
    public int WaivedCredits { get; private set; }

    private ApologyVoucher() { }

    public ApologyVoucher(Guid userId, Guid sourceMismatchId, DateTimeOffset grantedAtUtc, DateTimeOffset expiresAtUtc)
    {
        UserId = userId;
        SourceMismatchId = sourceMismatchId;
        GrantedAtUtc = grantedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
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
