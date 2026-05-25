using Microsoft.AspNetCore.SignalR;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Webora.Web.Identity;

/// <summary>
/// SignalR maps connections to users by this id. Webora puts the user id in the "sub" claim
/// (configured on Identity), so resolve that, enabling IHubContext.Clients.User(userId).
/// </summary>
public sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User?.FindFirst(Claims.Subject)?.Value
        ?? connection.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
}
