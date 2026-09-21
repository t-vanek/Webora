namespace D3Parking.Domain.Notifications;

public enum NotificationLevel
{
    Info,
    Security,
    Warning,
    /// <summary>Operational change the recipient must learn about, such as losing a confirmed spot.</summary>
    Critical,
}
