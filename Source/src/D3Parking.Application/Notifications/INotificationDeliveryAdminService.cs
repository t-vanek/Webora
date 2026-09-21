using D3Parking.Domain.Notifications;

namespace D3Parking.Application.Notifications;

public interface INotificationDeliveryAdminService
{
    Task<NotificationDeliverySummary> GetSummaryAsync(CancellationToken cancellationToken = default);

    Task<PagedResult<NotificationDeliveryListItem>> SearchAsync(
        string? search,
        NotificationDeliveryStatus? status,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<NotificationDeliveryRetryResult> RetryAsync(
        Guid deliveryId,
        Guid actingUserId,
        CancellationToken cancellationToken = default);
}

public sealed record NotificationDeliverySummary(
    int Pending,
    int DueNow,
    int Failed,
    int SentLast24Hours);

public sealed record NotificationDeliveryListItem(
    Guid Id,
    Guid UserId,
    string? RecipientEmail,
    string? RecipientName,
    string Title,
    string Message,
    string? ActionText,
    string? ActionUrl,
    string? DeadlineText,
    NotificationDeliveryStatus Status,
    int Attempts,
    int ManualRetryCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset NextAttemptUtc,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? SentAtUtc,
    string? LastError);

public enum NotificationDeliveryRetryResult
{
    Queued,
    NotFound,
    NotFailed,
}
