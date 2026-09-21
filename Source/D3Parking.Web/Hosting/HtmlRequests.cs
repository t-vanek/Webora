namespace D3Parking.Web.Hosting;

/// <summary>Shared predicate for middleware that only cares about HTML-bearing requests.</summary>
internal static class HtmlRequests
{
    /// <summary>
    /// Document and enhanced-navigation requests advertise text/html; asset fetches, WebSocket
    /// handshakes, APIs, hubs and SSE streams do not.
    /// </summary>
    internal static bool AcceptsHtml(HttpRequest request) =>
        request.Headers.Accept.Any(static v => v?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
}
