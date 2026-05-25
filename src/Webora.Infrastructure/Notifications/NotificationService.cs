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
    public async Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, CancellationToken cancellationToken = default)
    {
        var preferences = await GetOrCreatePreferencesAsync(userId, cancellationToken);

        // Category scope decides whether the notification is created at all.
        if (!preferences.Allows(category))
        {
            return;
        }

        var notification = new Notification(userId, category, level, title, message, timeProvider.GetUtcNow());
        dbContext.Notifications.Add(notification);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Muting suppresses only the live push; the notification is still stored.
        if (!preferences.IsCurrentlyMuted(timeProvider.GetUtcNow()))
        {
            await publisher.PublishAsync(userId, mapper.ToDto(notification), cancellationToken);
        }
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

    public async Task<NotificationPreferencesDto> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var preferences = await dbContext.NotificationPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        return preferences is null
            ? new NotificationPreferencesDto(false, null, false, NotificationScope.All)
            : ToDto(preferences);
    }

    public async Task MuteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var preferences = await GetOrCreatePreferencesAsync(userId, cancellationToken);
        preferences.MuteIndefinitely();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MuteUntilAsync(Guid userId, DateTimeOffset untilUtc, CancellationToken cancellationToken = default)
    {
        var preferences = await GetOrCreatePreferencesAsync(userId, cancellationToken);
        preferences.MuteUntil(untilUtc);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UnmuteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var preferences = await GetOrCreatePreferencesAsync(userId, cancellationToken);
        preferences.Unmute();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SetScopeAsync(Guid userId, NotificationScope scope, CancellationToken cancellationToken = default)
    {
        var preferences = await GetOrCreatePreferencesAsync(userId, cancellationToken);
        preferences.SetScope(scope);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<NotificationPreferences> GetOrCreatePreferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        // FindAsync consults the change tracker first, so a row created earlier in the same
        // request (and not yet saved) is reused rather than added twice.
        var preferences = await dbContext.NotificationPreferences.FindAsync([userId], cancellationToken);
        if (preferences is null)
        {
            preferences = new NotificationPreferences(userId);
            dbContext.NotificationPreferences.Add(preferences);
        }

        return preferences;
    }

    private NotificationPreferencesDto ToDto(NotificationPreferences preferences) =>
        new(preferences.Muted, preferences.MutedUntilUtc, preferences.IsCurrentlyMuted(timeProvider.GetUtcNow()), preferences.Scope);
}
