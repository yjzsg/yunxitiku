#!/usr/bin/env node
/**
 * 资源版本门禁（DL-06）
 *
 * 背景：public/index.html 里用 `app.js?v=20261013-sd4` / `style.css?v=20261013-cs4`
 * 这种**手工**版本号，而后端对带 `?v=` 的静态文件发一年 immutable 缓存
 * （src/YunxiTiku.Web/Program.cs 的静态文件策略）。改了 app.js 忘了抬版本号，
 * 正式用户就会一直吃旧文件；docs/VERSIONS.md 里记过一次真实事故。
 *
 * 做法：内容 sha256 与「版本号」必须**成对**登记在 deploy/asset-versions.lock.json 里。
 *   · 内容变了、版本号没抬   → 失败（这就是漏抬）
 *   · 内容变了、版本号也抬了 → 失败，但只需跑一次 `--update` 把新的一对登记进去
 *   · 内容没变、版本号变了   → 失败（清单过期）
 * 为什么用「清单」而不是自动 hash 文件名：index.html 引用的是 `/app.js?v=xxx`，
 * 文件名由 URL 决定，改成 hash 文件名要动后端静态路由与 SW 缓存键（不在本门禁范围）；
 * 清单方案零运行时改动、可本地复现、且 `--update` 会把「这一版到底发了什么」写进 git。
 *
 * 用法：
 *   node deploy/check-asset-versions.mjs            # 校验（CI 与本地发布前都跑这个）
 *   node deploy/check-asset-versions.mjs --update   # 按当前真实状态重写清单
 *   node deploy/check-asset-versions.mjs --root <仓库根> [--update]
 */
import { createHash } from "node:crypto";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const LOCK_REL = "deploy/asset-versions.lock.json";
const LOCK_SCHEMA = "yunxitiku-asset-versions/1";
const INDEX_REL = "public/index.html";

function usage(code = 0) {
  console.log(`用法: node deploy/check-asset-versions.mjs [--root <仓库根>] [--update]`);
  process.exit(code);
}

let root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
let update = false;
const argv = process.argv.slice(2);
for (let i = 0; i < argv.length; i++) {
  if (argv[i] === "--root") { root = path.resolve(argv[++i] || ""); }
  else if (argv[i] === "--update") { update = true; }
  else if (argv[i] === "--help" || argv[i] === "-h") { usage(0); }
  else { console.error(`未知参数: ${argv[i]}`); usage(1); }
}

const indexAbs = path.join(root, INDEX_REL);
if (!existsSync(indexAbs)) { console.error(`找不到 ${indexAbs}`); process.exit(1); }
const lockAbs = path.join(root, LOCK_REL);

/** 从 index.html 抓所有 `?v=` 引用
 *  ① href/src 属性（link/script）
 *  ② 引号里的其它 `?v=` 字符串 —— 覆盖 `navigator.serviceWorker.register("/sw.js?v=...")`
 *     这类内联脚本引用（sw.js 也是被缓存的静态资源，漏掉它等于漏了一半）
 *  同一个 (文件,版本) 只记一次。 */
