namespace D3Parking.Domain.Common;

/// <summary>
/// Converts between the UTC instants everything is stored in and the site's local wall-clock, which
/// is what the parking rules are actually written in: the peak window, the resident hold cutoff and
/// "today" all mean local time at the lot, not UTC.
/// </summary>
/// <remarks>
/// The offset is resolved per instant through <see cref="TimeZoneInfo"/>, so daylight saving is
/// handled — a fixed <see cref="TimeSpan"/> offset would silently drift by an hour twice a year.
/// Local times that do not exist (the spring-forward gap) resolve to the offset in effect just
/// before the transition rather than throwing, so a misconfigured cutoff can never break a sweep.
/// </remarks>
public static class SiteTime
{
    /// <summary>The local calendar date an instant falls on.</summary>
    public static DateOnly Today(DateTimeOffset instant, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);

    /// <summary>The local time of day an instant falls on.</summary>
    public static TimeOnly TimeOfDay(DateTimeOffset instant, TimeZoneInfo timeZone) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);

    /// <summary>The instant at which a local wall-clock time occurs on a local date.</summary>
    public static DateTimeOffset At(DateOnly date, TimeOnly time, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(time);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }

    /// <summary>
    /// The half-open instant range covering a local calendar day. Measured midnight to midnight, so
    /// the 23- and 25-hour days around a daylight-saving switch come out right.
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Day(DateOnly date, TimeZoneInfo timeZone) =>
        (At(date, TimeOnly.MinValue, timeZone), At(date.AddDays(1), TimeOnly.MinValue, timeZone));
}
