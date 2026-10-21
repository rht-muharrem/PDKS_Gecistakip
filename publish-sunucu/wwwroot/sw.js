"use strict";

/*
  Service worker: yalnizca HTTPS (veya localhost) uzerinden calisir.
  - API istekleri her zaman agdan gider, asla onbellekten sunulmaz (eski durum gostermemek icin).
  - Statik dosyalar once agdan denenir, basarisizsa onbellekten verilir.
  - Ag yoksa sayfa yerine kisa bir "baglanti yok" ekrani gosterilir.
*/

const CACHE = "server-monitor-v1";
const SHELL = ["/app.js", "/style.css", "/icons/icon-192.png"];

const OFFLINE_HTML = `<!DOCTYPE html>
<html lang="tr"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<title>Bağlantı yok</title>
<style>
  body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0f1419;color:#e3e8ef;
       font-family:-apple-system,"Segoe UI",sans-serif;text-align:center;padding:24px}
  h1{font-size:20px;margin:0 0 8px} p{color:#8b96a5;margin:0 0 20px;line-height:1.5}
  button{background:#4493f8;color:#fff;border:0;border-radius:10px;padding:12px 22px;font-size:16px;font-weight:600}
</style></head>
<body><div>
  <h1>Panele ulaşılamıyor</h1>
  <p>Telefonun şirket ağına (Wi-Fi veya VPN) bağlı olduğundan emin olun.</p>
  <button onclick="location.reload()">Tekrar dene</button>
</div></body></html>`;

self.addEventListener("install", event => {
  event.waitUntil(
    caches.open(CACHE)
      .then(cache => cache.addAll(SHELL))
      .catch(() => { /* ilk kurulumda onbellege alinamazsa sorun degil */ })
  );
  self.skipWaiting();
});

self.addEventListener("activate", event => {
  event.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener("fetch", event => {
  const request = event.request;
  if (request.method !== "GET") return;

  const url = new URL(request.url);
  if (url.origin !== self.location.origin || url.pathname.startsWith("/api/")) return;

  if (request.mode === "navigate") {
    event.respondWith(
      fetch(request).catch(() =>
        new Response(OFFLINE_HTML, { headers: { "Content-Type": "text/html; charset=utf-8" } }))
    );
    return;
  }

  event.respondWith(
    fetch(request)
      .then(response => {
        if (response.ok) {
          const copy = response.clone();
          caches.open(CACHE).then(cache => cache.put(request, copy));
        }
        return response;
      })
      .catch(() => caches.match(request))
  );
});
