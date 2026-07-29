using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using D3Parking.Contracts.Notifications;

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
        var request = new HttpRequestMessage(HttpMethod.Put, $"{Base}/push/subscription")
        {
            Content = JsonContent.Create(subscription, options: JsonOptions),
        };
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task UnsubscribePushAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"{Base}/push/subscription?endpoint={Uri.EscapeDataString(endpoint)}");
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task SendPostAsync(string url, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
