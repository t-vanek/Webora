using D3Parking.Application.Settings;
using D3Parking.Domain.Settings;
using NUnit.Framework;

namespace D3Parking.Application.Tests;

/// <summary>
/// Pins the canonical-domain redirect rules (pure functions inside the middleware). The headline
/// regression: a configured canonical port must never be glued onto a request served in the other
/// scheme — that minted permanent redirects to http://host:443, a dead URL browsers cache.
/// </summary>
[TestFixture]
public class CanonicalDomainRulesTests
{
    private static DomainPolicy Policy(
        string? canonicalHost = null,
        UrlScheme scheme = UrlScheme.Https,
        int? port = null,
        bool forceHttps = false,
        WwwPreference www = WwwPreference.NoPreference,
        IReadOnlyList<string>? aliases = null,
        bool lowercaseUrls = false,
        TrailingSlashPolicy trailingSlash = TrailingSlashPolicy.NoPreference) =>
        new(canonicalHost, scheme, port, forceHttps,
            HstsEnabled: false, HstsMaxAgeDays: 0, HstsIncludeSubDomains: false, HstsPreload: false,
            www, aliases ?? [], lowercaseUrls, trailingSlash);

    [Test]
    public void Https_port_is_not_applied_to_a_plain_http_request()
    {
        var policy = Policy(canonicalHost: "example.com", scheme: UrlScheme.Https, port: 443);

        var (scheme, host, port) = CanonicalDomainRules.ResolveSchemeHost(policy, "http", "example.com");

        Assert.That(scheme, Is.EqualTo("http"), "Without ForceHttps the scheme must stay as requested.");
        Assert.That(host, Is.EqualTo("example.com"));
        Assert.That(port, Is.Null, "The canonical port belongs to the canonical scheme — http://example.com:443 is a dead URL.");
    }

    [Test]
    public void Forced_https_carries_the_canonical_port()
    {
        var policy = Policy(canonicalHost: "example.com", scheme: UrlScheme.Https, port: 8443, forceHttps: true);

        var (scheme, host, port) = CanonicalDomainRules.ResolveSchemeHost(policy, "http", "example.com");

        Assert.That(scheme, Is.EqualTo("https"));
        Assert.That(host, Is.EqualTo("example.com"));
        Assert.That(port, Is.EqualTo(8443));
    }

    [Test]
    public void Https_request_is_never_downgraded_by_an_http_policy()
    {
        var policy = Policy(canonicalHost: "example.com", scheme: UrlScheme.Http, port: 8080);

        var (scheme, _, port) = CanonicalDomainRules.ResolveSchemeHost(policy, "https", "example.com");

        Assert.That(scheme, Is.EqualTo("https"));
        Assert.That(port, Is.Null, "An http-scheme port must not ride on an https response.");
    }

    [Test]
    public void Alias_redirects_to_the_canonical_host()
    {
        var policy = Policy(canonicalHost: "example.com", aliases: ["old.example.com"]);

        var (_, host, _) = CanonicalDomainRules.ResolveSchemeHost(policy, "https", "old.example.com");

        Assert.That(host, Is.EqualTo("example.com"));
    }

    [Test]
    public void Unrecognized_hosts_are_left_untouched()
    {
        var policy = Policy(canonicalHost: "example.com", port: 443, forceHttps: false);

        var (_, host, port) = CanonicalDomainRules.ResolveSchemeHost(policy, "https", "staging.example.org");

        Assert.That(host, Is.EqualTo("staging.example.org"));
        Assert.That(port, Is.Null, "The canonical port applies only on the canonical host itself.");
    }

    [TestCase("/hubs/notifications")]
    [TestCase("/api/notifications")]
    [TestCase("/connect/token")]
    [TestCase("/culture/set")]
    public void Machine_endpoints_are_never_path_normalized(string path)
    {
        var policy = Policy(lowercaseUrls: true, trailingSlash: TrailingSlashPolicy.Add);

        Assert.That(CanonicalDomainRules.NormalizePath(policy, path, allow: true), Is.EqualTo(path),
            "A cosmetic 301 on a WS handshake or API call breaks the client instead of tidying a URL.");
    }

    [Test]
    public void Page_paths_still_normalize()
    {
        var policy = Policy(lowercaseUrls: true, trailingSlash: TrailingSlashPolicy.Add);

        Assert.That(CanonicalDomainRules.NormalizePath(policy, "/Parking", allow: true), Is.EqualTo("/parking/"));
    }
}
