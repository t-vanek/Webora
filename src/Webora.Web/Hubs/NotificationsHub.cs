using Microsoft.AspNetCore.SignalR;

namespace Webora.Web.Hubs;

public class NotificationsHub : Hub
{
    public const string Path = "/hubs/notifications";

    public Task Broadcast(string message) =>
        Clients.All.SendAsync("ReceiveNotification", message);
}
