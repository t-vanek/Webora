using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>Canonical interpretation of reservation windows in the parking site's time zone.</summary>
public static class ReservationWindowRules
{
    public static bool MatchesMode(
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        ReservationTimeMode mode,
        TimeZoneInfo timeZone) => mode switch
    {
        ReservationTimeMode.AllDay => IsFullLocalDay(startUtc, endUtc, timeZone),
        _ => !IsFullLocalDay(startUtc, endUtc, timeZone)
             && SiteTime.Today(startUtc, timeZone) == SiteTime.Today(endUtc.AddTicks(-1), timeZone),
    };

    public static bool IsFullLocalDay(DateTimeOffset startUtc, DateTimeOffset endUtc, TimeZoneInfo timeZone)
    {
        var date = SiteTime.Today(startUtc, timeZone);
        var day = SiteTime.Day(date, timeZone);
        return startUtc == day.Start && endUtc == day.End;
    }
}
