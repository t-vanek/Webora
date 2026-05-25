using Webora.Domain.Settings;

namespace Webora.Application.Settings;

/// <summary>The domain section of the site settings.</summary>
public sealed record DomainSettingsDto(
    string? CanonicalHost,
    UrlScheme Scheme,
    int? Port,
    bool ForceHttps,
    bool HstsEnabled,
    int HstsMaxAgeDays,
    bool HstsIncludeSubDomains,
    WwwPreference WwwPreference,
    IReadOnlyList<string> Aliases);

/// <summary>The regional section of the site settings.</summary>
public sealed record RegionalSettingsDto(
    string? DefaultLanguage,
    string? DefaultTimeZoneId);

/// <summary>The encoding section of the site settings (page and email charsets).</summary>
public sealed record EncodingSettingsDto(
    string? PageCharset,
    string? EmailCharset);

/// <summary>The full site settings read model. New sections are added alongside the existing ones.</summary>
public sealed record SiteSettingsDto(
    DomainSettingsDto Domain,
    RegionalSettingsDto Regional,
    EncodingSettingsDto Encoding,
    string? CanonicalBaseUrl);
