using Webora.Domain.Parking.Incentives;

namespace Webora.Application.Parking;

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

    Task<ParkingResult> UpdateAsync(ParkingSettingsDto settings, Guid actingUserId, CancellationToken cancellationToken = default);
}
