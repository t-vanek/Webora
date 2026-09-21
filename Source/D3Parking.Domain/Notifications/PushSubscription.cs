using D3Parking.Domain.Common;

namespace D3Parking.Domain.Notifications;

/// <summary>
/// Web Push subscription of a single browser installation. The endpoint identifies the browser's
/// push channel globally, so it is unique — when a different account subscribes on the same
/// installation, the record is reassigned rather than duplicated.
/// </summary>
public class PushSubscription : Entity
{
    public Guid UserId { get; private set; }

    public string Endpoint { get; private set; } = string.Empty;

    public string P256dh { get; private set; } = string.Empty;

    public string Auth { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    private PushSubscription() { }

    public PushSubscription(Guid userId, string endpoint, string p256dh, string auth, DateTimeOffset createdAtUtc)
    {
        UserId = userId;
        Endpoint = endpoint;
        P256dh = p256dh;
        Auth = auth;
        CreatedAtUtc = createdAtUtc;
    }

    public void Reassign(Guid userId, string p256dh, string auth, DateTimeOffset at)
    {
        UserId = userId;
        P256dh = p256dh;
        Auth = auth;
        CreatedAtUtc = at;
    }
}
