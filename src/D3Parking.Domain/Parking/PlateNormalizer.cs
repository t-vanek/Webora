namespace D3Parking.Domain.Parking;

/// <summary>
/// The one canonical form a license plate is compared in: letters and digits only, upper-cased.
/// Plates arrive typed by people ("1AB 2345", "1ab-2345") and every consumer — fleet pairing,
/// the blocked-spot plate matching, visitor bookings — must agree on the same tolerance, or a
/// plate would pair in one place and miss in another.
/// </summary>
public static class PlateNormalizer
{
    public static string Normalize(string plate) =>
        new(plate.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
