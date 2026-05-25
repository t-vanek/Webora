using Microsoft.AspNetCore.SignalR;
using Webora.Application.Notifications;
using Webora.Contracts.Notifications;
using Webora.Web.Hubs;

namespace Webora.Web.Notifications;

public sealed class SignalRNotificationPublisher(IHubContext<NotificationsHub> hub) : INotificationRealtimePublisher
{
    public Task PublishAsync(Guid userId, NotificationDto notification, CancellationToken cancellationToken = default) =>
        hub.Clients.User(userId.ToString()).SendAsync("ReceiveNotification", notification, cancellationToken);
}
