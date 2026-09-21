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
| `ba95605` | 交付评审整改（2026-09-21）：账号/会话/题库导入加固，见下方「交付评审整改」 |
| `57756d4` | 修复运行镜像缺少 `/app/public/`，确保前端文件随镜像发布 |
| `5becc6e` | 修复 .NET 顶层入口返回值，保证 Docker 发布可编译 |
| `0c5211b` | 调整发布内容项，减少 MSBuild 内容项冲突 |
| `d4e17f6` | 改为 GHCR 预构建镜像部署，新增 GitHub Actions |
| `ac4c8ba` | 移除运行镜像里不必要的 curl 安装 |
| `a91f899` | 增加空数据初始化和题库/用户数据 zip 上传下载 |
| `6477c9c` | 初始公开 Docker 部署版本 |

## 交付评审整改（2026-09-21，提交 `ba95605`）

一次交付级评审（架构 / 安全 / 验收三视角）后做的整改，均已部署并验证。

**阻断级**

| 编号 | 问题 | 修复 |
| --- | --- | --- |
| B1 | `?user=sessions` 能读写会话存储本身 → 可伪造持久 admin 凭据 | `ResolveRequestedUser` 拒绝保留名（400） |
| B2 | 题库包只校验"表存在"，坏包上传"成功"并把 `/api/courses` 打成 500 | 校验改跑读路径真实查询；换入后复验，失败自动回滚 |
| B3 | 初始口令未文档化 + 界面写死 `123456` | `docker-compose.yml` 显式设 `App__DefaultLoginPassword`；界面文案改用服务端返回值 |
| B4 | 结构错的 `accounts.dat` 上传会静默清空全部凭据、重启后锁死 | 结构校验 + 先写临时文件再原子改名 |
| B5 | 两套 SQLite DDL 不一致 → 全新部署拉取必然 `no such column: ctypscount` | DDL 补列 + `EnsureBankColumns` 迁移旧库 + `CopyMetadataFrom` 改列交集 |

**高危**

- 删号/重置/停用/改密立即吊销该用户会话（新增 `SessionStore.RevokeUser`）；被删账号由中间件直接拒绝。
- `mustChangePassword` 改为**服务端强制**（admin 无档案也算）。
- 拉取图片扩展名净化 + 落盘路径包含校验（上游 `cextname`/`ctblname` 可造成容器内任意文件写）。
- `BankWriter.ReplaceCourse` 把 delete+insert+水位收进**单事务**；`BankWriteGate` 串行化拉取/上传/清广告。
- 容器加 `TZ=Asia/Shanghai`。

**中/低**

- 全局异常处理（坏 JSON → 400 而非裸 500）；`/api/user/save` 要求 JSON 对象。
- `/api/health` 跑真实查询（库坏了不再报绿）且不再泄露题库规模。
- `ForwardedHeaders` + 状态变更请求的 `Sec-Fetch-Site` 校验。
- `sessions.json` 只存 `SHA256(token)`；凭据/会话/用户 json 权限 `0600`；哈希比较改常量时间；旧 SHA256 档案登录后自动升级 PBKDF2。
- 备份名加随机后缀；备份保留改为**硬上限**（原先"排 20 名外且超 30 天"才删，等于不删）。
- 前导 `//` 路径回 404 JSON；日志查询串脱敏；`/api` 响应 `Cache-Control: no-store`；资产响应加 CSP `sandbox`。
- 新增 `.gitattributes`（行尾统一 LF）；`.dockerignore` 补 `docker-compose.override.yml*` 与 `src/userdata/**`。

**已知限制（本次未改）**：全新空库（未上传真实题库）直接拉取时，拉取库里没有 `course` 行
（`CopyMetadataFrom` 是从主库拷课程行的），该课程不会出现在列表里。正常部署（先上传题库，主库已含全部课程元数据）不受影响。

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

## 前端主题与玻璃层

三套配色：浅色（默认）/ 暗色 / 护眼，靠 `<html data-theme>` 切换，选择存在 `localStorage["yunxi-theme"]`。

### 令牌分层

`public/style.css` 顶部三个块定义全部颜色，**改主题只改这三块**：

| 块 | 作用 |
| --- | --- |
| `:root` | 浅色（默认） |
| `[data-theme="dark"]` | 暗色 |
| `[data-theme="eye"]` | 护眼（强调色是护眼绿，不是品牌靛蓝） |

令牌分两类，**不要混用**：

- **表面类**（`--bg`、`--card`、`--surface-2/3/4`、`--glass-rgb` 等）→ 暗色下压深。
- **文字类**（`--text`、`--text-2/3/4`、`--muted` 系、`--primary-ink`、`--warning-text` 系等）→ 暗色下提亮。
  混用会出「深底深字」。`--primary-600` 是特例：既是按钮 hover 底色（配白字）又是文字色，
  文字那部分已拆成 `--primary-ink`。

### 玻璃层

文件**末尾**的「玻璃层」段（用顺序覆盖前面的实色写法）负责：

- 顶栏跟随主题：浅色 = 品牌靛蓝玻璃 / 暗色 = 深色玻璃 / 护眼 = 浅绿玻璃（令牌 `--nav-*`）。
- 页面底色：**纯色**铺在 `html`（`background-color: var(--bg)`），28px 网格纹理铺在 `body`。
  （原先是 4~5 团彩色光晕 `--bg-image`，2026-09-18 已去掉，见下面「底色改纯色」。）
- 卡片/面板半透明（`--card` 等用 `rgba(...)`）+ 结构性表面加 `backdrop-filter`。
- `@supports not (backdrop-filter)` 退回不透明；`@media print` 强制回浅色不透明。

### iOS 玻璃配方

只写 `backdrop-filter: blur()` 是做不出 iOS 观感的。要五样齐：

```css
.panel {
  backdrop-filter: blur(var(--glass-blur)) saturate(var(--glass-sat)) brightness(var(--glass-bright));
  border-color: var(--glass-border);
  box-shadow: var(--glass-highlight),   /* 镜面高光边（inset 0 1px 0 白）*/
              var(--glass-shade),       /* 内阴影（inset 0 -1px 0 暗）*/
              var(--glass-shadow);      /* 分层投影：1px 贴近 + 大范围柔光 */
}
```

外加顶栏那层「光从上面来」的白色渐变（`--nav-spec`）。

**最容易被忽略的一条：背后必须有颜色起伏。**
`blur()` 只是把背后的东西糊掉，`saturate()` 只是把背后的颜色加浓 —— 背后若是平的，
两个函数都无事可做，玻璃就退化成一块半透明色块。所以页面底色做成了 4~5 团大范围低饱和
光晕（`--bg-image`），面板压到 0.58~0.64 不透明度，光晕才透得出来。

量化判据：量「面板之外」的底色，各通道跨度要足够大（早期版本亮度跨度只有 10.6%，
面板 0.76 不透明 → 透出的差异只有 `0.24 × 10.6% ≈ 2.4%`，肉眼等于没有）。

> **2026-09-18 更新**：那层光晕已按要求去掉（用户觉得花），底色改为纯色。
> 所以现在玻璃表面背后的「颜色起伏」很小，`saturate()` 没什么可放大的 ——
> **玻璃感比之前弱**，半透明主要体现为「能隐约看到背后的网格和下层卡片」。
> 要恢复光晕，把三套 `--bg-image` 定义加回 `:root` / `[data-theme="dark"]` / `[data-theme="eye"]`，
> 并在 `html` 上重新写 `background-image: var(--bg-image)`（详见下面「底色改纯色」）。

### iOS 26 Liquid Glass（折射 + 镜面边）· 已回退，仅存档

> **2026-09-18 已回退。** 用户看过实机效果后认为「太丑」。
> 生产回到本页「玻璃层」那一版（`style.css?v=20260917-glass2`）。
> 液态玻璃版镜像存档为 `yunxi-tiku:local-liquid1`，要再试可以直接起这个 tag。
> 下面这段技术记录保留，供以后**局部**使用（比如只给某一块用），别整体套。

普通毛玻璃只有「模糊 + 半透明」。iOS 26 的 Liquid Glass 多了两样 signature：

| | 做法 |
| --- | --- |
| **折射** | 背后内容在表面边缘被「掰弯」——`<svg>` 里 `feTurbulence` 生成有机噪声 → `feDisplacementMap` 按噪声位移像素，通过 `filter: url(#lg-refract)` 引用。滤镜定义在 `public/index.html` 里 |
| **镜面边** | 一整圈高光（`--lg-rim`）：上沿最亮、下沿次之、一圈内辉光，外加一层外投影 |

**折射必须放在独立的背景图层上**，不能直接挂在表面上：
`filter: url()` 会把元素画出来的**所有东西**一起扭曲，**包括文字**。
不改 HTML 的分层写法（用伪元素）：

```css
.surface          { position: relative; isolation: isolate; background: none; }
.surface::before  { position: absolute; inset: 0; z-index: -1; border-radius: inherit;
                    background: <表面底色>;
                    backdrop-filter: blur() saturate() brightness();
                    filter: url(#lg-refract); }          /* 折射层 */
.surface::after   { position: absolute; inset: 0; z-index: 1; pointer-events: none;
                    box-shadow: var(--lg-rim); }         /* 镜面边 */
```

**背景必须挪到 `::before`**：`backdrop-filter` 采样的是「它下面已经画了什么」，
背景留在 `.surface` 自己身上，`::before` 采到的就是那层平背景，等于什么都没折射。

**折射要有东西可掰。** 背后是平滑渐变时折射看不出来 —— 必须让页底有细节
（这里是 28px 网格，alpha 提到 0.085），并且表面不能太不透明
（顶栏 0.80 / 工具栏 0.48 / 登录面板 0.46）。

**只给不滚动的表面用折射。** 折射层是绝对定位伪元素，挂在 `overflow: auto` 的容器上
会跟着内容滚走。所以折射只给顶栏 / 工具栏 / 登录面板；弹窗面板（有 `overflow: auto`）
用元素自身的磨砂。

**性能**：SVG 位移滤镜每帧合成约 4~8ms，所以只给 3 个主力表面。
实测 1440×900 固定视口下 63fps（vsync 上限），开关折射没有可测差异；
但**别**铺到列表里每个卡片上。

**降级**：`@media (prefers-reduced-transparency: reduce)` 退回不透明；
`@supports not (backdrop-filter)` 退回不透明；滤镜引用不到时浏览器按 `filter: none` 处理。

### 卡片悬浮（hover 抬起 + 加深投影）

只改 `transform` + `box-shadow`，**不动 `color` / `background`** —— 所以文字对比度完全不受影响。

```css
.card:hover {
  transform: translateY(-2px);
  box-shadow: var(--lift-shadow);                      /* 普通卡片 */
  /* box-shadow: var(--glass-highlight), var(--glass-shade), var(--lift-shadow);  玻璃卡片 */
}
```

- `--lift-shadow` 是独立令牌（三套主题各一份），比基准 `--glass-shadow` 更重更散。
- **玻璃卡片的投影必须叠在原有基础上**：它们自带镜面高光 + 内阴影，
  hover 时直接换成一层投影会把玻璃感弄丢。
- `.card-cell.current` 自带一圈实色环，hover 时保留。
- **大面板不参与**（科目区 / 题目区 / 工具条 / 顶栏 / 弹窗）：
  鼠标会长时间停在上面，一直抬着反而烦。
