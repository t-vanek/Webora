using D3Parking.Domain.Common;

namespace D3Parking.Domain.Parking;

/// <summary>Canonical interpretation of reservation windows in the parking site's time zone.</summary>
public static class ReservationWindowRules
{
    /// <summary>
    /// A started booking keeps its spot until its stored end, independently of current policy.
    /// All-day windows therefore lock at the site's midnight, including 23/25-hour days.
    /// The holder may still give up the booking voluntarily.
    /// </summary>
    public static bool IsProtectedFromDisplacement(
        DateTimeOffset startUtc, DateTimeOffset endUtc, ReservationStatus status, DateTimeOffset now) =>
        status is ReservationStatus.Reserved or ReservationStatus.CheckedIn
        && now < endUtc && (startUtc <= now || status == ReservationStatus.CheckedIn);

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
