using D3Parking.Domain.Common;

namespace D3Parking.Domain.Notifications;

/// <summary>Company-wide delivery channels for one category and severity combination.</summary>
public sealed class NotificationDeliveryRule : Entity
{
    public NotificationCategory Category { get; private set; }
    public NotificationLevel Level { get; private set; }
    public bool InboxEnabled { get; private set; } = true;
    public bool LiveEnabled { get; private set; } = true;
    public NotificationEmailMode EmailMode { get; private set; } = NotificationEmailMode.WhenRequested;

    private NotificationDeliveryRule() { }

    public NotificationDeliveryRule(NotificationCategory category, NotificationLevel level,
        bool inboxEnabled, bool liveEnabled, NotificationEmailMode emailMode)
    {
        Category = category;
        Level = level;
        Update(inboxEnabled, liveEnabled, emailMode);
    }

    public void Update(bool inboxEnabled, bool liveEnabled, NotificationEmailMode emailMode)
    {
        InboxEnabled = Level is NotificationLevel.Security or NotificationLevel.Critical || inboxEnabled;
        LiveEnabled = InboxEnabled && liveEnabled;
        EmailMode = emailMode;
    }

    public bool ShouldEmail(bool explicitlyRequested) => EmailMode switch
    {
        NotificationEmailMode.Always => true,
        NotificationEmailMode.WhenRequested => explicitlyRequested,
        _ => false,
    };

    public bool HasAnyDelivery(bool explicitlyRequested) => InboxEnabled || ShouldEmail(explicitlyRequested);

    public static NotificationDeliveryRule CreateDefault(NotificationCategory category, NotificationLevel level) =>
        new(category, level, true, true,
            level is NotificationLevel.Security or NotificationLevel.Critical
                ? NotificationEmailMode.Always
                : category == NotificationCategory.Availability
                    ? NotificationEmailMode.Never
                    : NotificationEmailMode.WhenRequested);
}
