using Webora.Domain.Common;

namespace Webora.Domain.Notifications;

public class Notification : Entity
{
    public Guid UserId { get; private set; }

    public NotificationCategory Category { get; private set; }

    public NotificationLevel Level { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Message { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? ReadAtUtc { get; private set; }

    public bool IsRead => ReadAtUtc is not null;

    private Notification() { }

    public Notification(Guid userId, NotificationCategory category, NotificationLevel level, string title, string message, DateTimeOffset createdAtUtc)
    {
        UserId = userId;
        Category = category;
        Level = level;
        Title = title;
        Message = message;
        CreatedAtUtc = createdAtUtc;
    }

    public void MarkRead(DateTimeOffset at) => ReadAtUtc ??= at;
}
