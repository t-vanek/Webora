using D3Parking.Domain.Parking;

namespace D3Parking.Application.Parking;

/// <summary>
/// Resident-facing operations on a reserved ("owned") spot: confirming arrival to keep it for the
/// day, proactively releasing it into the shared pool (rewarded by how early), setting the monthly
/// share allowance that scales the reward, and the standing usage plan that releases the days the
/// resident does not need without them having to ask.
/// </summary>
public interface IResidentSpotService
{
    /// <summary>The caller's reserved spot with today's state, or null when they have none.</summary>
    Task<OwnedSpotDto?> GetMyOwnedSpotAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Confirm arrival on the owned spot for today so it is not auto-shared.</summary>
    Task<ParkingResult> ConfirmArrivalAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Proactively release the owned spot for every day in the range [fromDate, toDate]. Each day is
    /// rewarded on its own (advance notice × allowance multiplier); days already released or claimed
    /// are skipped.
    /// </summary>
    Task<ParkingResult> ReleaseAsync(Guid userId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// The points <see cref="ReleaseAsync"/> would award right now for the same range — the same
    /// skipped days and the same monthly share-allowance cap — without changing anything. A range
    /// the release would reject outright (inverted, past, too long) previews as 0.
    /// </summary>
    Task<int> PreviewReleaseRewardAsync(Guid userId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes released days in [fromDate, toDate] back out of the shared pool — the resident's
    /// right of first refusal. Days a guest already booked stay shared (a firm booking is never
    /// evicted); a day merely held for a waitlist offer is reclaimed and the offer withdrawn.
    /// The reward each taken-back day earned is returned.
    /// </summary>
    Task<ParkingResult> ReclaimAsync(Guid userId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default);

    /// <summary>Set how many times a month the resident is willing to share their spot.</summary>
    Task<ParkingResult> SetShareAllowanceAsync(Guid userId, int allowance, CancellationToken cancellationToken = default);

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
    /// Reminds residents who have not confirmed arrival or released as the hold cutoff approaches.
    /// Intended for the maintenance loop. Returns the number of reminders sent.
    /// </summary>
    Task<int> SendDueHoldRemindersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells residents whose spot auto-shared today — past the hold cutoff without a confirmed arrival
    /// or a release — once per day. For the maintenance loop. Returns the number of notices sent.
    /// </summary>
    Task<int> NotifyDueAutoSharesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles past shared days: a released day that nobody ever booked produced no utilization,
    /// so its reward is reversed (the share was contingent on actual demand). For the maintenance
    /// loop. Returns the number of releases clawed back.
    /// </summary>
    Task<int> ReconcileUnusedSharesAsync(CancellationToken cancellationToken = default);
}
