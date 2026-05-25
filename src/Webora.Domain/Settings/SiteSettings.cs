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

    public WwwPreference WwwPreference { get; private set; } = WwwPreference.NoPreference;

    /// <summary>Additional hostnames that should resolve to the canonical host.</summary>
    public IReadOnlyList<string> Aliases { get; private set; } = [];

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
        WwwPreference = wwwPreference;
        Aliases = aliases
            .Select(NormalizeHost)
            .Where(host => host is not null)
            .Select(host => host!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
