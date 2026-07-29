namespace D3Parking.Domain.Notifications;

public enum NotificationCategory
{
    /// <summary>Triggered by the user's own self-service actions.</summary>
    SelfService,

    /// <summary>Triggered by an administrator or the system (e.g. account blocked).</summary>
    Administrative,

    /// <summary>
    /// Proactive capacity tips ("the lot is wide open next week"). The only non-transactional
    /// category — governed by its own per-user opt-out, never mirrored to email.
    /// </summary>
    Availability,
}
