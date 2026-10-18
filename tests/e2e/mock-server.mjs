// 端到端回归用的假后端：静态文件直出 public/，用户数据接口复刻 Program.cs 的 revision 语义。
//
// 为什么要它：真实后端要 SQLite + 题库 + 账号，跑一遍太重；而这次要守的 bug 全在
// 「/api/user/load | /api/user/save 的 revision 前置条件 × 前端草稿状态机」这一小段，
// 所以只把这一段的语义抄准，其余接口给最小可用数据。
//
// 语义对齐点（改 Program.cs 的这些行为时必须同步改这里，否则回归会失真）：
//   · save 的前置条件：If-Match / If-None-Match / ?revision= / ?expectEmpty=1
//   · revision 是「服务器上这份 body 的 sha256」，不是自增号
//   · 冲突回 409 + { ok:false, code:"user_data_conflict" }
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import zlib from "node:zlib";
import { fileURLToPath } from "node:url";

const PUBLIC_DIR = fileURLToPath(new URL("../../public/", import.meta.url));
const USER = "tester";

const MIME = {
  ".html": "text/html; charset=utf-8",
  ".js": "application/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".webmanifest": "application/manifest+json",
};

const sha = (s) => crypto.createHash("sha256").update(Buffer.from(s, "utf8")).digest("hex");

/** 最小题库：只有 1 个科目 / 1 章 / 4 道题，够前端进入答题页即可。 */
const fixtures = {
  courses: [{ id: 1, name: "测试科目", questionCount: 4, changedAt: "2026-01-01 00:00:00", available: true }],
  chapters: [{ id: 10, name: "第一章", questionCount: 4, parentId: 0 }],
  types: [{ id: 1, name: "单选题", questionCount: 4 }],
  questions: [101, 102, 103, 104].map((id) => ({ id, typeId: 1, chapterId: 10, typeName: "单选题" })),
};

/**
 * @param {{ port?: number, saveDelay?: number }} [opts]
 * @returns {Promise<{ url: string, port: number, ctl: Function, close: Function }>}
 */
