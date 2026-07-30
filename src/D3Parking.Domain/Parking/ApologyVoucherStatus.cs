namespace D3Parking.Domain.Parking;

/// <summary>
/// Review state of an apology voucher. A voucher starts pending and only a spot manager's
/// approval — after judging the reporter's photo proof — makes it redeemable.
/// </summary>
public enum ApologyVoucherStatus
{
    /// <summary>Granted by the blocked-spot flow, waiting for a spot manager's review.</summary>
    PendingApproval,

    /// <summary>A spot manager confirmed the report; the voucher is redeemable until it expires.</summary>
    Approved,

    /// <summary>A spot manager rejected the report; the voucher can never be redeemed.</summary>
    Rejected,
}
