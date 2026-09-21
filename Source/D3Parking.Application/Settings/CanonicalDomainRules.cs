using D3Parking.Domain.Settings;

namespace D3Parking.Application.Settings;

/// <summary>
/// The pure decision rules behind the canonical-domain middleware: which scheme/host/port a
/// request should be redirected to and how its path is normalized. Kept free of HTTP types so
/// the unit tests can pin them directly.
/// </summary>
public static class CanonicalDomainRules
{
    public static (string Scheme, string Host, int? Port) ResolveSchemeHost(DomainPolicy policy, string scheme, string host)
    {
        var targetScheme = policy.ForceHttps ? "https" : scheme;
        var targetHost = host;

        if (!string.IsNullOrEmpty(policy.CanonicalHost))
        {
            var canonical = policy.CanonicalHost;
            var isAlias = policy.Aliases.Any(a => string.Equals(a, host, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(host, canonical, StringComparison.OrdinalIgnoreCase) && (isAlias || IsWwwVariant(host, canonical)))
            {
                targetHost = canonical;
            }
            // Any other (unrecognized) host is left as-is.
        }
        else if (policy.Www == WwwPreference.PreferWww && !host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            targetHost = "www." + host;
        }
        else if (policy.Www == WwwPreference.PreferNonWww && host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            targetHost = host[4..];
        }

        // The configured port belongs to the configured canonical scheme. Gluing it onto a
        // request served in the other scheme minted dead URLs — an http request with
        // Scheme=Https, Port=443 redirected permanently to http://host:443, plaintext at the
        // TLS port, and browsers cache permanent redirects.
        var schemeMatchesPolicy = IsHttps(targetScheme) == (policy.Scheme == UrlScheme.Https);
        var port = !string.IsNullOrEmpty(policy.CanonicalHost)
            && string.Equals(targetHost, policy.CanonicalHost, StringComparison.OrdinalIgnoreCase)
            && schemeMatchesPolicy
                ? policy.Port
                : null;

        return (targetScheme, targetHost, port);
    }

    /// <summary>
    /// Hosts the domain policy is explicitly about: the canonical host, its aliases, and the
    /// www/non-www twin. With no canonical host configured every served host is "recognized".
    /// </summary>
    public static bool IsRecognizedHost(DomainPolicy policy, string host)
    {
        if (string.IsNullOrEmpty(policy.CanonicalHost))
        {
            return true;
        }

        return string.Equals(host, policy.CanonicalHost, StringComparison.OrdinalIgnoreCase)
            || policy.Aliases.Any(a => string.Equals(a, host, StringComparison.OrdinalIgnoreCase))
            || IsWwwVariant(host, policy.CanonicalHost);
    }

    // Protocol and machine endpoints, where a cosmetic 301 breaks the client instead of tidying
    // a URL: the SignalR WebSocket handshake is a GET and the WebSocket API never follows
    // redirects (a rewritten /hubs/... would permanently kill the WS transport), and API/OAuth
    // clients should not eat a redirect hop per call.
    private static readonly string[] NeverNormalized = ["/api", "/hubs", "/connect", "/culture"];

    public static string NormalizePath(DomainPolicy policy, string path, bool allow)
    {
        if (!allow || !IsNormalizablePath(path))
        {
            return path;
        }

        var result = policy.LowercaseUrls ? path.ToLowerInvariant() : path;
        result = policy.TrailingSlash switch
        {
            TrailingSlashPolicy.Remove => result.TrimEnd('/'),
            TrailingSlashPolicy.Add => result.EndsWith('/') ? result : result + "/",
            _ => result,
        };

        return result.Length == 0 ? "/" : result;
    }

    // Skips the root, framework/Blazor paths (/_framework, /_content, /_blazor), machine
    // endpoints and static files (a dot in the last segment) so those URLs are never rewritten.
    private static bool IsNormalizablePath(string path)
    {
        if (path.Length <= 1 || path.StartsWith("/_", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var prefix in NeverNormalized)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (path.Length == prefix.Length || path[prefix.Length] == '/'))
            {
                return false;
            }
        }

        var lastSlash = path.LastIndexOf('/');
        return path.IndexOf('.', lastSlash + 1) < 0;
    }

    public static bool IsWwwVariant(string host, string canonical) =>
        canonical.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(host, canonical[4..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(host, "www." + canonical, StringComparison.OrdinalIgnoreCase);

    public static bool IsHttps(string scheme) => string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);

    public static int DefaultPort(string scheme) => IsHttps(scheme) ? 443 : 80;
}