export async function startMockServer(opts = {}) {
  const state = {
    body: "",        // 服务器上存的那份用户数据（原样 JSON 字符串）
    revision: () => (state.body.length ? sha(state.body) : ""),
    saves: [],       // 每次 save 一行：时间 / 字节数 / 期望 revision / 当时 revision / 状态码
    loads: [],       // 每次 load 一行：取快照的时刻、快照 revision、回包时刻 —— 排查竞态顺序用
    requests: [],
    saveDelay: opts.saveDelay ?? 0,
    /* load 的延迟单独可控：只有让「保存先落地、load 后返回」，
       才能稳定复现「load 快照把 revision 拽回旧值」的竞态。 */
    loadDelay: 0,
    /* 接下来若干个 /api/user/patch 请求直接回 400（模拟服务端结构分叉拒绝）。
       客户端必须退回整包保存，不能把这次改动丢掉。 */
    rejectPatch: 0,
  };

  const json = (res, code, obj) => {
    res.writeHead(code, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
    res.end(JSON.stringify(obj));
  };

  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, "http://127.0.0.1");
    const p = url.pathname;
    state.requests.push(`${req.method} ${req.url}`);

    if (p.startsWith("/api/")) return handleApi(req, res, url, p);

    // 控制面：给驱动用，不暴露给被测页面
    if (p === "/__ctl/saves") return json(res, 200, state.saves);
    if (p === "/__ctl/loads") return json(res, 200, state.loads);
    if (p === "/__ctl/body") return json(res, 200, { revision: state.revision(), body: state.body });
    if (p === "/__ctl/reset") {
      state.body = ""; state.saves = []; state.loads = []; state.requests = [];
      state.saveDelay = 0; state.loadDelay = 0; state.rejectPatch = 0;
      return json(res, 200, { ok: true });
    }
    if (p === "/__ctl/config") {
      const chunks = [];
      for await (const c of req) chunks.push(c);
      const cfg = JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}");
      if (typeof cfg.saveDelay === "number") state.saveDelay = cfg.saveDelay;
      if (typeof cfg.loadDelay === "number") state.loadDelay = cfg.loadDelay;
      if (typeof cfg.rejectPatch === "number") state.rejectPatch = cfg.rejectPatch;
      return json(res, 200, { ok: true, saveDelay: state.saveDelay, loadDelay: state.loadDelay, rejectPatch: state.rejectPatch });
    }
    if (p === "/__ctl/seed") {
      const chunks = [];
      for await (const c of req) chunks.push(c);
      state.body = Buffer.concat(chunks).toString("utf8");
      return json(res, 200, { ok: true, revision: state.revision() });
    }

    // 静态文件
    const rel = p === "/" ? "index.html" : decodeURIComponent(p.slice(1));
    const file = path.join(PUBLIC_DIR, rel);
    if (!file.startsWith(PUBLIC_DIR)) { res.writeHead(403); return res.end("forbidden"); }
    try {
      const buf = fs.readFileSync(file);
      res.writeHead(200, { "Content-Type": MIME[path.extname(file)] || "application/octet-stream", "Cache-Control": "no-store" });
      return res.end(buf);
    } catch {
      res.writeHead(404, { "Content-Type": "text/plain" });
      return res.end("not found");
    }
  });

  async function handleApi(req, res, url, p) {
    if (p === "/api/auth/session") return json(res, 200, { ok: true, user: USER, admin: false, mustChangePassword: false });
    if (p === "/api/auth/login") return json(res, 200, { ok: true, user: USER, token: "tok-e2e" });
    if (p === "/api/auth/logout") return json(res, 200, { ok: true });
    if (p === "/api/health") return json(res, 200, { ok: true, sqlite: true });

    if (p === "/api/user/load") {
      /* 先按请求时刻取快照，再延迟回包 —— 这就是真实竞态的形态：
         服务端读的是旧内容，回包却晚于另一次保存的完成。 */
      const revision = state.revision();
      const row = { at: new Date().toISOString().slice(11, 23), rev: revision.slice(0, 8), returnedAt: "", conditional: String(req.headers["if-none-match"] || "") };
      state.loads.push(row);
      if (state.loadDelay > 0) await new Promise((r) => setTimeout(r, state.loadDelay));
      row.returnedAt = new Date().toISOString().slice(11, 23);
      /* 与真实服务端的条件请求对齐：If-None-Match 精确命中当前 revision → 304、不带 body。
         `*` 不算命中（那会把「探测有没有数据」变成「拿到空包」）。 */
      const inm = String(req.headers["if-none-match"] || "");
      res.setHeader("ETag", `"${revision}"`);
      if (revision && inm.split(",").some((t) => t.trim() === `"${revision}"`)) {
        row.notModified = true;
        res.writeHead(304);
        return res.end();
      }
      const snapshot = {
        ok: true, user: USER,
        data: state.body ? JSON.parse(state.body) : {},
        revision,
      };
      return json(res, 200, snapshot);
    }

    if (p === "/api/user/save") {
      const chunks = [];
      for await (const c of req) chunks.push(c);
      const wire = Buffer.concat(chunks);
      const encoding = String(req.headers["content-encoding"] || "").toLowerCase();
      /* 与真实服务端的 UseRequestDecompression 对齐：带 Content-Encoding 就先解压，
         之后一律当原始 JSON 看（revision 也按解压后的内容算）。 */
      let raw = wire;
      try {
        if (encoding === "gzip") raw = zlib.gunzipSync(wire);
        else if (encoding === "deflate") raw = zlib.inflateSync(wire);
        else if (encoding === "br") raw = zlib.brotliDecompressSync(wire);
      } catch {
        // 与真实服务端一致：解压失败是客户端请求体的问题，回 400 而不是把连接打断
        return json(res, 400, { ok: false, error: "请求体解压失败：Content-Encoding 与实际内容不符" });
      }
      const body = raw.toString("utf8") || "{}";
      if (state.saveDelay > 0) await new Promise((r) => setTimeout(r, state.saveDelay));

      const { expected, expectEmpty } = parsePrecondition(req.headers, url);
      const current = state.revision();
      let status = 200;
      let out;
      if (expectEmpty) {
        if (current.length) {
          status = 409;
          out = { ok: false, error: "用户数据已在其他页面更新，请刷新后重试", code: "user_data_conflict" };
        }
      } else if (expected.length && expected.toLowerCase() !== current.toLowerCase()) {
        status = 409;
        out = { ok: false, error: "用户数据已在其他页面更新，请刷新后重试", code: "user_data_conflict" };
      }
      if (status === 200) {
        state.body = body;
        out = { ok: true, user: USER, revision: state.revision(), savedAt: new Date().toISOString() };
      }
      state.saves.push({
        at: new Date().toISOString().slice(11, 23),
        bytes: Buffer.byteLength(body),
        // 线上实际传输的字节数（gzip 压缩后）与所用编码，用来断言上传侧真的压了
        wireBytes: wire.length,
        encoding,
        expected: expected.slice(0, 8),
        current: current.slice(0, 8),
        status,
        // 没带 Content-Type 的（sendBeacon 的 text/plain）标记出来，便于区分两条保存通道
        channel: req.headers["content-type"] ? "fetch" : "beacon",
      });
      return json(res, status, out);
    }

    if (p === "/api/user/patch") {
      /* 与真实 /api/user/patch 对齐：base 必须等于服务端当前 revision（否则 409），
         在同一段临界区里合并后写回，revision 仍按合并后全文算。
         这里用 JSON.stringify 重排落盘文本（真实服务端用 System.Text.Json），
         两者语义相同、字节不同 —— 断言一律走语义比较，不比较字符串。 */
      const chunks = [];
      for await (const c of req) chunks.push(c);
      const wire = Buffer.concat(chunks);
      const encoding = String(req.headers["content-encoding"] || "").toLowerCase();
      let raw = wire;
      try {
        if (encoding === "gzip") raw = zlib.gunzipSync(wire);
        else if (encoding === "deflate") raw = zlib.inflateSync(wire);
        else if (encoding === "br") raw = zlib.brotliDecompressSync(wire);
      } catch {
        return json(res, 400, { ok: false, error: "请求体解压失败：Content-Encoding 与实际内容不符" });
      }
      let payload;
      try {
        payload = JSON.parse(raw.toString("utf8") || "{}");
      } catch {
        return json(res, 400, { ok: false, error: "增量保存请求不是有效 JSON" });
      }
      if (state.saveDelay > 0) await new Promise((r) => setTimeout(r, state.saveDelay));
      const base = String(payload?.base || "");
      const ops = Array.isArray(payload?.ops) ? payload.ops : null;
      const current = state.revision();
      const row = {
        at: new Date().toISOString().slice(11, 23),
        kind: "patch",
        ops: ops ? ops.length : 0,
        wireBytes: wire.length,
        encoding,
        expected: base.slice(0, 8),
        current: current.slice(0, 8),
        status: 200,
        channel: "fetch",
      };
      if (state.rejectPatch > 0) {
        /* 真实服务端会在「结构分叉 / op 非法」时回 400 + user_data_patch_rejected。
           客户端必须把这次改动改走整包保存 —— 这里模拟的正是那条退路。 */
        state.rejectPatch -= 1;
        state.saves.push({ ...row, status: 400 });
        return json(res, 400, { ok: false, error: "模拟服务端拒绝增量（结构分叉）", code: "user_data_patch_rejected" });
      }
      if (!base || !ops) {
        state.saves.push({ ...row, status: 400 });
        return json(res, 400, { ok: false, error: "增量保存必须带 base 与 ops" });
      }
      if (base.toLowerCase() !== current.toLowerCase()) {
        state.saves.push({ ...row, status: 409 });
        return json(res, 409, { ok: false, error: "用户数据已在其他页面更新，请刷新后重试", code: "user_data_conflict" });
      }
      let merged;
      try {
        merged = applyPatchOps(state.body, ops);
      } catch (err) {
        state.saves.push({ ...row, status: 400 });
        return json(res, 400, { ok: false, error: String(err.message || err), code: "user_data_patch_rejected" });
      }
      state.body = merged;
      state.saves.push({ ...row, bytes: Buffer.byteLength(merged) });
      return json(res, 200, { ok: true, user: USER, revision: state.revision(), applied: ops.length, savedAt: new Date().toISOString() });
    }

    if (p === "/api/courses") return json(res, 200, fixtures.courses);
    if (p === "/api/chapters") return json(res, 200, fixtures.chapters);
    if (p === "/api/types") return json(res, 200, fixtures.types);
    if (p === "/api/questions") return json(res, 200, fixtures.questions);
    if (p === "/api/question-chapters") return json(res, 200, { pairs: [] });
    if (p === "/api/chapter-map") return json(res, 200, {});
    if (p === "/api/question") {
      return json(res, 200, {
        id: Number(url.searchParams.get("id")),
        stem: "题干（e2e）",
        options: [{ key: "A", text: "甲" }, { key: "B", text: "乙" }, { key: "C", text: "丙" }, { key: "D", text: "丁" }],
        answer: "A",
        analysis: "解析（e2e）",
        typeName: "单选题",
      });
    }
    if (p === "/api/push/config") return json(res, 200, { ok: true, enabled: false });
    if (p === "/api/push/vapid") return json(res, 200, { publicKey: "" });

    return json(res, 404, { ok: false, error: "mock 未实现：" + p });
  }

  await new Promise((resolve) => server.listen(opts.port ?? 0, "127.0.0.1", resolve));
  const { port } = server.address();
  return {
    url: `http://127.0.0.1:${port}`,
    port,
    state,
    ctl: async (p, init) => (await fetch(`http://127.0.0.1:${port}${p}`, init)).json(),
    close: () => new Promise((r) => server.close(r)),
  };
}