- `prefers-reduced-motion: reduce` → 只留投影，不做位移。

### 响应式：两处已修的既有布局问题

改完配色/玻璃后做过 **12 分辨率 × 3 主题 = 36 组**复查（对比度 + 答题区布局），查出两处
**与主题改动无关的既有问题**（开关 `backdrop-filter` 逐项对比，布局数值完全一致）：

| 问题 | 区间 | 症状 | 修法 |
| --- | --- | --- | --- |
| 题干被挤成一字一行 | 761–1199px | `#questionBody` 两栏布局写死右栏下限 `minmax(330px, 36%)`，题目区只有 386~671px 时正文列只剩 40~280px（768px 实测题干宽 **30px**） | 该区间退回单栏，侧栏内容顺排到题目下方 |
| 答题卡盖住操作按钮 | 761–1440px（卡片展开） | 答题卡是 `position: fixed; z-index: 90`，题目区没为它留底部空间；1440×900 实测卡高 162px，`.question-actions`（z-index 3）整条被压住，看不见也点不到 | `.question-pane:has(> .answer-card-wrap)` 加 `padding-bottom`，值跟随卡片**实测高度** |

> 第二处有两个坑：
> 1. 第一版把 padding 加在内层 `.question-view` 上，**没用** ——
>    内容没超出容器时它不滚动，padding 只是空转。必须让**外层题目区变矮**。
> 2. 第二版写死了 `188px / 64px`，只在 1281px 以上成立。卡片高度随宽度变化极大
>    —— 收起 38~44px；展开 162~462px（题号格子换行行数不同，且受 `max-height: 46vh` 约束）。
>    768px 实测卡高 462px，188px 远远不够。
>    **改成实测高度**：`app.js` 用 `ResizeObserver` 把卡片高度写进 `--answer-card-h`，
>    CSS 用 `padding-bottom: calc(var(--answer-card-h, 44px) + 16px)`。
>    （`+16px` = 卡片 `bottom: 8px` 的偏移 + 8px 间隙。）

### 断点与「跟着窗口缩放」的行为

页面是**响应式重排**，不是等比缩放（文字始终是同一 px 尺寸）。
实测 320~2560px 全程：`documentElement.scrollWidth == innerWidth`（**零横向溢出**），
`.app-shell` 宽度始终等于视口宽。主要断点：

| 断点 | 变化 |
| --- | --- |
| `>1440px` | 答题卡在**文档流内**（`position: relative`），自己占位置 |
| `1281px` | 顶栏由「全部导航项」收成**汉堡菜单**（`.nav-item` 变 `display:none`） |
| `761–1440px` | 答题卡变 `position: fixed` 底部覆盖层（**收起/展开都是**），需预留底部空间 |
| `761–1199px` | 题干退回单栏（侧栏下限 330px 在这个区间必然挤爆正文） |
| `≤760px` | 手机布局；答题卡**收起**时是 `relative`（在流内），**展开**时才变 `fixed` |
| `≤420px` | 更窄的间距/字号调整 |

**`.app-shell` 没有最小宽度。** 它原先有一条 `min-width: 980px`（「桌面端至少 980px」），
但 `@media screen` 和 `@media print` 里各有一处 `min-width: 0` 把它抵消了 ——
print 那处是**多行选择器** `.app-shell, .workspace, .question-pane { … min-width: 0 }`，
用「找单行规则头」的脚本扫不到，容易误判成「只在打印时生效」。
用 CSSOM 遍历（见下）确认屏幕/打印实测计算值恒为 `0px` 后，2026-09-18 已删除该声明。

> 想查某个属性的**真实生效规则**（含多行选择器、`@media` 嵌套）时，
> 别 grep 样式表 —— 遍历 `document.styleSheets[].cssRules`，
> 对每条规则看 `selectorText` 与 `style.<prop>`，同时打印它所在的 `media.mediaText`。

> 判「是不是响应式」别只看截图：量 `scrollWidth - innerWidth` 与 `.app-shell` 宽度。
> 判「元素有没有被盖」用 `elementFromPoint`（中心点是否命中自己），
> 别用矩形相交 —— 被覆盖的只是 padding 时矩形会相交但按钮其实点得到。

### 顶栏 / 工具条降高（>1280px）

用户反馈「顶栏有点高，太占地方」。量下来**原因不在容器，而在子元素**：

| | 改前 | 改后 | 说明 |
| --- | --- | --- | --- |
| 顶栏 `.main-nav` | 62px | **50px** | 高度其实是被 `.nav-item` 撑的，不是 `min-height` 定的 |
| └ 药丸 `.nav-item` | 56px | **44px** | 自然高 = padding 7+10 + 图标 18 + gap 5 + 文字行盒 16.1 = 56 |
| 工具条 `.toolbar` | 61px | **51px** | = padding 10+10 + 按钮 40（`--control-h`） |
| **两条合计** | **111px** | **101px** | 1366×768 上占屏 14.5% → 13.2% |

改法：在文件末尾加一个 `@media screen and (min-width: 1281px)` 块，只覆盖尺寸
（`min-height` / `padding` / `gap` / 图标框），**不动颜色与结构**，所以对比度不受影响。

```css
@media screen and (min-width: 1281px) {
  .main-nav { min-height: 50px; }
  .main-nav > .nav-item { min-height: 42px; padding: 4px 12px 5px; gap: 3px; }
  .main-nav > .nav-item::before { width: 16px; height: 16px; flex-basis: 16px; font-size: 15px; }
  .toolbar { min-height: 50px; padding: 5px 18px; }
}
```

> **为什么必须限定 `min-width: 1281px`**：顶栏高度在三个区间是三套值 ——
> `>1280px` 是 62px、`761~1280px` 是 46px（另一条 `max-width: 1280px` 规则）、
> `≤760px` 是 44~56px（视视口高而定）。直接改共用的 `.nav-item` / `@media screen` 基础规则
> 会把窄屏也一起改掉。加一条更靠后、带断点的新规则，改动面最小。
> 顺带消掉一个跳变：原先 1280px 是 47px、1366px 却是 62px。

> **怎么找「谁撑起了高度」**：别只看容器的 `min-height`。
> 量容器和每个可见子元素的 `getBoundingClientRect().height`，最高的那个就是答案；
> 药丸类元素还要看 `padding` + 图标框 + `gap` + 文字行盒（用 `Range` 量纯文字行盒）四项相加。

### 质感统一：圆角尺度 + 胶囊主按钮

原先 `border-radius` 有 **16 种硬编码值**（4/6/8/9/10/11/12/13/14/15/16/18/20/22/24/999），
同一类组件在不同地方圆角不同，观感就「不统一」。收敛成 5 档 + 胶囊，全部走令牌：

```css
--radius-2xs: 8px;    /* 小元素：图例色块、题号格、开关 */
--radius-sm: 12px;    /* 控件：按钮、输入框、小卡 */
--radius: 16px;       /* 卡片 */
--radius-lg: 20px;    /* 大面板 / 弹窗 / 登录面板 */
--radius-pill: 999px; /* 胶囊：标签、角标、主按钮 */
```

**156 处**硬编码值按就近原则映射到令牌（4/6/8/9→2xs，10/11/12/13→sm，
14/15/16→默认，18/20/22/24→lg，999→pill），并删掉 `@media screen` 里那组重复的
`--radius-*` 覆盖（base 现在就是目标值，留两份只会分叉）。

**主 CTA 统一成胶囊**：`.primary-action`（登录 / 保存并进入 / 创建账号…）与 `#submitBtn`（提交答卷）
→ `--radius-pill`；工具栏其余按钮保持 `--radius-sm`，这样一眼能分出主次。

> 阴影**不用动** —— 全表 80 处 `box-shadow` 已经 100% 走 `var()`，0 处硬编码。

### 手机端底部悬浮标签栏（≤760px）

把顶栏的导航项移到一条固定在底部的悬浮胶囊栏，顶栏只留「当前页名 + 工具 + 账号」。

**关键取巧：新按钮直接复用 `.nav-item[data-mode]`** —— 点击切页、选中态
（`renderMode` 切 `.active`）、图标（`.nav-item[data-mode="x"]::before`）
全部由既有逻辑接管，**一行 JS 都不用加**。顶栏的活动项 `offsetParent` 为 null 时
`updateNavIndicator` 会自动隐藏指示条，所以隐藏顶栏导航项不会留下一条悬空的横线。

底部空间用 `.app-shell { padding-bottom: calc(var(--tabbar-h) + 18px) }` 让出
（`height: 100dvh` + `box-sizing: border-box`，加 padding 让内部区域整体变矮，各面板自己滚）。

**三个必须知道的坑**：

1. **源样式里那批手机端规则用的是裸选择器，会漏进新栏。**
   `.nav-item { display: none }`、`.nav-item.active { order: 1 !important; grid-column: 1 !important;
   flex: 1 1 auto; min-width: 112px; max-width: 140px }`（「把活动项提到最前 / 撑宽」）
   —— 症状是**当前页那个标签自己跳到最后一个**。要在新栏里逐项复位，
   其中带 `!important` 的必须同样用 `!important`。
   同理 `body.admin-view .nav-item { display: none }` 会把标签栏的项也藏掉，
   只留一个空壳胶囊 → `body.admin-view .mobile-tabbar { display: none }` 整条藏掉。
2. **答题卡的定位依赖 `.question-pane` 的 `backdrop-filter`。** 有它时 `.question-pane`
   是 fixed 元素的包含块，展开的答题卡相对面板定位；而 `.app-shell` 已让出底部空间，
   所以自动就落在标签栏上方。**没有它**（`@supports not (backdrop-filter)` 的降级路径）
   答题卡退回「相对视口」，会**盖住标签栏** → 必须在那个 `@supports not` 块里
   显式 `bottom: calc(var(--tabbar-h) + 18px)`。
   > 注意：**用 `*{backdrop-filter:none}` 是测不出这个的** —— 那是运行时关属性，
   > `@supports` 是解析期特性检测，仍然报「支持」。要验证回退，得把回退规则原样注入。
3. **`app-shell` 上加 `padding-bottom` 不等于内容都安全** —— 固定在底部的元素
   （答题卡、工具条浮层）不受它影响，得单独处理。

### 突出做题区：答题卡默认收起

用户原则：**做题区是主角，要给它留够位置**。按这个原则做了一次空间预算审计，
最大的浪费是**答题卡默认展开**：

| 视口 | 改前读题区 | 改后读题区 | 说明 |
| --- | --- | --- | --- |
| 1920×1080 | 664px（61.5%） | **780px（72.2%）** | +116px |
| 1600×900 | 484px（53.8%） | **600px（66.7%）** | +116px |
| 1440×900 | 464px（51.6%） | **546px（60.7%）** | +82px |
| 2560×1440 | 1067px（74.1%） | 1067px（74.1%） | 不变（仍默认展开） |

改法一（`app.js`）：默认收起。答题卡是「跳题用的导航」，不是正文；它一展开就要吃掉
150~460px（宽度越窄格子换行越多）。**只在 ≥1200px 高的屏幕上才默认展开** ——
那时卡片只占约 8% 高度，读题区仍能拿到 ~74%。

```js
function shouldAutoCollapseAnswerCard() {
  const h = window.innerHeight || document.documentElement.clientHeight || 0;
  return h < 1200;   // 只在很高的屏幕上才默认展开
}
```

