using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

/// <summary>
/// Resident-facing operations on a reserved ("owned") spot: planning which days it is needed,
/// proactively releasing it into the shared pool (rewarded by how early), and the standing usage plan that releases the days the
/// resident does not need without them having to ask.
/// </summary>
public interface IResidentSpotService
{
    /// <summary>The caller's reserved spot with today's state, or null when they have none.</summary>
    Task<OwnedSpotDto?> GetMyOwnedSpotAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Proactively release the owned spot for every day in the range [fromDate, toDate]. Each day is
    /// rewarded on its own from the advance notice; days already released or claimed
    /// are skipped.
    /// </summary>
    Task<ParkingResult> ReleaseAsync(Guid userId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// The points <see cref="ReleaseAsync"/> would award right now for the same range — the same
    /// skipped days and the same advance-notice calculation — without changing anything. A range
    /// the release would reject outright (inverted, past, too long) previews as 0.
    /// </summary>
    Task<int> PreviewReleaseRewardAsync(Guid userId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes still-unbooked released days in [fromDate, toDate] back out of the shared pool. A
    /// confirmed guest plan is never displaced by self-service; exceptional changes belong to the
    /// manager workflow. A pending waitlist offer is withdrawn without changing the waiter's
    /// position. Any reward previously attached to a reclaimed day is returned.
    /// </summary>
    Task<ParkingResult> ReclaimAsync(Guid userId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the standing usage plan: the weekdays the resident needs their spot, and whether the
    /// remaining days are released into the pool ahead of time. Saving the plan re-applies it over
    /// the whole horizon, so a day taken back by hand before the change may be released again.
    /// </summary>
    Task<ParkingResult> SetUsagePlanAsync(Guid userId, Weekday plannedUseDays, bool autoReleaseUnplannedDays,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the upcoming days that residents' usage plans mark as not needed, as far ahead as the
    /// configured horizon and rewarded exactly like a manual release — the advance notice is what the
    /// plan is for. Each day is decided once, so a day the resident took back stays theirs. For the
    /// maintenance loop. Returns the number of days released.
    /// </summary>
    Task<int> ApplyDuePlanReleasesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles past shared days: a released day that nobody ever booked produced no utilization,
    /// so its reward is reversed (the share was contingent on actual demand). For the maintenance
    /// loop. Returns the number of releases clawed back.
    /// </summary>
    Task<int> ReconcileUnusedSharesAsync(CancellationToken cancellationToken = default);
}
