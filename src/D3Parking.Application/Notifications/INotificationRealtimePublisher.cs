using D3Parking.Contracts.Notifications;

namespace D3Parking.Application.Notifications;

/// <summary>Pushes a freshly created notification to the user in real time. Implemented over SignalR in the web host.</summary>
public interface INotificationRealtimePublisher
{
    Task PublishAsync(Guid userId, NotificationDto notification, CancellationToken cancellationToken = default);
}
