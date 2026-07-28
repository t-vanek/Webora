using D3Parking.Application.Notifications;
using D3Parking.Contracts.Notifications;

namespace D3Parking.Infrastructure.Notifications;

/// <summary>Fallback publisher used when no real-time transport is configured (e.g. background/tests).</summary>
public sealed class NullNotificationRealtimePublisher : INotificationRealtimePublisher
{
    public Task PublishAsync(Guid userId, NotificationDto notification, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
