using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

/// <summary>
/// Resident-facing operations on a reserved ("owned") spot: planning which days it is needed,
/// proactively releasing it into the shared pool, and the standing usage plan that releases the days the
/// resident does not need without them having to ask.
/// </summary>
public interface IResidentSpotService
{
    /// <summary>The caller's reserved spot with today's state, or null when they have none.</summary>
    Task<OwnedSpotDto?> GetMyOwnedSpotAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Proactively release the owned spot for every day in the range [fromDate, toDate]. Days
    /// already released or claimed are skipped.
    /// </summary>
    Task<ParkingResult> ReleaseAsync(Guid userId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default);

    Task<ResidentReleasePreviewDto> PreviewReleaseAsync(Guid userId, DateOnly fromDate, DateOnly toDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reclaims released days under the configured resident priority, deadline and replacement
    /// rules. Started bookings always remain protected. A pending waitlist offer is withdrawn
    /// without changing the waiter's position. The preview uses this same decision.
    /// </summary>
    Task<ParkingResult> ReclaimAsync(Guid userId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the standing usage plan: the weekdays the resident needs their spot, and whether the
    /// remaining days are released into the pool ahead of time. Explicit kept/released days and
    /// confirmed bookings survive changes; only unclaimed automatic releases are reconciled.
    /// </summary>
    Task<ParkingResult> SetUsagePlanAsync(Guid userId, Weekday plannedUseDays, bool autoReleaseUnplannedDays,
        CancellationToken cancellationToken = default);

    Task<ResidentUsagePlanPreviewDto> PreviewUsagePlanAsync(Guid userId, Weekday plannedUseDays,
        bool autoReleaseUnplannedDays, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the upcoming days that residents' usage plans mark as not needed, as far ahead as the
    /// configured horizon. Each day is decided once, so a day the resident took back stays theirs. For the
    /// maintenance loop. Returns the number of days released.
    /// </summary>
    Task<int> ApplyDuePlanReleasesAsync(CancellationToken cancellationToken = default);

}
