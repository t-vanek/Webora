using Microsoft.EntityFrameworkCore;
using Webora.Application.Mapping;
using Webora.Application.Notifications;
using Webora.Domain.Notifications;
using Webora.Infrastructure.Persistence;

namespace Webora.Infrastructure.Notifications;

public sealed class NotificationService(
    WeboraDbContext dbContext,
    NotificationMapper mapper,
    INotificationRealtimePublisher publisher,
    TimeProvider timeProvider) : INotificationService
{
    public async Task NotifyAsync(Guid userId, NotificationLevel level, string title, string message, CancellationToken cancellationToken = default)
    {
        var notification = new Notification(userId, level, title, message, timeProvider.GetUtcNow());
        dbContext.Notifications.Add(notification);
        await dbContext.SaveChangesAsync(cancellationToken);

        await publisher.PublishAsync(userId, mapper.ToDto(notification), cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationDto>> GetAsync(Guid userId, bool unreadOnly = false, int take = 50, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAtUtc == null);
        }

        var ordered = query.OrderByDescending(n => n.CreatedAtUtc).Take(take);
        return await mapper.ProjectToDtos(ordered).ToListAsync(cancellationToken);
    }

    public Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default) =>
        dbContext.Notifications.CountAsync(n => n.UserId == userId && n.ReadAtUtc == null, cancellationToken);

    public async Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        var notification = await dbContext.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId, cancellationToken);
        if (notification is null || notification.IsRead)
        {
            return;
        }

        notification.MarkRead(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await dbContext.Notifications
            .Where(n => n.UserId == userId && n.ReadAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, now), cancellationToken);
    }
}
