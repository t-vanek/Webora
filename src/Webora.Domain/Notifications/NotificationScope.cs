namespace Webora.Domain.Notifications;

public enum NotificationScope
{
    /// <summary>Deliver notifications of every category.</summary>
    All,

    /// <summary>Deliver only notifications about the user's own self-service actions.</summary>
    SelfServiceOnly,
}
