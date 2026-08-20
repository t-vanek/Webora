using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using D3Parking.Application.Mapping;
using D3Parking.Application.Notifications;
using D3Parking.Contracts.Notifications;
using D3Parking.Domain.Notifications;
using D3Parking.Infrastructure.Persistence;

namespace D3Parking.Infrastructure.Notifications;

public sealed class NotificationService(
    IDbContextFactory<D3ParkingDbContext> dbContextFactory,
    NotificationMapper mapper,
    INotificationRealtimePublisher publisher,
    ILogger<NotificationService> logger,
    TimeProvider timeProvider,
    INotificationRuleService ruleService) : INotificationService
{
    private const int MaxBatchDeliveryConcurrency = 8;

    public async Task<int> NotifyManyAsync(IReadOnlyCollection<NotificationRequest> requests, CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0)
        {
            return 0;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var userIds = requests.Select(r => r.UserId).Distinct().ToList();
        var preferencesByUser = (await dbContext.NotificationPreferences
                .Where(p => userIds.Contains(p.UserId))
                .ToListAsync(cancellationToken))
            .ToDictionary(p => p.UserId);

        // A batch must not turn the first campaign for many employees into a SaveChanges per
        // preference. The individual path keeps its race retry; here all rows are one atomic write.
        foreach (var userId in userIds.Where(id => !preferencesByUser.ContainsKey(id)))
        {
            var preferences = new NotificationPreferences(userId);
            preferencesByUser.Add(userId, preferences);
            dbContext.NotificationPreferences.Add(preferences);
        }

        var rules = (await ruleService.GetAsync(cancellationToken))
            .ToDictionary(r => (r.Category, r.Level));

        var now = timeProvider.GetUtcNow();
        var deliveries = new List<(Notification Notification, NotificationRequest Request,
            NotificationPreferences Preferences, NotificationDeliveryRuleDto Rule)>();
        foreach (var request in requests)
        {
            var preferences = preferencesByUser[request.UserId];
            var rule = rules[(request.Category, request.Level)];
            if ((!IsMandatory(request.Level) && !preferences.Allows(request.Category))
                || (!rule.InboxEnabled && !rule.LiveEnabled && !ShouldEmail(rule.EmailMode, request.Email)))
            {
                continue;
            }

            var notification = new Notification(request.UserId, request.Category, request.Level, request.Title, request.Message, now);
            if (rule.InboxEnabled)
            {
                dbContext.Notifications.Add(notification);
            }

            // Email is persisted before delivery. Muting still suppresses external channels, while
            // security and critical messages deliberately bypass a personal mute.
            if ((IsMandatory(request.Level) || !preferences.IsCurrentlyMuted(now))
                && ShouldEmail(rule.EmailMode, request.Email))
            {
                dbContext.NotificationEmailDeliveries.Add(CreateEmailDelivery(
                    request.UserId, request.Title, request.Message, request.EmailOptions, now));
            }
            deliveries.Add((notification, request, preferences, rule));
        }

        if (deliveries.Count == 0)
        {
            return 0;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await Parallel.ForEachAsync(deliveries,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = MaxBatchDeliveryConcurrency },
            async (delivery, ct) =>
            {
                if (!IsMandatory(delivery.Request.Level)
                    && delivery.Preferences.IsCurrentlyMuted(now))
                {
                    return;
                }

                if (delivery.Rule.LiveEnabled)
                {
                    try
                    {
                        await publisher.PublishAsync(delivery.Notification.UserId, mapper.ToDto(delivery.Notification), ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Realtime notification delivery failed for {UserId}.", delivery.Notification.UserId);
                    }
                }
            });

        return deliveries.Count;
    }

    public Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, CancellationToken cancellationToken = default) =>
        NotifyAsync(userId, category, level, title, message, email: false, emailOptions: null, cancellationToken);

    public Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, bool email, CancellationToken cancellationToken = default) =>
        NotifyAsync(userId, category, level, title, message, email, emailOptions: null, cancellationToken);

    public async Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, bool email, NotificationEmailOptions? emailOptions, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var preferences = await GetOrCreatePreferencesAsync(dbContext, userId, cancellationToken);
        var rule = await ruleService.GetAsync(category, level, cancellationToken);

        // Security history cannot be removed by a personal scope. Other messages respect both the
        // company delivery matrix and the user's category preference.
        if ((!IsMandatory(level) && !preferences.Allows(category))
            || (!rule.InboxEnabled && !rule.LiveEnabled && !ShouldEmail(rule.EmailMode, email)))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var notification = new Notification(userId, category, level, title, message, now);
        if (rule.InboxEnabled)
        {
            dbContext.Notifications.Add(notification);
        }

        var externalDeliveryAllowed = IsMandatory(level) || !preferences.IsCurrentlyMuted(now);
        if (externalDeliveryAllowed && ShouldEmail(rule.EmailMode, email))
        {
            dbContext.NotificationEmailDeliveries.Add(CreateEmailDelivery(
                userId, title, message, emailOptions, now));
        }

        // The inbox row and its email delivery request enter the database together. SMTP is handled
        // by the background dispatcher, so a slow or unavailable mail server cannot hold the action.
        await dbContext.SaveChangesAsync(cancellationToken);

        // Muting suppresses external channels; the inbox notification is still stored.
        if (externalDeliveryAllowed)
        {
            if (rule.LiveEnabled)
            {
                try
                {
                    await publisher.PublishAsync(userId, mapper.ToDto(notification), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Realtime notification delivery failed for {UserId}.", userId);
                }
            }
        }
    }

    private static bool ShouldEmail(NotificationEmailMode mode, bool explicitlyRequested) => mode switch
    {
        NotificationEmailMode.Always => true,
        NotificationEmailMode.WhenRequested => explicitlyRequested,
        _ => false,
    };

    private static bool IsMandatory(NotificationLevel level) =>
        level is NotificationLevel.Security or NotificationLevel.Critical;

    private static NotificationEmailDelivery CreateEmailDelivery(Guid userId, string title, string message,
        NotificationEmailOptions? options, DateTimeOffset createdAtUtc) =>
        new(userId, title, message, options?.ActionText, options?.ActionUrl, options?.DeadlineText, createdAtUtc);

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
