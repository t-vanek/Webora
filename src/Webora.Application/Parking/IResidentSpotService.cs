namespace Webora.Application.Parking;

/// <summary>
/// Resident-facing operations on a reserved ("owned") spot: confirming arrival to keep it for the
/// day, proactively releasing it into the shared pool (rewarded by how early), and setting the
/// monthly share allowance that scales the reward.
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

    /// <summary>Set how many times a month the resident is willing to share their spot.</summary>
    Task<ParkingResult> SetShareAllowanceAsync(Guid userId, int allowance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reminds residents who have not confirmed arrival or released as the hold cutoff approaches.
    /// Intended for the maintenance loop. Returns the number of reminders sent.
    /// </summary>
    Task<int> SendDueHoldRemindersAsync(CancellationToken cancellationToken = default);
}
