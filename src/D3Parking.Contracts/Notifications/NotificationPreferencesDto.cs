using D3Parking.Domain.Notifications;

namespace D3Parking.Contracts.Notifications;

public sealed record NotificationPreferencesDto(
    bool Muted,
    DateTimeOffset? MutedUntilUtc,
    bool IsCurrentlyMuted,
    NotificationScope Scope);
