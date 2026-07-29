using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using D3Parking.Application.Abstractions.Email;
using D3Parking.Application.Mapping;
using D3Parking.Application.Notifications;
using D3Parking.Contracts.Notifications;
using D3Parking.Domain.Notifications;
using D3Parking.Infrastructure.Email;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Notifications;

public sealed class NotificationService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    NotificationMapper mapper,
    INotificationRealtimePublisher publisher,
    IEmailSender emailSender,
    IStringLocalizer<ParkingMessages> messages,
    ILogger<NotificationService> logger,
    TimeProvider timeProvider) : INotificationService
{
    public Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, CancellationToken cancellationToken = default) =>
        NotifyAsync(userId, category, level, title, message, email: false, emailOptions: null, cancellationToken);

    public Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, bool email, CancellationToken cancellationToken = default) =>
        NotifyAsync(userId, category, level, title, message, email, emailOptions: null, cancellationToken);

    public async Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, bool email, NotificationEmailOptions? emailOptions, CancellationToken cancellationToken = default)
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
                await TrySendEmailAsync(dbContext, userId, title, message, emailOptions, cancellationToken);
            }
        }
    }

    private async Task TrySendEmailAsync(D3ParkingDbContext dbContext, Guid userId, string title, string message, NotificationEmailOptions? options, CancellationToken cancellationToken)
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

            var reason = messages["Email_Chrome_Reason"].Value;
            var html = BrandedEmail.RenderHtml(new BrandedEmail.Content(
                Heading: title,
                BodyHtml: System.Net.WebUtility.HtmlEncode(message),
                FooterReason: reason,
                FooterSettings: messages["Email_Chrome_Settings"].Value,
                ActionText: options?.ActionText,
                ActionUrl: options?.ActionUrl,
                DeadlineText: options?.DeadlineText));

            await emailSender.SendAsync(new EmailMessage
            {
                To = recipient.Email,
                ToName = recipient.DisplayName,
                Subject = title,
                HtmlBody = html,
                TextBody = BrandedEmail.RenderText(title, message, options?.ActionUrl, options?.DeadlineText, reason),
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
        // take comes straight from the query string; unclamped it would be a free-form page size
        // (and a negative value is a SQL error in TOP()).
        var limit = Math.Clamp(take, 1, 200);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = dbContext.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAtUtc == null);
        }

        var ordered = query.OrderByDescending(n => n.CreatedAtUtc).Take(limit);
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
            ? new NotificationPreferencesDto(false, null, false, NotificationScope.All, AllowAvailability: true)
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

    public async Task SetAvailabilityOptInAsync(Guid userId, bool allow, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var preferences = await GetOrCreatePreferencesAsync(dbContext, userId, cancellationToken);
        preferences.SetAvailabilityOptIn(allow);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SubscribeToPushAsync(Guid userId, PushSubscriptionDto subscription, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await dbContext.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == subscription.Endpoint, cancellationToken);

        if (existing is null)
        {
            dbContext.PushSubscriptions.Add(new PushSubscription(
                userId, subscription.Endpoint, subscription.P256dh, subscription.Auth, timeProvider.GetUtcNow()));
        }
        else
        {
            // The browser reuses the endpoint when a different account subscribes on the same
            // installation — re-point the record instead of duplicating it.
            existing.Reassign(userId, subscription.P256dh, subscription.Auth, timeProvider.GetUtcNow());
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Concurrent subscribe with the same endpoint lost the unique-index race; the winning
            // row already carries fresh keys, so there is nothing left to do.
        }
    }

    public async Task UnsubscribeFromPushAsync(Guid userId, string endpoint, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await dbContext.PushSubscriptions
            .Where(s => s.UserId == userId && s.Endpoint == endpoint)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task<NotificationPreferences> GetOrCreatePreferencesAsync(D3ParkingDbContext dbContext, Guid userId, CancellationToken cancellationToken)
    {
        // FindAsync consults the change tracker first, so a row created earlier in the same
        // operation (and not yet saved) is reused rather than added twice.
        var preferences = await dbContext.NotificationPreferences.FindAsync([userId], cancellationToken);
        if (preferences is null)
        {
            preferences = new NotificationPreferences(userId);
            dbContext.NotificationPreferences.Add(preferences);
            try
            {
                // Persist right away: the first two notifications for a user can run concurrently,
                // and deferring this insert to the caller's SaveChanges would fail the whole
                // operation on the primary-key race instead of just this row.
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Lost the race — drop the local add and use the winner's committed row.
                dbContext.Entry(preferences).State = EntityState.Detached;
                preferences = (await dbContext.NotificationPreferences.FindAsync([userId], cancellationToken))!;
            }
        }

        return preferences;
    }

    private NotificationPreferencesDto ToDto(NotificationPreferences preferences) =>
        new(preferences.Muted, preferences.MutedUntilUtc, preferences.IsCurrentlyMuted(timeProvider.GetUtcNow()), preferences.Scope, preferences.AllowAvailability);
}
