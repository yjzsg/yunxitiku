// 极简 CDP 驱动：直接开一个 headless Chrome，用 DevTools 协议操作页面。
// 不引 puppeteer/playwright —— 仓库没有 package.json，也不想为一条回归引一整套依赖。
// 代价：只能跑「真实浏览器里点几下、看几个状态」这类场景，够守住现在这几条 bug。
import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** 找浏览器：优先 CHROME_PATH，其次各平台常见路径。 */
export function findChrome() {
  if (process.env.CHROME_PATH && fs.existsSync(process.env.CHROME_PATH)) return process.env.CHROME_PATH;
  const candidates = {
    win32: [
      "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
      "C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe",
      path.join(process.env.LOCALAPPDATA || "", "Google\\Chrome\\Application\\chrome.exe"),
      "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe",
    ],
    darwin: [
      "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
      "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
    ],
    linux: ["/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/chromium", "/usr/bin/chromium-browser"],
  }[process.platform] || [];
  for (const c of candidates) if (c && fs.existsSync(c)) return c;
  throw new Error(`找不到 Chrome/Edge，请用 CHROME_PATH 指定（当前平台 ${process.platform}）`);
}

/**
 * 启动浏览器并连上第一个页面 target。
 * @param {{ baseUrl: string, artifactDir: string, debug?: boolean }} opts
 */
