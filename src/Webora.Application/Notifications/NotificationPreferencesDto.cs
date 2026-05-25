using Webora.Domain.Notifications;

namespace Webora.Application.Notifications;

public sealed record NotificationPreferencesDto(
    bool Muted,
    DateTimeOffset? MutedUntilUtc,
    bool IsCurrentlyMuted,
    NotificationScope Scope);
