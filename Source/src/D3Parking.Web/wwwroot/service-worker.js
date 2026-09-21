// Service worker pro PWA režim (instalace na plochu, offline fallback).
//
// Blazor Web App se serverovou interaktivitou potřebuje k práci síť (SignalR circuit),
// takže strategie je záměrně konzervativní:
//   – navigace: vždy ze sítě, při výpadku se servíruje offline.html (HTML se nikdy
//     necachuje — stránky jsou personalizované a závislé na přihlášení),
//   – statické assety (styly, skripty, fonty, obrázky, manifest): stale-while-revalidate,
//   – fingerprintovaný runtime WASM (_framework): cache-first, je immutable,
//   – realtime a datové endpointy (_blazor, huby, API, OpenIddict) jdou mimo cache.
//
// Při změně strategie nebo precache seznamu zvyš verzi cache — stará se smaže v activate.

const CACHE_NAME = 'd3parking-v2';
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

// request.destination hodnoty, které se cachují jako statické assety. Runtime WASM si Blazor
// stahuje obyčejným fetch() s prázdnou destination, takže sem nespadne — řeší ho pravidlo níž.
const STATIC_DESTINATIONS = new Set(['style', 'script', 'font', 'image', 'manifest']);

// Runtime .NET WebAssembly (zvoneček notifikací je WASM island, takže se bootuje na každém
// načtení stránky). Bez tohohle pravidla šlo ~47 MB souborů mimo cache: prohlížeč si drobné
// soubory podržel ve své HTTP cache, ale dva balíky ikon Fluent UI (10,3 a 8,6 MB) se přes
// limit na velikost jedné položky nevešly, takže se při KAŽDÉM refreshi stahovalo 18,5 MB.
// Cache Storage takový limit nemá.
//
// Cache-first jen pro soubory s content fingerprintem v názvu (`dotnet.a1b2c3d4e5.js`) — ty
// jsou z definice immutable, takže je nelze podat zastaralé. Nefingerprintované bootstrappery
// (`blazor.web.js`, `dotnet.js`) jdou dál ze sítě, aby nasazení nešlo zamknout na starou verzi.
const FRAMEWORK_PREFIX = '/_framework/';
const FINGERPRINTED = /\.[0-9a-z]{8,}\.[0-9a-z]+$/i;

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

    if (url.pathname.startsWith(FRAMEWORK_PREFIX) && FINGERPRINTED.test(url.pathname)) {
        event.respondWith(cacheFirstImmutable(request));
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
// Immutable asset: z cache, a když tam není, ze sítě a uložit. Žádná revalidace — jiný obsah
// znamená jiný fingerprint, tedy jiné URL.
//
// Při uložení nové verze se zahodí předchozí fingerprinty téhož souboru. Bez toho by každý
// `dotnet build` (nové fingerprinty) přisypal do cache dalších ~47 MB, které by tam po vývoji
// zůstaly ležet; v produkci by totéž dělalo každé nasazení.
async function cacheFirstImmutable(request) {
    const cache = await caches.open(CACHE_NAME);
    const cached = await cache.match(request);
    if (cached) {
        return cached;
    }

    const response = await fetch(request).catch(() => undefined);
    if (response?.ok && response.type === 'basic') {
        await cache.put(request, response.clone());
        await dropOtherVersionsOf(cache, new URL(request.url));
    }

    return response ?? Response.error();
}

// Smaže z cache položky, které jsou jinou fingerprintovanou verzí stejného souboru: shodná
// cesta i koncovka, jiný fingerprint mezi nimi.
async function dropOtherVersionsOf(cache, url) {
    const segments = url.pathname.split('/');
    const name = segments.pop() ?? '';
    const parts = name.split('.');
    if (parts.length < 3) {
        return;
    }

    const directory = segments.join('/');
    const base = parts.slice(0, -2).join('.');
    const extension = parts[parts.length - 1];

    for (const key of await cache.keys()) {
        const candidate = new URL(key.url);
        if (candidate.pathname === url.pathname) {
            continue;
        }

        const candidateSegments = candidate.pathname.split('/');
        const candidateName = candidateSegments.pop() ?? '';
        if (candidateSegments.join('/') !== directory) {
            continue;
        }

        const candidateParts = candidateName.split('.');
        if (candidateParts.length >= 3
            && candidateParts.slice(0, -2).join('.') === base
            && candidateParts[candidateParts.length - 1] === extension) {
            await cache.delete(key);
        }
    }
}

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