改法二：**让收起态那条细栏变得有用**。原先它只写「答题卡已收起」——占着 38px 却不说信息。
现在显示实时进度，所以默认收起不会让人少看到东西：

```js
// updateStats() 里
card.dataset.summary = total ? `已做 ${done} / ${total} 题` : "答题卡";
```
```css
body.answer-card-collapsed .answer-card-wrap::before { content: attr(data-summary); }
```
（`<footer class="answer-card-wrap" data-summary="答题卡">` 给了兜底值，
免得 JS 还没跑时渲染成空白。）

> 顺带确认：`#questionView` 的内容高度 ≤ 它的可视高度（1440 下 374 vs 464），
> 也就是说**普通长度的题目本来就不用滚动**，这次腾出来的高度是留给长题（案例分析等）的余量。

### 底色改纯色（去掉彩色光晕 + 网格纹理）

用户反馈「底色的渐变不要了，难看」，看过两版对比后又确认**连网格一起去掉**。

页面底色原本有**两层装饰**，现在都没了：

| | 原来 | 用途 | 现在 |
| --- | --- | --- | --- |
| 彩色光晕 | `html` 上 4~5 团径向渐变（`--bg-image`） | 给玻璃的 `saturate()` 提供「可放大的颜色起伏」 | 删除 |
| 网格纹理 | `body` 上 28px 网格（`linear-gradient` ×2） | 桌面端原有纹理；后为给折射提供「可掰的细节」把 alpha 提上去 | 删除 |

```css
/* 改前 */
html { background-color: var(--bg); background-image: var(--bg-image);
       background-repeat: no-repeat; background-attachment: fixed; }
body { background: <28px 网格 ×2>, var(--bg); background-size: 28px 28px, 28px 28px, auto; }

/* 改后 —— 底色只在 html 上定义一处，body 透明 */
html { background-color: var(--bg); }
body { background: transparent; }
```

三套 `--bg-image` 定义（`:root` / dark / eye）一并删除，原地留注释说明
「为什么曾经有 / 为什么去掉 / 怎么恢复」。

**去掉的代价（原理决定，不是 bug）**：

1. **玻璃感变弱** —— 没有颜色起伏，`saturate()` 无事可做。半透明现在主要体现为
   「能隐约看到下层卡片」。**要补回来只能把「背后的颜色起伏」加回去**，
   别去调 `blur` / 不透明度（那补的是"糊"不是"玻璃"）。
2. 网格是给折射用的（折射已回退），去掉后纯属减噪。

> **排查时注意：网格有两处定义。** 一处是 `@media screen` 里的
> `body { background: <网格>, var(--bg) }`，另一处是文件末尾玻璃层里的
> `body { background-image: <网格> }`（后加的覆盖层）。只删末尾那条，`@media screen`
> 里那条会**接管显示**，看起来像"没删掉"。同理 `.login-view::before` 里还有一条 34px 网格，
> 已被玻璃层的 `background: none` 覆盖（不显示）。
> **改背景类属性前，先用 CSSOM 或 `getComputedStyle` 确认「谁在真正生效」**，别只 grep 一处。

> 恢复方式：把 `--bg-image` 定义加回对应主题块并在 `html` 上引用（光晕）；
> 或恢复 `body` 的网格三条声明（纹理）。两者独立，可只恢复一个。

> 后续又按要求做了调整（背景改成极淡单色明暗、面板降不透明度、投影加强），见下节。

### 悬浮毛玻璃：单色明暗 + 厚玻璃

用户要求「悬浮毛玻璃效果」，明确不要彩色光晕、不要网格。

#### 先说结论：纯色/渐变底上，毛玻璃「物理上做不出来」

`backdrop-filter: blur()` 只能糊掉**高频细节**。平滑渐变糊前糊后几乎一样，
所以**"模糊"这个效果本身看不见**。要让它可见，背后必须有高频细节（纹理）；
而纹理会让背景"不干净" —— **两者互斥**。

量化判据：量元素矩形内的**高频能量**（水平相邻像素亮度差的平均绝对值）。
纯色底上面板内外都是 ~0.0；铺 3px 点阵后面板外 5.9 / 面板内 0.0。

#### 试过并否掉的（都实测过）

| 做法 | 结果 |
| --- | --- |
| SVG `feTurbulence` 噪点（`feColorMatrix type='saturate' values='0'`） | **方差被削**：三个独立噪声通道做加权平均，对比度只剩约 `1/√3`。alpha 从 0.028 提到 0.075，面板外高频只从 0.59 → 0.90，肉眼不可见 |
| CSS 点阵 3px 锐点 | 缩略显示下呈**"布纹"**，一眼可见 → 背景不干净 |
| CSS 点阵 12px 柔点 | 放大后是**"波点纸"** → 同样不干净 |

#### 最终采用（不依赖"透光"，靠光影做悬浮）

1. **背景**：极淡**单色**明暗（`--bg-image`，白 → 中性灰，幅度约 5%，
   大范围平滑过渡所以看不出"渐变"，只有一点光感）。
2. **面板降不透明度**：`.login-panel` 0.82 → **0.62**。
   ⚠️ 结构性表面的不透明度是**硬编码**的（`0.78~0.96` 散在 20 多处，没走 `--card` 令牌）；
   目前只调了 `.login-panel` 这一处。
3. **悬浮**：`--glass-shadow` 两层 → **四层**：
   `0 0 0 1px`（外描边，定义边界）+ `0 1px 2px`（贴地）+ `0 8px 18px`（中层）
   + `0 24px 52px`（大范围柔光，负责"浮起来"）。
4. **厚度**：`--glass-highlight` 从 `inset 0 1px 0` → `inset 0 1.5px 0` + 一圈 `inset 0 0 0 1px`。

**验收**：面板内高频 `0.000`。面板确实比背景"干净"，但差异来自「明暗透出」而非
「纹理被糊」—— 这是纯色底上能做到的天花板。

> **层数自洽（又踩一次）**：`background-image: var(--bg-noise), var(--bg-image)`
> 是 1+3 = **4 层**，而 `background-size: 140px 140px, auto` 只有 **2 个值**
> → 被循环补齐成 4 个，第 3 层（渐变）被当成 140px 平铺。
> **单值可以（应用到所有层），多值必须写全。**
> 最后改成「渐变铺 `html`（3 层 / 单值 `no-repeat`）、颗粒铺 `body`（1 层）」才自洽。

### 审计工具的两处修正（暴露出一批既有问题）

调这个效果时发现审计脚本本身有两个盲点，都会**漏检**：

1. **`bg.frac` 阈值 0.34 对"小元素"过严。** 按钮/标签里文字笔画占比高，
   底色众数占比自然低 → 被整片跳过（顶栏导航、主题按钮、账号区）。
   改成**先用众数，占比 < 0.34 时再"排除文字色附近的像素"重算一次**（阈值 200）。
2. **排除文字色后，众数不能再落在抗锯齿过渡色上。** 之前"白字在靛蓝上"被算成
   `rgb(125,116,223)`（过渡色）→ 3.86，而真值是 5.49。

修完后**多查出 23 处不达标**，全部是**既有的**（采样对比过改动前后，顶栏颜色分布
几乎逐色一致：`rgb(104,93,217)` 16.2% vs 13.1%）。主要是：

| 位置 | 数值 | 原因 |
| --- | --- | --- |
| 顶栏「退出」「账号：xxx」「暗/护」按钮 | **3.08 ~ 3.9** | `--nav-bg` 是 **0.80 不透明**（不是 0.92），再叠 `--nav-spec`（30% 白高光）+ `--nav-pill-bg`（14% 白）→ 按钮区域底色被提亮到 `rgb(125,116,223)`，白字压不住 |
| 护眼底部标签栏「做题/错题/收藏」 | 3.3（**假阳性**，静态算真值 **7.23**） | 众数落在文字抗锯齿过渡色上 |

**顶栏那批是真问题**，修法（已算过，能让白字回到 ~5.0）：

```css
--nav-bg:      rgba(69, 54, 207, 0.80) → rgba(64, 50, 190, 0.92)   /* 提高不透明度 = 更深 */
--nav-spec:    rgba(255, 255, 255, 0.30) → 0.14                     /* 削弱顶部白色高光 */
--nav-pill-bg: rgba(255, 255, 255, 0.14) → 0.10
```

**代价是顶栏会更"实"、玻璃感更弱**，所以**没有擅自改**，留给用户决定。

### 护眼主题：正确态加深（`--success` 在绿底上失效）

用户反馈「护眼答题区的正确答案颜色不明显」。

**根因**：护眼的 `--success-*` 色阶是为「不刺眼」整体调成**低饱和灰绿**的
（`--success-50: #e4f1e0`），而护眼的**页面底色本身就是豆沙绿** `#c7edcc`
—— 两个绿一叠，正确选项和普通选项**背景只差 6~7 个色阶**，肉眼分不出。

**对照发现**：`.card-cell.correct`（答题卡格子）用的是 `--success-200` + `--success-500`，
本来就深一档，所以格子一直没问题 —— 说明是「选项 / 答案区」这一档选浅了。
**修法就是把它们提到同一档。**

| 选择器 | 改前 | 改后 |
| --- | --- | --- |
| `[data-theme="eye"] .option.correct` | 边框 `--success-300` / 底 `--success-50` | 边框 `--success-700` **2px** / 底 `--success-200` |
| `[data-theme="eye"] .option.correct b` | 底 `--success-100` / 字 `--success-700` | 底 `--success-700` / **白字** |
| `[data-theme="eye"] .answer` | 边框 `--success-200` / 底 `--success-50` | 边框 `--success-700` / 底 `--success-200` |
| `[data-theme="eye"] #answerText` | 继承正文色 | `--success-800` + `font-weight: 800` |
| `[data-theme="eye"] .mark.correct` | 底 `--success-200` | 底 `--success-500` |
| `[data-theme="eye"] .review-line.correct` | 底 `--success-50` | 底 `--success-200` |

> **边框也要取深一档。** `--success-500: #16a34a` 对面板底只有 **2.96**，
> 而非文本对比度要求 3.0 —— 差一点点。改用 `--success-700: #14682f`（6.17）。

对比度（对面板底 ≈ `rgb(230,247,232)`）：正文 **9.29** / 字母标白字 **6.88** /
`#answerText` **5.88** / 边框 **6.17** —— 全部达标。

**只改护眼**：浅色（白面板 + 淡绿底）和暗色（深底 + 亮绿）实测本来就清楚，色相拉得开。

### 顶栏去掉白色高光渐变（「像褪色了一样」）

用户反馈「顶部的渐变还是不好看，像褪色了一样」。

`.main-nav` 上有一条：

```css
background-color: var(--nav-bg);                                          /* 靛蓝 */
background-image: linear-gradient(180deg, var(--nav-spec), transparent 58%);  /* --nav-spec = 30% 白 */
```

30% 白从顶栏上沿往下洗到 58% 处 —— **靛蓝被洗成"褪色的蓝"**。

量化（顶栏空白处取平均，从上到下）：

| 主题 | y=2（顶部） | y=44（底部） | 差 |
| --- | --- | --- | --- |
| 浅色 | `rgb(146,137,227)` | `rgb(105,93,217)` | **+41** |
| 护眼 | `rgb(245,252,246)` | `rgb(226,249,229)` | +19 |
| 暗色 | `rgb(52,63,84)` | `rgb(28,39,63)` | +24 |

