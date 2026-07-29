using System.Security.Claims;
using System.Text;
using D3Parking.Application.Parking;
using D3Parking.Application.Settings;
using D3Parking.Domain.Parking;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace D3Parking.Web.Parking;

/// <summary>
/// Calendar export of a reservation as an iCalendar (.ics) file — the one format Outlook, Google
/// Calendar and Apple Calendar all import natively. Times are emitted in UTC, so the event lands
/// correctly regardless of the calendar's time zone; a 30-minute display alarm nudges the driver
/// before the window (mirroring the in-app reminder).
/// </summary>
public static class CalendarEndpoints
{
    public static IEndpointRouteBuilder MapCalendarApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/parking/reservations/{id:guid}/calendar",
            async (Guid id, ClaimsPrincipal user, IReservationService reservations, ISiteSettingsService siteSettings, CancellationToken ct) =>
            {
                if (!Guid.TryParse(user.FindFirstValue(Claims.Subject), out var userId))
                {
                    return Results.Unauthorized();
                }

                var reservation = await reservations.GetMyReservationAsync(userId, id, ct);
                if (reservation is null)
                {
                    return Results.NotFound();
                }

                // Only a live booking makes sense as a calendar entry.
                if (reservation.Status is not (ReservationStatus.Reserved or ReservationStatus.CheckedIn))
                {
                    return Results.NotFound();
                }

                var identity = await siteSettings.GetIdentityAsync(ct);
                var siteName = string.IsNullOrWhiteSpace(identity.Name) ? "D3Parking" : identity.Name.Trim();

                var ics = BuildIcs(reservation, siteName);
                return Results.File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8",
                    $"parkovani-{reservation.SpotCode}.ics");
            }).RequireAuthorization();

        return app;
    }

    private static string BuildIcs(ReservationDto reservation, string siteName)
    {
        var summary = Escape($"{siteName}: parkování {reservation.SpotCode}");
        var description = Escape($"Rezervace parkovacího místa {reservation.SpotCode}.");

        // CRLF line endings are part of the iCalendar grammar.
        var lines = new[]
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//D3Parking//Reservation//CS",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            "BEGIN:VEVENT",
            $"UID:{reservation.Id}@d3parking",
            $"DTSTAMP:{FormatUtc(DateTimeOffset.UtcNow)}",
            $"DTSTART:{FormatUtc(reservation.StartUtc)}",
            $"DTEND:{FormatUtc(reservation.EndUtc)}",
            $"SUMMARY:{summary}",
            $"LOCATION:{Escape(siteName)}",
            $"DESCRIPTION:{description}",
            "BEGIN:VALARM",
            "ACTION:DISPLAY",
            $"DESCRIPTION:{summary}",
            "TRIGGER:-PT30M",
            "END:VALARM",
            "END:VEVENT",
            "END:VCALENDAR",
        };

        return string.Join("\r\n", lines) + "\r\n";
    }

    private static string FormatUtc(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>iCalendar TEXT escaping: backslash, semicolon, comma and newlines.</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");
}
