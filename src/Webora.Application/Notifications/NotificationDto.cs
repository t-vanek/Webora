using Webora.Domain.Notifications;

namespace Webora.Application.Notifications;

public sealed record NotificationDto(
    Guid Id,
    NotificationLevel Level,
    string Title,
    string Message,
    DateTimeOffset CreatedAtUtc,
    bool IsRead);
