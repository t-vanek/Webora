// Service worker pro PWA režim (instalace na plochu, offline fallback).
//
// Blazor Web App se serverovou interaktivitou potřebuje k práci síť (SignalR circuit),
// takže strategie je záměrně konzervativní:
//   – navigace: vždy ze sítě, při výpadku se servíruje offline.html (HTML se nikdy
//     necachuje — stránky jsou personalizované a závislé na přihlášení),
//   – statické assety (styly, skripty, fonty, obrázky, manifest): stale-while-revalidate,
//   – realtime a datové endpointy (_blazor, huby, API, OpenIddict) jdou mimo cache.
//
// Při změně strategie nebo precache seznamu zvyš verzi cache — stará se smaže v activate.

const CACHE_NAME = 'd3parking-v1';
const OFFLINE_URL = 'offline.html';

// Minimální sada pro vykreslení offline stránky bez sítě.
const PRECACHE = [
    OFFLINE_URL,
    'favicon.png',
    'icons/icon-192.png',
    'lib/sora/sora-latin.woff2',
    'lib/sora/sora-latin-ext.woff2',
];

// Cesty, kterých se service worker nesmí dotýkat: realtime spojení a datové endpointy.
const NETWORK_ONLY_PREFIXES = ['/_blazor', '/hubs/', '/api/', '/connect/', '/culture/'];

// request.destination hodnoty, které se cachují jako statické assety. WASM zdroje
// (_framework) si Blazor stahuje obyčejným fetch() s prázdnou destination — ty se sem
// schválně nepletou, mají vlastní integrity mechanismus.
const STATIC_DESTINATIONS = new Set(['style', 'script', 'font', 'image', 'manifest']);

self.addEventListener('install', (event) => {
    event.waitUntil(
        caches.open(CACHE_NAME)
            .then((cache) => cache.addAll(PRECACHE))
            .then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys()
            .then((keys) => Promise.all(keys.filter((key) => key !== CACHE_NAME).map((key) => caches.delete(key))))
            .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (event) => {
    const request = event.request;
    if (request.method !== 'GET') {
        return;
    }

    const url = new URL(request.url);
    if (url.origin !== self.location.origin
        || NETWORK_ONLY_PREFIXES.some((prefix) => url.pathname.startsWith(prefix))) {
        return;
    }

    if (request.mode === 'navigate') {
        event.respondWith(fetch(request).catch(() => caches.match(OFFLINE_URL)));
        return;
    }

    if (STATIC_DESTINATIONS.has(request.destination)) {
        event.respondWith(staleWhileRevalidate(request));
    }
});

// Web Push: server posílá JSON {title, body, url, tag}. Když je aplikace viditelná, oznámení
// doručí SignalR zvoneček — OS notifikace by byla duplicitní, takže se potlačí.
self.addEventListener('push', (event) => {
    if (!event.data) {
        return;
    }

    let payload;
    try {
        payload = event.data.json();
    } catch {
        return;
    }

    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        if (windows.some((client) => client.visibilityState === 'visible')) {
            return;
        }

        await self.registration.showNotification(payload.title ?? 'D3Parking', {
            body: payload.body ?? '',
            icon: 'icons/icon-192.png',
            badge: 'icons/badge-96.png',
            tag: payload.tag ?? undefined,
            data: { url: payload.url ?? '/' },
        });
    })());
});

// Klik na notifikaci: naviguj existující okno aplikace na cíl notifikace a fokusuj ho,
// jinak otevři nové. Samotný focus bez navigace by nechal uživatele na nesouvisející stránce.
self.addEventListener('notificationclick', (event) => {
    event.notification.close();
    const url = event.notification.data?.url ?? '/';

    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        const existing = windows.find((client) => 'focus' in client);
        if (existing) {
            if ('navigate' in existing) {
                // navigate() umí selhat (např. okno mimo scope) — focus má proběhnout i tak.
                await existing.navigate(url).catch(() => undefined);
            }
            await existing.focus();
        } else {
            await self.clients.openWindow(url);
        }
    })());
});

// Vrátí okamžitě cache (pokud existuje) a na pozadí ji obnoví ze sítě. Fingerprintované
// assety (@Assets / ImportMap) jsou immutable, takže stará odpověď nikdy není špatně;
// nefingerprintované se srovnají při příští návštěvě.
async function staleWhileRevalidate(request) {
    const cache = await caches.open(CACHE_NAME);
    const cached = await cache.match(request);

    const network = fetch(request)
        .then((response) => {
            if (response.ok && response.type === 'basic') {
                cache.put(request, response.clone());
            }
            return response;
        })
        .catch(() => undefined);

    return cached ?? (await network) ?? Response.error();
}