**改法**：删掉那条 `background-image`。`--nav-spec` 令牌保留，只用于
`box-shadow: inset 0 1px 0 var(--nav-spec)`（下沿 1px 高光，玻璃的厚度）。
改后三主题顶栏 `getComputedStyle` 的 `background-image` 均为 `none`，逐行取色**完全均匀**。

#### 顺带修掉顶栏按钮的对比度（同一个根因）

上一节报告过「顶栏按钮白字只有 3.08~3.9」，根因就是这层白 —— **去掉渐变后升到 4.09**，
再把按钮自己的白底压一档就达标了：

| 令牌 | 改前 | 改后 | 说明 |
| --- | --- | --- | --- |
| `--nav-bg`（浅色） | `rgba(69,54,207,0.80)` | `rgba(66,52,198,0.84)` | 略深一点，给白字留余量 |
| `--nav-pill-bg`（浅色） | `rgba(255,255,255,0.14)` | `rgba(255,255,255,0.03)` | 白底是"把靛蓝洗浅"的第二个来源 |
| `--nav-pill-hover`（浅色） | `rgba(255,255,255,0.26)` | `rgba(255,255,255,0.10)` | 同理 |

**为什么必须压到 0.03**：靛蓝 `rgb(105,93,217)` 的 L≈0.158，白字只有 5.05 ——
**本来余量就很小**，任何白底叠加都会把它推到 4.5 以下。0.14 时是 3.87，0.06 时 4.09，
0.03 才回到 4.5 以上。（按钮的"可点击感"靠 `--nav-border` 的 1px 白边，不靠底色。）

**结果**：审计从 21 处降到 **5 处**，且全部是护眼的既有项（底部标签栏 / 状态胶囊，
静态算真值 6.77~7.23，属审计工具的假阳性）。

> **教训**：在"中等亮度"的底上放白字，余量天生很小。
> **任何"白色高光/白色胶囊"叠加都会吃掉对比度** —— 加高光前先算一遍，
> 别等审计报出来才发现。

### 答题卡四态配色：格子与图例对齐 + 护眼「未做」中性化

用户反馈「答题卡区域的正确和未做，颜色也不好区分」。

#### 根因一：护眼的「未做」格子就是页面底色，而页面底色是绿的

`.card-cell` 的默认态是 `background: var(--bg)`。浅色主题里 `--bg` 是近白 `#f8fafc`，
所以「未做」= 一个安静的近白小格；但**护眼的 `--bg` 是豆沙绿 `#c7edcc`**
—— 于是护眼里「未做」变成了**一整片绿格子**，正好撞上「正确」的绿。

实测填充色两两距离（RGB 欧氏）：

| 主题 | 未做↔正确 | 未做↔已做 | 未做↔错误 | 最小的一对 |
| --- | --- | --- | --- | --- |
| 浅色 | 35.1 | 53.8 | 35.9 | 已做↔错误 31.9 |
| **护眼** | **18.5** ← | 46.2 | 47.0 | **已做↔错误 15.3** |
| 暗色 | 32.3 | 44.3 | 39.5 | 已做↔错误 19.3 |

护眼的 18.5 是全场最小值 —— 用户看到的就是这一条。

#### 根因二：图例小方块和实际格子**不是同一个色**

`index.html` 的图例是三个 `.mark` 小方块（已做/错误/正确），样式：

```css
.mark.selected { background: var(--warning-150); }   /* 深一档 */
.mark.correct  { background: var(--success-200); }   /* 深一档 */
.mark.wrong    { background: var(--danger-200);  }   /* 深一档 */
```

而 `.card-cell.*` 的**基础**规则（L1600 附近）用的也是 `--*-200`，看起来一致 ——
但文件后面还有一个 `@media screen` 块用**同选择器**重新声明了一遍，用的是浅一档的
`--*-100`。**同选择器、同特异性，靠后的赢** → 实际生效的是浅的那套。

实测差：浅色 33.9~61.1 / 护眼 38.4~52.7 / 暗色 13.2~38.4 个 RGB 距离。
用户看到的图例是「深绿」，格子是「浅绿」，两种绿。

#### 改法

1. **把 `@media screen` 里那套浅的改回 `--*-200`**，与基础规则、与图例三者同值。
   实测改后三主题的「格子色」与「图例色」**逐值相等**。
2. **护眼的「未做」不再用 `--bg`，改成中性近白 `#f6faf7`**
   （语义上回到「未做 = 最安静的一格」，和浅色主题一致）。
3. **护眼「正确」加深**到 `--success-200` + `--success-700` 边框
   （与上一轮 `[data-theme="eye"] .option.correct` 同一套处理）。
   上一轮把 `[data-theme="eye"] .mark.correct` 单独加深成了实心深绿 `--success-500`，
   这次一并回到与格子同色，否则图例又会和格子对不上。
4. **图例补一个「未做」小方块**（`.mark.todo`），用户才能把中性小格对上号。
   ⚠️ 必须加在**第 7 个 span**（末尾）—— 手机端有
   `.stats span:nth-child(n + 4) { display: none }`，放到前面会挤进手机端的三格布局。

#### 结果

两两填充色距离（改后）：

| 主题 | 未做↔正确 | 未做↔已做 | 未做↔错误 | 已做↔正确 | 已做↔错误 | 正确↔错误 | 最小值 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 浅色 | 75.3 | 114.6 | 69.6 | 97.1 | 74.4 | 80.9 | **69.6** |
| 护眼 | 101.9 | 83.4 | 94.3 | 57.7 | 38.9 | 54.5 | **38.9** |
| 暗色 | 65.0 | 56.5 | 75.7 | 49.7 | 34.4 | 66.4 | **34.4** |

改前最小值 15.3（护眼）→ 改后最小值 34.4（暗色），护眼那一对从 **18.5 → 101.9**。

文字对比度（对各自格子底色）：浅色 5.09/5.89/5.88/5.74、护眼 7.25/5.67/5.88/5.81、
暗色 7.26/8.74/7.50/8.47 —— 全部 ≥ 4.5。

> **边框也要跟着深一档**：护眼「正确」的边框若用 `--success-500` `#16a34a`，
> 对卡底只有 **2.81**（非文本对比度要求 3.0）→ 改用 `--success-700` `#14682f`（6.17）。

#### 遗留（**没改**，等用户定）

`app.js:3118` 里「未做」在交卷后被打上的是 **`wrong`** 而不是独立状态：

```js
if (state.submitted || isQuestionVerified(item.id)) {
  if (!hasAnswer(item.id)) btn.classList.add("wrong");      // ← 未做 = 错误
  else if (item.detail) btn.classList.add(isAnswerCorrect(item.detail) ? "correct" : "wrong");
}
```

→ 交卷后**「没做」和「做错」长得一模一样**（都是红色）。改法是加一个 `todo` class
并在 CSS 里给一套中性色，但这属于产品语义（有人可能正靠「未做显示为红」来扫漏题），
**没有擅自改**。

### 顶栏账号改「头像 + 下拉菜单」；手机端顶栏只留两个图标

用户反馈「手机页面模式下，底部已经有 dock 栏，那么顶部可以省略一些东西吧，都挤到一起去了」
「账号不用显示名字，用个图像代替」「退出可以做到账号按钮下级菜单」。

#### 改前：手机顶栏（390px）三个大药丸，账号文字压在「工具」上

| 元素 | 宽度 |
| --- | --- |
| ☀ 智能训练 | 90px（底部 dock 已经高亮「训练」，重复） |
| ⚙ 工具 | 75px |
| 账号区 | **172px（44vw，占 44%）** |

账号区里塞了 4 样：`账号：xxx` + 保存状态 + `浅/暗/护` + `退出` → **内容超出 172px，
文字直接压在「工具」按钮上**。这是被反复打补丁缩出来的：`#accountLabel` 的
`max-width` 一路 `130 → 112 → 104 → 96 → 92 → 88 → 74px`，高度 `36 → 34 → 32 → 30 → 28px`。

#### 改法

1. **账号区 → 一个 40px 头像按钮（通用用户剪影 SVG）+ 下拉菜单**，菜单里放：
   用户名 / 保存状态 / 配色模式（浅暗护）/ 退出登录。**全部宽度生效**。
2. **手机端（≤760px）隐藏模式按钮与「菜单」按钮**，「工具」改纯图标
   → 顶栏只剩「工具 + 账号」两个图标。顶栏内容 337px → 约 90px。
3. **顺带补 iPhone 安全区**（改前完全没有）。

#### 三个必须记的坑

**① `backdrop-filter` 让顶栏自建层叠上下文，菜单的 `z-index` 出不去。**
`.main-nav` 上有 `backdrop-filter: blur(28px)`。菜单里写 `z-index: 140` 只在「顶栏这一层」
内部比大小；顶栏本身 `z-index: auto`，被 `.admin-toolbar`(5) 这类页内 sticky 元素盖住，
菜单跟着一起被盖 → **点不到「退出登录」**（Playwright 报
`admin-toolbar ... intercepts pointer events`）。
**要抬的是顶栏本身，不是菜单。** 顺手理顺全局层级：

| 层 | z-index |
| --- | --- |
| 页内元素（粘性表头 1~5 / 答题卡 90 / 底部 dock 96） | 0–99 |
| **顶栏（含账号下拉菜单）** | **100** |
| 全屏浮层与弹窗（`.modal-backdrop` / `.assign-overlay`） | **200** |
| `.toast` | **300** |

> 顺带修掉一个**既有 bug**：改前 `.mobile-tabbar`(96) 与 `.assign-overlay`(90)
> 都压在 `.modal-backdrop`(50) 之上 —— 也就是**手机上弹窗的遮罩盖不住底部 dock**。

**② 加 `!important` 会短路 `@supports` 开关。**
我本想给 `.question-pane > .answer-card-wrap` 的 `bottom` 加上标签栏高度与安全区，
于是写了一条带 `!important` 的同选择器规则。结果 **320×568 上 `#options` 被答题卡遮挡**，
27 分辨率审计从「布局 OK」变成「布局问题」。

排查：那条「`bottom: calc(var(--tabbar-h) + 18px)`」的规则**被包在
`@supports not (backdrop-filter: …)` 里** —— 只在浏览器**不支持**磨砂时才该生效
（那时 `.question-pane` 不再是 fixed 的包含块，卡片会退回相对视口定位）。
我的 `!important` 把开关短路了：卡片的 `bottom` 从 **8px 变成 72px**，
被顶上去 64px，正好压住 `#options` 的中心点（卡 374 vs options 中心 379）。

**教训**：给一条「看起来等价」的规则加 `!important` 之前，
**先确认它在哪个条件块里**（`@supports` / 媒体查询 / 嵌套）。
改完用「改前文件跑同一脚本」做 A/B —— 这次就是靠 A/B 才定位到的。

**③ 手机端 `overflow-x: hidden` 会把下拉菜单切掉。**
`.main-nav` 在 ≤760px 有 `overflow-x: hidden; overflow-y: visible`。
**CSS 规定：一个轴是 `visible`、另一个不是时，`visible` 会计算成 `auto`** →
菜单被纵向裁掉。顶栏现在只有两个图标、不需要横向裁剪，所以显式 `overflow: visible !important`。

#### 保存状态：用 `:has()` 做状态点，**零 JS**

