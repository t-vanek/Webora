using D3Parking.Application.Notifications;
using D3Parking.Contracts.Notifications;

namespace D3Parking.Web.Notifications;

/// <summary>
/// Fans a freshly created notification out to every live channel: the SignalR bell for open tabs
/// and Web Push for installed apps with no tab open. Null channels (a feature that is not
/// configured) are skipped, and channels fail independently — a wedged SignalR connection must
/// not cost the user their Web Push (or vice versa).
/// </summary>
public sealed class CompositeNotificationPublisher(
    ILogger<CompositeNotificationPublisher> logger,
    params INotificationRealtimePublisher?[] publishers)
    : INotificationRealtimePublisher
{
    public async Task PublishAsync(Guid userId, NotificationDto notification, CancellationToken cancellationToken = default)
    {
        foreach (var publisher in publishers)
        {
            if (publisher is null)
            {
                continue;
            }

            try
            {
                await publisher.PublishAsync(userId, notification, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Notification channel {Channel} failed for {UserId}.",
                    publisher.GetType().Name, userId);
            }
        }
    }
}
