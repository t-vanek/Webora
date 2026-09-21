namespace D3Parking.Domain.Parking;

/// <summary>
/// A set of weekdays, used for a resident's standing usage plan ("which days I need my spot").
/// A flags set rather than a table: the plan is a fixed seven-slot pattern, so one int column on
/// the spot keeps it a single read wherever the plan is consulted.
/// </summary>
[Flags]
public enum Weekday
{
    None = 0,
    Monday = 1 << 0,
    Tuesday = 1 << 1,
    Wednesday = 1 << 2,
    Thursday = 1 << 3,
    Friday = 1 << 4,
    Saturday = 1 << 5,
    Sunday = 1 << 6,

    /// <summary>Monday through Friday — the default plan for a commuter.</summary>
    Workdays = Monday | Tuesday | Wednesday | Thursday | Friday,

    Everyday = Workdays | Saturday | Sunday,
}

public static class WeekdayExtensions
{
    /// <summary>The flag standing for a calendar day of week.</summary>
    public static Weekday ToWeekday(this DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Monday => Weekday.Monday,
        DayOfWeek.Tuesday => Weekday.Tuesday,
        DayOfWeek.Wednesday => Weekday.Wednesday,
        DayOfWeek.Thursday => Weekday.Thursday,
        DayOfWeek.Friday => Weekday.Friday,
        DayOfWeek.Saturday => Weekday.Saturday,
        _ => Weekday.Sunday,
    };

    /// <summary>Whether the set contains the given calendar date's weekday.</summary>
    public static bool Includes(this Weekday set, DateOnly date) => (set & date.DayOfWeek.ToWeekday()) != 0;

    /// <summary>Drops any bit outside <see cref="Weekday.Everyday"/>, so a bad value cannot persist.</summary>
    public static Weekday Sanitize(this Weekday set) => set & Weekday.Everyday;

    /// <summary>Number of real weekdays represented by the sanitized flag set.</summary>
    public static int CountDays(this Weekday set)
    {
        var flags = (int)set.Sanitize();
        var count = 0;
        while (flags != 0)
        {
            count += flags & 1;
            flags >>= 1;
        }

        return count;
    }

    /// <summary>Bounds a weekly reservation quota by the weekdays the calendar exposes.</summary>
    public static int ClampWeeklyReservationLimit(this Weekday set, int requestedLimit) =>
        Math.Clamp(requestedLimit, 1, Math.Max(1, set.CountDays()));
}
