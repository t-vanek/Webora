using D3Parking.Domain.Parking;
using D3Parking.Domain.Parking.Incentives;
using D3Parking.Domain.Common;

namespace D3Parking.Application.Parking;

/// <summary>The visitor form, availability query and command share the same temporal contract.</summary>
public static class VisitorBookingWindowRules
{
    /// <summary>Choose a usable initial interval inside the site's current booking calendar.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End)? FindInitialWindow(
        IncentivePolicy policy, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var today = SiteTime.Today(now, timeZone);
        var lastDay = today.AddDays(Math.Clamp(policy.ReservationHorizonDays, 1, 366));
        for (var day = policy.FirstBookableDate(today); day <= lastDay; day = day.AddDays(1))
        {
            if (policy.GetReservationDateAvailability(day, today) != ReservationDateAvailability.Allowed)
                continue;

            var wholeDay = SiteTime.Day(day, timeZone);
            if (policy.ReservationTimeMode == ReservationTimeMode.AllDay)
                return wholeDay;

            var start = SiteTime.At(day, new TimeOnly(8, 0), timeZone);
            if (start <= now)
            {
                // Native time inputs use minute precision. Round forward so opening the form
                // cannot immediately prefill an elapsed interval, including late in the evening.
                start = now.AddTicks(TimeSpan.TicksPerMinute - now.Ticks % TimeSpan.TicksPerMinute);
            }
            if (start >= wholeDay.End) continue;

            var end = SiteTime.At(day, new TimeOnly(17, 0), timeZone);
            if (end < start.AddHours(1)) end = start.AddHours(1);
            if (end > wholeDay.End) end = wholeDay.End;
            if (Validate(start, end, policy, now, timeZone) is null)
                return (start, end);
        }

        return null;
    }

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
