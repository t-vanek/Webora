using System.Net.Http.Json;
using Webora.Contracts.Notifications;

namespace Webora.Web.Client.Api;

/// <summary>
/// Typed wrapper around the /api/notifications endpoints. WASM components inject this instead of
/// HttpClient so the HTTP plumbing (paths, antiforgery token, deserialization) stays in one place.
/// </summary>
public sealed class NotificationsApiClient(HttpClient http, AntiforgeryTokenProvider antiforgery)
{
    private const string Base = "api/notifications";

    public Task<int> GetUnreadCountAsync(CancellationToken cancellationToken = default) =>
        http.GetFromJsonAsync<int>($"{Base}/unread-count", cancellationToken);

    public async Task<IReadOnlyList<NotificationDto>> GetAsync(int take = 50, bool unreadOnly = false, CancellationToken cancellationToken = default)
    {
        var url = $"{Base}?take={take}&unreadOnly={unreadOnly}";
        return await http.GetFromJsonAsync<IReadOnlyList<NotificationDto>>(url, cancellationToken) ?? [];
    }

    public Task<NotificationPreferencesDto?> GetPreferencesAsync(CancellationToken cancellationToken = default) =>
        http.GetFromJsonAsync<NotificationPreferencesDto>($"{Base}/preferences", cancellationToken);

    public Task MarkReadAsync(Guid notificationId, CancellationToken cancellationToken = default) =>
        SendPostAsync($"{Base}/{notificationId}/read", cancellationToken);

    public Task MarkAllReadAsync(CancellationToken cancellationToken = default) =>
        SendPostAsync($"{Base}/read-all", cancellationToken);

    private async Task SendPostAsync(string url, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        await antiforgery.AttachAsync(request, cancellationToken);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
