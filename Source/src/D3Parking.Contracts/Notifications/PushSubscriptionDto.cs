namespace D3Parking.Contracts.Notifications;

/// <summary>Web Push subscription as produced by <c>PushManager.subscribe()</c> in the browser.</summary>
public sealed record PushSubscriptionDto(string Endpoint, string P256dh, string Auth);

/// <summary>VAPID public key the browser needs to create a push subscription.</summary>
public sealed record PushPublicKeyDto(string PublicKey);
