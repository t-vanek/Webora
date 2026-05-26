using Webora.Application.Parking;

namespace Webora.Infrastructure.Parking;

/// <summary>Straight-line (great-circle) distance. A driving-distance provider can replace this.</summary>
public sealed class HaversineDistanceProvider : IDistanceProvider
{
    private const double EarthRadiusKm = 6371.0;

    public Task<double?> DistanceKmAsync(GeoPoint from, GeoPoint to, CancellationToken cancellationToken = default)
    {
        var dLat = Deg2Rad(to.Latitude - from.Latitude);
        var dLon = Deg2Rad(to.Longitude - from.Longitude);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(Deg2Rad(from.Latitude)) * Math.Cos(Deg2Rad(to.Latitude))
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return Task.FromResult<double?>(EarthRadiusKm * c);
    }

    private static double Deg2Rad(double degrees) => degrees * Math.PI / 180.0;
}
