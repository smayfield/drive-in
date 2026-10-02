// The offline gate page's service worker (served at /manage/{id}/gate/offline-sw.js, scope /manage/{id}/gate/offline).
// It keeps the page and its scripts and styles so the page opens with no signal. The admit list and check-ins don't go
// through here: the page keeps those in IndexedDB and talks to the server itself.
const CACHE = "drivein-gate-v1";
const NAVIGATION_TIMEOUT_MS = 5000;

self.addEventListener("install", () => self.skipWaiting());

self.addEventListener("activate", event => {
    event.waitUntil((async () => {
        for (const key of await caches.keys())
            if (key.startsWith("drivein-gate-") && key !== CACHE)
                await caches.delete(key);
        await self.clients.claim();
    })());
});

// The page sends the files it loaded (it may have loaded them before this worker was in control). Anything else in the
// cache is from an older deploy and is dropped.
self.addEventListener("message", event => {
    if (event.data?.type === "cache" && Array.isArray(event.data.urls))
        event.waitUntil(keepOnly(event.data.urls));
});

async function keepOnly(urls) {
    const cache = await caches.open(CACHE);
    const wanted = new Set(urls.map(u => new URL(u, self.location.origin).href));
    for (const url of wanted) {
        if (!(await cache.match(url)))
            await store(cache, url);
    }
    for (const request of await cache.keys())
        if (!wanted.has(request.url))
            await cache.delete(request);
}

async function store(cache, url) {
    try {
        const response = await fetch(url, { credentials: "same-origin", cache: "no-cache" });
        // Never keep a redirect (e.g. to sign in) or an error page as the page itself.
        if (response.ok && !response.redirected && response.type === "basic")
            await cache.put(url, response);
    } catch {
        // Offline: keep what's there.
    }
}

self.addEventListener("fetch", event => {
    const request = event.request;
    const url = new URL(request.url);
    if (request.method !== "GET" || url.origin !== self.location.origin)
        return;
    // The admit list: always the network (the page falls back to its own copy).
    if (url.pathname.endsWith("/gate/offline/data"))
        return;
    if (request.mode === "navigate")
        event.respondWith(navigate(event));
    else
        event.respondWith(cacheFirst(event, request));
});

// The page: the network when it answers in time (so a new deploy or a sign-in redirect gets through), otherwise the copy.
async function navigate(event) {
    const request = event.request;
    const cache = await caches.open(CACHE);
    const key = new URL(request.url).origin + new URL(request.url).pathname;
    const network = fetch(request).then(async response => {
        // A redirect (e.g. to sign in) comes back opaque and isn't kept; checked here too so it never replaces the page.
        if (response.ok && !response.redirected && response.type === "basic")
            await cache.put(key, response.clone());
        return response;
    });
    event.waitUntil(network.catch(() => undefined));
    const timeout = new Promise(resolve => setTimeout(() => resolve(null), NAVIGATION_TIMEOUT_MS));
    try {
        const response = await Promise.race([network, timeout]);
        if (response)
            return response;
    } catch {
        // Fall through to the copy.
    }
    const cached = await cache.match(key);
    if (cached)
        return cached;
    // No copy yet and no answer so far: wait for the network after all.
    try {
        return await network;
    } catch {
        return new Response("<!doctype html><meta charset=utf-8><title>Offline</title><p>You're offline, and this device " +
            "hasn't saved the gate page yet. Open it once with a connection.</p>",
            { status: 503, headers: { "Content-Type": "text/html; charset=utf-8" } });
    }
}

// Scripts, styles, fonts and icons: the copy if there is one (refreshed in the background), otherwise the network.
async function cacheFirst(event, request) {
    const cache = await caches.open(CACHE);
    const cached = await cache.match(request);
    const network = fetch(request).then(async response => {
        if (response.ok && response.type === "basic")
            await cache.put(request, response.clone());
        return response;
    });
    if (cached) {
        event.waitUntil(network.catch(() => undefined));
        return cached;
    }
    return network;
}
