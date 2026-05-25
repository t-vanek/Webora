using Webora.Domain.Notifications;

namespace Webora.Application.Notifications;

public interface INotificationService
{
    Task NotifyAsync(Guid userId, NotificationLevel level, string title, string message, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationDto>> GetAsync(Guid userId, bool unreadOnly = false, int take = 50, CancellationToken cancellationToken = default);

    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default);

    Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default);

    Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default);
}
