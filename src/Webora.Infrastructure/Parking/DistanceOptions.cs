namespace Webora.Infrastructure.Parking;

/// <summary>Selects and configures the commute distance provider, bound from the "Distance" section.</summary>
public sealed class DistanceOptions
{
    public const string SectionName = "Distance";

    /// <summary>"Haversine" (straight-line, offline) or "Osrm" (driving distance via a routing service).</summary>
    public string Provider { get; set; } = "Haversine";

    /// <summary>Base URL of an OSRM-compatible routing service (used when Provider is "Osrm").</summary>
    public string OsrmBaseUrl { get; set; } = "https://router.project-osrm.org";

    public bool UseOsrm => string.Equals(Provider, "Osrm", StringComparison.OrdinalIgnoreCase);
}
