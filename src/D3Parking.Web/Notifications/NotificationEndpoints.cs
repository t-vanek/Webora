using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using D3Parking.Application.Notifications;
using D3Parking.Contracts.Notifications;
using D3Parking.Domain.Notifications;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace D3Parking.Web.Notifications;

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").RequireAuthorization();

        // Cookie-authenticated JSON endpoints get no automatic antiforgery validation — the
        // UseAntiforgery middleware only checks endpoints carrying antiforgery metadata, which
        // minimal APIs infer for form binding alone. Validate the RequestVerificationToken header
        // (attached by the WASM client) explicitly on every unsafe verb, so CSRF protection does
        // not rest on the SameSite cookie attribute alone.
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var httpContext = invocation.HttpContext;
            var method = httpContext.Request.Method;
            if (HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsDelete(method))
            {
                try
                {
                    await httpContext.RequestServices.GetRequiredService<IAntiforgery>()
                        .ValidateRequestAsync(httpContext);
                }
                catch (AntiforgeryValidationException)
                {
                    // With a body on purpose: bodyless 4xx responses are re-executed by the
                    // status-code pages middleware (see the subscription endpoint below).
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid antiforgery token.");
                }
            }

            return await next(invocation);
        });

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

        group.MapPost("/preferences/mute", async (ClaimsPrincipal user, INotificationService service, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            if (userId is null) return Results.Unauthorized();
            await service.MuteAsync(userId.Value, ct);
            return Results.NoContent();
        });

        group.MapPost("/preferences/unmute", async (ClaimsPrincipal user, INotificationService service, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            if (userId is null) return Results.Unauthorized();
            await service.UnmuteAsync(userId.Value, ct);
            return Results.NoContent();
        });

        group.MapPut("/preferences/scope", async (NotificationScopeRequest request, ClaimsPrincipal user,
            INotificationService service, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            if (userId is null) return Results.Unauthorized();
            await service.SetScopeAsync(userId.Value, request.Scope, ct);
            return Results.NoContent();
        });

        group.MapPut("/preferences/availability", async (AvailabilityPreferenceRequest request, ClaimsPrincipal user,
            INotificationService service, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            if (userId is null) return Results.Unauthorized();
            await service.SetAvailabilityOptInAsync(userId.Value, request.Allow, ct);
            return Results.NoContent();
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

    private sealed record NotificationScopeRequest(NotificationScope Scope);
    private sealed record AvailabilityPreferenceRequest(bool Allow);
}
