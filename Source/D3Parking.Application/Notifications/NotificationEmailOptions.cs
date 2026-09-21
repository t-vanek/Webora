namespace D3Parking.Application.Notifications;

/// <summary>
/// How the email mirror of a notification is dressed up beyond the plain title + message: an
/// optional call-to-action button and an optional deadline line. Null options render the plain
/// "formal record" shape.
/// </summary>
public sealed record NotificationEmailOptions(
    string? ActionText = null,
    string? ActionUrl = null,
    string? DeadlineText = null);
