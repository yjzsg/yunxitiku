/* 云习题库 service worker（S7）。
 *
 * 策略：
 *   - 只处理同源 GET；`/api/` 一律走网络 —— 题库与用户数据必须实时，不能吃缓存。
 *   - 导航请求（HTML）用 network-first：在线时永远拿最新页面，离线时回退到缓存的壳。
 *   - 其它静态资源用 stale-while-revalidate：先用缓存快速渲染，同时后台更新。
 *
 * 静态资源 URL 都带 `?v=版本号`，所以发新版本就是新 URL，不会卡在旧文件上；
 * 唯一需要 network-first 的就是 index.html 本身（它决定引用哪个版本号）。
 */
const CACHE = "yunxi-shell-v1";
const SHELL = ["/", "/favicon.svg", "/manifest.json"];

self.addEventListener("install", (event) => {
  event.waitUntil(
    caches.open(CACHE)
      .then((cache) => cache.addAll(SHELL))
      .catch(() => {})
      .then(() => self.skipWaiting())
  );
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((key) => key !== CACHE).map((key) => caches.delete(key))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener("fetch", (event) => {
  const request = event.request;
  if (request.method !== "GET") return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;
  if (url.pathname.startsWith("/api/")) return;   // 数据必须实时

  if (request.mode === "navigate") {
    event.respondWith(
      fetch(request)
        .then((response) => {
          const copy = response.clone();
          caches.open(CACHE).then((cache) => cache.put("/", copy)).catch(() => {});
          return response;
        })
        .catch(() => caches.match("/").then((hit) => hit || Response.error()))
    );
    return;
  }

  event.respondWith(
    caches.match(request).then((hit) => {
      const network = fetch(request)
        .then((response) => {
          if (response && response.ok) {
            const copy = response.clone();
            caches.open(CACHE).then((cache) => cache.put(request, copy)).catch(() => {});
          }
          return response;
        })
        .catch(() => hit);
      return hit || network;
    })
  );
});
