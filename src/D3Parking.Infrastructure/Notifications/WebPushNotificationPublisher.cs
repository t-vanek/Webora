using System.Net;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using D3Parking.Application.Notifications;
using D3Parking.Contracts.Notifications;
using D3Parking.Infrastructure.Persistence;
using PushServiceSubscription = Lib.Net.Http.WebPush.PushSubscription;

namespace D3Parking.Infrastructure.Notifications;

/// <summary>
/// Delivers a notification to every Web Push subscription of the user, so an installed PWA gets
/// it even when no tab is open. Best-effort like the email mirror: transport failures are logged
/// and never surface to the caller; subscriptions the push service reports dead (404/410) are
/// removed. The service worker suppresses the OS notification while the app is visible, so the
/// SignalR bell and the push channel do not double-notify.
/// </summary>
public sealed class WebPushNotificationPublisher(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    PushServiceClient pushClient,
    ILogger<WebPushNotificationPublisher> logger) : INotificationRealtimePublisher
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(Guid userId, NotificationDto notification, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var subscriptions = await dbContext.PushSubscriptions.AsNoTracking()
            .Where(s => s.UserId == userId)
            .ToListAsync(cancellationToken);

        if (subscriptions.Count == 0)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            title = notification.Title,
            body = notification.Message,
            url = "/",
            tag = notification.Id,
        }, PayloadJson);

        foreach (var subscription in subscriptions)
        {
            var target = new PushServiceSubscription { Endpoint = subscription.Endpoint };
            target.SetKey(PushEncryptionKeyName.P256DH, subscription.P256dh);
            target.SetKey(PushEncryptionKeyName.Auth, subscription.Auth);

            try
            {
                await pushClient.RequestPushMessageDeliveryAsync(target, new PushMessage(payload), cancellationToken);
            }
            catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                await dbContext.PushSubscriptions
                    .Where(s => s.Id == subscription.Id)
                    .ExecuteDeleteAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to deliver a push notification to {Endpoint}.", subscription.Endpoint);
            }
        }
    }
}
