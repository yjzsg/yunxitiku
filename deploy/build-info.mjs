#!/usr/bin/env node
/**
 * 生成 build-info.json（DL-07）
 *
 * 目的：让「跑起来的这份产物」能被对回一个具体的 commit 与具体的前端内容哈希。
 * 本仓库的前端是**手工 ?v= 版本号 + 一年 immutable 缓存**，所以「哪个 app.js 进了这个镜像」
 * 必须有机器可读的记录，而不是靠人记。
 *
 * 用法：
 *   node deploy/build-info.mjs --out build-info.json [--digest sha256:xxxx]
 * 环境变量（CI 里由 GitHub 提供，本地跑缺了就写空串）：
 *   GITHUB_SHA / GITHUB_REF / GITHUB_RUN_ID / GITHUB_RUN_NUMBER / GITHUB_EVENT_NAME /
 *   GITHUB_ACTOR / GITHUB_SERVER_URL / GITHUB_REPOSITORY
 *   以及 IMAGE_NAME（镜像名）、PLATFORMS（默认 linux/amd64,linux/arm64）
 *   TAGS（逗号分隔，可选）
 *
 * 资源哈希直接取 deploy/asset-versions.lock.json（与门禁脚本同源，避免两份实现漂移）。
 */
import { createHash } from "node:crypto";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const LOCK_REL = "deploy/asset-versions.lock.json";
const SCHEMA = "yunxitiku-build-info/1";

let out = "build-info.json";
let digest = "";
let root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const argv = process.argv.slice(2);
for (let i = 0; i < argv.length; i++) {
  if (argv[i] === "--out") out = argv[++i];
  else if (argv[i] === "--digest") digest = argv[++i] || "";
  else if (argv[i] === "--root") root = path.resolve(argv[++i] || "");
  else { console.error(`未知参数: ${argv[i]}`); process.exit(1); }
}

const sha256 = (s) => createHash("sha256").update(s).digest("hex");
const lockAbs = path.join(root, LOCK_REL);
if (!existsSync(lockAbs)) { console.error(`找不到 ${lockAbs}（先跑 node deploy/check-asset-versions.mjs --update）`); process.exit(1); }
const lockText = readFileSync(lockAbs, "utf8");
const lock = JSON.parse(lockText);
if (lock.schema !== "yunxitiku-asset-versions/1") { console.error(`${LOCK_REL} schema 不对: ${lock.schema}`); process.exit(1); }

const commit = process.env.GITHUB_SHA || "";
const tags = (process.env.TAGS || "").split(",").map((t) => t.trim()).filter(Boolean);
const info = {
  schema: SCHEMA,
  generatedAt: new Date().toISOString(),
  source: {
    repository: process.env.GITHUB_REPOSITORY || "",
    serverUrl: process.env.GITHUB_SERVER_URL || "",
    commit,
    commitShort: commit.slice(0, 7),
    ref: process.env.GITHUB_REF || "",
    eventName: process.env.GITHUB_EVENT_NAME || "",
    actor: process.env.GITHUB_ACTOR || "",
    runId: process.env.GITHUB_RUN_ID || "",
    runNumber: process.env.GITHUB_RUN_NUMBER || "",
  },
  image: {
    name: process.env.IMAGE_NAME || "",
    platforms: (process.env.PLATFORMS || "linux/amd64,linux/arm64").split(",").map((p) => p.trim()).filter(Boolean),
    tags,
    digest,
  },
  assetVersionLock: { path: LOCK_REL, sha256: sha256(lockText), generatedAt: lock.generatedAt || "" },
  assets: (lock.assets || []).map((a) => ({ path: a.path, version: a.version, bytes: a.bytes, sha256: a.sha256 })),
};
// digest 由构建步骤在推送成功后回填（--digest sha256:...）：镜像 ↔ commit ↔ 产物哈希三方对齐
writeFileSync(out, JSON.stringify(info, null, 1) + "\n", "utf8");
console.log(`已写入 ${out}`);
console.log(`  commit=${info.source.commit || "(空)"} image=${info.image.name || "(空)"} digest=${info.image.digest || "(待回填)"}`);
console.log(`  assets=${info.assets.length} 条，lock sha256=${info.assetVersionLock.sha256.slice(0, 12)}`);
console.log(`  build-info sha256=${sha256(readFileSync(out))}`);
