using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Caching.Memory;
using D3Parking.Application.Settings;

namespace D3Parking.Web.Hosting;

/// <summary>
/// Lowest-priority culture provider: applies the site's configured default language when the
/// visitor expressed no supported preference (no culture cookie, no matching Accept-Language).
/// The value is cached briefly so it does not hit the database on every request.
/// </summary>
public sealed class SiteDefaultCultureProvider : RequestCultureProvider
{
    private const string CacheKey = SettingsCacheKeys.DefaultLanguage;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var cache = httpContext.RequestServices.GetRequiredService<IMemoryCache>();

        var language = await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            var settings = httpContext.RequestServices.GetRequiredService<ISiteSettingsService>();
            return await settings.GetDefaultLanguageAsync(httpContext.RequestAborted) ?? string.Empty;
        });

        return string.IsNullOrEmpty(language) ? null : new ProviderCultureResult(language);
    }
}
