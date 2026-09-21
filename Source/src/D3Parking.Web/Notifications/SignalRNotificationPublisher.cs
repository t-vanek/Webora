using Microsoft.AspNetCore.SignalR;
using D3Parking.Application.Notifications;
using D3Parking.Contracts.Notifications;
using D3Parking.Web.Hubs;

namespace D3Parking.Web.Notifications;

public sealed class SignalRNotificationPublisher(IHubContext<NotificationsHub> hub) : INotificationRealtimePublisher
{
    public Task PublishAsync(Guid userId, NotificationDto notification, CancellationToken cancellationToken = default) =>
        hub.Clients.User(userId.ToString()).SendAsync("ReceiveNotification", notification, cancellationToken);
}
