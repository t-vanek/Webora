using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace D3Parking.Web.Hubs;

[Authorize]
public class NotificationsHub : Hub
{
    public const string Path = "/hubs/notifications";

    // Clients receive pushes via IHubContext (Clients.User); no inbound methods are needed.
}
