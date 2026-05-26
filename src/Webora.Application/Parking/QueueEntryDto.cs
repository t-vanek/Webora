using Webora.Domain.Parking;

namespace Webora.Application.Parking;

/// <summary>A user's waitlist entry, with its queue position and any active spot offer.</summary>
public sealed record QueueEntryDto(
    Guid Id,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    QueueEntryStatus Status,
    int Position,
    Guid? OfferedSpotId,
    string? OfferedSpotCode,
    DateTimeOffset? OfferExpiresAtUtc);
