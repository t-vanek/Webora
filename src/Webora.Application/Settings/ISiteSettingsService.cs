using Webora.Application.Accounts;

namespace Webora.Application.Settings;

/// <summary>Reads and updates the site-wide settings (a single persisted instance).</summary>
public interface ISiteSettingsService
{
    Task<SiteSettingsDto> GetAsync(CancellationToken cancellationToken = default);

    Task<AccountResult> UpdateDomainAsync(DomainSettingsDto domain, Guid actingUserId, CancellationToken cancellationToken = default);

    Task<AccountResult> UpdateRegionalAsync(RegionalSettingsDto regional, Guid actingUserId, CancellationToken cancellationToken = default);

    Task<AccountResult> UpdateEncodingAsync(EncodingSettingsDto encoding, Guid actingUserId, CancellationToken cancellationToken = default);

    Task<AccountResult> UpdateAccountsAsync(AccountsSettingsDto accounts, Guid actingUserId, CancellationToken cancellationToken = default);

    /// <summary>The configured canonical base URL (scheme://host[:port]), or null when no host is set.</summary>
    Task<string?> GetCanonicalBaseUrlAsync(CancellationToken cancellationToken = default);

    /// <summary>A read-only snapshot of the domain settings for the enforcement middleware.</summary>
    Task<DomainPolicy> GetDomainPolicyAsync(CancellationToken cancellationToken = default);

    /// <summary>The configured fallback UI culture, or null when none is set.</summary>
    Task<string?> GetDefaultLanguageAsync(CancellationToken cancellationToken = default);

    /// <summary>The configured display time zone, falling back to the server local zone when unset.</summary>
    Task<TimeZoneInfo> GetTimeZoneAsync(CancellationToken cancellationToken = default);

    /// <summary>The charset declared for served HTML pages (defaults to "utf-8").</summary>
    Task<string> GetPageCharsetAsync(CancellationToken cancellationToken = default);

    /// <summary>The charset used to encode outgoing email bodies (defaults to "utf-8").</summary>
    Task<string> GetEmailCharsetAsync(CancellationToken cancellationToken = default);

    /// <summary>The role automatically granted to newly self-registered accounts, or null when none.</summary>
    Task<string?> GetDefaultRoleAsync(CancellationToken cancellationToken = default);
}
