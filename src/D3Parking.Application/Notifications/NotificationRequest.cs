using D3Parking.Domain.Notifications;

namespace D3Parking.Application.Notifications;

/// <summary>A notification ready for a batch persistence and realtime-delivery pass.</summary>
public sealed record NotificationRequest(
    Guid UserId,
    NotificationCategory Category,
    NotificationLevel Level,
    string Title,
    string Message,
    bool Email = false,
    NotificationEmailOptions? EmailOptions = null);
