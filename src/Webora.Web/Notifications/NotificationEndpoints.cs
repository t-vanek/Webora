using System.Security.Claims;
using Webora.Application.Notifications;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Webora.Web.Notifications;

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
        }).DisableAntiforgery();

        group.MapPost("/read-all", async (ClaimsPrincipal user, INotificationService service, CancellationToken ct) =>
        {
            var userId = GetUserId(user);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            await service.MarkAllReadAsync(userId.Value, ct);
            return Results.NoContent();
        }).DisableAntiforgery();

        return app;
    }

    private static Guid? GetUserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(Claims.Subject), out var id) ? id : null;
}
