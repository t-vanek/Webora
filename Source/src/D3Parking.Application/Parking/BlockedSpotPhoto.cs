namespace D3Parking.Application.Parking;

/// <summary>
/// The photo a driver attaches to an "I can't park" report as proof of the blocked spot.
/// Required — a report without one is refused, and the spot manager judges it before the
/// apology voucher becomes redeemable.
/// </summary>
public sealed record BlockedSpotPhoto(byte[] Content, string ContentType)
{
    /// <summary>Upper bound for the upload, shared by the UI and the service. 8 MB fits any phone camera JPEG.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;
}
