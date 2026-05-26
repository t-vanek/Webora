using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Webora.Application.Abstractions.Email;
using Webora.Application.Mapping;
using Webora.Application.Notifications;
using Webora.Contracts.Notifications;
using Webora.Domain.Notifications;
using Webora.Infrastructure.Persistence;

namespace Webora.Infrastructure.Notifications;

public sealed class NotificationService(
    IDbContextFactory<WeboraDbContext> dbContextFactory,
    NotificationMapper mapper,
    INotificationRealtimePublisher publisher,
    IEmailSender emailSender,
    ILogger<NotificationService> logger,
    TimeProvider timeProvider) : INotificationService
{
    public Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, CancellationToken cancellationToken = default) =>
        NotifyAsync(userId, category, level, title, message, email: false, cancellationToken);

    public async Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, bool email, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var preferences = await GetOrCreatePreferencesAsync(dbContext, userId, cancellationToken);

        // Category scope decides whether the notification is created at all.
        if (!preferences.Allows(category))
        {
            return;
        }

        var notification = new Notification(userId, category, level, title, message, timeProvider.GetUtcNow());
        dbContext.Notifications.Add(notification);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Muting suppresses the live push and the email mirror; the notification is still stored.
        if (!preferences.IsCurrentlyMuted(timeProvider.GetUtcNow()))
        {
            await publisher.PublishAsync(userId, mapper.ToDto(notification), cancellationToken);

            if (email)
            {
                await TrySendEmailAsync(dbContext, userId, title, message, cancellationToken);
            }
        }
    }

    private async Task TrySendEmailAsync(WeboraDbContext dbContext, Guid userId, string title, string message, CancellationToken cancellationToken)
    {
        try
        {
            var recipient = await dbContext.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.Email, u.DisplayName })
                .FirstOrDefaultAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(recipient?.Email))
            {
                return;
            }

            await emailSender.SendAsync(new EmailMessage
            {
                To = recipient.Email,
                ToName = recipient.DisplayName,
                Subject = title,
                HtmlBody = $"<p>{System.Net.WebUtility.HtmlEncode(message)}</p>",
                TextBody = message,
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            // Email is best-effort; a transport failure must not break the notification or its caller.
            logger.LogWarning(ex, "Failed to mirror notification to email for {UserId}.", userId);
        }
    }

    public async Task<IReadOnlyList<NotificationDto>> GetAsync(Guid userId, bool unreadOnly = false, int take = 50, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = dbContext.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAtUtc == null);
        }

        var ordered = query.OrderByDescending(n => n.CreatedAtUtc).Take(take);
        return await mapper.ProjectToDtos(ordered).ToListAsync(cancellationToken);
    }

    public async Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Notifications.CountAsync(n => n.UserId == userId && n.ReadAtUtc == null, cancellationToken);
    }

    public async Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
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
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await dbContext.Notifications
            .Where(n => n.UserId == userId && n.ReadAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, now), cancellationToken);
    }

    public async Task<NotificationPreferencesDto> GetPreferencesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var preferences = await dbContext.NotificationPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        return preferences is null
            ? new NotificationPreferencesDto(false, null, false, NotificationScope.All)
            : ToDto(preferences);
    }

    public async Task MuteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var preferences = await GetOrCreatePreferencesAsync(dbContext, userId, cancellationToken);
        preferences.MuteIndefinitely();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MuteUntilAsync(Guid userId, DateTimeOffset untilUtc, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var preferences = await GetOrCreatePreferencesAsync(dbContext, userId, cancellationToken);
        preferences.MuteUntil(untilUtc);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UnmuteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var preferences = await GetOrCreatePreferencesAsync(dbContext, userId, cancellationToken);
        preferences.Unmute();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SetScopeAsync(Guid userId, NotificationScope scope, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var preferences = await GetOrCreatePreferencesAsync(dbContext, userId, cancellationToken);
        preferences.SetScope(scope);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task<NotificationPreferences> GetOrCreatePreferencesAsync(WeboraDbContext dbContext, Guid userId, CancellationToken cancellationToken)
    {
        // FindAsync consults the change tracker first, so a row created earlier in the same
        // operation (and not yet saved) is reused rather than added twice.
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
