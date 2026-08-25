using D3Parking.Domain.Parking.Incentives;

namespace D3Parking.Application.Parking;

/// <summary>Reads and updates the persisted parking/incentive settings (a single instance).</summary>
public interface IParkingSettingsService
{
    /// <summary>The current incentive policy, cached for the hot reservation paths.</summary>
    Task<IncentivePolicy> GetPolicyAsync(CancellationToken cancellationToken = default);

    /// <summary>How often the background maintenance should run.</summary>
    Task<TimeSpan> GetSweepIntervalAsync(CancellationToken cancellationToken = default);

    /// <summary>The parking lot's coordinates, or null when not configured.</summary>
    Task<GeoPoint?> GetLotLocationAsync(CancellationToken cancellationToken = default);

    /// <summary>The full settings read model for the admin editor.</summary>
    Task<ParkingSettingsDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Live capacity facts derived from spots, resident memberships and registered vehicles.</summary>
    Task<PlannerCapacityDto> GetPlannerCapacityAsync(CancellationToken cancellationToken = default);

    Task<ParkingResult> UpdateAsync(ParkingSettingsDto settings, Guid actingUserId, CancellationToken cancellationToken = default);

    /// <summary>Future records that the proposed calendar rules would make invalid.</summary>
    Task<ParkingCalendarChangeImpactDto> GetCalendarChangeImpactAsync(
        ParkingSettingsDto settings,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ParkingCalendarChangeImpactDto.None);

    /// <summary>Updates settings, explicitly confirming cancellation of affected future records.</summary>
    Task<ParkingResult> UpdateAsync(
        ParkingSettingsDto settings,
        Guid actingUserId,
        bool confirmCalendarInvalidation,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(settings, actingUserId, cancellationToken);

    Task<ParkingMapImageDto?> GetOrientationMapAsync(CancellationToken cancellationToken = default);

    Task<ParkingResult> SetOrientationMapAsync(byte[] content, Guid actingUserId, CancellationToken cancellationToken = default);

    Task<ParkingResult> ClearOrientationMapAsync(Guid actingUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lets the adaptive controller nudge the peak surcharge toward target occupancy from the measured
    /// peak occupancy. No-op unless enabled and the interval has elapsed. Returns whether it changed.
    /// </summary>
    Task<bool> AdaptPeakSurchargeAsync(double measuredOccupancy, CancellationToken cancellationToken = default);
}

public sealed record ParkingMapImageDto(byte[] Content, string ContentType);

public sealed record PlannerCapacityDto(
    int ActiveSpots,
    int ResidentSpots,
    int SharedSpots,
    int ActiveResidents,
    int RegisteredVehicles);

public sealed record ParkingCalendarChangeImpactDto(
    int Reservations,
    int QueueEntries,
    int Handoffs,
    int VisitorBookings,
    int SpotReleases)
{
    public static ParkingCalendarChangeImpactDto None { get; } = new(0, 0, 0, 0, 0);

    public int Total => Reservations + QueueEntries + Handoffs + VisitorBookings + SpotReleases;

    public bool RequiresConfirmation => Total > 0;
}
