# 文件地图

这个仓库只保留 Docker 部署和运行需要的文件，不放真实题库、图片包、用户数据、授权文件或本地导出工具。

## 常用入口

| 路径 | 用途 | 什么时候改 |
| --- | --- | --- |
| `docker-compose.yml` | NAS 默认部署文件，使用 `ghcr.io/yjzsg/yunxitiku:latest` | 端口、挂载目录、容器环境变量变化时 |
| `Dockerfile` | 预构建镜像构建规则 | 运行时依赖、发布路径、镜像内容变化时 |
| `.github/workflows/docker.yml` | GitHub Actions 自动构建并推送 GHCR 镜像 | 镜像名、平台、触发规则变化时 |

## 前端

| 路径 | 用途 |
| --- | --- |
| `public/index.html` | 页面骨架、主要 DOM 节点 |
| `public/style.css` | 页面样式、桌面端和手机端适配 |
| `public/app.js` | 登录、刷题、模拟考场、管理员面板等前端逻辑 |

## 后端

| 路径 | 用途 |
| --- | --- |
| `src/YunxiTiku.Web/Program.cs` | .NET Web 服务、API、数据导入导出、账号管理、题库读取 |
| `src/YunxiTiku.Web/appsettings.json` | 本地开发默认配置 |
| `src/YunxiTiku.Web/YunxiTiku.Web.csproj` | .NET 项目依赖和目标框架 |

## 数据挂载

| 路径 | 用途 | Git 策略 |
| --- | --- | --- |
| `data/question-bank.db` | 题库 SQLite 数据库 | 不提交 |
| `data/assets/` | 题目图片等资源 | 不提交 |
| `userdata/accounts.dat` | 账号配置 | 不提交 |
| `userdata/*.json` | 各账号做题、收藏、笔记、纠错数据 | 不提交 |
| `data/.gitkeep`、`userdata/.gitkeep` | 保留空目录 | 可以提交 |

## 备用部署

| 路径 | 用途 |
| --- | --- |
| `deploy/docker-compose.build.yml` | 预构建镜像不可用时，本地源码构建备用 |

## 修改建议

- 改页面布局、按钮、移动端适配：优先看 `public/index.html`、`public/style.css`、`public/app.js`。
- 改接口、数据导入导出、账号权限：优先看 `src/YunxiTiku.Web/Program.cs`。
- 改 NAS 部署：优先看 `docker-compose.yml`。
- 改镜像构建：优先看 `Dockerfile` 和 `.github/workflows/docker.yml`。