export async function launchBrowser({ baseUrl, artifactDir, debug = false }) {
  const chromePath = findChrome();
  fs.mkdirSync(artifactDir, { recursive: true });
  const profile = path.join(artifactDir, `profile-${Date.now()}`);
  const port = 9300 + Math.floor(Math.random() * 500);

  const args = [
    "--headless=new",
    `--remote-debugging-port=${port}`,
    `--user-data-dir=${profile}`,
    "--no-first-run",
    "--no-default-browser-check",
    "--disable-gpu",
    "--disable-extensions",
    "--disable-dev-shm-usage", // 容器 / CI 里默认的 /dev/shm 太小
    "--no-sandbox",            // CI 上无 user namespace 时会起不来
    "--window-size=1400,900",
    "about:blank",
  ];
  if (debug) args.push("--enable-logging=stderr", "--v=0");
  const chrome = spawn(chromePath, args, { stdio: debug ? "inherit" : "ignore" });

  const deadline = Date.now() + 30000;
  let wsUrl = null;
  while (Date.now() < deadline) {
    try {
      const list = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      const page = list.find((t) => t.type === "page");
      if (page?.webSocketDebuggerUrl) { wsUrl = page.webSocketDebuggerUrl; break; }
    } catch { /* 还没起来 */ }
    await sleep(200);
  }
  if (!wsUrl) {
    chrome.kill();
    throw new Error("Chrome 调试端口 30s 内没就绪（设 E2E_DEBUG=1 看 stderr）");
  }

  if (typeof WebSocket === "undefined") {
    chrome.kill();
    throw new Error("当前 Node 没有内置 WebSocket，需要 Node ≥ 22");
  }

  const ws = new WebSocket(wsUrl);
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });

  let nextId = 1;
  const pending = new Map();
  const netLog = [];
  const consoleLog = [];   // 页面里的 error/warning + 未捕获异常，失败时最有用
  ws.onmessage = (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) reject(new Error(JSON.stringify(msg.error)));
      else resolve(msg.result);
      return;
    }
    if (msg.method === "Network.requestWillBeSent") {
      const u = msg.params.request.url;
      if (u.includes("/api/user/")) {
        netLog.push({
          at: Date.now(), dir: "→", method: msg.params.request.method,
          url: u.replace(baseUrl, ""),
          ifMatch: (msg.params.request.headers["If-Match"] || "").slice(1, 9),
          contentType: (msg.params.request.headers["Content-Type"] || "").slice(0, 24),
        });
      }
    }
    if (msg.method === "Network.responseReceived") {
      const u = msg.params.response.url;
      if (u.includes("/api/")) {
        netLog.push({ at: Date.now(), dir: "←", status: msg.params.response.status, url: u.replace(baseUrl, "").replace(/^http:\/\/[^/]+/, "") });
      }
    }
    if (msg.method === "Network.loadingFailed") {
      netLog.push({ at: Date.now(), dir: "✗", error: msg.params.errorText, requestId: msg.params.requestId });
    }
    if (msg.method === "Runtime.consoleAPICalled" && (msg.params.type === "warning" || msg.params.type === "error")) {
      const text = (msg.params.args || []).map((a) => a.value || a.description || "").join(" ").slice(0, 300);
      consoleLog.push({ type: msg.params.type, text });
    }
    if (msg.method === "Runtime.exceptionThrown") {
      const d = msg.params.exceptionDetails;
      consoleLog.push({ type: "exception", text: `${d.text} ${d.exception?.description || ""}`.slice(0, 300) });
    }
  };

  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    ws.send(JSON.stringify({ id, method, params }));
  });

  await send("Page.enable");
  await send("Runtime.enable");
  await send("Network.enable");

  /** 在页面里求值；抛异常时把异常文本带回来，别静默变成 undefined。 */
  async function evaluate(expression) {
    const r = await send("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) {
      throw new Error(`${r.exceptionDetails.text} ${r.exceptionDetails.exception?.description || ""}`);
    }
    return r.result.value;
  }

  /** 轮询直到表达式为真，超时抛错。比 sleep 死等稳。 */
  async function waitFor(expression, { timeout = 15000, interval = 100, label = expression } = {}) {
    const end = Date.now() + timeout;
    let last;
    while (Date.now() < end) {
      try { last = await evaluate(expression); } catch (e) { last = `error: ${e.message}`; }
      if (last) return last;
      await sleep(interval);
    }
    throw new Error(`等待超时(${timeout}ms)：${label}（最后值 ${JSON.stringify(last)}）`);
  }

  /**
   * 快照：页面状态 + 草稿索引 + 弹窗可见性。
   * 只读，不改页面。字段名与 app.js 里的 state 保持一致。
   */
  async function snapshot() {
    return evaluate(`(() => {
      const idx = JSON.parse(localStorage.getItem("yunxi-save-drafts") || "[]");
      const drafts = idx.map((r) => {
        let rec = null;
        try { rec = JSON.parse(localStorage.getItem("yunxi-save-draft:" + r.id) || "null"); } catch (e) {}
        return {
          id: String(r.id).slice(-12),
          at: r.at,
          reason: r.reason,
          dirty: rec ? rec.dirty : null,
          rev: String(r.revision || "").slice(0, 8),
          bytes: rec ? JSON.stringify(rec.storage).length : 0,
        };
      });
      const modal = document.getElementById("conflictModal");
      const label = document.getElementById("saveStatusLabel");
      let s = {};
      try {
        s = {
          user: state.user,
          rev: String(state.storageRevision || "").slice(0, 8),
          dirty: state.storageDirty,
          status: state.saveStatus,
          conflictOpen: state.conflictOpen,
          pendingDrafts: state.pendingConflict ? (state.pendingConflict.drafts || []).length : 0,
        };
      } catch (e) { s = { stateError: String(e) }; }
      return {
        ...s,
        drafts,
        modalVisible: modal ? !modal.classList.contains("hidden") : null,
        message: ((document.getElementById("conflictMessage") || {}).textContent || "").slice(0, 100),
        label: (label ? label.textContent : "").trim(),
      };
    })()`);
  }

  return {
    wsUrl,
    netLog,
    consoleLog,
    evaluate,
    waitFor,
    snapshot,
    send,
    /** 打开页面并等到前端进入已登录状态（state.user 有值）。 */
    async goto(url = baseUrl + "/") {
      await send("Page.navigate", { url });
      /* 注意：app.js 顶层的 `state` 是 let/const 声明，**不是** window 的属性，
         所以只能用裸标识符 + typeof 判断，写 window.state 永远是 undefined。 */
      await waitFor("typeof state !== 'undefined' && !!state.user", { label: "前端完成登录/加载" });
    },
    /** 刷新并等到页面真正重新加载完成（用标记位判断，不依赖页面内部计数器）。 */
    async reload() {
      await evaluate("window.__e2eMark = 1");
      await send("Page.reload", { ignoreCache: false });
      await waitFor(
        "!window.__e2eMark && typeof state !== 'undefined' && !!state.user",
        { label: "刷新完成" },
      );
    },
    async click(selector) {
      return evaluate(`(() => {
        const el = document.querySelector(${JSON.stringify(selector)});
        if (!el) return "missing";
        el.click();
        return "ok";
      })()`);
    },
    async close() {
      try { ws.close(); } catch { /* ignore */ }
      chrome.kill();
      await sleep(200);
      try { fs.rmSync(profile, { recursive: true, force: true }); } catch { /* windows 上可能还占着 */ }
    },
  };
}
