#!/bin/sh
# 发布：等 CI 把当前提交构建完，再拉取部署。
#
# 为什么要有这个脚本（而不是直接 `docker compose pull && up -d`）：
#   镜像用的是 `:latest` 标签。**构建失败时 GHCR 上的 `latest` 不会变、也不会报错** ——
#   这时候直接 pull 会「成功」，但拉下来的是**上一次**的旧镜像，
#   服务照常运行、界面一切正常，你根本看不出这次发布没生效。
#   所以这里先按提交 sha 去确认 `sha-<7位>` 这个标签**真的存在**（CI 成功才会推它），
#   确认之后才 pull `latest`。
#
# 用法（在仓库目录里跑）：
#   sh deploy/pull-and-verify.sh            # 等最多 40 分钟
#   sh deploy/pull-and-verify.sh 10         # 等最多 10 分钟
#   sh deploy/pull-and-verify.sh 40 --allow-stale   # 等不到也照拉（应急，自己确认清楚）
set -eu

REPO_DIR=$(pwd)
WAIT_MINUTES=${1:-40}
ALLOW_STALE=${2:-}

if [ ! -f docker-compose.yml ]; then
  echo "请在仓库根目录运行（找不到 docker-compose.yml）" >&2
  exit 2
fi
if ! command -v git >/dev/null 2>&1; then
  echo "需要 git" >&2
  exit 2
fi

SHA=$(git rev-parse --short HEAD)
FULL=$(git rev-parse HEAD)
TAG="ghcr.io/yjzsg/yunxitiku:sha-$SHA"
echo "== 发布 =="
echo "  提交   : $SHA ($FULL)"
echo "  等构建 : $TAG（最多 ${WAIT_MINUTES} 分钟）"

i=0
LIMIT=$((WAIT_MINUTES * 3))
while [ $i -lt $LIMIT ]; do
  i=$((i+1))
  if docker pull "$TAG" >/dev/null 2>&1; then
    echo "  ✓ CI 构建已完成（等了约 $((i * 20)) 秒）"
    break
  fi
  sleep 20
done

if ! docker pull "$TAG" >/dev/null 2>&1; then
  # 这个提交不一定触发过构建：workflow 的 paths 过滤只认
  # Dockerfile / public/** / src/** / 两个 deploy 文件 / workflow 自己 ——
  # 只改 docker-compose.yml、README、docs 的提交**不会**产生镜像，这是有意的。
  # 所以不能直接判「构建失败」，按**内容**判断镜像是不是已经包含了 HEAD 的应用代码。
  echo "  · 没有 $TAG（可能这个提交本来就不触发构建），改按内容判断…"
  docker pull ghcr.io/yjzsg/yunxitiku:latest >/dev/null 2>&1 || true
  img_rev=$(docker inspect -f '{{index .Config.Labels "org.opencontainers.image.revision"}}' \
            ghcr.io/yjzsg/yunxitiku:latest 2>/dev/null || echo "")
  echo "  镜像里的提交: ${img_rev:-未知}"
  if [ -n "$img_rev" ] && git rev-parse --verify --quiet "$img_rev^{commit}" >/dev/null 2>&1; then
    if git diff --quiet "$img_rev" HEAD -- public src Dockerfile; then
      echo "  ✓ 镜像已经包含 HEAD 的应用代码（差异只在 compose/文档之类）—— 可以发布"
    else
      echo "  ✗ 镜像($img_rev) 与 HEAD 的应用代码**不一致**：CI 要么失败要么还没跑完" >&2
      git --no-pager diff --stat "$img_rev" HEAD -- public src Dockerfile >&2 || true
      if [ "$ALLOW_STALE" != "--allow-stale" ]; then
        echo "    已中止，**没有**动线上服务（避免把旧镜像当新版本拉下来）。" >&2
        echo "    去看 https://github.com/yjzsg/yunxitiku/actions" >&2
        exit 1
      fi
      echo "    ⚠️ --allow-stale：照拉。拉到的可能是**旧**镜像，请自己确认。" >&2
    fi
  else
    echo "  ✗ 拿不到镜像的 revision 标签，无法判断它对应哪个提交。" >&2
    if [ "$ALLOW_STALE" != "--allow-stale" ]; then
      echo "    已中止，没有动线上服务。" >&2
      exit 1
    fi
  fi
fi

echo "== 拉取并重启 =="
docker compose pull 2>&1 | tail -1
docker compose up -d 2>&1 | tail -2

echo "== 等健康检查 =="
i=0
while [ $i -lt 20 ]; do
  i=$((i+1)); sleep 3
  st=$(docker inspect yunxi-tiku --format '{{.State.Health.Status}}' 2>/dev/null || echo "无")
  if [ "$st" = "healthy" ]; then echo "  ✓ $st"; break; fi
  [ $i -eq 20 ] && echo "  ✗ 健康检查没通过（当前：$st），去看 docker logs yunxi-tiku" >&2
done

# 发布生效的三个硬证据：
#   ① 容器跑的镜像 ID == 本机 latest 的镜像 ID（不是「拉到了」就算，要比对象）
#   ② 服务端真正吐出来的 ?v= == 仓库里 index.html 写的 ?v=
#      （只有最后一个应用放行；这条能抓住「pull 成功但 up 没换容器」）
#   ③ 健康接口 ok
PORT=$(docker compose port yunxi-tiku 8787 2>/dev/null | head -1 | sed 's/.*://' || true)
[ -z "$PORT" ] && PORT=8787
BASE="http://127.0.0.1:$PORT"

echo "== 校验 =="
run_img=$(docker inspect -f '{{.Image}}' yunxi-tiku 2>/dev/null || echo "")
latest_img=$(docker inspect -f '{{.Id}}' ghcr.io/yjzsg/yunxitiku:latest 2>/dev/null || echo "")
if [ -n "$run_img" ] && [ "$run_img" = "$latest_img" ]; then
  echo "  ✓ 容器镜像 == latest（$run_img）"
else
  echo "  ✗ 容器镜像 != latest（run=$run_img latest=$latest_img）" >&2
fi

want_app=$(grep -o 'app\.js?v=[a-z0-9-]*' "$REPO_DIR/public/index.html" | head -1)
want_css=$(grep -o 'style\.css?v=[a-z0-9-]*' "$REPO_DIR/public/index.html" | head -1)
got_app=$(curl -s "$BASE/" | grep -o 'app\.js?v=[a-z0-9-]*' | head -1)
got_css=$(curl -s "$BASE/" | grep -o 'style\.css?v=[a-z0-9-]*' | head -1)
echo "  仓库: $want_app  $want_css"
echo "  线上: $got_app  $got_css"
if [ "$want_app" = "$got_app" ] && [ "$want_css" = "$got_css" ]; then
  echo "  ✓ 线上就是这次提交的前端"
else
  echo "  ✗ 线上前端与仓库不一致 —— 发布没真正生效" >&2
fi

echo "  health: $(curl -s "$BASE/api/health" || echo '取不到')"
echo "== 完 =="
