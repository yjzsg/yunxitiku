/* 云习题库 service worker（S7）。
 *
 * 策略（v2）：**只有 SHELL_PATHS 白名单里的公开壳文件允许进 Cache Storage**。
 *   - `/api/` 一律走网络 —— 题库与用户数据必须实时，不能吃缓存。
 *   - `/assets/` 等**需要会话**的路径一律直接用网络：SW 既不读也不写 Cache Storage。
 *     旧版把「除 /api/ 外的所有同源 GET」都缓存了，于是：
 *       · 退出登录/账号停用后，同一浏览器仍能从 SW 命中旧题图（网络侧已 401 也不撤销缓存）；
 *       · 同一 URL 换了图片先显示旧图，违背后端 no-cache「逐次回源校验」的意图。
 *     根因：Cache Storage 不是 HTTP 缓存 —— 不看响应里的 no-cache，缓存 key 里也没有用户身份。
 *     受保护资源要做离线必须先设计「按用户隔离 + 明确失效」，本 SW 选择不缓存。
 *   - 导航请求（HTML）用 network-first：在线时永远拿最新页面，离线时回退到缓存的壳。
 *   - 白名单内的静态资源用 stale-while-revalidate：先用缓存快速渲染，同时后台更新。
 *
 * 静态资源 URL 都带 `?v=版本号`，所以发新版本就是新 URL，不会卡在旧文件上；
 * 白名单按 pathname 匹配（忽略 ?v=），唯一需要 network-first 的就是 index.html 本身。
 *
 * 老 SW 怎么失效：CACHE 常量已从 v1 改成 v2，activate 会删掉**所有**其它 cache，
 * 所以旧 SW 缓存过的受保护题图会在这次升级里被清掉；install 里还有 skipWaiting，
 * 新 SW 装上后立即接管，不用等所有标签页关掉。index.html 的注册串也带了
 * `?v=`（见 public/index.html），让浏览器把它当成新脚本 URL 立刻回源。
 */
const CACHE = "yunxi-shell-v2";
const SHELL = ["/", "/favicon.svg", "/manifest.json"];

/* 允许进 Cache Storage 的**公开**路径。白名单而不是黑名单：
   漏写一个受保护路径只是少缓存，不会把需要会话的资源泄漏进缓存。 */
const SHELL_PATHS = [
  "/", "/index.html",
  "/favicon.svg", "/manifest.json",
  "/style.css", "/drawer.css", "/app.js"
];

/* 清空本 SW 的缓存。退出登录时调用：受保护资源本来就不进缓存了，
   这里连公开壳一起清掉，保证「退出后 Cache Storage 里不留任何东西」。 */
function purgeCaches() {
  return caches.keys()
    .then((keys) => Promise.all(keys.map((key) => caches.delete(key))))
    .catch(() => {});
}

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
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;

  if (request.method !== "GET") {
    /* 退出登录（POST /api/auth/logout）时清空 SW 缓存。
       只是"顺路观察"这个请求，不接管它，所以不用 respondWith。 */
    if (url.pathname === "/api/auth/logout") event.waitUntil(purgeCaches());
    return;
  }
  if (url.pathname.startsWith("/api/")) return;   // 数据必须实时

  if (request.mode === "navigate") {
    event.respondWith(
      fetch(request)
        .then((response) => {
          if (response && response.ok) {
            const copy = response.clone();
            caches.open(CACHE).then((cache) => cache.put("/", copy)).catch(() => {});
          }
          return response;
        })
        .catch(() => caches.match("/").then((hit) => hit || Response.error()))
    );
    return;
  }

  /* 白名单之外的一切 GET（含 /assets/ 这类需要会话的资源）都直接走网络：
     不读缓存、不写缓存、永远拿服务器的真实结果（401/403 也照实返回）。 */
  if (SHELL_PATHS.indexOf(url.pathname) === -1) return;

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

/* 页面可以主动要求清缓存（目前 app.js 没调用；留给「退出/切换账号」这类显式场景）。
   只处理同源页面发来的消息。 */
self.addEventListener("message", (event) => {
  const data = event.data;
  const type = typeof data === "string" ? data : (data && data.type);
  if (type !== "yunxi:clear-cache" && type !== "CLEAR_CACHE") return;
  event.waitUntil(purgeCaches().then(() => {
    const port = event.ports && event.ports[0];
    if (port) port.postMessage({ ok: true });
  }));
});
