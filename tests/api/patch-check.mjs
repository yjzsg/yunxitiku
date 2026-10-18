// 验收真实服务端的增量保存：gzip 上传、patch 合并、400/409 语义、revision = 落盘全文 SHA256。
//
// 打的是**真实的** C# 实现（不是 tests/e2e 里的 mock）。用法：
//   1. dotnet publish src/YunxiTiku.Web -c Release -o <临时目录>
//   2. 在 <临时目录> 下起服务：ASPNETCORE_URLS=http://127.0.0.1:5199 dotnet YunxiTiku.Web.dll
//   3. node tests/api/patch-check.mjs
// 可用环境变量覆盖：PATCH_BASE / PATCH_USER / PATCH_PASSWORD / PATCH_INIT_FILE。
// 为什么要有它：e2e 的 mock 抄的是契约，抄错了两边一起错；这个脚本直接验真实合并与冲突语义。
import fs from "node:fs";
import crypto from "node:crypto";
import zlib from "node:zlib";

const BASE = process.env.PATCH_BASE || "http://127.0.0.1:5199";
const INIT_FILE = process.env.PATCH_INIT_FILE || "";
const USER = process.env.PATCH_USER || "admin";
const PWD = process.env.PATCH_PASSWORD || "";
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
    headers: { ...(opts.headers || {}), ...(cookie ? { Cookie: cookie } : {}), ...(token ? { "X-Session-Token": token } : {}) },
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
  return { status: res.status, json, text, headers: res.headers };
}

const sha = (s) => crypto.createHash("sha256").update(s, "utf8").digest("hex");

// ---- 登录 ----
let login = await call("/api/auth/login", {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ user: USER, password: PWD }),
});
if (login.status !== 200 && fs.existsSync(INIT_FILE)) {
  const pwd = fs.readFileSync(INIT_FILE, "utf8").match(/[0-9A-F]{12}/)[0];
  login = await call("/api/auth/login", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ user: USER, password: PWD }),
  });
}
ok("login", login.status === 200, `status=${login.status} ${login.text.slice(0, 120)}`);
token = login.json?.token || "";
if (login.json?.mustChangePassword === true) {
  const ch = await call("/api/auth/change-password", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ oldPassword: login.json?.password || "", newPassword: PWD }),
  });
  ok("change-password", ch.status === 200, ch.text.slice(0, 120));
}

// ---- 读取当前数据 ----
let load = await call("/api/user/load");
ok("load", load.status === 200, `status=${load.status}`);
let data = load.json?.data || {};
let revision = load.json?.revision || "";
console.log(`  基线 revision=${revision.slice(0, 8)} 顶层键=${Object.keys(data).length}`);

// ---- 1. 整包保存走 gzip ----
data.settings = data.settings && typeof data.settings === "object" ? data.settings : {};
data.settings.verifyFull = "1";
/* 测试账号平时是空的；这里塞够 1MB 才能验证「gzip 到底省了多少」和真实上传路径。 */
if (JSON.stringify(data).length < 800000) {
  data.notes = Array.isArray(data.notes) ? data.notes : [];
  while (JSON.stringify(data).length < 850000) {
    data.notes.push({ id: "filler-" + data.notes.length, text: "填充数据，用于验证 gzip 压缩比。" + "x".repeat(60), at: 1700000000000 + data.notes.length });
  }
}
const fullText = JSON.stringify(data);
const gz = zlib.gzipSync(Buffer.from(fullText, "utf8"));
const saveRes = await call("/api/user/save", {
  method: "POST",
  headers: { "Content-Type": "application/json", "Content-Encoding": "gzip", ...(revision ? { "If-Match": revision } : { "If-None-Match": "*" }) },
  body: gz,
});
ok("gzip 整包保存 200", saveRes.status === 200, `status=${saveRes.status} ${saveRes.text.slice(0, 160)}`);
const r1 = saveRes.json?.revision || "";
ok("revision = 解压后全文 SHA256", r1 === sha(fullText), `server=${r1.slice(0, 12)} local=${sha(fullText).slice(0, 12)}`);
ok("压缩确实生效", gz.length < fullText.length / 3, `${fullText.length} -> ${gz.length}`);

// ---- 2. patch：set ----
const patch1 = await call("/api/user/patch", {
  method: "POST",
  headers: { "Content-Type": "application/json", "If-Match": r1 },
  body: JSON.stringify({ base: r1, ops: [{ p: "/settings/verifyPatch", v: "v1" }] }),
});
ok("patch set 200", patch1.status === 200, `status=${patch1.status} ${patch1.text.slice(0, 160)}`);
const r2 = patch1.json?.revision || "";
ok("patch applied=1", patch1.json?.applied === 1, JSON.stringify(patch1.json));

// ---- 3. 合并结果落盘了吗 ----
load = await call("/api/user/load");
ok("load 后 revision 前进", load.json?.revision === r2, `${load.json?.revision?.slice(0, 8)} vs ${r2.slice(0, 8)}`);
ok("set 生效", load.json?.data?.settings?.verifyPatch === "v1", JSON.stringify(load.json?.data?.settings).slice(0, 120));
ok("原有字段没丢", load.json?.data?.settings?.verifyFull === "1");

