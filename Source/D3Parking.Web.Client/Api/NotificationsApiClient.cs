using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using D3Parking.Contracts.Notifications;
using D3Parking.Domain.Notifications;

namespace D3Parking.Web.Client.Api;

/// <summary>
/// Typed wrapper around the /api/notifications endpoints. WASM components inject this instead of
/// HttpClient so the HTTP plumbing (paths, antiforgery token, deserialization) stays in one place.
/// </summary>
public sealed class NotificationsApiClient(HttpClient http, AntiforgeryTokenProvider antiforgery)
{
    private const string Base = "api/notifications";

    // The host serializes enums as strings (JsonStringEnumConverter); match that when reading so
    // DTOs carrying enums (NotificationCategory/Level, NotificationScope) deserialize correctly.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public Task<int> GetUnreadCountAsync(CancellationToken cancellationToken = default) =>
        http.GetFromJsonAsync<int>($"{Base}/unread-count", JsonOptions, cancellationToken);

    public async Task<IReadOnlyList<NotificationDto>> GetAsync(int take = 50, bool unreadOnly = false, CancellationToken cancellationToken = default)
    {
        var url = $"{Base}?take={take}&unreadOnly={unreadOnly}";
        return await http.GetFromJsonAsync<IReadOnlyList<NotificationDto>>(url, JsonOptions, cancellationToken) ?? [];
    }

    public Task<NotificationPreferencesDto?> GetPreferencesAsync(CancellationToken cancellationToken = default) =>
        http.GetFromJsonAsync<NotificationPreferencesDto>($"{Base}/preferences", JsonOptions, cancellationToken);

    public Task MarkReadAsync(Guid notificationId, CancellationToken cancellationToken = default) =>
        SendPostAsync($"{Base}/{notificationId}/read", cancellationToken);

    public Task MarkAllReadAsync(CancellationToken cancellationToken = default) =>
        SendPostAsync($"{Base}/read-all", cancellationToken);

    public Task SetMutedAsync(bool muted, CancellationToken cancellationToken = default) =>
        SendPostAsync($"{Base}/preferences/{(muted ? "mute" : "unmute")}", cancellationToken);

    public async Task SetScopeAsync(NotificationScope scope, CancellationToken cancellationToken = default)
    {
        using var response = await SendUnsafeAsync(() => new HttpRequestMessage(HttpMethod.Put, $"{Base}/preferences/scope")
        {
            Content = JsonContent.Create(new { Scope = scope }, options: JsonOptions),
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SetAvailabilityAsync(bool allow, CancellationToken cancellationToken = default)
    {
        using var response = await SendUnsafeAsync(() => new HttpRequestMessage(HttpMethod.Put, $"{Base}/preferences/availability")
        {
            Content = JsonContent.Create(new { Allow = allow }, options: JsonOptions),
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Null means Web Push is not configured on the server (the endpoint returns 204).</summary>
    public async Task<PushPublicKeyDto?> GetPushPublicKeyAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"{Base}/push/public-key", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PushPublicKeyDto>(JsonOptions, cancellationToken);
    }

    public async Task SubscribePushAsync(PushSubscriptionDto subscription, CancellationToken cancellationToken = default)
    {
        using var response = await SendUnsafeAsync(() => new HttpRequestMessage(HttpMethod.Put, $"{Base}/push/subscription")
        {
            Content = JsonContent.Create(subscription, options: JsonOptions),
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UnsubscribePushAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        using var response = await SendUnsafeAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"{Base}/push/subscription?endpoint={Uri.EscapeDataString(endpoint)}"),
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task SendPostAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await SendUnsafeAsync(() => new HttpRequestMessage(HttpMethod.Post, url), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // Unsafe verbs carry the antiforgery token. A 400 typically means the token/cookie pair has
    // rotated under us (fresh sign-in, key-ring change) — fetch a fresh token and retry once. The
    // request is rebuilt because an HttpRequestMessage cannot be sent twice.
    private async Task<HttpResponseMessage> SendUnsafeAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        var request = createRequest();
        await antiforgery.AttachAsync(request, cancellationToken);
        var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.BadRequest)
        {
            return response;
        }

        response.Dispose();
        request = createRequest();
        await antiforgery.AttachAsync(request, cancellationToken, forceRefresh: true);
        return await http.SendAsync(request, cancellationToken);
    }
}
