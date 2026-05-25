using Webora.Domain.Notifications;

namespace Webora.Contracts.Notifications;

public sealed record NotificationDto(
    Guid Id,
    NotificationCategory Category,
    NotificationLevel Level,
    string Title,
    string Message,
    DateTimeOffset CreatedAtUtc,
    bool IsRead);
