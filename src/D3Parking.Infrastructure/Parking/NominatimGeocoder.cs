using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using D3Parking.Application.Parking;

namespace D3Parking.Infrastructure.Parking;

/// <summary>Geocodes addresses via a Nominatim (OpenStreetMap) service. Network is best-effort.</summary>
public sealed class NominatimGeocoder(HttpClient http, ILogger<NominatimGeocoder> logger) : IGeocoder
{
    public async Task<GeoPoint?> GeocodeAsync(string address, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        try
        {
            using var response = await http.GetAsync($"search?q={Uri.EscapeDataString(address)}&format=json&limit=1", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var first = doc.RootElement[0];
            if (first.TryGetProperty("lat", out var latEl) && first.TryGetProperty("lon", out var lonEl)
                && double.TryParse(latEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                && double.TryParse(lonEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            {
                return new GeoPoint(lat, lon);
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Geocoding failed for address.");
            return null;
        }
    }
}
