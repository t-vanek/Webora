namespace Webora.Infrastructure.Parking;

/// <summary>Configuration for the geocoding provider, bound from the "Geocoding" section.</summary>
public sealed class GeocodingOptions
{
    public const string SectionName = "Geocoding";

    /// <summary>Base URL of a Nominatim-compatible geocoding service.</summary>
    public string NominatimBaseUrl { get; set; } = "https://nominatim.openstreetmap.org";

    /// <summary>User-Agent sent with requests (Nominatim requires an identifying value).</summary>
    public string UserAgent { get; set; } = "Webora/1.0";
}
