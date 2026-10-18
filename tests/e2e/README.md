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
| `save-roundtrip` | 一次正常保存：落库、revision 前进、状态回到「已同步」、不留草稿 |
| `unload-beacon-toobig` | 保存还在飞时刷新：大包不发 beacon、续跑那条已在飞的保存、连刷两次都不弹框不留草稿 |
| `stale-draft-no-popup` | 本机存着过期草稿：开页**不弹模态**、改挂「未补存」入口；点「加载服务器数据」后草稿真被删掉；之后继续答题不会 409 |

## 写新场景的几条硬规矩

1. **不 mock `public/app.js`**：跑真实前端文件，这是这套测试唯一的价值来源。
2. 断言**用户能观察到的结果**（有没有弹框、状态条文案、草稿条数、HTTP 状态），
   不要断言内部变量名——内部实现随时会改。
3. 时间相关的场景别用「等 N 毫秒再断言」凑，用 `mock` 的 `saveDelay`/`loadDelay`
   造确定性的顺序，再用 `waitFor` 等最终态。
4. 加了 `?delay=` / `/__ctl/*` 这类测试钩子，也要保证默认配置下的语义和真后端一致。
5. 每个场景先 `reset` 再 `seed`，场景之间不共享状态。

## CI

`.github/workflows/docker.yml` 的 `e2e` job 会在 `ubuntu-latest` 上跑全部场景（runner 自带 Chrome），
失败时把 `tests/e2e/.artifacts/` 打成 artifact。镜像是 `needs: [verify, e2e]`——
**回归挂了就不构建、不发布**。
