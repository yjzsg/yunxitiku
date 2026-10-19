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

/** 最小题库：只有 1 个科目 / 1 章 / 5 道题，够前端进入答题页即可。
    （105 是「掌握校准」用来验证纯行为推断的题：永远不标信心，只反复错。） */
const fixtures = {
  courses: [{ id: 1, name: "测试科目", questionCount: 5, changedAt: "2026-01-01 00:00:00", available: true }],
  chapters: [{ id: 10, name: "第一章", questionCount: 5, parentId: 0 }],
  types: [{ id: 1, name: "单选题", questionCount: 5 }],
  questions: [101, 102, 103, 104, 105].map((id) => ({ id, typeId: 1, chapterId: 10, typeName: "单选题" })),
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
    /* 全站统计：stats 是「服务器上已有的比例」，statPosts 记每次上报（断言用）。 */
    stats: Object.create(null),
    statPosts: [],
    /* AI 解析：默认关闭（真实部署也要管理员显式开启）。
       aiCalls 只数真正「生成」的次数 —— 缓存命中不增加，用来验「同一道题全站只生成一次」。 */
    ai: { enabled: false, baseUrl: "https://api.openai.com/v1", model: "gpt-4o-mini", apiKeyMasked: "", dailyLimit: 100, maxTokens: 600, prompt: "你是讲解老师" },
    aiCalls: 0,
    aiCache: Object.create(null),
    aiFail: false,
    adminSession: false,
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
    /* 全站统计的控制面：GET 看当前统计与每次上报，POST {rows:[{q,attempts,wrong}]} 预置。 */
    if (p === "/__ctl/stats") {
      if (req.method === "POST") {
        const chunks = [];
        for await (const c of req) chunks.push(c);
        const payload = JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}");
        for (const row of payload.rows || []) {
          const id = Number(row?.q || 0);
          if (id <= 0) continue;
          state.stats[id] = {
            attempts: Number(row.attempts || 0),
            wrong: Number(row.wrong || 0),
            /* 选项分布：{A: 9, B: 9, C: 6}。质量队列的「分布过均」判定靠它。 */
            options: { ...(row.options || {}) },
            ackedAttempts: Number(row.ackedAttempts || 0),
          };
        }
      }
      return json(res, 200, { stats: state.stats, posts: state.statPosts });
    }
    if (p === "/__ctl/reset") {
      state.body = ""; state.saves = []; state.loads = []; state.requests = [];
      state.saveDelay = 0; state.loadDelay = 0; state.rejectPatch = 0;
      state.stats = Object.create(null); state.statPosts = [];
      state.ai.enabled = false; state.aiCalls = 0; state.aiCache = Object.create(null); state.aiFail = false;
      state.adminSession = false;
      return json(res, 200, { ok: true });
    }
    /* AI 控制面：POST {enabled?, fail?, resetCalls?} 改开关 / 模拟上游失败 / 清空调用计数与缓存。 */
    if (p === "/__ctl/ai") {
      if (req.method === "POST") {
        const chunks = [];
        for await (const c of req) chunks.push(c);
        const cfg = JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}");
        if (typeof cfg.enabled === "boolean") state.ai.enabled = cfg.enabled;
        if (typeof cfg.fail === "boolean") state.aiFail = cfg.fail;
        if (cfg.resetCalls) { state.aiCalls = 0; state.aiCache = Object.create(null); }
        if (typeof cfg.model === "string") state.ai.model = cfg.model;
        if (typeof cfg.dailyLimit === "number") state.ai.dailyLimit = cfg.dailyLimit;
      }
      return json(res, 200, { ai: state.ai, calls: state.aiCalls, cached: Object.keys(state.aiCache).length });
    }
    /* 后台会话开关：给「管理员配置界面」那类场景用，普通场景保持 false。 */
    if (p === "/__ctl/admin" && req.method === "POST") {
      const chunks = [];
      for await (const c of req) chunks.push(c);
      const cfg = JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}");
      state.adminSession = !!cfg.on;
      return json(res, 200, { admin: state.adminSession });
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
    if (p === "/api/auth/session") return json(res, 200, { ok: true, user: state.adminSession ? "admin" : USER, admin: state.adminSession, mustChangePassword: false });
    if (p === "/api/auth/login") return json(res, 200, { ok: true, user: state.adminSession ? "admin" : USER, token: "tok-e2e", admin: state.adminSession });
    if (p === "/api/auth/logout") return json(res, 200, { ok: true });
    if (p === "/api/health") return json(res, 200, { ok: true, sqlite: true });

    /* 全站答题统计：与真实服务端同一套语义 —— 只累计 (题号, 对错)，题数满 3 才对外给比例。 */
    if (p === "/api/stats/answers" && req.method === "POST") {
      const chunks = [];
      for await (const c of req) chunks.push(c);
      const payload = JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}");
      const items = Array.isArray(payload.items) ? payload.items : [];
      const accepted = [];
      for (const item of items) {
        const id = Number(item?.q || 0);
        if (id <= 0) continue;
        const row = state.stats[id] || (state.stats[id] = { attempts: 0, wrong: 0, options: Object.create(null), ackedAttempts: 0 });
        row.attempts += 1;
        if (!item.ok) row.wrong += 1;
        /* 与真实服务端对齐：只收单字母选项（多选答案是多字符，不参与分布）。 */
        const pick = String(item.c || "").trim().toUpperCase();
        if (pick.length === 1 && /^[A-Z]$/.test(pick)) {
          row.options[pick] = (row.options[pick] || 0) + 1;
        }
        accepted.push({ q: id, ok: !!item.ok, c: pick.length === 1 ? pick : undefined });
      }
      state.statPosts.push({ at: Date.now(), items: accepted, token: String(req.headers["x-session-token"] || "") });
      return json(res, 200, { ok: true, accepted: accepted.length });
    }
    if (p === "/api/stats/questions") {
      const ids = String(url.searchParams.get("ids") || "").split(",").map(Number).filter((id) => id > 0);
      const items = ids
        .map((id) => {
          const row = state.stats[id];
          return row && row.attempts >= 3
            ? { id, attempts: row.attempts, wrong: row.wrong, rate: row.wrong / row.attempts }
            : null;
        })
        .filter(Boolean);
      return json(res, 200, { ok: true, threshold: 3, items });
    }
    if (p === "/api/stats/course") {
      let attempts = 0;
      let wrong = 0;
      for (const row of Object.values(state.stats)) {
        attempts += row.attempts;
        wrong += row.wrong;
      }
      return json(res, 200, {
        ok: true,
        courseId: Number(url.searchParams.get("courseId") || 0),
        attempts,
        wrong,
        rate: attempts ? wrong / attempts : 0,
      });
    }

    if (p === "/api/admin/users") {
      if (!state.adminSession) return json(res, 401, { ok: false, error: "请先登录" });
      return json(res, 200, [{ name: USER, disabled: false, createdAt: "2026-01-01 00:00:00", lastLoginAt: "2026-01-02 08:00:00" }]);
    }
    if (p === "/api/admin/user-courses") return json(res, 200, { ok: true, assigned: null });

    /* 题库质量队列：阈值与判定抄自 StatsEndpoints.cs 的 QualityReasons ——
       改服务端口径时必须同步这里，否则这个回归就失真了。 */
    if (p === "/api/admin/quality") {
      if (!state.adminSession) return json(res, 401, { ok: false, error: "请先登录" });
      const minAttempts = 10;
      const flatMinAttempts = 20;
      const lowRate = 0.6;
      const flatTopShare = 0.55;
      const flatGap = 0.05;
      const recheck = 5;
      const limit = Math.max(1, Math.min(200, Number(url.searchParams.get("limit") || 60)));
      const items = Object.entries(state.stats)
        .map(([id, row]) => {
          const options = row.options || {};
          const total = Object.values(options).reduce((sum, n) => sum + Number(n || 0), 0);
          const ordered = Object.entries(options)
            .map(([key, count]) => [key, Number(count || 0)])
            .sort((a, b) => b[1] - a[1]);
          const topShare = total > 0 ? ordered[0][1] / total : 0;
          const secondShare = total > 0 && ordered.length > 1 ? ordered[1][1] / total : 0;
          const reasons = [];
          if (row.attempts >= minAttempts && row.wrong / Math.max(1, row.attempts) >= lowRate) reasons.push("low_rate");
          if (row.attempts >= flatMinAttempts && ordered.length >= 3
              && topShare <= flatTopShare && topShare - secondShare <= flatGap) reasons.push("flat_options");
          return {
            id: Number(id),
            courseId: 1,
            chapterId: 10,
            attempts: row.attempts,
            wrong: row.wrong,
            rate: Number((row.wrong / Math.max(1, row.attempts)).toFixed(4)),
            options,
            topOption: ordered.length ? ordered[0][0] : "",
            topShare: Number(topShare.toFixed(4)),
            reasons,
            ackedAttempts: row.ackedAttempts || 0,
          };
        })
        .filter((item) => item.reasons.length && item.attempts >= item.ackedAttempts + recheck)
        .sort((a, b) => b.rate - a.rate || b.attempts - a.attempts)
        .slice(0, limit);
      return json(res, 200, {
        ok: true,
        courseId: Number(url.searchParams.get("courseId") || 0),
        items,
        threshold: { minAttempts, flatMinAttempts, lowRate, flatTopShare, flatGap, recheckAttempts: recheck },
      });
    }
    if (p === "/api/admin/quality/ack" && req.method === "POST") {
      if (!state.adminSession) return json(res, 401, { ok: false, error: "请先登录" });
      const chunks = [];
      for await (const c of req) chunks.push(c);
      const payload = JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}");
      const id = Number(payload.id || 0);
      const row = state.stats[id];
      if (!row) return json(res, 404, { ok: false, error: "这道题还没有统计" });
      row.ackedAttempts = row.attempts;
      return json(res, 200, { ok: true, id, attempts: row.attempts });
    }
    /* 题库编辑的题面查询：质量队列用它批量补题干，这里只实现 ids 过滤这条路。 */
    if (p === "/api/admin/bank-editor/questions") {
      if (!state.adminSession) return json(res, 401, { ok: false, error: "请先登录" });
      const ids = String(url.searchParams.get("ids") || "").split(",").map(Number).filter((id) => id > 0);
      const list = (ids.length ? ids : fixtures.questions.map((q) => Number(q.id))).map((id) => ({
        id,
        courseId: 1,
        chapterId: 10,
        chapterName: "第一章",
        title: `题目 ${id}（e2e）`,
      }));
      return json(res, 200, { ok: true, items: list, count: list.length, limit: 500, localEditsError: null });
    }
    /* 单题详情：质量队列里的「编辑题库」会跳到题库编辑并拉这一条。
       只回编辑器要用的字段，够断言「点进去打开的是这道题」即可。 */
    if (p === "/api/admin/bank-editor/question") {
      if (!state.adminSession) return json(res, 401, { ok: false, error: "请先登录" });
      const id = Number(url.searchParams.get("id") || 0);
      if (id <= 0) return json(res, 400, { ok: false, error: "id 不合法" });
      return json(res, 200, {
        ok: true,
        id,
        courseId: 1,
        courseName: "测试科目",
        chapterId: 10,
        chapterName: "第一章",
        chapterPath: [{ id: 10, name: "第一章" }],
        type: "单选题",
        title: `题目 ${id}（e2e）`,
        question: "",
        answer: "A",
        description: "e2e 解析",
        updatedAt: "2026-01-01 00:00:00",
        localEdit: null,
      });
    }

    /* AI 解析：状态只回「开没开 + 模型名」，正文按题缓存。语义对齐真实服务端：
       未开启 503、上游失败 502、超额度 429、命中缓存不再生成。 */
    if (p === "/api/ai/status") {
      return json(res, 200, { ok: true, enabled: !!state.ai.enabled, model: state.ai.model, dailyLimit: state.ai.dailyLimit, used: state.aiCalls });
    }
    if (p === "/api/ai/explain") {
      if (req.method !== "POST") return json(res, 405, { ok: false, error: "请用 POST" });
      const chunks = [];
      for await (const c of req) chunks.push(c);
      const payload = JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}");
      const id = Number(payload.questionId || 0);
      if (!state.ai.enabled) return json(res, 503, { ok: false, error: "AI 解析未开启" });
      if (state.aiFail) return json(res, 502, { ok: false, error: "AI 服务暂时不可用" });
      if (id <= 0) return json(res, 400, { ok: false, error: "questionId 不合法" });
      const hit = state.aiCache[id];
      if (hit) return json(res, 200, { ok: true, text: hit.text, cached: true });
      if (state.ai.dailyLimit > 0 && state.aiCalls >= state.ai.dailyLimit) {
        return json(res, 429, { ok: false, error: "今日 AI 解析额度已用完" });
      }
      state.aiCalls += 1;
      const text = `第 ${state.aiCalls} 次生成的讲解。\n先看题干考点，再对比选项（mock）。`;
      state.aiCache[id] = { text, at: new Date().toISOString() };
      return json(res, 200, { ok: true, text, cached: false });
    }
    /* 管理端 AI 配置：读只回掩码；密钥留空表示不改。 */
    if (p === "/api/admin/ai") {
      if (!state.adminSession) return json(res, 401, { ok: false, error: "请先登录" });
      const config = () => ({
        enabled: !!state.ai.enabled,
        baseUrl: state.ai.baseUrl,
        model: state.ai.model,
        apiKeyMasked: state.ai.apiKeyMasked,
        dailyLimit: state.ai.dailyLimit,
        maxTokens: state.ai.maxTokens,
        prompt: state.ai.prompt,
        cached: Object.keys(state.aiCache).length,
      });
      if (req.method === "GET") return json(res, 200, { ok: true, config: config() });
      if (req.method === "PUT") {
        const chunks = [];
        for await (const c of req) chunks.push(c);
        const cfg = JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}");
        if (typeof cfg.enabled === "boolean") state.ai.enabled = cfg.enabled;
        if (typeof cfg.baseUrl === "string") state.ai.baseUrl = cfg.baseUrl;
        if (typeof cfg.model === "string") state.ai.model = cfg.model;
        if (typeof cfg.dailyLimit === "number") state.ai.dailyLimit = cfg.dailyLimit;
        if (typeof cfg.maxTokens === "number") state.ai.maxTokens = cfg.maxTokens;
        if (typeof cfg.prompt === "string") state.ai.prompt = cfg.prompt;
        if (typeof cfg.apiKey === "string" && cfg.apiKey) state.ai.apiKeyMasked = `sk-****${cfg.apiKey.slice(-4)}`;
        return json(res, 200, { ok: true, config: config() });
      }
      return json(res, 405, { ok: false, error: "不支持的方法" });
    }

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
        ok: true, user: state.adminSession ? "admin" : USER,
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
        out = { ok: true, user: state.adminSession ? "admin" : USER, revision: state.revision(), savedAt: new Date().toISOString() };
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
      return json(res, 200, { ok: true, user: state.adminSession ? "admin" : USER, revision: state.revision(), applied: ops.length, savedAt: new Date().toISOString() });
    }

    if (p === "/api/courses") return json(res, 200, fixtures.courses);
    if (p === "/api/chapters") return json(res, 200, fixtures.chapters);
    if (p === "/api/types") return json(res, 200, fixtures.types);
    if (p === "/api/questions") {
      /* 真后端支持 ids=102,103 这种指定题单（智能训练/专练都靠它，见 loadQuestions）。
         mock 必须同样过滤：否则「点进去只有这几道」的断言会把全部题放过去而白过。 */
      const ids = (url.searchParams.get("ids") || "").split(",").map((s) => Number(s)).filter(Boolean);
      if (!ids.length) return json(res, 200, fixtures.questions);
      const byId = new Map(fixtures.questions.map((q) => [Number(q.id), q]));
      return json(res, 200, ids.map((id) => byId.get(id)).filter(Boolean));
    }
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
