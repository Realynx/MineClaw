/* The live app is always network-first. Never persist accounts, commands, API replies, chunks or imported assets. */
const CACHE = 'mineclaw-pwa-v1';
const STATIC = ['/offline.html', '/icons/mineclaw-192.png', '/icons/mineclaw-512.png'];
self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(STATIC)));
});
self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches
      .keys()
      .then((keys) =>
        Promise.all(
          keys.filter((key) => key.startsWith('mineclaw-pwa-') && key !== CACHE).map((key) => caches.delete(key)),
        ),
      ),
  );
});
self.addEventListener('fetch', (event) => {
  const request = event.request;
  const url = new URL(request.url);
  if (
    request.method !== 'GET' ||
    request.mode !== 'navigate' ||
    url.origin !== self.location.origin ||
    url.pathname === '/api' ||
    url.pathname.startsWith('/api/')
  )
    return;
  // A failed network navigation gets a generic page, never a previous user's authenticated HTML.
  event.respondWith(fetch(request).catch(async () => (await caches.match('/offline.html')) || Response.error()));
});
