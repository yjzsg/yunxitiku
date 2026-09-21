# 云习题库 Docker 版

这是一个自托管题库练习 Web 应用，提供 Docker/NAS 部署所需的最小源码与前端文件。

公开仓库只包含运行程序，不包含任何题库数据、用户数据、授权文件、导出工具或第三方软件说明。

## 功能

- 多账号登录与管理员管理。
- 科目、章节、刷题、收藏、笔记、纠错反馈。
- 模拟考场、错题强化、进度分析。
- 管理员看板、题库直连拉取更新、题库数据上传/下载、用户数据上传/下载。
- Docker Compose 一键运行。

## 文件分类

```text
docker-compose.yml        NAS 默认部署文件，优先使用预构建镜像
Dockerfile                GitHub Actions 构建镜像使用
public/                   Web 前端页面、样式和交互脚本
src/YunxiTiku.Web/        .NET 后端接口和数据读写逻辑
src/JkdWeb.BankPuller/    跨平台题库拉取层（直连金考典 WebService，无 Windows 依赖）
data/                     题库数据库和图片挂载目录，只保留空占位文件
userdata/                 用户数据挂载目录，只保留空占位文件
deploy/                   备用部署文件
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
docker compose pull
docker compose up -d
```

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
http://NAS_IP:8787/
```

健康检查：

```text
http://NAS_IP:8787/api/health
```

返回中的 `sqlite` 为 `true` 表示题库数据库能正常读取。首次启动创建的是空题库，上传真实题库包后才会显示科目和章节。

## 初始登录口令

管理员首次登录、以及新建/重置账号，用的都是同一个「初始口令」，由配置项
`App:DefaultLoginPassword`（环境变量 `App__DefaultLoginPassword`）决定：

- **`docker-compose.yml` 里已经设成 `123456`**（公开默认值）。首次登录后**必须改密**——
  这条现在由**服务端强制**：没改密之前，除「查会话 / 改密 / 退出」外的接口一律 403。
- 不想用公开默认值：删掉 `docker-compose.yml` 里那一行，改到 `docker-compose.override.yml`
  （override 优先）或环境变量里配一个强口令。
- **完全没配**时：后端每次启动随机生成 12 位口令，只在「管理员账号还不存在」时写进
  `userdata/admin-init-password.txt` 并打印到启动日志；管理员已存在时既不写文件也不打印
  （那个随机串并不是有效口令，写了会误导）。

> `123456` 是弱口令。内网自用可接受；**对外暴露前务必换掉**，并确认首次登录已完成改密。

## 管理员数据维护

登录管理员账号后，进入“数据管理”：

- 上传题库：只支持 zip 包，包内包含 `question-bank.db` 和 `assets/`。
- 下载题库：导出当前数据库和图片资源 zip。
- 上传用户数据：只支持 zip 包，包内包含 `accounts.dat` 和各账号 json。
- 下载用户数据：导出当前 userdata zip。

上传替换前会自动生成备份。题库包会先校验「读路径能否跑通」再换入，换入后仍读不通会自动回滚；
用户数据包里的 `accounts.dat` 会先做结构校验，且采用「先写临时文件、全部就绪再改名」的原子替换。

## 备份与回滚

- **自动备份**：题库类操作（拉取、整包/单课上传、清广告、编辑）会先备份到 `data/_backups/`；
  用户数据上传会先备份到 `userdata/_backups/`。
  保留策略：**最多 20 份，且不超过 30 天**（超出即删）。
- **恢复**：管理端目前**没有**一键恢复按钮。要回滚请停容器，用 `_backups/` 里对应文件覆盖
  `data/question-bank.db` / `data/assets/` / `userdata/`，再启动。
- **镜像回滚**：默认用 `ghcr.io/yjzsg/yunxitiku:latest`。生产建议同时保留带版本/摘要的标签
  （如 `:sha-xxxx`），出问题可指定旧标签重启。

## 本地开发

需要 .NET 8 SDK：

```sh
dotnet restore src/YunxiTiku.Web/YunxiTiku.Web.csproj
dotnet run --project src/YunxiTiku.Web
```

默认监听：

```text
http://127.0.0.1:8787/
```

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

- **不要直接暴露到公网**。默认 `docker-compose.yml` 把 8787 绑在所有网卡且**没有 TLS**；
  管理口令与上游题库账号都是明文传输。要对外访问，请只绑回环（`127.0.0.1:8787:8787`）
  并前置一个终结 TLS 的反向代理或隧道。
- 放在反代/隧道后面时，应用会读 `X-Forwarded-For` / `X-Forwarded-Proto` 识别真实客户端
  （登录限流按它计数）。当前配置**信任所有代理来源**，请确保只有你的反代能连到容器端口。
- **PWA（离线/安装）需要 HTTPS**：按 `http://NAS_IP:8787/` 访问不是安全上下文，
  Service Worker 不会注册，离线能力不生效。
- **容器时区**：`docker-compose.yml` 已设 `TZ=Asia/Shanghai`；删掉的话容器按 UTC 记录时间戳，
  会比本地早 8 小时。
- 会话令牌只以 SHA256 落盘（`userdata/sessions.json`），`accounts.dat` / `sessions.json` /
  用户 json 权限为 `0600`；改密/重置/停用/删除账号会立即吊销该用户全部会话。

### 不要向公开仓库提交：

- 题库数据库和图片资源。
- 用户账号与做题数据。
- 上游题库服务账号（放 `docker-compose.override.yml`，该文件已 gitignore）。
- 授权文件、令牌、日志、打包文件。
- 与 Docker 运行无关的本地工具文件。

## 维护文档

- [文件地图](docs/FILE_MAP.md)
- [版本记录](docs/VERSIONS.md)