手机上既有规则把 `#saveStatusLabel` 整个 `display: none` 了（**改前手机上完全没有保存反馈**）。
这次把它放进菜单，并在头像右上角加一个状态点，靠 `:has()` 驱动，不写一行 JS：

```css
.account-btn::after { opacity: 0; background: var(--warning-500); }
.account-box:has(#saveStatusLabel[data-status="dirty"])  .account-btn::after { opacity: 1; }
.account-box:has(#saveStatusLabel[data-status="saving"]) .account-btn::after { opacity: 1; background: var(--primary-200); }
.account-box:has(#saveStatusLabel[data-status="error"])  .account-btn::after { opacity: 1; background: var(--danger-500); }
```

下拉菜单的底色：`--card` 是半透明的，直接铺在靛蓝顶栏上会发紫。
用 `background-color: var(--bg)` 打底再叠 `linear-gradient(var(--card), var(--card))`
= 得到不透明的「卡片色」，不用新增令牌。

#### iPhone 安全区（改前完全没有）

```html
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
```

配套 `env(safe-area-inset-*)`（非刘海机型取 0，等于没变）：

| 位置 | 处理 |
| --- | --- |
| 顶栏 | `padding-top: calc(5px + inset-top)`；左右 `calc(8px + inset-left/right)`（横屏刘海在侧边） |
| 底部 dock | `bottom: calc(10px + inset-bottom)`；左右同理 |
| 内容底部留位 | `.app-shell { padding-bottom: calc(var(--tabbar-h) + 18px + inset-bottom) }` |

#### 遗留（**没改**，等用户定）

- 菜单里**保留了用户名**（`账号：xxx`）。头像本身是通用剪影，但菜单里显示名字 ——
  本机可在多个账号间切换，需要知道当前是谁。若不想显示，删掉 `.account-menu-head` 里的
  `#accountLabel` 即可（但 `setText("accountLabel", …)` 会找不到元素，需要一并处理）。
- 顶栏高度 47 → 49px（`5 + 38 + 5 + 1px 边框`），基本持平。

### 761–1280px 顶栏常显导航；「菜单」折叠退役（删 428 行死代码）

上一节末尾留的问题：「761–1280px 的『菜单』展开态很乱，要不要清一次？」

#### 量化之后发现：折叠本身就是多余的

那个区间顶栏只显示「当前模式 + 菜单 + 工具 + 账号」——实测：

| 视口 | 顶栏内容总宽 | 可用宽 | 浪费 |
| --- | --- | --- | --- |
| 761px | **232px** | 761px | 529px |
| 900px | **232px** | 900px | 668px |
| 1280px | **232px** | 1280px | 1048px |

6 个导航项紧凑排列只需约 **600px**（< 761px），**折叠毫无必要**。

#### 改法：常显 6 项，菜单退役

`@media screen and (min-width: 761px) and (max-width: 1280px)` 里让
`.main-nav > .nav-item`（含 `.active`）全部 `display: inline-flex`，紧凑成
**40px 高的纵向小胶囊**（图标 15px + 文字 12px），并隐藏 `#mobileMenuBtn`。

结果：761px 下 6 项合计 396px，加工具(50) + 账号(40) 共 486px，一行放下、无重叠。

> **踩到的坑**：基础 `.nav-item` 是 `flex-direction: column`。一开始只把高度压到
> **34px**，而「图标 17 + 间距 4 + 文字 16 = 37 > 34」→ **图标和文字叠在一起**。
> 纵向布局的高度必须按「图标 + 间距 + 文字 + 内边距」给足，并同步缩小图标。

#### 顺手删掉 428 行死代码

`#mobileMenuBtn` 退役后，`body.mobile-menu-open` **永不可能出现**，
76 个规则块（散在 30 处、跨 3 个断点、大量 `!important`）全是死代码 —— 已全部删除。

**这里踩了一个必须记的坑**：很多规则是**多选择器列表**，例如

```css
.nav-item.active,
body.mobile-menu-open .nav-item { display: inline-flex; }
```

**整块删会连带删掉 `.nav-item.active` 这条活规则**（我第一版就是这么删的，
写完发现 76 块里有 **18 块**是这种「部分含」的情况）。必须**按选择器粒度**处理：
列表里全部含才删整块，部分含就只摘掉那几个、保留其余并重写规则头。

**验证「删掉的确实是死代码」用逐像素对比**：8 个宽度（390 / 760 / 761 / 900 /
1100 / 1280 / 1281 / 1440）改前改后各截一次 —— **7 个宽度 0 差异**，
只有 900×700 有 8465 个像素最大差 **2**（抗锯齿噪声）。这才敢说「只删了死代码」。

### 交卷后「未作答」独立成 `todo`；「未做」统一中性灰

上一节末尾留的第二个问题：「交卷后『没做』和『做错』长得一模一样」。

`app.js` 里：

```js
if (state.submitted || isQuestionVerified(item.id)) {
  if (!hasAnswer(item.id)) btn.classList.add("wrong");   // ← 未作答 = 错误，同色
  else if (item.detail) btn.classList.add(isAnswerCorrect(item.detail) ? "correct" : "wrong");
}
```

改成 `btn.classList.add("todo")`。

#### 顺带把「未做」统一成中性灰（而不是页面底色）

原来「未做」是 `background: var(--bg)` —— 浅色下 `--bg` 是近白，看着没问题；
但**护眼的 `--bg` 是豆沙绿**，于是护眼里「未做」变成一整片绿格子，和「正确」撞车
（这就是更早那轮「护眼正确 vs 未做分不清」的根源）。

既然现在要给交卷后的「未做」一个**独立于红色的颜色**，索性统一：

```css
.card-cell {
  border-color: var(--muted);
  background: var(--surface-4);   /* 半透明，会透出卡片底 */
  color: var(--text-3);
}
```

`--surface-4` 三主题都不用额外覆盖，于是**图例 `.mark.todo` 与实体格子逐值一致**
（之前图例是深色、格子是另一档，是两种绿/两种黄）。护眼那两条专用覆盖也一并删掉。

实测（改后）：

| 主题 | 文字对比度 | 边框对卡底 | 与正确 | 与错误 | 与已做 |
| --- | --- | --- | --- | --- | --- |
| 浅色 | 4.61 | 5.24 | 60.5 | 59.1 | 107.7 |
| 护眼 | 5.99 | 6.52 | 51.2 | 61.4 | 55.8 |
| 暗色 | 6.01 | 6.95 | 40.1 | 56.4 | 44.5 |

（填充色 RGB 欧氏距离，全部 ≥ 40；「未做」与卡底 24.1~28.8，看得出是「一格」。）

### 761–1100px：答题卡「42vw 左下角浮层」删除，改为铺满底部

用户贴了 1080×1080 的截图：「这也是问题」。

#### 根因在 `drawer.css`（不在 `style.css`）

```css
/* drawer.css:64-71 */
/* Keep the main question column clear on narrow desktop viewports. */
@media screen and (min-width: 761px) and (max-width: 1100px) {
  .question-pane > .answer-card-wrap {
    right: auto;
    width: min(clamp(280px, 42vw, 520px), calc(100vw - 16px));
    max-height: min(46vh, calc(100dvh - 16px));
  }
}
```

实测（1080×1080）：答题卡 `[25, 743, 454, 312]`，`width: 453.594px`（= 42vw），
**右侧空出 584px**；而 1101–1440px 本来就是铺满底部的（`left: 8px; right: 8px`）。

**注释写的意图和实际效果正好相反**：注释说「给窄桌面视口让开主问题栏」，
但题目区在这个区间是**单列全宽**（实测 `.question-pane` 的
`grid-template-columns` 只有 `1046px` 一列）—— 浮层贴左只会**遮住问题栏本身**，
同时把右边空着。这个注释应该是题目区还是多列布局时写的，布局改了、注释没跟着改。

**次生问题**：卡片只有 404px 宽时，底部图例的 7 项放不下 → 挤成 2~3 行
（「○ 未做」孤零零占一行）。上一节加的「未做」图例项是导火索，但根因是卡片太窄。

#### 改法：删掉这条规则

761–1100px 与 1101–1440px 一致铺满底部。实测：

| 视口 | 改前卡片 | 改后卡片 | 图例 |
| --- | --- | --- | --- |
| 1080×1080 | `[25,743,454,312]` 42vw | `[25,1017,1030,38]` 铺满 | — |
| 1024×768 | `[25,402,430,341]` 42vw | `[25,540,974,203]` 铺满 | **2 行 → 1 行** |

**顺带的好处：卡片反而变矮了。** 格子是 `repeat(auto-fill, minmax(34px, 1fr))`，
铺满后一行能放 24 个（原来 8 个）→ 行数少了 → 卡片高度 **312px → 203px**，
**遮住的题目内容比改前更少**。

内容不会被永久遮住：`.question-pane` 的 `padding-bottom` 由 `--answer-card-h`
动态跟随卡片高度（实测卡片 312px → padding-bottom 328px），可以滚出来。

> **留下的话**：若将来真要「让开主问题栏」，前提是那个区间改成多列布局；
> 单列全宽下这条规则没有意义。已把原规则和被删原因写进注释，避免有人再加回来。

### 手机端顶栏：左侧显示当前科目名，内容区那条收掉

用户贴了手机截图问「顶上空不空？」。

#### 实测：左边空 287px（76%）

上一节把手机端顶栏精简成「工具 + 头像」之后：

| | |
| --- | --- |
| 顶栏 | 390×49 |
| 可见项 | 「工具」`[287..331]` + 「账号」`[344..382]` |
| **左边空白** | **287px = 76%** |

而顶栏**正下方**就是 `.course-head` 里的科目名（`.course-head` 高 31px）——
两边都放会紧挨着重复。

#### 改法：搬进顶栏 + 内容区收掉

1. `.main-nav` 最前面加 `<span id="navCourseTitle">`，**只在 ≤760px 显示**
2. `app.js` 加 `setCourseTitle()`，把 `#courseTitle` 与 `#navCourseTitle` 文案同步
   （原来两处调用 `setText("courseTitle", …)` 直接换成 `setCourseTitle(…)`）
3. ≤760px：`#courseTitle` 收掉、`.course-head` 的上下 padding 归零

结果：**左边空白 287px → 8px**；内容区顶部省下约 29px
（resaudit 实测 `options底 485 → 456`）；科目名在滚动时也常驻。

**管理视图同样适用** —— 实测 `body.admin-view` 下顶栏与学员视图完全一样
（工具 + 账号、左边空），`.course-head` 也只显示「管理员数据看板」一个标题，
搬进顶栏不丢信息。顶栏显示「管理员数据看板」。

#### 一个差点丢信息的细节

`#courseMeta`（「建筑工程 一级造价工程师 · 12283题 · 更新 5 天前」）在用户截图里是**可见**的，
但按 CSS 推算 ≤760px 应该被隐藏 —— 对不上。于是**扫了 14 个宽度**（320→1100）实测：

| 宽度 | `.course-head` | `#courseMeta` | 底部 dock |
| --- | --- | --- | --- |
| ≤760 | 隐藏 | **隐藏** | 可见 |
| ≥761 | 可见 62px | **可见 16px** | 隐藏 |

**两者互斥**（dock 只在 ≤760 显示，meta 只在 ≥761 显示）。

结论：≤760 时 `.course-head` 只剩科目名，收掉不丢信息。但为了绝对保险，
实现上**只收 `#courseTitle` 和 padding，不动 `.course-head` 整条** ——
万一 meta 在某些设备上可见，也不会被藏掉；而实测 ≤760 时它会收成 0 高，
效果等同于整条隐藏。

