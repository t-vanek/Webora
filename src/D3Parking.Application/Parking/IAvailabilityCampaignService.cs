namespace D3Parking.Application.Parking;

/// <summary>
/// Proactive "the lot is wide open" tips. Never announces momentary per-spot availability — only
/// a stable stretch of underbooked days well ahead, so the message cannot go stale in seconds and
/// there is no single spot for recipients to race for (booking itself stays serialized anyway).
/// </summary>
public interface IAvailabilityCampaignService
{
    /// <summary>
    /// Evaluates the availability outlook and sends at most one campaign per local day, at the
    /// configured hour. Returns the number of recipients notified (0 when nothing was due).
    /// </summary>
    Task<int> RunDueCampaignsAsync(CancellationToken cancellationToken = default);
}
