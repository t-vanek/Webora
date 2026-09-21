using D3Parking.Application.Notifications;
using D3Parking.Domain.Notifications;
using D3Parking.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace D3Parking.Infrastructure.Parking;

/// <summary>The inbox and durable email outbox participate in the caller's parking transaction.</summary>
internal static class ParkingNotifications
{
    public static Task EnqueueAsync(D3ParkingDbContext db, DateTimeOffset now, Guid userId,
        NotificationCategory category, NotificationLevel level, string title, string message,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(db, now, userId, category, level, title, message, false, null, cancellationToken);

    public static Task EnqueueAsync(D3ParkingDbContext db, DateTimeOffset now, Guid userId,
        NotificationCategory category, NotificationLevel level, string title, string message,
        bool email, CancellationToken cancellationToken = default) =>
        EnqueueAsync(db, now, userId, category, level, title, message, email, null, cancellationToken);

    public static async Task<Guid?> EnqueueAsync(D3ParkingDbContext db, DateTimeOffset now, Guid userId,
        NotificationCategory category, NotificationLevel level, string title, string message,
        bool email, NotificationEmailOptions? options, CancellationToken cancellationToken = default)
    {
        // Parking promises always leave an inbox history. Personal preferences still control
        // optional external delivery; critical/security delivery follows the existing policy.
        db.Notifications.Add(new Notification(userId, category, level, title, message, now));
        var rule = await db.NotificationDeliveryRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Category == category && r.Level == level, cancellationToken)
            ?? NotificationDeliveryRule.CreateDefault(category, level);
        var preferences = await db.NotificationPreferences.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        var mandatory = level is NotificationLevel.Critical or NotificationLevel.Security;
        if (rule.ShouldEmail(email) && (mandatory || preferences is null
            || preferences.Allows(category) && !preferences.IsCurrentlyMuted(now)))
        {
            var delivery = new NotificationEmailDelivery(userId, title, message,
                options?.ActionText, options?.ActionUrl, options?.DeadlineText, now);
            db.NotificationEmailDeliveries.Add(delivery);
            return delivery.Id;
        }
        return null;
    }

    public static Task PublishAsync(D3ParkingDbContext db, INotificationService notifications, CancellationToken ct) =>
        notifications.PublishPersistedAsync(db.ChangeTracker.Entries<Notification>().Select(e => e.Entity.Id).ToList(), ct);
}
