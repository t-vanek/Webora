using Webora.Domain.Common;

namespace Webora.Domain.Settings;

/// <summary>
/// Site-wide configuration. There is a single instance identified by <see cref="SingletonId"/>.
/// The first section is the domain configuration used to generate absolute URLs (email links,
/// canonical links). Enforcement values (force HTTPS, HSTS, www, aliases) are stored here but are
/// not actively applied by middleware.
/// </summary>
public class SiteSettings : Entity, IAggregateRoot
{
    public static readonly Guid SingletonId = new("00000000-0000-0000-0000-0000000000a1");

    public string? CanonicalHost { get; private set; }

    public UrlScheme Scheme { get; private set; } = UrlScheme.Https;

    /// <summary>Non-default port for generated URLs; null means the scheme default (80/443).</summary>
    public int? Port { get; private set; }

    public bool ForceHttps { get; private set; } = true;

    public bool HstsEnabled { get; private set; }

    public int HstsMaxAgeDays { get; private set; } = 365;

    public bool HstsIncludeSubDomains { get; private set; }

    /// <summary>Adds the "preload" directive to the HSTS header (only meaningful with a long max-age and subdomains).</summary>
    public bool HstsPreload { get; private set; }

    public WwwPreference WwwPreference { get; private set; } = WwwPreference.NoPreference;

    /// <summary>Additional hostnames that should resolve to the canonical host.</summary>
    public IReadOnlyList<string> Aliases { get; private set; } = [];

    /// <summary>Fallback UI culture (e.g. "cs") used when the visitor expresses no supported preference.</summary>
    public string? DefaultLanguage { get; private set; }

    /// <summary>System time zone id (e.g. "Europe/Prague") used to display times. Null = server local.</summary>
    public string? DefaultTimeZoneId { get; private set; }

    /// <summary>Charset declared for served HTML pages. Null = UTF-8.</summary>
    public string? PageCharset { get; private set; }

    /// <summary>Charset used to encode outgoing email bodies. Null = UTF-8.</summary>
    public string? EmailCharset { get; private set; }

    private SiteSettings() { }

    public static SiteSettings CreateDefault()
    {
        var settings = new SiteSettings();
        settings.Id = SingletonId;
        return settings;
    }

    public void UpdateDomain(
        string? canonicalHost,
        UrlScheme scheme,
        int? port,
        bool forceHttps,
        bool hstsEnabled,
        int hstsMaxAgeDays,
        bool hstsIncludeSubDomains,
        bool hstsPreload,
        WwwPreference wwwPreference,
        IReadOnlyList<string> aliases)
    {
        CanonicalHost = NormalizeHost(canonicalHost);
        Scheme = scheme;
        Port = port;
        ForceHttps = forceHttps;
        HstsEnabled = hstsEnabled;
        HstsMaxAgeDays = hstsMaxAgeDays < 0 ? 0 : hstsMaxAgeDays;
        HstsIncludeSubDomains = hstsIncludeSubDomains;
        HstsPreload = hstsPreload;
        WwwPreference = wwwPreference;
        Aliases = aliases
            .Select(NormalizeHost)
            .Where(host => host is not null && !string.Equals(host, CanonicalHost, StringComparison.OrdinalIgnoreCase))
            .Select(host => host!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void UpdateRegional(string? defaultLanguage, string? defaultTimeZoneId)
    {
        DefaultLanguage = string.IsNullOrWhiteSpace(defaultLanguage) ? null : defaultLanguage.Trim();
        DefaultTimeZoneId = string.IsNullOrWhiteSpace(defaultTimeZoneId) ? null : defaultTimeZoneId.Trim();
    }

    public void UpdateEncoding(string? pageCharset, string? emailCharset)
    {
        PageCharset = Normalize(pageCharset);
        EmailCharset = Normalize(emailCharset);

        static string? Normalize(string? charset) =>
            string.IsNullOrWhiteSpace(charset) ? null : charset.Trim().ToLowerInvariant();
    }

    /// <summary>The canonical base URL (scheme://host[:port]), or null when no host is configured.</summary>
    public string? BaseUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CanonicalHost))
            {
                return null;
            }

            var scheme = Scheme == UrlScheme.Https ? "https" : "http";
            var defaultPort = Scheme == UrlScheme.Https ? 443 : 80;
            var portSuffix = Port is { } port && port != defaultPort ? $":{port}" : string.Empty;
            return $"{scheme}://{CanonicalHost}{portSuffix}";
        }
    }

    /// <summary>Strips any scheme/path the user may have pasted, leaving a bare lower-cased host.</summary>
    private static string? NormalizeHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var host = value.Trim();
        var schemeIndex = host.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            host = host[(schemeIndex + 3)..];
        }

        host = host.Trim().Trim('/');
        return host.Length == 0 ? null : host.ToLowerInvariant();
    }
}
