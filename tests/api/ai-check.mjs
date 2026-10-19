// 验收真实服务端的 AI 解析：开关语义、密钥掩码、配额、上游失败的用户可见文案、题目归属校验。
//
// 打的是**真实的** C# 实现（不是 tests/e2e 里的 mock）。用法：
//   1. dotnet publish src/YunxiTiku.Web -c Release -o <临时目录>
//   2. 在 <临时目录> 下起服务：ASPNETCORE_URLS=http://127.0.0.1:5199 dotnet YunxiTiku.Web.dll
//   3. node tests/api/ai-check.mjs
// 可用环境变量覆盖：AI_BASE / AI_USER / AI_PASSWORD / AI_INIT_FILE。
//
// 为什么要有它：e2e 的 mock 抄的是契约，抄错了两边一起错；这个脚本验的是真实的
// 「未开启 → 503 ai_disabled」「上游连不上 → 502 ai_upstream」「次数用完 → 429 ai_quota」
// 这三条前端分支，以及管理端保存后密钥不会回显。
import fs from "node:fs";
import http from "node:http";

const BASE = process.env.AI_BASE || "http://127.0.0.1:5199";
const USER = process.env.AI_USER || "admin";
const PWD = process.env.AI_PASSWORD || "";
const INIT_FILE = process.env.AI_INIT_FILE || "";
let cookie = "";
let token = "";
let failures = 0;

function ok(name, cond, extra = "") {
  if (cond) console.log(`  ok  ${name}`);
  else {
    failures += 1;
    console.log(`  FAIL ${name}${extra ? " :: " + extra : ""}`);
  }
}

async function call(path, opts = {}) {
  const res = await fetch(BASE + path, {
    ...opts,
    headers: {
      ...(opts.headers || {}),
      ...(cookie ? { Cookie: cookie } : {}),
      ...(token ? { "X-Session-Token": token } : {}),
    },
  });
  const sc = res.headers.getSetCookie?.() || [];
  if (sc.length) cookie = sc.map((c) => c.split(";")[0]).join("; ");
  const text = await res.text();
  let json = null;
  try {
    json = JSON.parse(text);
  } catch {
    /* 非 JSON */
  }
  return { status: res.status, json, text };
}

const jsonPost = (path, body) =>
  call(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });

// ---- 0. 未登录必须挡住 ----
const anon = await call("/api/ai/status");
ok("未登录 /api/ai/status 回 401", anon.status === 401, `status=${anon.status} ${anon.text.slice(0, 120)}`);
const anonExplain = await jsonPost("/api/ai/explain", { questionId: 1 });
ok("未登录 /api/ai/explain 回 401", anonExplain.status === 401, `status=${anonExplain.status}`);

// ---- 1. 登录 ----
let login = await jsonPost("/api/auth/login", { user: USER, password: PWD });
if (login.status !== 200 && fs.existsSync(INIT_FILE)) {
  const pwd = fs.readFileSync(INIT_FILE, "utf8").match(/[0-9A-F]{12}/)?.[0] || "";
  if (pwd) login = await jsonPost("/api/auth/login", { user: USER, password: pwd });
}
ok("login", login.status === 200, `status=${login.status} ${login.text.slice(0, 120)}`);
token = login.json?.token || "";
if (login.json?.mustChangePassword === true) {
  const ch = await jsonPost("/api/auth/change-password", {
    oldPassword: login.json?.password || "",
    newPassword: PWD,
  });
  ok("change-password", ch.status === 200, ch.text.slice(0, 120));
  if (ch.status === 200) {
    login = await jsonPost("/api/auth/login", { user: USER, password: PWD });
    token = login.json?.token || "";
  }
}

// ---- 2. 收尾时把开关关掉，所以先记下拿一条真实题目 ----
let questionId = 0;
let questionId2 = 0;
const courses = await call("/api/courses?available=1&q=");
/* 这两个端点回的是裸数组（GetCourses/GetQuestions 直接序列化），不是 {courses:[]}。 */
const courseList = Array.isArray(courses.json) ? courses.json : (courses.json?.courses || []);
const courseId = Number(courseList?.[0]?.id || 0);
if (courseId > 0) {
  const qs = await call(`/api/questions?courseId=${courseId}&limit=2`);
  const list = Array.isArray(qs.json) ? qs.json : (qs.json?.questions || []);
  questionId = Number(list?.[0]?.id || 0);
  questionId2 = Number(list?.[1]?.id || 0) || questionId;
}
ok("能从真实题库取到一道题", questionId > 0, `courseId=${courseId} questionId=${questionId}`);
if (questionId2 === questionId) console.log("  warn 题库只有一道题，配额用例会命中缓存");

