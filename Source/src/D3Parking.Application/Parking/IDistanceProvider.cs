namespace D3Parking.Application.Parking;

/// <summary>
/// Computes the distance between two points. The default implementation is straight-line
/// (haversine); a driving-distance provider can be swapped in via configuration.
/// </summary>
public interface IDistanceProvider
{
    Task<double?> DistanceKmAsync(GeoPoint from, GeoPoint to, CancellationToken cancellationToken = default);
}