> **教训**：`display` 的可见性只由「媒体查询 + 源顺序」决定，与设备/字体无关。
> 当「截图看到的」和「按 CSS 推算的」矛盾时，**扫一遍宽度区间**比继续推理快得多。

### 科目名 = 切换科目的入口（所有分辨率）

用户问「要把它做成『点一下切换科目』的入口吗？现在换科目得绕到底部 dock 的『题库』」，
答「大概是这么个思路，各个分辨率下都看一看」。

#### 先说一个查出来的事实：`#coursePickerBtn` 是**死 UI**

`index.html` 里有个「切换科目」按钮，样式也写好了（`--primary-50` 底 + `--primary-ink` 字），
但 **`style.css:1035` 无条件 `display: none`，翻遍全部备份都没见过它被放出来**。
实测 15 个宽度（320→1920）确认：**所有宽度都是 `display:none`**。

所以 ≥761px 之前换科目**只有一条路**：点顶栏的「考试题库」。
那是 `app.js:7640` 的隐藏交互 ——

```js
if (nextMode === "practice" && state.mode === "practice") {
  setCoursePicker(!state.pickerOpen);   // 点「当前那个模式项」→ 开关科目面板
  ...
}
```

**界面上没有任何提示**。而 ≤760px 时顶栏的模式按钮全被隐藏（只留「工具 + 账号」），
所以手机上只能绕底部 dock 的「题库」（同一段代码）。

#### 改法：科目名本身就是开关

| 分辨率 | 入口 | 说明 |
| --- | --- | --- |
| ≤760px | `#navCourseTitle`（顶栏） | 屏幕上唯一能看到科目名的地方 |
| ≥761px | `#courseTitle`（内容区标题） | 同一份文案 |

两者都从 `<span>` / `<div>` 改成 `<button type="button" aria-haspopup="true" aria-expanded="false">`，
点击 → `toggleCoursePicker()`（`app.js`）。右侧加一个**用边框画**的下拉箭头
（两个 2px 边旋转 45° —— 不依赖字体里有 `▾` 这个字符），面板展开时翻转向上。

`setCoursePicker()` 里同步 `aria-expanded`，读屏能知道开合状态。

**管理看板下不给「可点」的暗示**：`body.admin-view` 里 `.course-pane` 是 `display:none`，
根本没有科目可选 → `pointer-events: none` + 隐藏箭头。

#### 踩到的坑：`inline-block` 会让父容器多出行盒

第一版把 `#courseTitle` 写成 `display: inline-block`（想让它只包住文字）。
结果 `.course-head` 的高度 **61.6px → 63.9px**：

```
courseHead   61.6 → 63.9   (+2.3)
courseTitle  top 129 → 131.3
courseMeta   top 155.8 → 158
#stem        top 253.6 → 255.9
#options     top 364.7 → 367
```

**整个内容区下移 2.3px。** 原因：`inline-block` 在块容器里会生成一个**行盒**，
行盒按 `line-height` + 基线对齐算高度，比原来纯块级的 24.78px 多出 2.3px。

改法：`display: block` + **`width: fit-content`** —— 既回到块级（不生成行盒），
又只包住文字（不会变成一条横贯整行的可点区域）。改完几何**逐值回到改前**。

#### 顺带：全局 `button:hover` 的特异性比单个 class 高

```
button:hover            → (0,1,1)
.course-title           → (0,1,0)   ← 更低
```

所以 `.course-title` 里设的 `background: transparent` 会被 `button:hover` 的
`background: var(--primary-50)` 盖掉。必须显式写 `.course-title:hover { … }`
或加 `!important`（这条用了后者，和文件既有风格一致）。

#### 验证

- **同脚本 A/B 逐像素对比**（同一份脚本跑改前/改后文件各一次）：
  ≤760px 差异 **168 像素**、≥761px 差异 **164 像素**，区域分别是
  `(185..196, 22..30)` 和 `(164..178, 89..146)` —— **正好就是那个箭头**，其余零差异。
  （对照组：同一份代码跑两次 = **0 差异**，所以这个对比是可信的。）
- 15 个宽度 × 开/关各一次：全部 `ok@#navCourseTitle` / `ok@#courseTitle`，
  `body.picker-open` 正确开关，`aria-expanded` 同步
- 管理看板 390 / 900：两个入口都是 `pointer-events: none`，点击不打开面板
- 15 视图审计：6 处不达标（**全是既有的护眼底部标签栏/工具条**，已知假阳性），0 页面错误
- 27 分辨率 × 3 主题 = **81 组**：0 处不达标、布局 OK、0 页面错误

> **待清理**：`#coursePickerBtn` 现在彻底没用了（`display:none` + 与科目名重复）。
> 代码和样式都还在，没删 —— 删它属于「清理死代码」，等确认后再动。

### 清理死代码：`#coursePickerBtn` + `#mobileMenuBtn`

上一节末尾留了「`#coursePickerBtn` 现在彻底没用了，你说一声我再动」，本节就是动手的结果。
顺手把同类问题的 `#mobileMenuBtn` 一起清了。

#### 先量，再判「死」

**差点误判**：第一次量 `#mobileActionsBtn` 是「不可见 0x0」，差点把它当死代码删掉。
实际是**探针跑在「训练」页**——`body.panel-page .question-actions { display:none }` 把它父级藏了。
换到答题页（用底部 dock 的「题库」进入）后实测 **≤760px 可见（390 下 115×36）**。
**它不是死代码**，`body.mobile-actions-open` 也是活的。

最终的判定（13 个宽度实测）：

| 元素 | 结论 | 证据 |
| --- | --- | --- |
| `#coursePickerBtn` | **死** | 320→1920 全宽度 `display:none` |
| `#mobileMenuBtn` | **死** | 全宽度 `display:none`；`setMobileMenu(true)` 唯一触发点是它自己的 `onclick` |
| `#mobileToolsBtn` | 活 | ≤1280 可见（`≤760` 44×38 / `761–1280` 50×34） |
| `#mobileActionsBtn` | 活 | ≤760 可见（390 下 115×36） |

`#mobileMenuBtn` 死了之后，`state.mobileMenuOpen` 恒为 `false`、`body.mobile-menu-open` 永不出现
—— 上一轮已经删掉的 428 行规则确实不会再生效。

#### 删了什么

| 文件 | 改动 | 行数 |
| --- | --- | --- |
| `style.css` | 按选择器粒度：**整块删 19 块 + 摘除 12 块**（共 31 处） | 11546 → 11407 |
| `app.js` | 删 `setMobileMenu` 整个函数、`#mobileMenuBtn` 的创建、`state.mobileMenuOpen`、2 处重置调用、2 处 `onclick`、`#coursePickerBtn` 的文案更新与绑定 | 7847 → 7797 |
| `index.html` | 删 `#coursePickerBtn` 那一行 | 313 → 312 |

**一个必须小心的连带点**：`ensureMobileControls()` 里 `#mobileToolsBtn` 的插入锚点原来是
`#mobileMenuBtn`（`insertBefore(btn, menuBtn.nextSibling)`）。直接改成 `appendChild`
会把它排到**头像右边**，顶栏顺序就反了。改成以 `[data-action="refresh"]`（= `.account-box` 前）为锚点。

#### 两个自己踩的坑

**① 我的 CSS 解析脚本判断媒体条件一直是错的。**
用「数花括号」的做法被文件里的**字符串**（`content: "{"`）打乱 —— 把
`@media screen and (min-width: 1281px)` 里的规则报成「无媒体查询」，
差点据此把 `#mobileToolsBtn` 的隐藏规则删掉。改成逐字符扫描（跳过注释、跳过字符串、括号感知）。

**② 选择器前面的注释被算进 header**，导致 `@media` 被误判成普通规则 ——
而我在注释里写过 `#mobileMenuBtn`，于是**整块注释被当成「死规则」要删掉**。
现在先把前导注释剥掉再分类。

**③ 第一版把「body 区间」当成「选择器区间」用**，等于用选择器文本覆盖掉整个声明块
（幸好 dry-run 后立刻发现并回滚）。现在加了**声明块保全校验**：
所有「至少保留一个选择器」的规则，body 必须逐字节不变（本次 1641 块全等）。

#### 验证

- **同脚本 A/B 逐像素对比：15 个宽度全部 0 差异。** 两个被删元素都是 `display:none`，
  所以「0 差异」正是预期结果 —— 这是「只删了死代码」最强的证据。
- 15 个宽度：两个元素确认「不存在」；`#mobileToolsBtn` 尺寸/位置与改前**逐值相同**；
  顶栏可见子元素顺序仍是 `navCourseTitle > mobileToolsBtn > account-box`
- 「工具」「操作」两个面板都能开合（`body.mobile-tools-open` / `mobile-actions-open`）
- 控制台 **0 报错**（漏删 `$("mobileMenuBtn").onclick` 之类的绑定会立刻抛）
- 15 视图审计：8 处不达标（**全是既有的护眼标签栏 / 管理看板标签**，已知假阳性），0 页面错误
- 27 分辨率 × 3 主题 = **81 组**：0 处不达标、布局 OK、0 页面错误

> **教训**：删死代码的安全网不是「看代码像死的」，而是
> **① 多状态、多分辨率实测它的可见性；② 改前改后逐像素必须 0 差异。**

### 全盘复查 + 修复：键盘焦点完全不可见

用户：「全盘复查一下」。做了 6 个维度的检查，**查出一个真问题并修掉了**。

#### 真问题：键盘用户看不到焦点在哪

全局有一条 `button, select, input, textarea { outline: none }`（约 L185），
而按钮**没有**自己的 `:focus` 样式（`input/select/textarea` 有 `:focus` 的 box-shadow，按钮没有）。

判据（**不是看有没有 outline，而是对比「聚焦前后」的 computed style**）：
给每个 Tab 到的元素打一个临时 `data-` 标记，全部失焦后再按标记查回来读一次，逐属性 diff。

```
修复前：26 个可聚焦元素，26 个「聚焦前后完全无变化」
修复后：26 个可聚焦元素， 0 个无变化
```

修法：给 `button / [role=button] / a / summary / [tabindex]` 加 `:focus-visible` 焦点环，
颜色用 `--primary-ink`（浅色 6.01 / 护眼 5.90 / 暗色 9.34，均 ≥ WCAG 2.2 焦点外观要求的 3:1）。

**顶栏 / 底部 dock 用 `currentColor`，不用固定色。** 这两处的底色在「深靛蓝」和
「浅色药丸（选中项）」之间来回 —— 写死任何颜色都会在另一半上消失。
`currentColor` 永远等于该元素自己的文字色，而文字色必然是配着它自己那块底色选的。

**验证**：同脚本 A/B 逐像素 **15 个宽度全部 0 差异**（焦点环只在键盘聚焦时出现）。

#### 另外 5 个维度的结果

| 维度 | 结果 |
| --- | --- |
| 运行时报错 | **0 条**（20 余个状态：6 个模式 + 管理看板 6 个页签 + 各弹窗 + 窄屏） |
| 对比度 | 15 视图 8 处（**全是既有的护眼标签栏 / 管理看板标签**）；81 组 0 处 |
| 布局 | 81 组全部 OK；8 档**等效缩放**下无横向溢出 |
| 打印 | `@media print` 正确隐藏顶栏/工具条/科目栏/答题卡，只留题干 |
| CSS 结构 | 花括号平衡；0 处可疑注释；`var()` 引用全部有定义；重复 id 0 |

