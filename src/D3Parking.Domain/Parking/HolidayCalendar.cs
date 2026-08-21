namespace D3Parking.Domain.Parking;

/// <summary>The public-holiday calendar used by the parking site.</summary>
public enum HolidayCalendarRegion
{
    None = 0,
    CzechRepublic = 1,
}

/// <summary>
/// Deterministic public-holiday dates used by reservation rules. The calendar belongs to the
/// parking site, not to the browser culture of the person making a reservation.
/// </summary>
public static class HolidayCalendar
{
    public static bool IsPublicHoliday(DateOnly date, HolidayCalendarRegion region) => region switch
    {
        HolidayCalendarRegion.CzechRepublic => IsCzechPublicHoliday(date),
        _ => false,
    };

    private static bool IsCzechPublicHoliday(DateOnly date)
    {
        if ((date.Month, date.Day) is
            (1, 1) or
            (5, 1) or (5, 8) or
            (7, 5) or (7, 6) or
            (9, 28) or
            (10, 28) or
            (11, 17) or
            (12, 24) or (12, 25) or (12, 26))
        {
            return true;
        }

        var easterSunday = GregorianEasterSunday(date.Year);
        return date == easterSunday.AddDays(-2) || date == easterSunday.AddDays(1);
    }

    // Meeus/Jones/Butcher algorithm for Gregorian Easter.
    private static DateOnly GregorianEasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }
}
