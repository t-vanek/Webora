using Webora.Application.Notifications;

namespace Webora.Infrastructure.Notifications;

/// <summary>Fallback publisher used when no real-time transport is configured (e.g. background/tests).</summary>
public sealed class NullNotificationRealtimePublisher : INotificationRealtimePublisher
{
    public Task PublishAsync(Guid userId, NotificationDto notification, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