// ---- 3. 未开启：状态与 explain 都要说清楚 ----
const statusOff = await call("/api/ai/status");
ok("/api/ai/status 未开启时 enabled=false", statusOff.status === 200 && statusOff.json?.enabled === false,
  `status=${statusOff.status} body=${statusOff.text.slice(0, 120)}`);
const off = await jsonPost("/api/ai/explain", { questionId: questionId || 1 });
ok("未开启时 explain 回 503 ai_disabled", off.status === 503 && off.json?.code === "ai_disabled",
  `status=${off.status} body=${off.text.slice(0, 160)}`);

// ---- 4. 管理端配置：参数校验与保存 ----
const cfg0 = await call("/api/admin/ai-config");
ok("admin 能读配置", cfg0.status === 200 && cfg0.json?.ok === true, `status=${cfg0.status} ${cfg0.text.slice(0, 120)}`);

const badUrl = await jsonPost("/api/admin/ai-config", { baseUrl: "ftp://x", enabled: false });
ok("BaseUrl 协议不对回 400", badUrl.status === 400, `status=${badUrl.status} ${badUrl.text.slice(0, 120)}`);

const noKey = await jsonPost("/api/admin/ai-config", { enabled: true, baseUrl: "http://127.0.0.1:9", model: "test-model" });
ok("开启但没密钥回 400", noKey.status === 400, `status=${noKey.status} ${noKey.text.slice(0, 160)}`);

/* 指向一个必然连不上的本地端口：用来验上游失败的用户可见文案，不产生外部流量。 */
const saved = await jsonPost("/api/admin/ai-config", {
  enabled: true,
  baseUrl: "http://127.0.0.1:9",
  model: "test-model",
  apiKey: "sk-e2e-tail1234",
  dailyLimit: 1000,
  maxTokens: 256,
});
ok("保存配置成功", saved.status === 200 && saved.json?.ok === true && saved.json?.hasKey === true,
  `status=${saved.status} ${saved.text.slice(0, 160)}`);

const cfg1 = await call("/api/admin/ai-config");
ok("密钥只回掩码，不回明文", cfg1.json?.keyPreview === "****1234" && !cfg1.text.includes("sk-e2e-tail1234"),
  `keyPreview=${cfg1.json?.keyPreview}`);

const statusOn = await call("/api/ai/status");
ok("开启后 status.enabled=true 且带模型名", statusOn.json?.enabled === true && statusOn.json?.model === "test-model",
  statusOn.text.slice(0, 120));

// ---- 5. explain 的边界 ----
const missing = await jsonPost("/api/ai/explain", {});
ok("缺 questionId 回 400", missing.status === 400, `status=${missing.status}`);

const ghost = await jsonPost("/api/ai/explain", { questionId: 99999999 });
ok("不存在的题号回 404", ghost.status === 404, `status=${ghost.status} ${ghost.text.slice(0, 120)}`);

const broken = await call("/api/ai/explain", {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: "{not json",
});
ok("请求体不是 JSON 回 400", broken.status === 400, `status=${broken.status}`);

/* 上游连不上：必须是 502 + ai_upstream + 一句人话，不能把连接异常原样甩给用户。
   带 force 绕过缓存 —— 缓存库是持久化的，重跑脚本时这道题可能已经有内容了。 */
const upstream = await jsonPost("/api/ai/explain", { questionId, force: true });
ok("上游连不上回 502 ai_upstream", upstream.status === 502 && upstream.json?.code === "ai_upstream",
  `status=${upstream.status} body=${upstream.text.slice(0, 200)}`);
ok("上游失败文案不含异常细节", (upstream.json?.error || "").includes("暂时不可用") && !(upstream.json?.error || "").includes("refused"),
  upstream.json?.error || "");

// ---- 6. 假上游：验真实成功链路与「同题只向上游要一次」 ----
/* 起一个最小的 OpenAI 兼容服务：只回 chat/completions。
   这样缓存、Bearer 头、提示词拼装走的都是真实代码路径，不打外网也不花钱。 */
