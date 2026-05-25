using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;
using Webora.Application.Settings;
using Webora.Domain.Settings;

namespace Webora.Web.Hosting;

/// <summary>
/// Applies the stored domain settings at runtime: redirects recognized hosts to the canonical host
/// (aliases, www/non-www), upgrades to HTTPS when forced, and emits the HSTS header. Unrecognized
/// hosts and localhost/IP literals are left untouched so misconfiguration cannot black-hole traffic.
/// Intended to run behind a trusted proxy (see UseForwardedHeaders) and only outside Development.
/// </summary>
public sealed class CanonicalDomainMiddleware(RequestDelegate next)
{
    private const string CacheKey = "webora:domain-policy";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public async Task InvokeAsync(HttpContext context, ISiteSettingsService settings, IMemoryCache cache)
    {
        var policy = (await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return await settings.GetDomainPolicyAsync(context.RequestAborted);
        }))!;

        var scheme = context.Request.Scheme;
        var host = context.Request.Host.Host.ToLowerInvariant();

        if (policy.HstsEnabled && IsHttps(scheme) && !IsLocalOrIp(host))
        {
            var maxAgeSeconds = Math.Max(0, policy.HstsMaxAgeDays) * 86_400L;
            context.Response.Headers[HeaderNames.StrictTransportSecurity] =
                policy.HstsIncludeSubDomains ? $"max-age={maxAgeSeconds}; includeSubDomains" : $"max-age={maxAgeSeconds}";
        }

        if (ResolveRedirect(policy, scheme, host) is { } target)
        {
            var portSuffix = target.Port is { } port && port != DefaultPort(target.Scheme) ? $":{port}" : string.Empty;
            var location = $"{target.Scheme}://{target.Host}{portSuffix}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
            context.Response.Redirect(location, permanent: true);
            return;
        }

        await next(context);
    }

    internal static (string Scheme, string Host, int? Port)? ResolveRedirect(DomainPolicy policy, string scheme, string host)
    {
        if (IsLocalOrIp(host))
        {
            return null;
        }

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

        var schemeChanged = !string.Equals(targetScheme, scheme, StringComparison.OrdinalIgnoreCase);
        var hostChanged = !string.Equals(targetHost, host, StringComparison.OrdinalIgnoreCase);
        if (!schemeChanged && !hostChanged)
        {
            return null;
        }

        var port = !string.IsNullOrEmpty(policy.CanonicalHost)
            && string.Equals(targetHost, policy.CanonicalHost, StringComparison.OrdinalIgnoreCase)
                ? policy.Port
                : null;

        return (targetScheme, targetHost, port);
    }

    private static bool IsWwwVariant(string host, string canonical) =>
        canonical.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(host, canonical[4..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(host, "www." + canonical, StringComparison.OrdinalIgnoreCase);

    private static bool IsHttps(string scheme) => string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalOrIp(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out _);

    private static int DefaultPort(string scheme) => IsHttps(scheme) ? 443 : 80;
}

public static class CanonicalDomainMiddlewareExtensions
{
    public static IApplicationBuilder UseDomainEnforcement(this IApplicationBuilder app) =>
        app.UseMiddleware<CanonicalDomainMiddleware>();
}
