using System.Text;
using Microsoft.Extensions.Caching.Memory;
using D3Parking.Application.Settings;

namespace D3Parking.Web.Hosting;

/// <summary>
/// Applies the configured page charset. The resolved charset is exposed via
/// <see cref="HttpContextItemKey"/> (read by App.razor for the &lt;meta charset&gt; tag). When the
/// charset is anything other than UTF-8, text/html responses are re-encoded from UTF-8 to the
/// target charset and the Content-Type is updated. UTF-8 (the default) is a zero-cost pass-through.
/// </summary>
public sealed class CharsetMiddleware(RequestDelegate next)
{
    public const string HttpContextItemKey = "d3parking:page-charset";
    private const string CacheKey = SettingsCacheKeys.PageCharset;
    private const string Utf8 = "utf-8";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public async Task InvokeAsync(HttpContext context, ISiteSettingsService settings, IMemoryCache cache)
    {
        var charset = (await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return await settings.GetPageCharsetAsync(context.RequestAborted);
        }))!;

        context.Items[HttpContextItemKey] = charset;

        if (string.Equals(charset, Utf8, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            // Misconfigured charset: fall back to serving UTF-8 untouched rather than failing.
            await next(context);
            return;
        }

        // Buffering is only worth it where HTML can come back. Wrapping every response would
        // materialize multi-megabyte framework assets in memory, hold SSE streams (the SignalR
        // fallback transport) until connection end, and turn streaming SSR into buffered pages.
        // A rare HTML fetch without an Accept header stays UTF-8 — the Content-Type header wins
        // over the meta tag, so it renders correctly, just without the configured charset.
        if (!HtmlRequests.AcceptsHtml(context.Request))
        {
            await next(context);
            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;
        // Only plain HTML can be re-encoded. Anything compressed (precompressed static assets,
        // response compression) must pass through byte-for-byte — reading gzip/brotli bytes as
        // text would corrupt the body while Content-Encoding still promises compressed content.
        if (IsHtml(context.Response.ContentType) && context.Response.Headers.ContentEncoding.Count == 0)
        {
            var html = await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync();
            var bytes = encoding.GetBytes(html);
            context.Response.ContentType = $"text/html; charset={charset}";
            context.Response.ContentLength = bytes.Length;
            await originalBody.WriteAsync(bytes);
        }
        else
        {
            await buffer.CopyToAsync(originalBody);
        }
    }

    private static bool IsHtml(string? contentType) =>
        contentType is not null && contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase);
}

public static class CharsetMiddlewareExtensions
{
    public static IApplicationBuilder UsePageCharset(this IApplicationBuilder app) =>
        app.UseMiddleware<CharsetMiddleware>();
}
