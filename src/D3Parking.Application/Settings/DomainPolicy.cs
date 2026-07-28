using D3Parking.Domain.Settings;

namespace D3Parking.Application.Settings;

/// <summary>A lightweight snapshot of the domain settings used by the enforcement middleware.</summary>
public sealed record DomainPolicy(
    string? CanonicalHost,
    int? Port,
    bool ForceHttps,
    bool HstsEnabled,
    int HstsMaxAgeDays,
    bool HstsIncludeSubDomains,
    bool HstsPreload,
    WwwPreference Www,
    IReadOnlyList<string> Aliases,
    bool LowercaseUrls,
    TrailingSlashPolicy TrailingSlash);
