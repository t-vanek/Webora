namespace D3Parking.Domain.Notifications;

public enum NotificationCategory
{
    /// <summary>Triggered by the user's own self-service actions.</summary>
    SelfService,

    /// <summary>Triggered by an administrator or the system (e.g. account blocked).</summary>
    Administrative,
}