// ---- 4. 追加数组 ----
const notesLen = Array.isArray(load.json?.data?.notes) ? load.json.data.notes.length : 0;
const patch2 = await call("/api/user/patch", {
  method: "POST",
  headers: { "Content-Type": "application/json", "If-Match": r2 },
  body: JSON.stringify({ base: r2, ops: [{ p: "/notes", app: [{ id: "verify-note", text: "x", at: Date.now() }] }] }),
});
const r3 = patch2.json?.revision || "";
ok("patch app 200", patch2.status === 200, `status=${patch2.status} ${patch2.text.slice(0, 160)}`);
load = await call("/api/user/load");
const notesAfter = Array.isArray(load.json?.data?.notes) ? load.json.data.notes.length : 0;
ok("app 后数组 +1", notesAfter === notesLen + 1, `${notesLen} -> ${notesAfter}`);
ok("revision 与内容自洽", load.json?.revision === sha(JSON.stringify(load.json?.data)), "");

// ---- 5. 删除字段 ----
const patch3 = await call("/api/user/patch", {
  method: "POST",
  headers: { "Content-Type": "application/json", "If-Match": r3 },
  body: JSON.stringify({ base: r3, ops: [{ p: "/settings/verifyPatch", d: 1 }] }),
});
ok("patch del 200", patch3.status === 200, `status=${patch3.status} ${patch3.text.slice(0, 160)}`);
load = await call("/api/user/load");
ok("del 后字段消失", load.json?.data?.settings?.verifyPatch === undefined, JSON.stringify(load.json?.data?.settings).slice(0, 120));

// ---- 6. 过期 base 必须 409 ----
const stale = await call("/api/user/patch", {
  method: "POST",
  headers: { "Content-Type": "application/json", "If-Match": r1 },
  body: JSON.stringify({ base: r1, ops: [{ p: "/settings/shouldNotApply", v: 1 }] }),
});
ok("过期 base 409", stale.status === 409, `status=${stale.status}`);
ok("冲突码一致", stale.json?.code === "user_data_conflict", JSON.stringify(stale.json).slice(0, 160));
load = await call("/api/user/load");
ok("409 未写入", load.json?.data?.settings?.shouldNotApply === undefined);

// ---- 7. 非法 op 必须 400，而不是把数据写坏 ----
const current = load.json?.revision;
const badOps = [
  { name: "穿过标量继续下钻", body: { base: current, ops: [{ p: "/settings/verifyFull/x", v: 1 }] } },
  { name: "ops 项不是对象", body: { base: current, ops: ["nope"] } },
  { name: "op 数量超限", body: { base: current, ops: Array.from({ length: 2001 }, (_, i) => ({ p: "/settings/k" + i, v: i })) } },
  { name: "空路径", body: { base: current, ops: [{ p: "", v: 1 }] } },
];
for (const c of badOps) {
  const r = await call("/api/user/patch", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(c.body) });
  ok(`400 · ${c.name}`, r.status === 400 && r.json?.code === "user_data_patch_rejected", `status=${r.status} ${JSON.stringify(r.json).slice(0, 160)}`);
}
load = await call("/api/user/load");
ok("400 未改数据", load.json?.revision === current);

// ---- 8. 整包保存过期 base 也是 409 ----
const staleSave = await call("/api/user/save", {
  method: "POST",
  headers: { "Content-Type": "application/json", "If-Match": r1 },
  body: JSON.stringify(load.json?.data || {}),
});
ok("整包过期 base 409", staleSave.status === 409, `status=${staleSave.status}`);

// ---- 9. 明文整包仍可用（老客户端） ----
const plain = await call("/api/user/save", {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify(load.json?.data || {}),
});
ok("明文整包保存 200", plain.status === 200, `status=${plain.status}`);

// ---- 10. 条件请求：ETag / 304（省掉整包 ~1MB 的那条路） ----
const et1 = await call(`/api/user/load?user=${encodeURIComponent(USER)}`);
const tag = et1.headers.get("etag") || "";
ok("load 带 ETag", /^"[0-9a-f]{16,}"$/.test(tag), `etag=${tag}`);
ok("ETag 就是 revision", tag.replace(/"/g, "") === String(et1.json?.revision || ""), `etag=${tag} rev=${et1.json?.revision}`);
const et2 = await call(`/api/user/load?user=${encodeURIComponent(USER)}`, { headers: { "If-None-Match": tag } });
ok("If-None-Match 命中回 304", et2.status === 304, `status=${et2.status}`);
ok("304 不带 body", et2.text.length === 0, `body=${et2.text.slice(0, 60)}`);
const et3 = await call(`/api/user/load?user=${encodeURIComponent(USER)}`, { headers: { "If-None-Match": '"deadbeef"' } });
ok("校验器不匹配回 200 全量", et3.status === 200 && !!et3.json?.data, `status=${et3.status}`);
/* `*` 是「只要存在就别给我 body」的标准写法。这里必须**不**命中：
   否则「探测这个账号有没有数据」会拿到 304 空包，前端会把空数据当成基线。 */
const et4 = await call(`/api/user/load?user=${encodeURIComponent(USER)}`, { headers: { "If-None-Match": "*" } });
ok("If-None-Match: * 不算命中", et4.status === 200 && !!et4.json?.data, `status=${et4.status}`);

console.log(failures === 0 ? "ALL PASS" : `${failures} FAILURES`);
process.exit(failures === 0 ? 0 : 1);