#### 复查方法上踩的两个坑

**① `zoom: 2` 不是浏览器缩放的等效模拟。** 真缩放是**缩小 CSS 视口**（1440@200% → 720 CSS px），
而 `document.documentElement.style.zoom = 2` 只是把 1440 的布局放大成 2880 → 必然「溢出」。
改成正解：直接扫等效视口宽度（1152/960/823/720/512/384）→ 全部 OK。

**② 程序化 `.focus()` 不触发 `:focus-visible`。** 第一版焦点环截图用
`element.focus()` 聚焦后截图 → 截图里**没有环**，差点以为修复没生效。
`:focus-visible` 只在**键盘发起的**聚焦时匹配。必须真按 `Tab` 走到那个元素再截图。

#### 发现但**没动**的（产品判断，不是纯技术清理）

- **22 条整块无用 CSS 规则 / 14 个类名**（`exam-review-panel` ×12、`tag-editor` ×6、
  `tag-presets` ×6、`review-line` ×5、`quick-mark-row` ×3、`admin-only` ×2 …），
  看着是**已下线功能**留下的样式；`app.js` 里 `$("examReviewPanel")` 还在引用一个不存在的元素
  （有 `?.` 保护，不会抛错）。
- **2 个定义了但从未 `var()` 引用的令牌**：`--bg-soft`、`--card-solid`（各 5 处定义）。
- **技术债数字**：`!important` **665** 处；同一媒体条件下**重复声明**的选择器 **160 组**
  （`.main-nav` 在 `≤760px` 里写了 **14 次**）；规则体里字面 hex **281 处**。
- **不跟随系统深色偏好**（无 `prefers-color-scheme`）—— 主题是手动的，属设计选择。
- **工程卫生**：本轮所有改动**未提交 git**（7 文件 / +2935 −891）；
  Docker 回滚镜像累积 **14 个 × 289MB ≈ 4GB**。

### 考试复盘：实测「做了且能用」，另修掉一个状态条 bug

用户看了全盘复查报告里「`exam-review-panel` 看着是已下线功能的残留」这句，问
**「考试复盘没做好吗？」**。我上一轮那句是**猜的、不准确**，这轮端到端实测了一遍。

#### 结论：复盘**做了**，功能完整

完整跑一遍模拟考场（组卷 → 答 5 题 → 交卷 → 回考场首页 → 展开明细），实测结果：

```
考场首页「模拟记录」卡片：
  1 / 100 分   1/5 题 · 20%   2026-09-19 02:31:52 · 用时 0:06
  ▼ 查看明细：错题 4 道
     1  单选题 · 2026年一级造价工程师《建设工程计价》模考密训卷7
        答：A / 正：B · 0/1分
        非发包人要求，因承包人原因自行提前竣工，增加的费用应由（）承担。
     2  单选题 · …高频考点C      答：A / 正：D · 0/1分
     …
```

- 保留**最近 5 场**，按当前科目过滤（`renderExamHistory`）
- 每条给：分数 / 做对题数 / 正确率 / 交卷时间 / 用时 / 是否自动交卷
- 「查看明细」列出**错题**：题号 · 题型 · 章节 · **答：X / 正：Y · 得分** · 题干
- 考错的题**会进错题本**（默认「待复习」筛选看不到，切「全部错题」能看到）
- 控制台 0 报错

#### 但 `.exam-review-panel` 确实是个**空壳**（不是「已下线」）

```js
function renderExamReview() {          // 调用时传了 q，函数签名却没有参数
  $("examReviewPanel")?.remove();      // 只删，从不创建
}
```

- `renderQuestion()` 里 `renderExamReview(q)` 和 `renderQuestionTags(q)` 并排调用 ——
  原设计是**答题时侧栏就能看复盘**（`ensureQuestionSidePanel()` 里也预留了
  `["questionTagPanel", "examReviewPanel", …]` 要搬进去）
- 但 `#examReviewPanel` 全项目**从未被创建**，12 条 `.exam-review-panel` CSS 白写
- 所以准确说法是：**规划了、骨架搭了（CSS + 函数名 + 侧栏挂载点），实现没写** ——
  不是「做了又下线」

#### 顺带修掉一个真 bug：交卷后回考场首页，状态条不隐藏

实测（对比每一步的 `#examStatus`）：

| 步骤 | 状态条 |
| --- | --- |
| 刚进考场首页 | 隐藏 ✓ |
| 开考后 | 显示 `模拟考场 149:58 满分 100 分` ✓ |
| 交卷后 | 显示 `… 149:55 … 得分 0/100，正确率 0%` ✓ |
| **回考场首页** | **仍然显示上一场的分数和冻结的计时** ❌ |

根因：`renderExamHome()` 里已经 `stopExamTimer()` + `state.exam = null`，
但**没有调用 `renderExamStatus()`** —— 那个函数靠 `!state.exam` 来决定隐藏，
不调用就保留上一次渲染的内容。补一行即可。

**顺带发现自己的疏漏**：改完 `app.js` 忘了提 `index.html` 里的 `app.js?v=` ——
版本号还是上一轮的 `dead1`，浏览器会继续用缓存的旧文件。
**改了静态资源就必须提版本号**，这条已写进 skill 的交付前自检。

#### 复盘还剩下的短板（**未改**，属产品判断）

1. **入口太深**：模拟记录卡片在考场首页**最底部**（实测 `top=982`，视口只有 900），
   要滚过整块组卷配置才看得到；**交卷后没有任何「查看复盘」的直达入口**。
2. **明细只列错题**（全对时退化成「本卷前 12 题」）—— 想看自己答对的那道怎么答的，看不到。
3. **明细不可点**：每行是纯文本，不能点进去看解析或重做。
4. 只保留最近 5 场。

### 四个坑

1. **别把网格和 `var(--bg-image)` 塞进同一个 `background-image`。**
   `--bg-image` 是 3~4 层，若 `background-size` 只给 3 个值，CSS 会把值**循环补齐**，
   多出来的层被当成 28px 平铺，整页背景被反复刷薄色。渐变与网格要分开写在不同元素上。
2. **改完必须复查对比度**（正文 ≥ 4.5:1，大字 ≥ 3:1）。
   压深底色会连带压低次要文字（`--muted`）的对比度——护眼主题就踩过一次：
   `--bg` 从 `#dfe8d5` 压到 `#cfdfc6` 后，`--muted #5d6a51` 从 4.56 掉到 4.12。

### 复查做法

在隔离容器里跑无头 Chrome，注入 `data-theme` 后逐元素算实际对比度（沿祖先累积半透明背景），
覆盖登录页 / 答题页 / 学习看板 / 错题本 / 管理看板 / 弹窗 × 三主题。目标是 **0 处不达标**。
隔离容器用临时 data/userdata，**不要**指向生产数据目录；新建账号的默认口令是启动时随机生成的，
要可复现就给它加 `-e App__DefaultLoginPassword=...`。

### 复查的坑（审计工具本身）

1. **别用「沿祖先累积 background-color」算底色。** 玻璃面板背后是 `html` 的渐变/光晕，
   一遇到 `background-image` 就没法算，整片「面板上的文字」会被静默跳过 —— 恰好是玻璃改动最该查的地方。
   改成**截图 + 取元素矩形内的众数颜色**当实际底色，不受渐变/半透明/模糊影响。
2. **矩形要与所有 `overflow != visible` 的祖先求交。** 被滚动容器裁掉的元素 `getBoundingClientRect()`
   仍然返回位置，但那里画的其实是别的东西，直接采样会得到一堆假阳性
   （踩过：9 处「indigo 上的深色字」，实际是滚动区外的章节行压在「进入做题」按钮上）。
3. **PNG 解码放 Node 里做，别用第二个页面 + canvas。** 用 helper 页面解码大图后，
   `page.screenshot` 会卡死拿不到帧（45s/120s 超时都试过）。Node 内置 `zlib` 手写 PNG 解码器即可。
4. **弹窗打开时不要点主题按钮**（会点到遮罩上把弹窗关掉），直接改 `<html data-theme>` 属性。
   另外登录失败要**立刻断言报错**，否则会在登录页上「审计」出一堆看着正常的假结果。

### 改主题时容易漏的

- **主题作用域的覆盖**：暗色主题里有一条 `[data-theme="dark"] .login-view`（特异性 0,2,0），
  只写 `.login-view`（0,1,0）在暗色下会被它压住。改背景类属性时要把主题作用域的选择器一并列出。
- 顶栏 / 页底 / 面板是三层不同的东西，别指望改一个就都跟着变。

改完样式表记得同步改 `public/index.html` 里 `style.css?v=` 的版本号，否则浏览器会吃缓存。
## 全模块测试与修复（2026-09-19）

对全项目 **26 个模块**做了一轮「独立可用性 + 集成可用性 + 缺失模块」审计（报告见
`_verify/parallel-test/全模块并行测试报告.md`）。结论：**26/26 模块都能正常使用，无阻断级故障**；
但查出 **6 个功能受损级问题**，本节记录这 6 项修复。

### 修了什么

| # | 问题 | 根因 | 修法 |
| --- | --- | --- | --- |
| P-1 | **多选/不定项：用户全对却判错** | `normalizeAnswer` 只 trim + 去分隔符，**不排序**；而 `chooseOption` 写用户答案时会 `.sort()` → 两侧顺序不一致就比较失败 | `normalizeAnswer` 内对多字符答案排序（`hasAnswer` 看长度、部分分用 Set，均不受影响） |
| P-2 | **考场永久改掉用户「验证模式」偏好** | `startExamPaper` 里 `settings.verifyMode = "paper"` + `scheduleSave()`，全文件没有任何地方恢复 | 改为 `currentVerifyMode()` 在 `state.mode === "exam"` 时直接返回 `"paper"`；`startExamPaper` 不再写 storage |
| P-3 | **管理端错误提示显示 `[object Object]`** | 后端 `SafeError` 返回的是对象 `{ok,error}`，被包进 `new { ok, error = SafeError(ex) }` → `error` 字段嵌套成对象 | `SafeError` 改为返回字符串；前端 `api()` 另加类型防御 |
| P-4 | **退出/切账号时保存失败静默丢数据** | `saveUserData()` 4 个调用点只有 1 个挂了错误处理（`logout`/`switchUser`/`visibilitychange` 都是 `.catch(() => {})`） | 三处都补上：**有未保存改动时**提示并阻止退出；页面隐藏时失败退回 beacon、回前台补存 |
| P-5 | **会话失效后停在应用内，没有出路** | `api()` 收到 401 只让调用方 toast，页面不跳转，用户继续操作持续失败 | `api()` 统一处理 401（排除 `/api/auth/*`）→ 可选导出备份 → 回登录页 |
| P-6 | **组卷「清空章节」语义与行为相反** | `selectedIds.size ? examChaptersFromIds(...) : examChapterGroups()` —— 空集合回退成**全章节** | 空选择时禁用「开始组卷」+ `startExamPaper` 加守卫（提示"请至少选择一个章节"） |

### P-1 的实测证据（用真实题库数据）

全库 14838 条多字符答案里查出 **4 条未排序**（`BED` / `ABED` / `BED` / `BCED`）+ **1 条脏数据**
（`canswer = "D\n考点\n分项工程（8）"`）。修复前：

