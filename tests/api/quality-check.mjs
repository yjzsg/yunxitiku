// 验收真实服务端的「题库质量闭环」（tests/e2e 的 mock 只证明前端接线，这里证明 SQL 与阈值本身）：
//   · 错误率 ≥60% 且作答 ≥10 次 → low_rate
//   · 选项分布过均（≥20 次、≥3 项、top ≤55%、top-second ≤5%）→ flat_options
//   · 复核后移出，攒够 5 次新作答才重新入队
//   · 非管理员 403、无统计的题复核 404、非法 id 400
//
// 用法：
//   1. dotnet publish src/YunxiTiku.Web -c Release -o <临时目录>
//   2. 删掉临时目录旁边的 data/question-stats.db（否则累计次数会让断言对不上）
//   3. 在 <临时目录> 下起服务：ASPNETCORE_URLS=http://127.0.0.1:5199 dotnet YunxiTiku.Web.dll
//   4. Q_PASSWORD=<管理员密码> node tests/api/quality-check.mjs
// 可用 Q_BASE / Q_USER / Q_PASSWORD 覆盖。
//
// 注意：脚本会往统计库里写真实计数（本地临时库，别对着线上跑）。
const BASE = process.env.Q_BASE || "http://127.0.0.1:5199";
const USER = process.env.Q_USER || "admin";
const PWD = process.env.Q_PASSWORD || "";
if (!PWD) {
  console.error("需要管理员密码：Q_PASSWORD=... node tests/api/quality-check.mjs");
  process.exit(2);
}
let token = "";
let cookie = "";
let failures = 0;

const ok = (name, cond, extra = "") => {
  if (cond) console.log(`  ok  ${name}`);
  else { failures += 1; console.log(`  FAIL ${name}${extra ? " :: " + extra : ""}`); }
};

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
  try { json = JSON.parse(text); } catch { /* 非 JSON */ }
  return { status: res.status, json, text };
}
const post = (path, body) => call(path, {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify(body),
});
const answer = (id, okFlag, choice) => post("/api/stats/answers", {
  items: [{ q: id, ok: okFlag, ...(choice ? { c: choice } : {}) }],
});

const anon = await call("/api/admin/quality");
ok("未登录不能读质量队列", anon.status === 401 || anon.status === 403, `status=${anon.status}`);

let login = await post("/api/auth/login", { user: USER, password: PWD });
ok("管理员登录", login.status === 200, `status=${login.status} ${login.text.slice(0, 100)}`);
token = login.json?.token || "";
if (login.json?.mustChangePassword) {
  const changed = await post("/api/auth/change-password", {
    oldPassword: login.json?.password || "",
    newPassword: PWD,
  });
  ok("首次登录改密", changed.status === 200, changed.text.slice(0, 100));
  login = await post("/api/auth/login", { user: USER, password: PWD });
  token = login.json?.token || "";
}

const courses = await call("/api/courses?available=1");
const courseId = Number(courses.json?.[0]?.id || 0);
const list = await call(`/api/questions?courseId=${courseId}&limit=5`);
const ids = (list.json?.items || list.json || []).map((q) => Number(q.id)).filter(Boolean);
ok("取到真实题目", courseId > 0 && ids.length >= 2, `course=${courseId} ids=${ids.join(",")}`);
const [qLow, qFlat] = ids;

// 第一类：12 次作答、9 次答错（75%）
for (let i = 0; i < 12; i += 1) {
  const r = await answer(qLow, i >= 9, "ABCD"[i % 4]);
  if (r.status !== 200) { ok("上报统计", false, `status=${r.status} ${r.text.slice(0, 100)}`); break; }
}
let quality = await call(`/api/admin/quality?courseId=${courseId}&limit=60`);
let row = (quality.json?.items || []).find((item) => Number(item.id) === qLow);
ok("低正确率题入队", !!row && row.reasons.includes("low_rate"), `reasons=${row?.reasons}`);
ok("统计口径带作答数与错误率", Number(row?.attempts) === 12 && Math.abs(Number(row?.rate) - 0.75) < 1e-6,
  `attempts=${row?.attempts} rate=${row?.rate}`);
ok("返回选项分布", Number(row?.options?.A || 0) === 3, `options=${JSON.stringify(row?.options)}`);

const acked = await post(`/api/admin/quality/ack?user=${encodeURIComponent(USER)}`, { id: qLow });
ok("复核成功并记下样本量", acked.status === 200 && Number(acked.json?.attempts) === 12, `${acked.status} ${acked.text.slice(0, 100)}`);
quality = await call(`/api/admin/quality?courseId=${courseId}&limit=60`);
ok("复核后立即移出队列", !(quality.json?.items || []).some((item) => Number(item.id) === qLow));

for (let i = 0; i < 5; i += 1) await answer(qLow, false, "A");
quality = await call(`/api/admin/quality?courseId=${courseId}&limit=60`);
row = (quality.json?.items || []).find((item) => Number(item.id) === qLow);
ok("攒够 5 次新作答后重新入队", !!row && Number(row.attempts) === 17, `attempts=${row?.attempts}`);

// 第二类：24 次作答，选项 A9/B9/C6（最高两项各 37.5%、相差 0）
const picks = [...Array(9).fill("A"), ...Array(9).fill("B"), ...Array(6).fill("C")];
for (const [index, choice] of picks.entries()) {
  await post("/api/stats/answers", { items: [{ q: qFlat, ok: index >= 8, c: choice }] });
}
quality = await call(`/api/admin/quality?courseId=${courseId}&limit=60`);
row = (quality.json?.items || []).find((item) => Number(item.id) === qFlat);
ok("选项分布过均入队", !!row && row.reasons.includes("flat_options"), `reasons=${row?.reasons}`);
ok("选项分布计数正确", Number(row?.options?.A) === 9 && Number(row?.options?.B) === 9 && Number(row?.options?.C) === 6,
  `options=${JSON.stringify(row?.options)}`);

const badAck = await post(`/api/admin/quality/ack?user=${encodeURIComponent(USER)}`, { id: 99999999 });
ok("复核没有统计的题回 404", badAck.status === 404, `status=${badAck.status}`);
const badBody = await post(`/api/admin/quality/ack?user=${encodeURIComponent(USER)}`, { id: "x" });
ok("复核非法 id 回 400", badBody.status === 400, `status=${badBody.status}`);

console.log(failures ? `\n${failures} 条失败` : "\nALL PASS");
process.exit(failures ? 1 : 0);
