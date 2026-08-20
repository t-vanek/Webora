using D3Parking.Domain.Notifications;

namespace D3Parking.Application.Notifications;

public interface INotificationRuleService
{
    Task<IReadOnlyList<NotificationDeliveryRuleDto>> GetAsync(CancellationToken cancellationToken = default);
    Task<NotificationDeliveryRuleDto> GetAsync(NotificationCategory category, NotificationLevel level,
        CancellationToken cancellationToken = default);
    Task UpdateAsync(IReadOnlyCollection<NotificationDeliveryRuleDto> rules, Guid actingUserId,
        CancellationToken cancellationToken = default);
}
