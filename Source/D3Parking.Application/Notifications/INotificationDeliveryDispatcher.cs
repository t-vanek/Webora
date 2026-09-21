namespace D3Parking.Application.Notifications;

public interface INotificationDeliveryDispatcher
{
    Task DeliverAsync(Guid deliveryId, CancellationToken cancellationToken = default);
    Task<int> DeliverDueAsync(int take = 100, CancellationToken cancellationToken = default);
    Task<int> PurgeCompletedAsync(DateTimeOffset createdBeforeUtc, CancellationToken cancellationToken = default);
}