```
题 4157980  题库 "BED"  用户全对勾选(排序后) "BDE"
            isAnswerCorrect = false  ❌ 判错      得分 1.5 / 满分 2  ❌ 少给 0.5
题 24940959 题库 "D\n考点\n分项工程（8）"      得分 0 / 满分 1  ❌
```

**副作用比判错更麻烦**：`markResult` 会把它们**永久记为错题** → 用户在错题本里反复复习一道
"永远答不对"的题。修复后 5/5 全对给满分。

### 顺带修的数据问题（题库 4 行）

| 题号 | 原值 | 改为 | 依据 |
| --- | --- | --- | --- |
| 24940959 | `canswer = "D\n考点\n分项工程（8）"` | `"D"` | 单选题答案被解析文本污染；解析里写的就是"分项工程"，选项 D |
| 24941404 | `isubjecttype = 0` | `1` | 题干"下列选项中，属于…的是"、5 个选项、答案 `BC` → 实为多选 |
| 24941405 | `isubjecttype = 0` | `1` | 同上（答案 `ABCE`） |
| 24941406 | `isubjecttype = 0` | `1` | 同上（答案 `BD`） |

> 这 3 题虽然 `isubjecttype` 标成单选，但 `isMultiChoice()` 是按**答案长度**推断的，
> 所以交互上一直是多选；标错只影响**题型筛选归类**与**每题分值**（单选 1 分 / 多选 2 分）。

### P-2 的一个细节（值得记）

一开始只把 `startExamPaper` 里的三行删掉是不够的 —— `renderVerifyModeControls()` 会
`state.verifyMode = currentVerifyMode()`，而 `currentVerifyMode()` 读的是**用户偏好**，
于是考试期间用户偏好如果是「立即反馈 / 背题模式」，**答案会在考试中被揭示**（泄漏）。

所以真正的修法是让 `currentVerifyMode()` 在考试期间**直接返回 `"paper"`** ——
把"考试期间应该怎样"交给**模式**判断，而不是去改用户偏好。这一处同时修好了
`shouldRevealCurrentAnswer`、`shouldAutoVerifyInstant`、`renderVerifyModeControls` 三个消费方。

### P-4 的一个坑（我自己踩的）

第一版给 `logout` 补了提示后，**15 视图审计脚本挂了** —— 登录时点不到按钮，报
`conflict-modal intercepts pointer events`。

原因：`saveUserData()` 是**无条件 POST** 的，所以「没有未保存改动 + 另一个标签页先保存过」
也会 409。旧代码 `.catch(()=>{})` 把它吞了，新代码弹了冲突框 → 挡住后续操作。

修法：**只在 `state.storageDirty` 为真时才提示**。没有未保存改动时，退出本身是安全的，
不该弹框。双标签页实测：409 仍然发生，但正常退到登录页、不弹框；
有未保存改动时仍会弹 confirm + toast。

> 教训：给"静默吞掉的错误"补提示时，**要先分清哪些错误对用户是"无事发生"**，
> 否则会把正常流程也打断（尤其是有并发/多标签场景时）。

### 验证

- 判分单元 8/8（含顺序不同、少选、多选含错）+ 真实题库 5/5
- P-2：考试期间 `currentVerifyMode()` = `paper`、答题不泄漏答案；退出考场后偏好仍为 `instant`（刷新后也是）
- P-3：`/api/admin/course-update-check`、`/api/admin/update-bank` 的 `error` 字段已是**字符串**；管理端 toast 显示完整文案
- P-4：双标签页场景（无未保存改动）正常退出不弹框；有未保存改动时弹 confirm + toast
- P-5：清 cookie 后点选项 → 回到登录页 + 询问是否导出备份
- P-6：清空章节后按钮禁用（title 提示），强行调 `startExamPaper` 被拦住，重新全选后恢复可用
- 回归：考场端到端（组卷→答题→交卷→记录→明细）通过；练习↔看板往返进度保住；0 控制台报错
- 15 视图：不达标全是**既有假阳性**（护眼底部标签栏 / 管理看板标签）；27 分辨率 × 3 主题 81 组通过

### 没修的（体验瑕疵，已记录）

训练卡片题量文案与实际不符（"20 题"→实际 60）、组卷章节选择不记忆、刷新后不恢复上次模式、
题量填 0 无效、错题本默认「待复习」看不到刚考的错题、打印 `cleanupPrintView` 后 DOM 残留。
另有 14 项可补充模块建议，见测试报告第五节。

## 体验打磨与可补充模块（2026-09-19，polish1）

承接上一节。上轮修完 6 个**功能受损级**问题后，本轮把剩余的 **6 条体验瑕疵**与 **8 项可补充模块**
一并做完，并对全部改动做了运行时验证 + 两套审计。

### 一、6 条体验瑕疵

| # | 问题 | 修法 |
| --- | --- | --- |
| P-7 | 训练卡片题量与实际不符（写「20 题」实际给 60） | 卡片题量改为**调用真实生成逻辑**取长度（`buildSmartPracticeIds` / `buildSprintPracticeIds` / `buildSimilarWrongIds`），在 `renderTraining` 里算一次给三张卡片共用 |
| P-8 | 组卷章节选择不记忆 | 新增 `persistExamChapters()`，按科目 id 存进 `settings.examChapters`；`renderExamHome` 进页面时恢复（存的是空数组则按默认全选） |
| P-9 | 刷新后不恢复上次模式 | 新增 `RESTORABLE_MODES` + `persistLastMode()`（`renderMode` 里记录，仅变化时保存）；`enterApp` 恢复。考试与智能训练有各自的会话/草稿机制，**不在**恢复范围内 |
| P-10 | 组卷题量填 0 无效（仍出整卷） | 题量全为 0 时禁用「开始组卷」并给 title 提示；`startExamPaper` 加同款守卫；题量输入框加 input 监听 |
| P-11 | 错题本默认「待复习」看不到刚考的错题 | 空状态改为 `buildEmptyMessage()`：区分「确实没有错题」与「错题还没到期」，后者给出最近到期天数并引导把筛选切到「全部」 |
| P-12 | 打印视图 cleanup 后 DOM 残留 | `cleanupPrintView()` 改为真正 `remove()`；新增 `ensurePrintView()` 供 `renderPrintView` 按需重建 |

### 二、8 项可补充模块

| # | 模块 | 实现 |
| --- | --- | --- |
| S4 | 用户端数据导出/备份 | 账号菜单新增「导出我的数据」，下载含 `user` / `revision` / `data` 的 JSON（此前只有管理端能下载，学员无法自己备份） |
| S6 | 学习时长统计 | `recordPracticeActivity` 按相邻答题间隔累计，单次上限 5 分钟（防挂机）；`dailyActivity` 新增 `durationMs`（日级 + 课程级）；看板显示「用时 X」 |
| S7 | PWA / 离线 | 新增 `public/manifest.json` + `public/sw.js`；SW 只缓存同源静态资源，`/api/` 一律走网络，导航请求 network-first（保证发版能拿到新版本） |
| S10 | 错题导出 | 错题本新增「导出全部错题」，不受当前筛选/分页限制，分批拉全后走既有打印管线（浏览器可另存为 PDF） |
| S11 | 复习到期提醒 | 错题本导航项加 `data-badge` 角标（桌面顶栏与手机 dock 共用同一选择器），在 `updateStats()` 里刷新 |
| S12 | 纠错闭环 | 提交过纠错的题目在题号栏旁显示「已提交纠错 / 纠错已处理」角标；提交后立即重绘 |
| S13 | 跟随系统深色 | 主题增加第 4 个选项「跟随系统」；`data-theme` 放实际生效配色、`data-theme-choice` 放用户选择；监听 `prefers-color-scheme` 变化 |
| S14 | `renderExamReview` 空壳 | 删除死函数、`ensureQuestionSidePanel` 里的挂载点引用，以及 `style.css` 里 12 处 `.exam-review-panel` 死 CSS |

### 三、顺带修掉一个构建阻断

新增 `public/manifest.json` 后镜像构建失败：

```
error NETSDK1022: Duplicate 'Content' items were included. ...
The duplicate items were: 'public/manifest.json'
```

Web SDK 默认把 `**/*.json` 当 Content（`EnableDefaultContentItems`），与 `YunxiTiku.Web.csproj`
里显式的 `public\**\*` 重复。修法：在同一个 ItemGroup 里先 `<Content Remove="public\**\*.json" />`
再显式 Include。**注意：以后往 `public/` 加任何 `.json` 都会撞上这个问题**（`Remove` 已覆盖，不用再改）。

### 四、验证（27/27 通过，每项都有运行时证据）

- **P-7** 三张卡片题量分别 = 实际生成题量（60 / 100 / 0）
- **P-8** 全选 257 → 取消 2 个后 197 → 离开再回来仍是 197
- **P-9** practice → 刷新 → 仍是 practice（80 题）；回归脚本也验证了刷新后题号与已答数保住
- **P-10** 题量全 0 时按钮禁用（title 提示），强行 `click()` 被守卫拦住
- **P-11** 区分「无错题」与「未到期」两种空状态文案
- **P-12** `renderPrintView` 后 `#printView` 存在，`cleanupPrintView` 后不存在
- **S4** 点击真的触发下载，JSON 含 22 个数据键
- **S6** `getTodayActivity().durationMs` 正确；看板显示「用时 1 小时 2 分」
- **S7** manifest / sw.js 均 HTTP 200；安全上下文下 SW 注册成功（`reg:1, ctrl:true`）
- **S10** 导出生成「错题导出（N 题）」打印视图
- **S11** 有到期错题时角标出现、无到期时消失
- **S12** 提交纠错后题目立即出现「已提交纠错」角标
- **S13** 选择「跟随系统」被记住；系统切暗/切浅时 `data-theme` 跟着变
- **S14** 函数 / CSS / DOM 引用全部为 0
- 全程 **0 控制台报错**

**回归**：考场端到端（组卷→答题→交卷→记录→明细）通过；练习↔看板往返题数/题号/已答数全保住。

**审计**：
- 15 视图对比度：**6 处不达标，全部与上轮完全相同**（护眼主题弹窗里的底部标签栏采样假阳性）→ **0 新增**
- 27 分辨率：**0 处不达标、0 页面错误**

### 五、已知限制与坑

- **PWA 只在安全上下文生效**：`serviceWorker` 要求 HTTPS 或 localhost。当前部署是内网 HTTP
  （`0.0.0.0:8787`），浏览器不会注册 SW，`manifest.json` 也需要 HTTPS 才能用于「安装到桌面」。
  若通过 HTTPS 反向代理 / 隧道访问则自动生效。注册失败有 `catch` 兜底，**不影响任何既有功能**。
- **错题记录的字段在顶层，不在课程级**：`getWrongRecordsForCurrentCourse()` 对
  `courseStore.wrong` 只取 **id**（字段是硬编码默认值），完整记录（`stage` / `count` / `lastReviewAt`）
  在**顶层** `state.storage.wrong`。写错题相关代码或测试时务必注意。
- **主题有两个属性**：`data-theme` = 实际配色（CSS 令牌块认它），`data-theme-choice` = 用户选择
  （含 `auto`，按钮高亮认它）。首屏内联脚本两者都会设，`initTheme()` 兜底补 `data-theme-choice`。