function collectReferences(html) {
  const found = new Map();
  const push = (url) => {
    if (!url || !url.includes("?v=")) return;
    if (/^https?:\/\//i.test(url)) return;            // 外链不归本门禁管
    const [pathPart, query] = url.split("?");
    const params = new URLSearchParams(query || "");
    const version = (params.get("v") || "").trim();
    const rel = pathPart.replace(/^\//, "");          // `/app.js` -> `app.js`（相对 public/）
    const key = `${rel}@${version}`;
    if (found.has(key)) return;
    found.set(key, { url, rel: `public/${rel}`, version, abspath: path.join(root, "public", rel) });
  };
  for (const m of html.matchAll(/(?:href|src)\s*=\s*"([^"]+)"/gi)) push(m[1]);
  for (const m of html.matchAll(/["']([^"'\s`]*\?v=[^"'\s`]*)["']/gi)) push(m[1]);
  return [...found.values()];
}

function sha256(buf) { return createHash("sha256").update(buf).digest("hex"); }

const html = readFileSync(indexAbs, "utf8");
const refs = collectReferences(html);
const problems = [];
const notes = [];

if (!refs.length) problems.push(`在 ${INDEX_REL} 里没找到任何 ?v= 引用 —— 门禁会变成空转，拒绝通过`);

// 同一个文件被引用两次、版本号不一致 → 直接失败（浏览器会当成两个 URL）
const byRel = new Map();
for (const r of refs) {
  if (!byRel.has(r.rel)) byRel.set(r.rel, new Set());
  byRel.get(r.rel).add(r.version);
}
for (const [rel, versions] of byRel) {
  if (versions.size > 1) problems.push(`${rel} 在 ${INDEX_REL} 里被引用了多个版本号: ${[...versions].join(", ")}`);
}

// 实体必须存在 + 版本号不能空
const current = [];
for (const r of refs) {
  if (!r.version) { problems.push(`${r.url} 的 ?v= 是空的 —— 静态文件会走一年 immutable，必须带版本号`); continue; }
  if (!existsSync(r.abspath)) { problems.push(`${INDEX_REL} 引用了不存在的文件 ${r.rel}`); continue; }
  const buf = readFileSync(r.abspath);
  current.push({ path: r.rel, version: r.version, bytes: buf.length, sha256: sha256(buf) });
}

let lock = null;
if (!existsSync(lockAbs)) {
  problems.push(`缺少清单 ${LOCK_REL}（首次接入跑一次 --update）`);
} else {
  try { lock = JSON.parse(readFileSync(lockAbs, "utf8")); }
  catch (e) { problems.push(`${LOCK_REL} 不是合法 JSON: ${e.message}`); }
}
if (lock && lock.schema !== LOCK_SCHEMA) problems.push(`${LOCK_REL} 的 schema 不是 ${LOCK_SCHEMA}`);

const locked = new Map((lock?.assets || []).map((a) => [a.path, a]));
if (lock) {
  for (const c of current) {
    const l = locked.get(c.path);
    if (!l) { problems.push(`${c.path} 不在清单里（跑一次 --update 登记）`); continue; }
    const contentChanged = l.sha256 !== c.sha256;
    const versionChanged = l.version !== c.version;
    if (contentChanged && !versionChanged) {
      problems.push(`${c.path} 内容变了（sha256 ${l.sha256.slice(0, 12)} → ${c.sha256.slice(0, 12)}）但 ?v= 还是 ${c.version} —— `
        + `请抬版本号改 ${INDEX_REL}，再跑 --update`);
    } else if (contentChanged && versionChanged) {
      problems.push(`${c.path} 内容与版本号都变了（${l.version} → ${c.version}）—— 跑一次 --update 把这一对登记进清单`);
    } else if (!contentChanged && versionChanged) {
      problems.push(`${c.path} 只改了版本号（${l.version} → ${c.version}）而内容没变 —— 清单过期，跑一次 --update`);
    }
  }
  for (const l of locked.values()) {
    if (!current.some((c) => c.path === l.path)) {
      problems.push(`${l.path} 还在清单里，但 ${INDEX_REL} 已经不再引用它 —— 跑一次 --update 清掉`);
    }
  }
}

const label = update ? "更新清单" : "校验";
console.log(`资源版本门禁（${label}）  root=${root}`);
console.log(`  ${"文件".padEnd(20)} ${"版本".padEnd(20)} ${"sha256".padEnd(14)} 字节`);
for (const c of current.sort((a, b) => a.path.localeCompare(b.path))) {
  const l = locked.get(c.path);
  const mark = !l ? " + 新增" : (l.sha256 === c.sha256 ? (l.version === c.version ? " ✔" : " ⚠版本不一致") : " ⚠内容不一致");
  console.log(`  ${c.path.padEnd(20)} ${c.version.padEnd(20)} ${c.sha256.slice(0, 12).padEnd(14)} ${c.bytes}${mark}`);
}

if (update) {
  if (problems.some((p) => p.includes("不存在") || p.includes("被引用了多个版本号") || p.includes("?v= 是空的"))) {
    console.error("\n--update 拒绝写入：引用本身有问题，先修 index.html");
    for (const p of problems) console.error(`  ✗ ${p}`);
    process.exit(1);
  }
  const out = {
    schema: LOCK_SCHEMA,
    generatedAt: new Date().toISOString(),
    note: "内容 sha256 ↔ ?v= 版本号的成对记录；由 deploy/check-asset-versions.mjs --update 生成，勿手改。",
    assets: current.slice().sort((a, b) => a.path.localeCompare(b.path)),
  };
  writeFileSync(lockAbs, JSON.stringify(out, null, 1) + "\n", "utf8");
  console.log(`\n已写入 ${LOCK_REL}（${out.assets.length} 条）`);
  if (notes.length) notes.forEach((n) => console.log(`  提示: ${n}`));
  process.exit(0);
}

if (problems.length) {
  console.error("\n✗ 资源版本门禁未通过：");
  for (const p of problems) console.error(`  ✗ ${p}`);
  console.error("\n修好后重跑本脚本；确认无误再用 --update 更新清单。");
  process.exit(1);
}
console.log(`\n✔ 资源版本与内容一致（${current.length} 个引用，全部命中清单）`);
process.exit(0);
