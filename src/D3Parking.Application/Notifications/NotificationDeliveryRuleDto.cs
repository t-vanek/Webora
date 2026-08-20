using D3Parking.Domain.Notifications;

namespace D3Parking.Application.Notifications;

public sealed record NotificationDeliveryRuleDto(
    NotificationCategory Category,
    NotificationLevel Level,
    bool InboxEnabled,
    bool LiveEnabled,
    NotificationEmailMode EmailMode,
    bool InboxMandatory);
