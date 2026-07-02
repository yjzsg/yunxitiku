# 云习题库 Docker 版

这是一个自托管题库练习 Web 应用，提供 Docker/NAS 部署所需的最小源码与前端文件。

公开仓库只包含运行程序，不包含任何题库数据、用户数据、授权文件、导出工具或第三方软件说明。

## 功能

- 多账号登录与管理员管理。
- 科目、章节、刷题、收藏、笔记、纠错反馈。
- 模拟考场、错题强化、进度分析。
- 管理员看板、题库数据上传/下载、用户数据上传/下载。
- Docker Compose 一键运行。

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

把你的 `question-bank.db` 和图片资源放入 `data` 目录后启动：

```sh
docker compose up -d --build
```

浏览器访问：

```text
http://NAS_IP:8787/
```

健康检查：

```text
http://NAS_IP:8787/api/health
```

返回中的 `sqlite` 为 `true` 表示题库数据库已加载。

## 管理员数据维护

登录管理员账号后，进入“数据管理”：

- 上传题库：支持上传 `question-bank.db` 或包含数据库与 `assets` 目录的 zip。
- 下载题库：导出当前数据库和图片资源 zip。
- 上传用户数据：支持上传 userdata zip、单个用户 json 或账号配置文件。
- 下载用户数据：导出当前 userdata zip。

上传替换前会自动生成备份。

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

## 安全说明

请不要向公开仓库提交：

- 题库数据库和图片资源。
- 用户账号与做题数据。
- 授权文件、令牌、日志、打包文件。
- 与 Docker 运行无关的本地工具文件。