const upstreamHits = [];
const fakeUpstream = http.createServer((req, res) => {
  let body = "";
  req.on("data", (c) => (body += c));
  req.on("end", () => {
    upstreamHits.push({ url: req.url, auth: req.headers.authorization || "", body });
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ choices: [{ message: { content: `第 ${upstreamHits.length} 次生成` } }] }));
  });
});
await new Promise((resolve) => fakeUpstream.listen(0, "127.0.0.1", resolve));
const fakePort = fakeUpstream.address().port;
const wired = await jsonPost("/api/admin/ai-config", {
  enabled: true,
  baseUrl: `http://127.0.0.1:${fakePort}/v1`,
  model: "test-model",
  apiKey: "sk-e2e-tail1234",
  dailyLimit: 20,
  maxTokens: 256,
});
ok("可指向本地假上游", wired.status === 200 && wired.json?.ok === true, `status=${wired.status} ${wired.text.slice(0, 120)}`);

/* 第一次带 force：缓存库是持久化的，重跑脚本时不该被上一轮的内容影响。 */
const gen1 = await jsonPost("/api/ai/explain", { questionId, force: true });
ok("第一次生成成功（强制）", gen1.status === 200 && gen1.json?.ok === true && gen1.json?.cached === false,
  `status=${gen1.status} body=${gen1.text.slice(0, 200)}`);
ok("上游收到 Bearer 密钥和 chat/completions", upstreamHits[0]?.url === "/v1/chat/completions" &&
  upstreamHits[0]?.auth === "Bearer sk-e2e-tail1234", JSON.stringify(upstreamHits[0] || {}).slice(0, 160));
ok("提示词里带题干", (upstreamHits[0]?.body || "").length > 100, `bodyLen=${(upstreamHits[0]?.body || "").length}`);

const gen2 = await jsonPost("/api/ai/explain", { questionId });
ok("同题第二次直接回缓存", gen2.status === 200 && gen2.json?.cached === true && gen2.json?.text === gen1.json?.text,
  `status=${gen2.status} body=${gen2.text.slice(0, 160)}`);
ok("缓存命中不再打上游", upstreamHits.length === 1, `上游被调 ${upstreamHits.length} 次`);

const forced = await jsonPost("/api/ai/explain", { questionId, force: true });
ok("force 强制重生成并覆盖缓存", forced.status === 200 && forced.json?.cached === false &&
  forced.json?.text === `第 2 次生成` && upstreamHits.length === 2,
  `status=${forced.status} text=${forced.json?.text} hits=${upstreamHits.length}`);

const cfgAfter = await call("/api/admin/ai-config");
ok("缓存统计随生成增长", Number(cfgAfter.json?.cacheEntries) >= 1 && Number(cfgAfter.json?.cacheQuestions) >= 1,
  `entries=${cfgAfter.json?.cacheEntries} questions=${cfgAfter.json?.cacheQuestions}`);

fakeUpstream.close();

// ---- 7. 配额：上限设 1，连打两次，第二次必须 429 ----
/* 0 是「不限制」（TryTakeQuota 里 dailyLimit<=0 直接放行），不能拿 0 当耗尽。
   前面的上游失败已经消耗过次数，所以这里连打到出现 429 为止，最多三次。 */
const one = await jsonPost("/api/admin/ai-config", { dailyLimit: 1, enabled: true });
ok("可把每日上限改成 1", one.status === 200 && one.json?.ok === true, `status=${one.status} ${one.text.slice(0, 120)}`);
let quota = null;
for (let i = 0; i < 3 && !quota; i += 1) {
  /* 用第二道没缓存过的题：缓存命中发生在扣配额之前，拿缓存题永远打不出 429。 */
  const r = await jsonPost("/api/ai/explain", { questionId: questionId2 });
  if (r.status === 429) quota = r;
}
ok("上限用尽回 429 ai_quota", quota?.json?.code === "ai_quota",
  quota ? `status=${quota.status} body=${quota.text.slice(0, 160)}` : "连打三次都没出现 429");

// ---- 7. 收尾：关掉开关并清掉密钥，别把测试配置留在库里 ----
const cleanup = await jsonPost("/api/admin/ai-config", { enabled: false, clearKey: true, dailyLimit: 20 });
ok("收尾：关闭并清空密钥", cleanup.status === 200 && cleanup.json?.enabled === false && cleanup.json?.hasKey === false,
  `status=${cleanup.status} ${cleanup.text.slice(0, 160)}`);
const statusEnd = await call("/api/ai/status");
ok("收尾后 status.enabled=false", statusEnd.json?.enabled === false, statusEnd.text.slice(0, 120));

console.log(failures === 0 ? "\nALL PASS" : `\n${failures} FAILED`);
process.exit(failures === 0 ? 0 : 1);
