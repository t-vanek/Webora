using System.Globalization;
using System.Text;
using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

/// <summary>Standards-oriented iCalendar rendering shared by downloads and subscribed feeds.</summary>
public static class CalendarIcsRenderer
{
    public static string BuildSingle(ReservationDto reservation, string siteName, TimeZoneInfo timeZone) =>
        BuildCalendar([reservation], siteName, timeZone, $"{siteName} – parkování");

    public static string BuildFeed(
        IReadOnlyCollection<ReservationDto> reservations,
        string siteName,
        TimeZoneInfo timeZone) =>
        BuildCalendar(reservations, siteName, timeZone, $"{siteName} – moje parkování", includeRefreshHint: true);

    private static string BuildCalendar(
        IEnumerable<ReservationDto> reservations,
        string siteName,
        TimeZoneInfo timeZone,
        string calendarName,
        bool includeRefreshHint = false)
    {
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Webora//D3Parking//CS",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            $"X-WR-CALNAME:{Escape(calendarName)}",
        };

        if (includeRefreshHint)
        {
            // Widely understood subscription hints. Clients remain free to choose their own poll interval.
            lines.Add("REFRESH-INTERVAL;VALUE=DURATION:PT15M");
            lines.Add("X-PUBLISHED-TTL:PT15M");
        }

        foreach (var reservation in reservations.OrderBy(r => r.StartUtc).ThenBy(r => r.Id))
        {
            AppendEvent(lines, reservation, siteName, timeZone);
        }

        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines.SelectMany(FoldLine)) + "\r\n";
    }

    private static void AppendEvent(
        ICollection<string> lines,
        ReservationDto reservation,
        string siteName,
        TimeZoneInfo timeZone)
    {
        var summary = Escape($"{siteName}: parkování {reservation.SpotCode}");
        var description = Escape($"Rezervace parkovacího místa {reservation.SpotCode}.");
        var cancelled = reservation.Status is ReservationStatus.Cancelled
            or ReservationStatus.Released
            or ReservationStatus.NoShow;

        lines.Add("BEGIN:VEVENT");
        lines.Add($"UID:{reservation.Id:D}@d3parking");
        lines.Add($"DTSTAMP:{FormatUtc(reservation.CalendarUpdatedAtUtc)}");
        lines.Add($"CREATED:{FormatUtc(reservation.CreatedAtUtc)}");
        lines.Add($"LAST-MODIFIED:{FormatUtc(reservation.CalendarUpdatedAtUtc)}");
        lines.Add($"SEQUENCE:{reservation.CalendarSequence}");
        lines.Add($"STATUS:{(cancelled ? "CANCELLED" : "CONFIRMED")}");

        var allDay = ReservationWindowRules.IsFullLocalDay(
            reservation.StartUtc, reservation.EndUtc, timeZone);
        if (allDay)
        {
            var startDate = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(reservation.StartUtc, timeZone).DateTime);
            lines.Add($"DTSTART;VALUE=DATE:{FormatDate(startDate)}");
            lines.Add($"DTEND;VALUE=DATE:{FormatDate(startDate.AddDays(1))}");
        }
        else
        {
            lines.Add($"DTSTART:{FormatUtc(reservation.StartUtc)}");
            lines.Add($"DTEND:{FormatUtc(reservation.EndUtc)}");
        }

        lines.Add($"SUMMARY:{summary}");
        lines.Add($"LOCATION:{Escape(siteName)}");
        lines.Add($"DESCRIPTION:{description}");
        lines.Add("TRANSP:OPAQUE");

        if (!allDay && !cancelled)
        {
            lines.Add("BEGIN:VALARM");
            lines.Add("ACTION:DISPLAY");
            lines.Add($"DESCRIPTION:{summary}");
            lines.Add("TRIGGER:-PT30M");
            lines.Add("END:VALARM");
        }

        lines.Add("END:VEVENT");
    }

    private static string FormatUtc(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string FormatDate(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,")
            .Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    /// <summary>RFC 5545 content lines are folded at 75 UTF-8 octets, never inside a Unicode rune.</summary>
    private static IEnumerable<string> FoldLine(string value)
    {
        var current = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (bytes + runeBytes > 75 && current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
                current.Append(' ');
                bytes = 1;
            }

            current.Append(rune.ToString());
            bytes += runeBytes;
        }

        yield return current.ToString();
    }
}
