using Webora.Application.Accounts;

namespace Webora.Application.Settings;

/// <summary>Reads and updates the site-wide settings (a single persisted instance).</summary>
public interface ISiteSettingsService
{
    Task<SiteSettingsDto> GetAsync(CancellationToken cancellationToken = default);

    Task<AccountResult> UpdateDomainAsync(DomainSettingsDto domain, CancellationToken cancellationToken = default);

    Task<AccountResult> UpdateRegionalAsync(RegionalSettingsDto regional, CancellationToken cancellationToken = default);

    /// <summary>The configured canonical base URL (scheme://host[:port]), or null when no host is set.</summary>
    Task<string?> GetCanonicalBaseUrlAsync(CancellationToken cancellationToken = default);

    /// <summary>A read-only snapshot of the domain settings for the enforcement middleware.</summary>
    Task<DomainPolicy> GetDomainPolicyAsync(CancellationToken cancellationToken = default);

    /// <summary>The configured fallback UI culture, or null when none is set.</summary>
    Task<string?> GetDefaultLanguageAsync(CancellationToken cancellationToken = default);

    /// <summary>The configured display time zone, falling back to the server local zone when unset.</summary>
    Task<TimeZoneInfo> GetTimeZoneAsync(CancellationToken cancellationToken = default);
}
