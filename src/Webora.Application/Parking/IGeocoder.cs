namespace Webora.Application.Parking;

/// <summary>Turns a free-text address into coordinates.</summary>
public interface IGeocoder
{
    Task<GeoPoint?> GeocodeAsync(string address, CancellationToken cancellationToken = default);
}
