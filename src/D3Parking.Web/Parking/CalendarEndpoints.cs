using System.Security.Claims;
using System.Security.Cryptography;
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

                var identityTask = siteSettings.GetIdentityAsync(ct);
                var timeZoneTask = siteSettings.GetTimeZoneAsync(ct);
                await Task.WhenAll(identityTask, timeZoneTask);
                var identity = identityTask.Result;
                var siteName = string.IsNullOrWhiteSpace(identity.Name) ? "D3Parking" : identity.Name.Trim();

                var ics = CalendarIcsRenderer.BuildSingle(reservation, siteName, timeZoneTask.Result);
                return Results.File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8",
                    $"parkovani-{reservation.SpotCode}.ics");
            }).RequireAuthorization();

        // A private token is required because calendar clients fetch subscribed URLs outside the
        // browser session and therefore cannot use the application's login cookie.
        app.MapGet("/api/parking/calendar/{token}.ics",
            async (string token, HttpContext httpContext, ICalendarSubscriptionService subscriptions,
                ISiteSettingsService siteSettings, CancellationToken ct) =>
            {
                var userId = await subscriptions.ResolveUserAsync(token, ct);
                if (userId is null) return Results.NotFound();

                var reservationsTask = subscriptions.GetFeedReservationsAsync(userId.Value, ct);
                var identityTask = siteSettings.GetIdentityAsync(ct);
                var timeZoneTask = siteSettings.GetTimeZoneAsync(ct);
                await Task.WhenAll(reservationsTask, identityTask, timeZoneTask);

                var siteName = string.IsNullOrWhiteSpace(identityTask.Result.Name)
                    ? "D3Parking"
                    : identityTask.Result.Name.Trim();
                var ics = CalendarIcsRenderer.BuildFeed(
                    reservationsTask.Result, siteName, timeZoneTask.Result);
                var etag = $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ics)))}\"";

                httpContext.Response.Headers.ETag = etag;
                httpContext.Response.Headers.CacheControl = "private, no-cache";
                httpContext.Response.Headers.ContentDisposition = "inline; filename=moje-parkovani.ics";
                if (httpContext.Request.Headers.IfNoneMatch.Any(v =>
                        v is not null && v.Split(',').Any(t => t.Trim() == etag)))
                {
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                return Results.Text(ics, "text/calendar; charset=utf-8", Encoding.UTF8);
            }).AllowAnonymous();

        return app;
    }
}
