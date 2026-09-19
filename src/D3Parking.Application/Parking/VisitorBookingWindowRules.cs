using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;

namespace D3Parking.Application.Parking;

/// <summary>The visitor form, availability query and command share the same temporal contract.</summary>
public static class VisitorBookingWindowRules
{
    public static string? Validate(DateTimeOffset startUtc, DateTimeOffset endUtc,
        IncentivePolicy policy, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        if (endUtc <= startUtc)
            return "Parking_Error_InvalidWindow";
        if (endUtc <= now)
            return "Parking_Error_PastWindow";
        // Reject dates outside the configured horizon before constructing a local day's end
        // (the final representable date has no following midnight).
        if (policy.GetReservationDateAvailability(startUtc, now, timeZone).ToParkingErrorKey() is { } dateError)
            return dateError;
        if (!ReservationWindowRules.MatchesMode(startUtc, endUtc, policy.ReservationTimeMode, timeZone))
            return "Parking_Error_ReservationTimeModeChanged";
        return null;
    }
}
