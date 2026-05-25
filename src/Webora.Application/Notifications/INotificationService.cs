using Webora.Domain.Notifications;

namespace Webora.Application.Notifications;

public interface INotificationService
{
    Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationDto>> GetAsync(Guid userId, bool unreadOnly = false, int take = 50, CancellationToken cancellationToken = default);

    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default);

    Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default);

    Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default);

    // Preferences: muting (do-not-disturb) and category scope.
    Task<NotificationPreferencesDto> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken = default);

    Task MuteAsync(Guid userId, CancellationToken cancellationToken = default);

    Task MuteUntilAsync(Guid userId, DateTimeOffset untilUtc, CancellationToken cancellationToken = default);

    Task UnmuteAsync(Guid userId, CancellationToken cancellationToken = default);

    Task SetScopeAsync(Guid userId, NotificationScope scope, CancellationToken cancellationToken = default);
}
