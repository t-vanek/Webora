using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;
using Webora.Application.Settings;
using Webora.Domain.Settings;

namespace Webora.Web.Hosting;

/// <summary>
/// Applies the stored domain/URL settings at runtime: redirects recognized hosts to the canonical
/// host (aliases, www/non-www), upgrades to HTTPS when forced, canonicalizes the URL path (lowercase
/// and trailing slash), and emits the HSTS header. Unrecognized hosts and localhost/IP literals are
/// left untouched so misconfiguration cannot black-hole traffic. Static assets, framework paths and
/// the query string are never rewritten. Runs behind a trusted proxy and only outside Development.
/// </summary>
public sealed class CanonicalDomainMiddleware(RequestDelegate next)
{
    private const string CacheKey = SettingsCacheKeys.DomainPolicy;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public async Task InvokeAsync(HttpContext context, ISiteSettingsService settings, IMemoryCache cache)
    {
        // Error/status-code re-executions run the whole pipeline again; don't rewrite them, so an
        // error keeps its status instead of turning into a redirect.
        if (context.Features.Get<IStatusCodeReExecuteFeature>() is not null ||
            context.Features.Get<IExceptionHandlerPathFeature>() is not null)
        {
            await next(context);
            return;
        }

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
            var value = $"max-age={maxAgeSeconds}";
            if (policy.HstsIncludeSubDomains)
            {
                value += "; includeSubDomains";
            }

            // "preload" is only honored together with includeSubDomains.
            if (policy.HstsPreload && policy.HstsIncludeSubDomains)
            {
                value += "; preload";
            }

            context.Response.Headers[HeaderNames.StrictTransportSecurity] = value;
        }

        // Leave infrastructure hosts (localhost, IP literals) completely untouched.
        if (IsLocalOrIp(host))
        {
            await next(context);
            return;
        }

        var (targetScheme, targetHost, targetPort) = ResolveSchemeHost(policy, scheme, host);
        var path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
        var canNormalizePath = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        var targetPath = NormalizePath(policy, path, canNormalizePath);

        var schemeChanged = !string.Equals(targetScheme, scheme, StringComparison.OrdinalIgnoreCase);
        var hostChanged = !string.Equals(targetHost, host, StringComparison.OrdinalIgnoreCase);
        var pathChanged = !string.Equals(targetPath, path, StringComparison.Ordinal);

        if (schemeChanged || hostChanged || pathChanged)
        {
            var portSuffix = targetPort is { } port && port != DefaultPort(targetScheme) ? $":{port}" : string.Empty;
            var location = $"{targetScheme}://{targetHost}{portSuffix}{context.Request.PathBase}{targetPath}{context.Request.QueryString}";
            context.Response.Redirect(location, permanent: true);
            return;
        }

        await next(context);
    }

    internal static (string Scheme, string Host, int? Port) ResolveSchemeHost(DomainPolicy policy, string scheme, string host)
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

        var port = !string.IsNullOrEmpty(policy.CanonicalHost)
            && string.Equals(targetHost, policy.CanonicalHost, StringComparison.OrdinalIgnoreCase)
                ? policy.Port
                : null;

        return (targetScheme, targetHost, port);
    }

    internal static string NormalizePath(DomainPolicy policy, string path, bool allow)
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

    // Skips the root, framework/Blazor paths (/_framework, /_content, /_blazor) and static files
    // (a dot in the last segment) so asset URLs are never rewritten.
    private static bool IsNormalizablePath(string path)
    {
        if (path.Length <= 1 || path.StartsWith("/_", StringComparison.Ordinal))
        {
            return false;
        }

        var lastSlash = path.LastIndexOf('/');
        return path.IndexOf('.', lastSlash + 1) < 0;
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
