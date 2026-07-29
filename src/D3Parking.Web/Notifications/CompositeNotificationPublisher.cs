using D3Parking.Application.Notifications;
using D3Parking.Contracts.Notifications;

namespace D3Parking.Web.Notifications;

/// <summary>
/// Fans a freshly created notification out to every live channel: the SignalR bell for open tabs
/// and Web Push for installed apps with no tab open. Null channels (a feature that is not
/// configured) are skipped.
/// </summary>
public sealed class CompositeNotificationPublisher(params INotificationRealtimePublisher?[] publishers)
    : INotificationRealtimePublisher
{
    public async Task PublishAsync(Guid userId, NotificationDto notification, CancellationToken cancellationToken = default)
    {
        foreach (var publisher in publishers)
        {
            if (publisher is not null)
            {
                await publisher.PublishAsync(userId, notification, cancellationToken);
            }
        }
    }
}
