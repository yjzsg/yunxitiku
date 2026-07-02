# 版本记录

## 当前部署线

| 项 | 值 |
| --- | --- |
| 默认分支 | `main` |
| 默认镜像 | `ghcr.io/yjzsg/yunxitiku:latest` |
| 默认部署文件 | `docker-compose.yml` |
| 备用构建文件 | `deploy/docker-compose.build.yml` |
| 数据目录 | `data/`、`userdata/` |

## 重要提交

| 提交 | 说明 |
| --- | --- |
| `57756d4` | 修复运行镜像缺少 `/app/public/`，确保前端文件随镜像发布 |
| `5becc6e` | 修复 .NET 顶层入口返回值，保证 Docker 发布可编译 |
| `0c5211b` | 调整发布内容项，减少 MSBuild 内容项冲突 |
| `d4e17f6` | 改为 GHCR 预构建镜像部署，新增 GitHub Actions |
| `ac4c8ba` | 移除运行镜像里不必要的 curl 安装 |
| `a91f899` | 增加空数据初始化和题库/用户数据 zip 上传下载 |
| `6477c9c` | 初始公开 Docker 部署版本 |

## 发布流程

1. 修改代码或部署文件。
2. 本地确认没有真实题库、用户数据、令牌或无关工具文件。
3. 提交并推送到 `main`。
4. GitHub Actions 自动构建并推送 `ghcr.io/yjzsg/yunxitiku:latest`。
5. NAS 执行：

```sh
docker compose pull
docker compose up -d
```

如果容器仍使用旧镜像：

```sh
docker compose down
docker image rm ghcr.io/yjzsg/yunxitiku:latest
docker compose pull
docker compose up -d
```
