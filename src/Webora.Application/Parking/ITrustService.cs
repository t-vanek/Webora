namespace Webora.Application.Parking;

/// <summary>
/// Computes a trust-graph standing from positive sharing interactions (a guest responsibly using a
/// resident's shared spot) with a weighted PageRank, and awards the "Trusted" badge above a threshold.
/// </summary>
public interface ITrustService
{
    /// <summary>
    /// Recomputes trust scores if enabled and the interval has elapsed. Intended for the maintenance
    /// loop. Returns the number of members scored (0 when disabled, not due, or no interactions yet).
    /// </summary>
    Task<int> ComputeTrustAsync(CancellationToken cancellationToken = default);
}
