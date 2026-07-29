using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;
using D3Parking.Application.Settings;
using D3Parking.Domain.Settings;

namespace D3Parking.Web.Hosting;

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

        // HSTS is a promise about a specific domain. Stamping it on every DNS name pointed at
        // this server (staging aliases, internal names) would force HTTPS on them — and their
        // subdomains — for HstsMaxAgeDays after they stop being served here, so only hosts the
        // policy recognizes get the header. Without a canonical host there is nothing to
        // recognize against and the admin's enable applies to whatever host is served.
        if (policy.HstsEnabled && CanonicalDomainRules.IsHttps(scheme) && !IsLocalOrIp(host) && CanonicalDomainRules.IsRecognizedHost(policy, host))
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

        var (targetScheme, targetHost, targetPort) = CanonicalDomainRules.ResolveSchemeHost(policy, scheme, host);
        var path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
        var canNormalizePath = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        var targetPath = CanonicalDomainRules.NormalizePath(policy, path, canNormalizePath);

        var schemeChanged = !string.Equals(targetScheme, scheme, StringComparison.OrdinalIgnoreCase);
        var hostChanged = !string.Equals(targetHost, host, StringComparison.OrdinalIgnoreCase);
        var pathChanged = !string.Equals(targetPath, path, StringComparison.Ordinal);
        // Compare effective ports (explicit or the scheme default on each side), so the redirect
        // decision matches the URL that would actually be emitted — a permanent redirect to the
        // very URL being served, or to one that only differs in an implied default port, must
        // never fire.
        var currentPort = context.Request.Host.Port ?? CanonicalDomainRules.DefaultPort(scheme);
        var targetPortEffective = targetPort ?? CanonicalDomainRules.DefaultPort(targetScheme);
        var portChanged = targetPortEffective != currentPort;

        if (schemeChanged || hostChanged || pathChanged || portChanged)
        {
            var portSuffix = targetPortEffective != CanonicalDomainRules.DefaultPort(targetScheme) ? $":{targetPortEffective}" : string.Empty;
            var location = $"{targetScheme}://{targetHost}{portSuffix}{context.Request.PathBase}{targetPath}{context.Request.QueryString}";

            // canNormalizePath == GET/HEAD. Those keep the classic 301; anything else gets 308 so
            // the client replays the request with its method and body — a 301 would quietly turn a
            // POSTed form into a body-less GET.
            if (canNormalizePath)
            {
                context.Response.Redirect(location, permanent: true);
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
                context.Response.Headers.Location = location;
            }

            return;
        }

        await next(context);
    }

    // The decision rules (scheme/host/port resolution, path normalization, host recognition)
    // are pure functions in CanonicalDomainRules (Application layer), pinned by unit tests.
    private static bool IsLocalOrIp(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out _);
}

public static class CanonicalDomainMiddlewareExtensions
{
    public static IApplicationBuilder UseDomainEnforcement(this IApplicationBuilder app) =>
        app.UseMiddleware<CanonicalDomainMiddleware>();
}
