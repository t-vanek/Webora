using D3Parking.Contracts.Notifications;
using D3Parking.Domain.Notifications;

namespace D3Parking.Application.Notifications;

public interface INotificationService
{
    /// <summary>
    /// Stores a group of independent notifications in one database write, then delivers their live
    /// copies with bounded concurrency. Returns the number stored after preference filtering.
    /// </summary>
    Task<int> NotifyManyAsync(IReadOnlyCollection<NotificationRequest> requests, CancellationToken cancellationToken = default);

    Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// As above, but when <paramref name="email"/> is set the notification is also mirrored to the
    /// user's email (best-effort; subject to the same do-not-disturb and category-scope gating).
    /// </summary>
    Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, bool email, CancellationToken cancellationToken = default);

    /// <summary>
    /// As above, with control over the email mirror's shape: an optional call-to-action button
    /// and deadline line (e.g. a waitlist offer that expires).
    /// </summary>
    Task NotifyAsync(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, bool email, NotificationEmailOptions? emailOptions, CancellationToken cancellationToken = default);

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

    /// <summary>Opt-in/out for the proactive availability tips (independent of the category scope).</summary>
    Task SetAvailabilityOptInAsync(Guid userId, bool allow, CancellationToken cancellationToken = default);

    // Web Push subscriptions of the user's browser installations.
    Task SubscribeToPushAsync(Guid userId, PushSubscriptionDto subscription, CancellationToken cancellationToken = default);

    Task UnsubscribeFromPushAsync(Guid userId, string endpoint, CancellationToken cancellationToken = default);
}
