namespace D3Parking.Application.Notifications;

/// <summary>Browser push endpoints are untrusted input, not arbitrary outbound HTTP targets.</summary>
public static class PushEndpointPolicy
{
    public static bool IsAllowed(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && endpoint.Length <= 800
        && (uri.IdnHost.Equals("fcm.googleapis.com", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.Equals("updates.push.services.mozilla.com", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.Equals("web.push.apple.com", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".notify.windows.com", StringComparison.OrdinalIgnoreCase));
}
