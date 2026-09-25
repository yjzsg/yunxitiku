# 文件地图

这个仓库只保留 Docker 部署和运行需要的文件，不放真实题库、图片包、用户数据、上游授权/凭据文件或本地导出工具。

## 常用入口

| 路径 | 用途 | 什么时候改 |
| --- | --- | --- |
| `README.md` | 部署说明、功能清单、授权与许可入口 | 功能、部署方式、授权条款变化时 |
| `LICENSE` | 软件许可协议（专有、源码可见，商用需书面授权） | 权利人署名、授权范围、联系方式变化时 |
| `docker-compose.yml` | NAS 默认部署文件，使用 `ghcr.io/yjzsg/yunxitiku:latest` | 端口、挂载目录、容器环境变量变化时 |
| `Dockerfile` | 预构建镜像构建规则 | 运行时依赖、发布路径、镜像内容变化时 |
| `.github/workflows/docker.yml` | GitHub Actions 自动构建并推送 GHCR 镜像 | 镜像名、平台、触发规则变化时 |

## 前端

| 路径 | 用途 |
| --- | --- |
| `public/index.html` | 页面骨架、主要 DOM 节点；`<head>` 里还有主题防白闪脚本，改样式表要同步改 `style.css?v=` 的版本号 |
| `public/style.css` | 页面样式、桌面端和手机端适配；顶部是配色令牌（`:root` / `[data-theme="dark"]` / `[data-theme="eye"]`），文件末尾是「玻璃层」段 |
| `public/app.js` | 登录、刷题、模拟考场、管理员面板等前端逻辑；主题切换与 canvas 跟随重绘也在这里 |
| `public/manifest.json` | PWA 清单（名称 / 图标 / 主题色）；**需要 HTTPS** 才能用于安装 |
| `public/sw.js` | Service Worker：只缓存同源静态资源，`/api/` 一律走网络，导航请求 network-first；**需要 HTTPS/localhost** 才会注册 |

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
- 改响应式布局：`public/style.css` 里断点较多（420 / 760 / 1180 / 1280 / 1440 / 1600），
  改动后**必须跨分辨率复查**——光看 1440 看不出 768 下题干会被挤成一字一行。
  另外窄屏默认停在「选科目」页，要先点「进入做题」才能看到答题区。
  注意**同一个元素在不同断点/状态下 `position` 可能不一样**：答题卡 `.answer-card-wrap`
  在 `>1440` 与 `≤760 收起` 时是 `relative`（在流内），在 `761~1440` 与 `≤760 展开` 时是
  `fixed` 覆盖层（会盖住下面的东西，需要预留空间）。改它之前先量一遍 `getComputedStyle().position`。
  断点全表见 `docs/VERSIONS.md` 的「断点与『跟着窗口缩放』的行为」。
- 改配色 / 主题 / 玻璃效果：`public/style.css` 顶部的令牌块（`:root`、`[data-theme="dark"]`、`[data-theme="eye"]`）
  加文件末尾的「玻璃层」段。改完必须复查对比度（正文 ≥ 4.5:1），做法见 `docs/VERSIONS.md` 的「前端主题与玻璃层」。
- 改接口、数据导入导出、账号权限：优先看 `src/YunxiTiku.Web/Program.cs`。
- 改 NAS 部署：优先看 `docker-compose.yml`。
- 改镜像构建：优先看 `Dockerfile` 和 `.github/workflows/docker.yml`。
