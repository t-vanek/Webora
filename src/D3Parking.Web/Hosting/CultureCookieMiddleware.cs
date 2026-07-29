using Microsoft.AspNetCore.Localization;

namespace D3Parking.Web.Hosting;

/// <summary>
/// Persists the culture the request pipeline negotiated (Accept-Language, site default) into the
/// standard culture cookie whenever the visitor does not carry one yet.
/// </summary>
/// <remarks>
/// Without the cookie, only page requests speak the negotiated culture. The <c>/_blazor</c>
/// WebSocket handshake — whose request culture becomes the culture of every InteractiveServer
/// render — carries no Accept-Language header (and browsers that do send one send the browser UI
/// language, not the page's negotiation), and the WASM islands read only the cookie
/// (<c>d3parkingGetCulture</c> in App.razor). The result was a page whose static shell and
/// interactive islands rendered in different languages. Cookies do flow on the WS handshake, so
/// pinning the negotiated culture here keeps every render mode in one language.
/// <para>
/// The cookie is session-scoped and only written when absent: an explicit choice via
/// <c>/culture/set</c> (a one-year cookie) is never overwritten, and a changed site default picks
/// up for cookie-less visitors on their next browser session.
/// </para>
/// </remarks>
public static class CultureCookieMiddleware
{
    public static IApplicationBuilder UseCultureCookie(this IApplicationBuilder app) =>
        app.Use(static (context, next) =>
        {
            var request = context.Request;
            if (HttpMethods.IsGet(request.Method)
                && !request.Cookies.ContainsKey(CookieRequestCultureProvider.DefaultCookieName)
                && HtmlRequests.AcceptsHtml(request)
                && context.Features.Get<IRequestCultureFeature>() is { } negotiated)
            {
                context.Response.Cookies.Append(
                    CookieRequestCultureProvider.DefaultCookieName,
                    CookieRequestCultureProvider.MakeCookieValue(negotiated.RequestCulture),
                    new CookieOptions { Path = "/", IsEssential = true, SameSite = SameSiteMode.Lax });
            }

            return next(context);
        });
}
