namespace D3Parking.Domain.Settings;

/// <summary>How the canonical host treats the "www." prefix.</summary>
public enum WwwPreference
{
    /// <summary>No canonicalization; both forms are served as-is.</summary>
    NoPreference,

    /// <summary>The canonical form includes "www." (non-www redirects to www).</summary>
    PreferWww,

    /// <summary>The canonical form omits "www." (www redirects to the apex).</summary>
    PreferNonWww,
}
