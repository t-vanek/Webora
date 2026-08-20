using D3Parking.Domain.Common;

namespace D3Parking.Domain.Notifications;

public enum NotificationDeliveryStatus { Pending, Sent, Failed }

/// <summary>Durable email outbox. A business action never waits on a healthy SMTP server.</summary>
public sealed class NotificationEmailDelivery : Entity
{
    public Guid UserId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Message { get; private set; } = string.Empty;
    public string? ActionText { get; private set; }
    public string? ActionUrl { get; private set; }
    public string? DeadlineText { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public NotificationDeliveryStatus Status { get; private set; } = NotificationDeliveryStatus.Pending;
    public int Attempts { get; private set; }
    public int ManualRetryCount { get; private set; }
    public DateTimeOffset NextAttemptUtc { get; private set; }
    public DateTimeOffset? LastAttemptUtc { get; private set; }
    public DateTimeOffset? SentAtUtc { get; private set; }
    public string? LastError { get; private set; }
    public byte[] Version { get; private set; } = [];

    private NotificationEmailDelivery() { }

    public NotificationEmailDelivery(Guid userId, string title, string message,
        string? actionText, string? actionUrl, string? deadlineText, DateTimeOffset createdAtUtc)
    {
        UserId = userId;
        Title = title;
        Message = message;
        ActionText = actionText;
        ActionUrl = actionUrl;
        DeadlineText = deadlineText;
        CreatedAtUtc = createdAtUtc;
        NextAttemptUtc = createdAtUtc;
    }

    public void MarkSent(DateTimeOffset at)
    {
        Attempts++;
        LastAttemptUtc = at;
        SentAtUtc = at;
        LastError = null;
        Status = NotificationDeliveryStatus.Sent;
    }

    public void MarkFailed(DateTimeOffset at, string error)
    {
        Attempts++;
        LastAttemptUtc = at;
        LastError = error.Length <= 1000 ? error : error[..1000];
        if (Attempts >= 5)
        {
            Status = NotificationDeliveryStatus.Failed;
            return;
        }

        var delay = Attempts switch { 1 => 1, 2 => 5, 3 => 30, _ => 120 };
        NextAttemptUtc = at.AddMinutes(delay);
    }

    /// <summary>Starts a fresh five-attempt cycle without sending inside the administrator request.</summary>
    public bool QueueManualRetry(DateTimeOffset at)
    {
        if (Status != NotificationDeliveryStatus.Failed)
        {
            return false;
        }

        Status = NotificationDeliveryStatus.Pending;
        Attempts = 0;
        ManualRetryCount++;
        NextAttemptUtc = at;
        SentAtUtc = null;
        return true;
    }
}
