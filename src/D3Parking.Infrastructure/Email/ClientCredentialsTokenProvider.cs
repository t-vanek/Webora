using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace D3Parking.Infrastructure.Email;

/// <summary>
/// Obtains an access token via the OAuth2 client-credentials grant against a configurable token
/// endpoint, caching it until shortly before expiry. Registered as a singleton so the cache is shared.
/// </summary>
public sealed class ClientCredentialsTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<SmtpOAuth2Options> options) : ISmtpAccessTokenProvider
{
    public const string HttpClientName = "smtp-oauth2";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
        {
            return _token;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
            {
                return _token;
            }

            var settings = options.Value;
            if (string.IsNullOrWhiteSpace(settings.TokenEndpoint))
            {
                throw new InvalidOperationException(
                    "SMTP OAuth2 is enabled but 'Smtp:OAuth2:TokenEndpoint' is not configured.");
            }

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = settings.ClientId,
                ["client_secret"] = settings.ClientSecret,
            };
            if (!string.IsNullOrWhiteSpace(settings.Scope))
            {
                form["scope"] = settings.Scope;
            }

            using var httpClient = httpClientFactory.CreateClient(HttpClientName);
            using var response = await httpClient.PostAsync(
                settings.TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
                ?? throw new InvalidOperationException("Token endpoint returned an empty response.");

            // Renew a minute early to avoid using a token that expires mid-request.
            var lifetime = payload.ExpiresIn > 60 ? payload.ExpiresIn - 60 : payload.ExpiresIn;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetime);
            _token = payload.AccessToken;
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; init; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }
}
