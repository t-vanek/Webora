using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Webora.Application.Parking;

namespace Webora.Infrastructure.Parking;

/// <summary>
/// Driving distance via an OSRM routing service. Falls back to straight-line distance when the
/// service is unreachable or returns no route, so the reward never breaks on a routing outage.
/// </summary>
public sealed class OsrmDistanceProvider(
    HttpClient http,
    HaversineDistanceProvider fallback,
    ILogger<OsrmDistanceProvider> logger) : IDistanceProvider
{
    public async Task<double?> DistanceKmAsync(GeoPoint from, GeoPoint to, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = string.Create(CultureInfo.InvariantCulture,
                $"route/v1/driving/{from.Longitude},{from.Latitude};{to.Longitude},{to.Latitude}?overview=false");
            using var response = await http.GetAsync(path, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (doc.RootElement.TryGetProperty("routes", out var routes)
                    && routes.ValueKind == JsonValueKind.Array && routes.GetArrayLength() > 0
                    && routes[0].TryGetProperty("distance", out var meters)
                    && meters.TryGetDouble(out var m))
                {
                    return m / 1000.0;
                }
            }

            logger.LogWarning("OSRM returned no usable route; falling back to straight-line distance.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "OSRM routing failed; falling back to straight-line distance.");
        }

        return await fallback.DistanceKmAsync(from, to, cancellationToken);
    }
}
