namespace D3Parking.Domain.Notifications;

/// <summary>Per-user notification settings: muting (do-not-disturb) and category scope.</summary>
public class NotificationPreferences
{
    public Guid UserId { get; private set; }

    /// <summary>Muted indefinitely until explicitly unmuted.</summary>
    public bool Muted { get; private set; }

    /// <summary>Muted until this instant (snooze). Ignored once in the past.</summary>
    public DateTimeOffset? MutedUntilUtc { get; private set; }

    public NotificationScope Scope { get; private set; } = NotificationScope.All;

    /// <summary>
    /// Opt-out for the proactive capacity tips (the one marketing-ish category). Deliberately its
    /// own axis, independent of <see cref="Scope"/> — turning tips off must not cost the user any
    /// transactional notification, and vice versa.
    /// </summary>
    public bool AllowAvailability { get; private set; } = true;

    private NotificationPreferences() { }

    public NotificationPreferences(Guid userId) => UserId = userId;

    public bool IsCurrentlyMuted(DateTimeOffset now) =>
        Muted || (MutedUntilUtc.HasValue && MutedUntilUtc.Value > now);

    /// <summary>Whether a notification of the given category should be delivered at all.</summary>
    public bool Allows(NotificationCategory category) => category switch
    {
        NotificationCategory.Availability => AllowAvailability,
        NotificationCategory.SelfService => true,
        _ => Scope == NotificationScope.All,
    };

    public void MuteIndefinitely()
    {
        Muted = true;
        MutedUntilUtc = null;
    }

    public void MuteUntil(DateTimeOffset until)
    {
        Muted = false;
        MutedUntilUtc = until;
    }

    public void Unmute()
    {
        Muted = false;
        MutedUntilUtc = null;
    }

    public void SetScope(NotificationScope scope) => Scope = scope;

    public void SetAvailabilityOptIn(bool allow) => AllowAvailability = allow;
}
