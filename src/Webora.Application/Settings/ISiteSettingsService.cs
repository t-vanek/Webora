using Webora.Application.Accounts;

namespace Webora.Application.Settings;

/// <summary>Reads and updates the site-wide settings (a single persisted instance).</summary>
public interface ISiteSettingsService
{
    Task<SiteSettingsDto> GetAsync(CancellationToken cancellationToken = default);

    Task<AccountResult> UpdateDomainAsync(DomainSettingsDto domain, CancellationToken cancellationToken = default);

    /// <summary>The configured canonical base URL (scheme://host[:port]), or null when no host is set.</summary>
    Task<string?> GetCanonicalBaseUrlAsync(CancellationToken cancellationToken = default);

    /// <summary>A read-only snapshot of the domain settings for the enforcement middleware.</summary>
    Task<DomainPolicy> GetDomainPolicyAsync(CancellationToken cancellationToken = default);
}
