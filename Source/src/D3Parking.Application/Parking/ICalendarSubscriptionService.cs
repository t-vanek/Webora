namespace D3Parking.Application.Parking;

public sealed record CalendarSubscriptionStatus(
    bool Enabled,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);

public sealed record CalendarSubscriptionSecret(
    string Token,
    CalendarSubscriptionStatus Status);

/// <summary>Manages the private, revocable iCalendar feed for a user's reservations.</summary>
public interface ICalendarSubscriptionService
{
    Task<CalendarSubscriptionStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or rotates the feed credential. The clear-text token is returned only once and is
    /// never stored by the service.
    /// </summary>
    Task<CalendarSubscriptionSecret> CreateOrRotateAsync(Guid userId, CancellationToken cancellationToken = default);

    Task DisableAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<Guid?> ResolveUserAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>Recent and future reservations, including cancellations clients must learn about.</summary>
    Task<IReadOnlyList<ReservationDto>> GetFeedReservationsAsync(Guid userId, CancellationToken cancellationToken = default);
}
