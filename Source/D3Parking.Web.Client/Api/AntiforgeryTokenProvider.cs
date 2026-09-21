using System.Net.Http.Json;

namespace D3Parking.Web.Client.Api;

/// <summary>
/// Fetches and caches the ASP.NET Core antiforgery request token for the current session, and
/// attaches it as the RequestVerificationToken header to outgoing POST/PUT/DELETE calls. When the
/// server rejects a request with 400 (the token/cookie pair rotated), the API client asks for a
/// forced refresh and retries once — see NotificationsApiClient.SendUnsafeAsync.
/// </summary>
public sealed class AntiforgeryTokenProvider(HttpClient http)
{
    private const string TokenEndpoint = "api/antiforgery/token";
    private const string HeaderName = "RequestVerificationToken";

    private string? _token;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public async Task AttachAsync(HttpRequestMessage request, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var token = forceRefresh || _token is null ? await RefreshAsync(cancellationToken) : _token;
        request.Headers.Remove(HeaderName);
        request.Headers.Add(HeaderName, token);
    }

    public async Task<string> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var response = await http.GetFromJsonAsync<TokenResponse>(TokenEndpoint, cancellationToken)
                ?? throw new InvalidOperationException("Antiforgery token endpoint returned an empty response.");
            _token = response.Token;
            return response.Token;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private sealed record TokenResponse(string Token);
}
