// Web Push: tenká vrstva nad PushManagerem pro zvoneček notifikací (WASM komponenta ji
// importuje jako ES modul). Server worker musí být zaregistrovaný (app.js), jinak getState
// vrátí supported: false a přepínač se vůbec neukáže.

// applicationServerKey vyžaduje raw bytes — VAPID veřejný klíč chodí jako base64url.
function urlBase64ToUint8Array(base64Url) {
    const padding = '='.repeat((4 - (base64Url.length % 4)) % 4);
    const base64 = (base64Url + padding).replace(/-/g, '+').replace(/_/g, '/');
    const raw = atob(base64);
    return Uint8Array.from(raw, (c) => c.charCodeAt(0));
}

export async function getState() {
    if (!('serviceWorker' in navigator) || !('PushManager' in window) || !window.isSecureContext) {
        return { supported: false, permission: 'denied', subscribed: false };
    }

    const registration = await navigator.serviceWorker.ready;
    const subscription = await registration.pushManager.getSubscription();
    return { supported: true, permission: Notification.permission, subscribed: !!subscription };
}

// Vrací {endpoint, p256dh, auth}, nebo null, když uživatel oprávnění zamítl.
export async function subscribe(publicKey) {
    const registration = await navigator.serviceWorker.ready;
    try {
        const subscription = await registration.pushManager.subscribe({
            userVisibleOnly: true,
            applicationServerKey: urlBase64ToUint8Array(publicKey),
        });

        const json = subscription.toJSON();
        return { endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth };
    } catch (err) {
        if (err instanceof DOMException && err.name === 'NotAllowedError') {
            return null;
        }
        throw err;
    }
}

// Vrací endpoint zrušené subscription (server ji podle něj smaže), nebo null.
export async function unsubscribe() {
    const registration = await navigator.serviceWorker.ready;
    const subscription = await registration.pushManager.getSubscription();
    if (!subscription) {
        return null;
    }

    const endpoint = subscription.endpoint;
    await subscription.unsubscribe();
    return endpoint;
}
