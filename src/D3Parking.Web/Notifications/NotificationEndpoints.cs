using System.Security.Claims;
using Microsoft.Extensions.Options;
using D3Parking.Application.Notifications;
using D3Parking.Contracts.Notifications;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace D3Parking.Web.Notifications;

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").RequireAuthorization();

        group.MapGet("", async (ClaimsPrincipal user, INotificationService service, bool? unreadOnly, int? take, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            return userId is null
                ? Results.Unauthorized()
                : Results.Ok(await service.GetAsync(userId.Value, unreadOnly ?? false, take ?? 50, ct));
        });

        group.MapGet("/unread-count", async (ClaimsPrincipal user, INotificationService service, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            return userId is null
                ? Results.Unauthorized()
                : Results.Ok(await service.GetUnreadCountAsync(userId.Value, ct));
        });

        group.MapGet("/preferences", async (ClaimsPrincipal user, INotificationService service, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            return userId is null
                ? Results.Unauthorized()
                : Results.Ok(await service.GetPreferencesAsync(userId.Value, ct));
        });

        group.MapPost("/{id:guid}/read", async (Guid id, ClaimsPrincipal user, INotificationService service, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            await service.MarkReadAsync(userId.Value, id, ct);
            return Results.NoContent();
        });

        group.MapPost("/read-all", async (ClaimsPrincipal user, INotificationService service, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            await service.MarkAllReadAsync(userId.Value, ct);
            return Results.NoContent();
        });

        // Web Push: the browser needs the VAPID public key to subscribe; 204 means the feature is
        // not configured and the client hides its toggle.
        group.MapGet("/push/public-key", (IOptions<WebPushOptions> webPush) =>
            webPush.Value.IsConfigured
                ? Results.Ok(new PushPublicKeyDto(webPush.Value.PublicKey))
                : Results.NoContent());

        group.MapPut("/push/subscription", async (PushSubscriptionDto subscription, ClaimsPrincipal user, INotificationService service, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            if (!IsValidSubscription(subscription))
            {
                // With a body on purpose: bodyless 4xx responses are re-executed by the status-code
                // pages middleware, which turns them into a misleading 405 for non-GET requests.
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest);
            }

            await service.SubscribeToPushAsync(userId.Value, subscription, ct);
            return Results.NoContent();
        });

        group.MapDelete("/push/subscription", async (string endpoint, ClaimsPrincipal user, INotificationService service, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            await service.UnsubscribeFromPushAsync(userId.Value, endpoint, ct);
            return Results.NoContent();
        });

        return app;
    }

    // Transport-level sanity check; the length caps mirror the column sizes in the database.
    private static bool IsValidSubscription(PushSubscriptionDto subscription) =>
        Uri.TryCreate(subscription.Endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && subscription.Endpoint.Length <= 800
        && !string.IsNullOrWhiteSpace(subscription.P256dh) && subscription.P256dh.Length <= 128
        && !string.IsNullOrWhiteSpace(subscription.Auth) && subscription.Auth.Length <= 64;

    private static Guid? GetUserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(Claims.Subject), out var id) ? id : null;
}
