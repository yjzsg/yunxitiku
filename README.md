# 云习题库 Docker 版

这是一个自托管题库练习 Web 应用，提供 Docker/NAS 部署所需的最小源码与前端文件。

公开仓库只包含运行程序，不包含任何题库数据、用户数据、上游授权/凭据文件、导出工具或第三方软件说明。

## 授权与许可（重要）

本软件为**专有软件，源码可见但并非开源**，版权所有，保留所有权利。

- ✅ **允许**：查看源码用于学习与评估；在**个人、非商业、非公开**前提下安装运行。
- ❌ **禁止**（未经书面授权）：任何商业用途（含以部署、运维、托管、SaaS 等形式向他人收费）、
  再分发、修改后分发、去除版权声明、作为自己的产品或服务对外提供。
- 💼 **商业授权**：如需用于商业用途（部署服务、运维服务、私有化交付等），须事先取得书面授权。

完整条款见 [`LICENSE`](LICENSE)。权利人：`yjzsg`；授权联系走本仓库
[Issues](https://github.com/yjzsg/yunxitiku/issues)。

> 本软件**不包含任何题库数据或第三方内容**；用户自行接入或导入的内容，其来源合法性与版权合规性由用户自行负责。

## 功能

**学习端（六个模块）**

- **考试题库**：按科目/章节刷题，支持题型筛选、题目内搜索、随机练习、按题量组题（80 / 160 / 400 / 1000 题）。
- **智能训练**：错题强化与针对性训练，支持标签体系（计算量大 / 易错题 / 坑题 / 重要 / 待复盘）。
- **模拟考场**：计时组卷、交卷评分、历史成绩。
- **错题本**：自动收集错题，按科目/章节筛选复盘。
- **学习看板**：做题量、正确率、章节进度、每日活跃。
- **收藏试题**：收藏题目快速回看。

**通用能力**

- 答题卡进度、笔记、收藏、纠错反馈。
- 打印：打印当前题或整个题单，可选附带答案与解析。
- 配色模式：浅色 / 暗色 / 护眼 / 跟随系统。
- 手机端底部标签栏 + 桌面端顶栏；缩放 / 全屏。
- 导出我的数据。
- **PWA**：可安装到桌面、离线缓存静态资源（需 HTTPS）。
- **多端数据冲突处理**：检测到数据已在其他页面更新时，可「加载服务器最新数据 / 导出本地草稿 / 暂时保留本地」。

**管理员**

- 账号管理（新增 / 重置 / 停用 / 删除；首次登录强制改密）。
- 管理员数据看板。
- 题库直连拉取更新（含上游课程目录）、题库数据上传 / 下载。
- 用户数据上传 / 下载。

**部署**

- Docker Compose 一键运行。

## 文件分类

```text
docker-compose.yml        NAS 默认部署文件，优先使用预构建镜像
Dockerfile                GitHub Actions 构建镜像使用
LICENSE                   软件许可协议（专有，商用需授权）
public/                   Web 前端页面、样式和交互脚本
src/YunxiTiku.Web/        .NET 后端接口和数据读写逻辑
src/JkdWeb.BankPuller/    跨平台题库拉取层（直连金考典 WebService，无 Windows 依赖）
data/                     题库数据库和图片挂载目录，只保留空占位文件
userdata/                 用户数据挂载目录，只保留空占位文件
deploy/                   备用部署文件、资源版本门禁脚本（check-asset-versions.mjs / build-info.mjs）
docs/                     文件地图、版本记录和维护说明
.github/workflows/        自动构建 Docker 镜像
```

## 数据目录

运行时需要把数据放在仓库根目录的挂载目录中：

```text
data/question-bank.db
data/assets/
userdata/
```

说明：

- `data/question-bank.db` 是 SQLite 题库数据库。
- `data/assets/` 是题目图片等静态资源。
- `userdata/` 保存账号、密码、做题记录、收藏、笔记和纠错反馈。
- 这些真实数据不会提交到 Git。

## 启动

```sh
git clone https://github.com/yjzsg/yunxitiku.git
cd yunxitiku
mkdir -p data/assets userdata
```

可以直接启动。首次启动时，程序会在 `data/question-bank.db` 自动创建一个空题库数据库，便于先进入系统和管理员页面。

如果已有题库数据，也可以先把你的 `question-bank.db` 和图片资源放入 `data` 目录后启动。

默认使用已经构建好的镜像，不需要在 NAS 上编译：

```sh
# 推荐：等 CI 构建完再拉，并校验发布真的生效
sh deploy/pull-and-verify.sh

# 或者手动两步（**注意下面的坑**）
docker compose pull
docker compose up -d
```

> ⚠️ **手动 `docker compose pull` 有个静默的坑**：镜像标签是 `:latest`，
> 而 **CI 构建失败时 GHCR 上的 `latest` 不会变、也不会报错** ——
> 这时 pull 会「成功」，但拉下来的是**上一次**的旧镜像：服务照常运行、界面一切正常，
> 你根本看不出这次发布没生效。
>
> `deploy/pull-and-verify.sh` 就是为这个写的：它先按当前提交的 `sha-<7位>` 去确认
> CI 真的构建成功了（这个标签只有构建成功才会推），再拉 `latest`；
> 然后校验三件事 —— **容器镜像 ID == `latest` 的镜像 ID**、
> **服务端吐出来的 `?v=` == 仓库里 `index.html` 写的 `?v=`**、健康接口。
> 这三条能抓住「pull 成功但容器没换」和「拉到了旧镜像」。
>
> 只改了 `docker-compose.yml`／文档这类**不触发构建**的提交时，脚本会自动改成
> 「对比镜像里的 `revision` 标签与当前提交的应用代码是否一致」来判断，不会误报。
> `--allow-stale` 可以强制照拉（应急用，自己确认清楚）。

> **默认只绑回环**（`127.0.0.1:8787:8787`）：这样只有 NAS 本机上的反代/隧道能访问，
> 局域网里其它设备直接打不开。**要在局域网里按 `http://NAS_IP:8787/` 用**（也是 PWA 之前的常见用法），
> 任选一种：
>
> **① 建一个 `.env`**（已 gitignore，不进仓库）——推荐，不动任何被 git 跟踪的文件：
>
> ```bash
> echo 'YUNXI_BIND_ADDR=0.0.0.0' > .env
> docker compose up -d
> ```
>
> **② 或者直接改 `docker-compose.yml` 里那一行**：
>
> ```yaml
>     ports:
>       - "192.168.1.10:8787:8787"   # 换成 NAS 自己的 LAN IP；不要写 0.0.0.0
> ```
>
> ⚠️ **不要在 `docker-compose.override.yml` 里加 `ports:`** ——
> compose 对 `ports` 是**追加**而不是替换，会和基础文件那一行形成
> 「同一个容器端口发布两次」的冲突：容器会停在 `Created` 起不来、日志空白、端口探测 000。
>
> 直连是明文 HTTP（无 TLS），只在可信内网这么做；对外访问请用反代/隧道终结 TLS。

如果 NAS 的图形界面支持导入 Compose 文件，只需要导入 `docker-compose.yml`，并确认当前目录下有 `data` 和 `userdata` 两个文件夹。

首次发布新版本后，GitHub Actions 会自动构建镜像：

```text
ghcr.io/yjzsg/yunxitiku:latest
```

如果拉取镜像提示无权限，请在 GitHub Packages 中把该镜像设为 Public，或在 NAS 上先执行 `docker login ghcr.io`。

备用的源码构建方式如下，只有在预构建镜像不可用时才需要：

```sh
docker compose -f deploy/docker-compose.build.yml up -d --build
```

浏览器访问：

```text
http://NAS_IP:8787/          # 只绑回环时改成 http://127.0.0.1:8787/（在 NAS 本机或 SSH 端口转发里访问）
```

健康检查：

```text
http://NAS_IP:8787/api/health     # 同上：只绑回环时用 http://127.0.0.1:8787/api/health
```

返回中的 `sqlite` 为 `true` 表示题库数据库能正常读取。首次启动创建的是空题库，上传真实题库包后才会显示科目和章节。

## 初始登录口令

管理员首次登录、以及新建/重置账号，用的都是同一个「初始口令」，由配置项
`App:DefaultLoginPassword`（环境变量 `App__DefaultLoginPassword`）决定：

- **默认 Compose 不设这一项**（旧版里是公开的 `123456`，已移除）。不设时后端每次启动随机生成
  12 位口令，只在「管理员账号还不存在」时写进 `userdata/admin-init-password.txt` 并打印到启动日志；
  管理员已存在时既不写文件也不打印（那个随机串并不是有效口令，写了会误导）。
  用它在 NAS 上首次登录后**必须改密**——这条现在由**服务端强制**：没改密之前，
  除「查会话 / 改密 / 退出」外的接口一律 403；管理员改密后初始口令文件会被自动删除，
  容器重启也会换一个新口令。
- 想要固定口令（自动化测试、内网小范围）：写进 `docker-compose.override.yml`（已 gitignore，override 优先）
  或环境变量，**别写进要提交的 `docker-compose.yml`**。
- 新建/重置账号也用这个口令，所以它不该长期有效：一次部署用完就换，或改用 strong 值。

> 为什么删掉 `123456`：给一个公开默认值，等于谁先连上谁就能完成首次接管——
> 「首次登录强制改密」只能约束改密之后的事，拦不住"谁先登录"。

## 管理员数据维护

登录管理员账号后，进入“数据管理”：

- 上传题库：只支持 zip 包，包内包含 `question-bank.db` 和 `assets/`。
- 下载题库：导出当前数据库和图片资源 zip。
- 上传用户数据：只支持 zip 包，包内包含 `accounts.dat` 和各账号 json。
- 下载用户数据：导出当前 userdata zip。

上传替换前会自动生成备份。题库包会先校验「读路径能否跑通」再换入，换入后仍读不通会自动回滚；
用户数据包里的 `accounts.dat` 会先做结构校验，且采用「先写临时文件、全部就绪再改名」的原子替换。

题库更新还可**直连拉取**（管理员「题库更新」，含上游课程目录），无需上传 zip。

## 备份与回滚

- **自动备份**：题库类操作（拉取、整包/单课上传、清广告、编辑）会先备份到 `data/_backups/`；
  用户数据上传会先备份到 `userdata/_backups/`。
  保留策略：**最多 20 份，且不超过 30 天**（超出即删）。
- **恢复**：管理端目前**没有**一键恢复按钮。要回滚请停容器，用 `_backups/` 里对应文件覆盖
  `data/question-bank.db` / `data/assets/` / `userdata/`，再启动。
- **镜像回滚**：默认用 `ghcr.io/yjzsg/yunxitiku:latest`。每次构建还会推这几个标签，
  出问题可以指定旧标签重启（改 `docker-compose.override.yml` 里的 `image:`，再 `up -d`）：
  - `:sha-<7位commit>` —— 回滚首选，唯一且好写，例如 `:sha-b65b91e`
  - `:<YYYY-MM-DD>` —— 当天最后一次构建（同一天多次构建会覆盖）
  - `:<YYYY-MM-DD-HHMM>` —— 某一次具体构建
  - `:<40位commit>` —— Actions 一直在推的完整 sha 标签

## 本地开发

需要 .NET 8 SDK：

```sh
dotnet restore src/YunxiTiku.Web/YunxiTiku.Web.csproj
dotnet run --project src/YunxiTiku.Web
```

监听地址来自 `src/YunxiTiku.Web/appsettings.json` 的 `App:Urls`，**默认是 `http://0.0.0.0:8787`（所有网卡）**。
本地开发建议只绑回环，免得同网段的人能直接打开你的开发实例：

```sh
dotnet run --project src/YunxiTiku.Web --urls http://127.0.0.1:8787
# 或者： export ASPNETCORE_URLS=http://127.0.0.1:8787
```

> 这条以前写成「默认监听 127.0.0.1」，与实际不符（2026-10-13 修正）。

## 上游题库服务账号（必配，且不进仓库）

拉取题库、以及管理页的「检查可更新」，都要用上游题库服务的账号。
**账号不写在代码里、也不写在 `docker-compose.yml` 里**（那是要提交的文件），
放在本机的 `docker-compose.override.yml`（已 gitignore）：

```bash
cp docker-compose.override.yml.example docker-compose.override.yml
# 然后编辑：填 App__BankServiceUser / App__BankServicePassword
```

或者用环境变量直接注入：

```bash
docker run -e App__BankServiceUser=xxx -e App__BankServicePassword=yyy ...
```

没配置时不会静默失败：拉取接口会返回
`缺少上游题库服务账号。请任选一种方式配置：…`，界面直接提示。
服务地址不用填（代码里有金考典官方地址作默认值），要改就设 `App__BankServiceUrl`。

## 安全说明

### 部署前必读

- **默认只绑回环**：`docker-compose.yml` 现在是 `127.0.0.1:8787:8787`，本机反代/隧道能连，
  局域网和公网连不上。需要 NAS 内网直连时**显式**写本机 LAN IP（`- "192.168.1.10:8787:8787"`）；
  别用 `0.0.0.0` 或裸 `8787:8787` —— 直连就是明文 HTTP、**没有 TLS**，管理口令与上游题库账号都在明文里传。
  对外访问请前置一个终结 TLS 的反向代理或隧道。
- 放在反代/隧道后面时，应用会读 `X-Forwarded-For` / `X-Forwarded-Proto` 识别真实客户端
  （登录限流按它计数），但**默认只信任回环来源**。反代在别的容器/机器上时必须把它的地址写进
  `App__TrustedProxies`（逗号分隔 IP 或 CIDR，例：`App__TrustedProxies: "172.17.0.1"`；
  容器里 `ip route` 可看到默认网关），否则限流会把所有来访者算成同一个 IP（20 次/分钟）。
  反过来，信任范围放宽等于允许任何能直连的人用伪造 `X-Forwarded-For` 绕过限流，
  所以这里默认收窄而不是默认放开。
- 容器内的应用本身仍监听 `0.0.0.0:8787`（`App:Urls` 默认值）；对外是否可达由上面的端口绑定决定。
- **PWA（离线/安装）需要 HTTPS**：按 `http://NAS_IP:8787/` 访问不是安全上下文，
  Service Worker 不会注册，离线能力不生效。
- **容器时区**：`docker-compose.yml` 已设 `TZ=Asia/Shanghai`；删掉的话容器按 UTC 记录时间戳，
  会比本地早 8 小时。
- 会话令牌只以 SHA256 落盘（`userdata/sessions.json`），`accounts.dat` / `sessions.json` /
  用户 json 权限为 `0600`；改密/重置/停用/删除账号会立即吊销该用户全部会话。
  吊销的**耐久性**靠账号里的凭据版本（写在 `accounts.dat`，原子写、失败即报错）：
  即使 `sessions.json` 那次写入失败（磁盘满/只读），旧令牌在重启后一样进不来。
  这种情况会在服务端日志（`docker logs` / `srv.log`）记 `[session-persist-failed]`，并在改密/重置/停用/删除接口的响应里
  返回 `sessionRevoke.persisted=false` + `warning`，请按提示检查磁盘。

### 不要向公开仓库提交：

- 题库数据库和图片资源。
- 用户账号与做题数据。
- 上游题库服务账号（放 `docker-compose.override.yml`，该文件已 gitignore）。
- 上游授权/凭据文件、令牌、日志、打包文件。
- 与 Docker 运行无关的本地工具文件。

## 验证与已知限制

### 发布门禁（CI 里有，也能在本地跑）

| 门禁 | 位置 | 作用 |
| --- | --- | --- |
| 资源版本一致性 | `deploy/check-asset-versions.mjs`（CI 的 `verify` job，push 与 PR 都跑） | `public/index.html` 里 `?v=` 引用的文件内容 sha256 必须成对登记在 `deploy/asset-versions.lock.json`；「改了内容忘抬版本号」直接失败 |
| 依赖漏洞扫描 | `.github/workflows/docker.yml` | `dotnet list --vulnerable --include-transitive` 命中即失败 |
| 发布来源可追溯 | `.github/workflows/docker.yml` | 生成 `build-info.json`（commit + 前端产物哈希 + 标签 + 平台）随镜像发布；镜像 label 带 `org.opencontainers.image.revision` 与 `com.yunxitiku.build-info-sha256`；推送后 digest 写进 `image-digest.txt`（同 artifact） |

改了 `public/` 下任何静态资源（`app.js` / `style.css` / `drawer.css` / `sw.js`）之后的发布流程：

```sh
# 1) 抬版本号：改 public/index.html 里对应的 ?v=（改了哪个就抬哪个）
# 2) 更新清单：内容与版本号必须成对落进清单
node deploy/check-asset-versions.mjs --update
# 3) 本地自检（CI 跑的就是这一条）
node deploy/check-asset-versions.mjs
```

### 当前验证状态（2026-10-13 对齐）

- **CI 覆盖**：`dotnet restore/build`、包漏洞扫描、资源版本门禁、`linux/amd64` + `linux/arm64` 双架构镜像构建与推送。
- **没有自动化测试工程**：仓库里没有测试项目、没有 `package.json`，所以 `dotnet test` **不构成测试证据**；
  UI/功能验收目前依赖**仓库之外**的内部截图装置，它不在这个公开仓库里 —— 因此**本仓库不提供可公开复现的 UI 验收报告**，
  历史上引用过的内部报告与截图记录也不能当作当前 HEAD 的证据。
- **未验证**：镜像启动后的真实 API/判分/鉴权行为、arm64 上 SQLite 原生库的实际查询、
  真实 PWA 安装/离线/版本升级、真实题库数据下的业务路径。**构建成功 ≠ 运行正确。**

### 已知限制

- 默认端口绑定收紧为 `127.0.0.1:8787:8787`（只绑回环）且**无 TLS**，详见「部署前必读」；
  内网直连要显式写 LAN IP，对外访问必须前置终结 TLS 的反代/隧道。
- **PWA（安装/离线）只在安全上下文生效**（HTTPS 或 localhost）。
- 静态资源的 `?v=` 是**手工**版本号：门禁只能保证「内容与版本号成对」，不能自动生成内容哈希文件名；
  生产用户的缓存升级仍然依赖发布流程真的执行了上面三步。
- 健康检查 `/api/health` 覆盖有限（见其 `sqlite` 字段说明），不能当作业务就绪探针。

## 维护文档

- [文件地图](docs/FILE_MAP.md)
- [版本记录](docs/VERSIONS.md)
- [软件许可协议](LICENSE)
