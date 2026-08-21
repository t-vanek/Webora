namespace D3Parking.Application.Parking;

/// <summary>
/// Proactive low- and high-occupancy planning notices based on stable future stretches rather than
/// momentary per-spot availability.
/// </summary>
public interface IAvailabilityCampaignService
{
    /// <summary>
    /// Evaluates the occupancy outlook and sends at most one campaign of each kind per local day,
    /// at the configured hour. Returns the number of recipients notified (0 when nothing was due).
    /// </summary>
    Task<int> RunDueCampaignsAsync(CancellationToken cancellationToken = default);
}
