#!/usr/bin/env node
// 端到端回归入口：
//   node tests/e2e/run.mjs                # 跑全部场景
//   node tests/e2e/run.mjs --only=stale-draft-no-popup
//   node tests/e2e/run.mjs --list
//   node tests/e2e/run.mjs --debug        # 打印浏览器 stderr
// 依赖：Node ≥ 22（内置 WebSocket）+ 本机 Chrome/Edge（或用 CHROME_PATH 指定）。
// 退出码：0 全过；1 有失败；2 环境不满足。
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { startMockServer } from "./mock-server.mjs";
import { launchBrowser, sleep } from "./driver.mjs";
import { scenarios, makeSeed } from "./scenarios.mjs";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const flag = (name) => args.includes(`--${name}`);
const value = (name) => {
  const hit = args.find((a) => a.startsWith(`--${name}=`));
  return hit ? hit.slice(name.length + 3) : "";
};

if (flag("list")) {
  for (const s of scenarios) console.log(`${s.name.padEnd(26)} ${s.title}`);
  process.exit(0);
}

const only = value("only").split(",").filter(Boolean);
const picked = only.length ? scenarios.filter((s) => only.includes(s.name)) : scenarios;
if (!picked.length) {
  console.error(`没有匹配的场景：${only.join(",")}\n可用：${scenarios.map((s) => s.name).join(", ")}`);
  process.exit(2);
}

const runId = new Date().toISOString().replace(/[:.]/g, "-");
const outDir = path.join(HERE, ".artifacts", runId);
fs.mkdirSync(outDir, { recursive: true });

const seed = makeSeed();
const seedBytes = Buffer.byteLength(JSON.stringify(seed));
console.log(`e2e 开始：${picked.length} 个场景，种子数据 ${(seedBytes / 1024).toFixed(0)} KB，产物目录 ${path.relative(process.cwd(), outDir)}`);

const mock = await startMockServer();
if (mock.port) console.log(`假后端 ${mock.url}`);

let failed = 0;
const rows = [];

for (const scenario of picked) {
  await mock.ctl("/__ctl/reset");
  await mock.ctl("/__ctl/seed", { method: "POST", body: JSON.stringify(seed) });

  const failures = [];
  const check = (ok, message) => { if (!ok) failures.push(message); };
  const log = (...parts) => {
    if (flag("debug")) console.log("   ·", ...parts.map((p) => (typeof p === "string" ? p : JSON.stringify(p))));
  };

  let page = null;
  let result = null;
  const started = Date.now();
  process.stdout.write(`  ${scenario.name} … `);
  try {
    page = await launchBrowser({ baseUrl: mock.url, artifactDir: outDir, debug: flag("debug") });
    result = await scenario.run({ page, mock, check, log, sleep });
  } catch (err) {
    failures.push(`场景抛错：${err.message}`);
  }

  let shot = null;
  if (failures.length && page) {
    try {
      const { data } = await page.send("Page.captureScreenshot", { format: "png" });
      shot = path.join(outDir, `${scenario.name}.png`);
      fs.writeFileSync(shot, Buffer.from(data, "base64"));
    } catch { /* 截不到就算了 */ }
  }

  const netLog = page ? page.netLog.slice(-80) : [];
  const saves = await mock.ctl("/__ctl/saves").catch(() => []);
  const loads = await mock.ctl("/__ctl/loads").catch(() => []);
  const elapsed = Date.now() - started;
  fs.writeFileSync(
    path.join(outDir, `${scenario.name}.json`),
    JSON.stringify({ scenario: scenario.name, title: scenario.title, ok: !failures.length, failures, result, netLog, saves, loads, elapsed, shot }, null, 2),
  );
  if (page) await page.close();

  if (failures.length) {
    failed++;
    console.log(`FAIL (${elapsed}ms)`);
    for (const f of failures) console.log(`      ✗ ${f}`);
  } else {
    console.log(`ok (${elapsed}ms)`);
  }
  rows.push({ name: scenario.name, ok: !failures.length });
}

await mock.close();
console.log(`\n结果：${rows.length - failed}/${rows.length} 通过；产物在 ${outDir}`);
process.exit(failed ? 1 : 0);
