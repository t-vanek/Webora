namespace D3Parking.Application.Parking;

/// <summary>
/// Outcome of a blocked-spot report ("I can't park"): whether it went through, where the driver
/// goes now, and whether an apology voucher was granted. A null <see cref="RelocatedToSpotCode"/>
/// on success means the state was recorded and the reservation voided, but no replacement spot
/// was booked.
/// </summary>
public sealed record BlockedSpotOutcome(bool Succeeded, string? Error, string? RelocatedToSpotCode, bool VoucherGranted = false)
{
    public static BlockedSpotOutcome Recorded(bool voucherGranted) => new(true, null, null, voucherGranted);

    public static BlockedSpotOutcome Relocated(string spotCode, bool voucherGranted) => new(true, null, spotCode, voucherGranted);

    public static BlockedSpotOutcome Failure(string error) => new(false, error, null);
}