/** 前置条件解析：与 Program.cs 的 save 端点一致。 */
function parsePrecondition(headers, url) {
  const ifMatch = (headers["if-match"] || "").toString();
  const ifNone = (headers["if-none-match"] || "").toString();
  let expected = "";
  let expectEmpty = false;
  if (ifNone.includes("*")) expectEmpty = true;
  else if (ifMatch) {
    const tag = ifMatch.trim().replace(/^"|"$/g, "").trim();
    if (!tag.length) expectEmpty = true;
    else if (tag !== "*") expected = tag;
  } else if (url.searchParams.has("revision")) {
    const q = (url.searchParams.get("revision") || "").trim();
    if (q.length) expected = q; else expectEmpty = true;
  } else if (url.searchParams.has("expectEmpty")) {
    expectEmpty = url.searchParams.get("expectEmpty") === "1";
  }
  return { expected, expectEmpty };
}

/** 复刻 UserDataPatch.Apply：路径分段导航 → set / del / app。语义不对就抛，让端点回 400。 */
function applyPatchOps(currentBody, ops) {
  const root = currentBody.trim() ? JSON.parse(currentBody) : {};
  if (!root || typeof root !== "object" || Array.isArray(root)) throw new Error("当前用户数据不是 JSON 对象");
  for (const op of ops) {
    const segs = String(op?.p || "").split("/").slice(1);
    if (!segs.length || segs.some((s) => s.length === 0)) throw new Error(`ops 的 p 非法：${op?.p}`);
    let node = root;
    for (const seg of segs.slice(0, -1)) {
      if (node[seg] === undefined || node[seg] === null) node[seg] = {};
      if (typeof node[seg] !== "object" || Array.isArray(node[seg])) throw new Error(`增量路径 ${op.p} 的中间层不是对象`);
      node = node[seg];
    }
    const last = segs[segs.length - 1];
    if ("v" in op) node[last] = op.v;
    else if ("d" in op) delete node[last];
    else if (Array.isArray(op.app)) {
      if (!Array.isArray(node[last])) node[last] = [];
      node[last].push(...op.app);
    } else throw new Error(`ops 的 ${op.p} 缺少 v / d / app`);
  }
  return JSON.stringify(root);
}
