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
