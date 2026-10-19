# 端到端回归（tests/e2e）

守的是**前端状态机**这一类回归：用户数据同步、草稿补存、冲突弹窗。
这些 bug 的共同点是「后端 100% 正常，前端自己把状态搞坏了」，单元测试和 API 测试都看不见。

来历：线上真实事故——用户在同一台设备上**每次刷新都弹**「数据冲突」。根因是
`sendBeacon` 有 64KB 上限，1MB 的整包保存超限后浏览器静默失败（返回 `false`，一个字节都不发），
旧代码照样落一条 revision 永久过期的草稿，于是每次开页都被问一遍。

## 怎么跑

```bash
node tests/e2e/run.mjs                 # 跑全部
node tests/e2e/run.mjs --list          # 列出场景
node tests/e2e/run.mjs --only=stale-draft-no-popup
node tests/e2e/run.mjs --debug         # 打印浏览器 stderr 与中间快照
```

- 依赖 **Node ≥ 22**（用内置 `WebSocket` 直连 CDP，没有 `package.json`、没有 puppeteer）。
- 依赖本机 **Chrome / Edge**；不在默认路径就用 `CHROME_PATH` 指定。
- 不需要真后端、不需要联网：`mock-server.mjs` 起在 127.0.0.1 随机端口，静态托管 `public/`。

退出码：`0` 全过，`1` 有失败，`2` 环境不满足。

## 结构

| 文件 | 作用 |
| --- | --- |
| `run.mjs` | 入口：起假后端 → 逐个场景起浏览器 → 收集断言失败 / 截图 / 请求轨迹 |
| `mock-server.mjs` | 假后端。复刻 `Program.cs` 的语义：`revision = SHA256(UTF-8 body)`，`If-Match`/`If-None-Match`/`?revision=`/`?expectEmpty=1` 前置条件，失配返回 `409 {ok:false,code:"user_data_conflict"}`；另有 `?delay=` 与 `/__ctl/*` 控制面 |
| `driver.mjs` | 极简 CDP 驱动：`evaluate` / `waitFor` / `snapshot` / `click` / `reload` / 网络与 console 采集 |
| `scenarios.mjs` | 场景定义 + 种子数据（约 2MB，必须超过 64KB beacon 上限才有意义） |
| `probe.mjs` | 手工排查用：开一个页面，按需求值 / 截图（`node tests/e2e/probe.mjs --help`） |

## 场景

| 名称 | 守什么 |
| --- | --- |
| `idle-no-draft` | 纯开页不写草稿、不回写服务器（revision 不变） |
| `save-roundtrip` | 改一笔走**增量保存**（`/api/user/patch`：ops 少、线上字节 < 4KB），且合并不丢笔记 / 答题记录 |
| `full-save-gzip` | 没有增量基线（换账号 / 首次保存 / 刚应用草稿）时退回整包，整包仍要 gzip（线上字节 < 原文 1/3） |
| `patch-rejected-falls-back` | 服务端拒绝增量（400 `user_data_patch_rejected`）时，这一次改动必须改走整包保存（gzip）、不丢、不弹框 |
| `conditional-load-304` | 内容没变时刷新：`/api/user/load` 带 `If-None-Match` 得 304、页面照常渲染、不吃旧数据；服务端被别的设备改过后必须回全量 |
| `unload-beacon-toobig` | 保存还在飞时刷新：大包不发 beacon、续跑那条已在飞的保存、连刷两次都不弹框不留草稿 |
| `stale-draft-no-popup` | 本机存着过期草稿：开页**不弹模态**、改挂「未补存」入口；点「加载服务器数据」后草稿真被删掉、基线等于服务端；之后继续答题不会 409 |
| `calibration-blind-spot` | 掌握校准：信心×对错四桶各归各位；「危险盲区／侥幸」按钮进专练，题单只含对应题；行为推断的「疑似盲区」也能单独练 |
| `confidence-ask` | 答错当场问「确定吗」：点了才写 `storage.confidence`、答对不问、刷新后看板把它算进盲区 |
| `wrong-reason-plan` | 错因处方：答错后就地标错因、看板按错因给处方、按钮进专练且题单符合该错因的规则 |
| `crowd-stats` | 全站统计：答案没展开不显示比例（不剧透）、答完显示「全站 N 次作答 · M% 答错」、上报计入服务端、看板「全站参照」卡数值正确 |
| `ai-explain` | AI 解析：开关关着不出按钮；开启后按钮出现、正文来自服务端；同题第二次直接吃缓存（上游只调一次）；失败显示人话并可重试 |
| `ai-admin-config` | 管理端 AI 配置：非管理员进不去，管理员能存密钥、保存后密钥只回掩码 |

