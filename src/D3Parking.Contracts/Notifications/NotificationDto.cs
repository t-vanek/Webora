using D3Parking.Domain.Notifications;

namespace D3Parking.Contracts.Notifications;

public sealed record NotificationDto(
    Guid Id,
    NotificationCategory Category,
    NotificationLevel Level,
    string Title,
    string Message,
    DateTimeOffset CreatedAtUtc,
    bool IsRead);