### 一条实测教训：读到的快照可能比刚落地的那次保存还旧

`loadUserData()` 原本是「读到的快照过期就重读一次」，但慢网下保存会一笔接一笔落地，
重读本身也可能读到旧值 → `storageRevision` 被拽回旧值 → 下一次保存必 409。
现在按 `saveSuccessSeq` 逐轮重读（最多 3 轮）。`stale-draft-no-popup` 里那条
「加载服务器后基线应等于服务端」的断言就是钉它的：没有这条断言时，同一个 bug
表现出来只是 30 秒后超时，看不出原因。

### 为什么本机缓存可以直接吃

缓存里存的是上次服务端返回的 `(revision, body)` 整对；`revision` 是落盘全文的 SHA-256，
所以「304」等价于「逐字节相同」。缓存可能过期，但过期只会多下一次全量 ——
它**不参与任何写入判断**，服务器一旦有变化校验器就对不上。写入攒 3 秒（答题时别每笔都写 1MB），
localStorage 配额不足时直接丢弃缓存，优先保住草稿。

## 写新场景的几条硬规矩

1. **不 mock `public/app.js`**：跑真实前端文件，这是这套测试唯一的价值来源。
2. 断言**用户能观察到的结果**（有没有弹框、状态条文案、草稿条数、HTTP 状态），
   不要断言内部变量名——内部实现随时会改。
3. 时间相关的场景别用「等 N 毫秒再断言」凑，用 `mock` 的 `saveDelay`/`loadDelay`
   造确定性的顺序，再用 `waitFor` 等最终态。
4. 加了 `?delay=` / `/__ctl/*` 这类测试钩子，也要保证默认配置下的语义和真后端一致。
5. 每个场景先 `reset` 再 `seed`，场景之间不共享状态。

## 真实服务端的契约检查（tests/api）

e2e 的假后端是**抄**契约；抄错了两边一起错。`tests/api/patch-check.mjs` 直接打真实 C# 实现，
验的是合并语义本身：gzip 上传、`set`/`app`/`del` 生效、`revision = 落盘全文 SHA256`、
过期 base 409（一个字节都不写）、非法 op / 超过 2000 条回 400 且不改数据、老客户端的明文整包仍可用。

```bash
dotnet publish src/YunxiTiku.Web -c Release -o /tmp/yt
cd /tmp/yt && ASPNETCORE_URLS=http://127.0.0.1:5199 dotnet YunxiTiku.Web.dll &
node tests/api/patch-check.mjs          # 可用 PATCH_BASE/PATCH_USER/PATCH_PASSWORD 覆盖
```

它不接 CI（CI 里没有起好的服务端），改保存端点后手工跑一次。

`tests/api/ai-check.mjs` 同样打真实服务端，验 AI 解析的对外语义：未登录 401、未开启 503 `ai_disabled`、
密钥只回掩码、上游连不上 502 `ai_upstream`（且文案里没有异常细节）、配额用尽 429 `ai_quota`，
以及「同题只向上游要一次」——脚本会在本地起一个假的 OpenAI 兼容上游，第一次生成、第二次命中缓存、
`force` 强制重生成，三次上游调用计数都能对上。它抓到过缓存 upsert 里 `excluded.duped` 的拼写错误
（应为 `dupdated`），那个错误会让**每次生成都 500**。

```bash
node tests/api/ai-check.mjs             # 可用 AI_BASE/AI_USER/AI_PASSWORD 覆盖
```

## CI

`.github/workflows/docker.yml` 的 `e2e` job 会在 `ubuntu-latest` 上跑全部场景（runner 自带 Chrome），
失败时把 `tests/e2e/.artifacts/` 打成 artifact。镜像是 `needs: [verify, e2e]`——
**回归挂了就不构建、不发布**。
