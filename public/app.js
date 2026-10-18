const state = {
  mode: "training",
  user: "",
  users: [],
  courses: [],
  adminCourses: [],
  chapters: [],
  types: [],
  questions: [],
  analysisQuestions: [],
  analysisCourseId: 0,
  analysisLoadingCourseId: 0,
  /* 外挂章节映射：subjectId → 课本章名数组（一题多章）。见 loadChapterMap。 */
  chapterMap: new Map(),
  /* qid → chapterId 本地缓存的进程内镜像（键是 courseId）。见 questionChapterCache。 */
  qcMaps: new Map(),
  currentCourse: null,
  currentChapter: null,
  currentIndex: -1,
  answers: {},
  submitted: false,
  submitting: false,
  answerVisible: false,
  questionRequestId: 0,
  typeRequestId: 0,
  modeTransitionId: 0,
  submitToken: 0,
  zoom: 1,
  storage: {},
  storageRevision: "",
  /* 服务端读用户数据**失败**（5xx / 网络断）→ 只读态。
     这个基线是空的，但它**不是**「这个账号没有数据」：绝不能拿它去保存，
     否则第一次整包回写就把用户真实进度覆盖掉（实测 supa.json 2651 → 700 字节）。
     只读态下：scheduleSave 直接 return、saveUserData 拒发、顶部横幅提示。 */
  storageReadFailed: false,
  storageReadErrorMessage: "",
  saveTimer: null,
  saveChain: null,
  saveErrorShownAt: 0,
  storageDirty: false,
  saveStatus: "idle",
  /* storage 的**变更代际**。saveChain 只串行化网络请求，不跟踪「请求发出去之后
     storage 又被改过」；没有这个计数，v1 的响应回来时会把 v2 标成已同步
     （关页即丢，见 saveUserData / ST-05）。每次 markStorageDirty 自增。 */
  mutationVersion: 0,
  /* 上下文**世代**：每次 loadUserData / enterApp / logout 自增，
     用来隔离上一个账号（或上一次读数据）的在途保存响应。 */
  loadGeneration: 0,
  /* 「成功保存」的单调计数。只用来判一件事：一次 /api/user/load 拿回来的快照，
     在它飞的过程中有没有被本页自己的保存超过。
     超过就不能用（那份 revision 已经旧了，拿它去存必然 409）——见 loadUserData 的重读。 */
  saveSuccessSeq: 0,
  /* 「服务端已知内容」的快照（JSON 文本），用于增量保存算 diff。
     只在两个时机写：load 成功之后、保存成功之后 —— 它必须始终对应 state.storageRevision，
     所以一旦 revision 对不上就直接放弃增量、改发整包（见 buildPatchPayload）。 */
  syncedBody: "",
  /* 冲突弹窗打开时记录「是哪一次 storage 变更触发的」，用来判断恢复后
     storage 是否已经变了（见 applyPendingSaveDraft / keepLocalConflictDraft）。 */
  conflictGeneration: -1,
  conflictDraft: null,
  conflictDraftId: "",
  /* 允许删除**已确认内容**对应的草稿：loadUserData 确认了服务端就是这份内容之后，
     才准把对应 draftId 定向删掉（「已确认」的显式凭证）。 */
  draftConfirmations: [],
  /* 开页时发现有「revision 已过期、补存不了」的草稿：不弹模态，只挂这个入口，
     用户点账号菜单里的保存状态再打开冲突框（见 openPendingConflict）。 */
  pendingConflict: null,
  conflictOpen: false,
  conflictLocalSnapshot: null,
  pendingPasswordUser: "",
  pendingOldPassword: "",
  pickerOpen: false,
  mobileActionsOpen: false,
  mobileToolsOpen: false,
  answerCardCollapsed: false,
  answerCardUserTouched: false,
  /* 「显示/隐藏答案」的**显式**覆盖位（S-34）。
     旧实现只靠 state.answerVisible，而 shouldRevealCurrentAnswer 在背题模式下恒真，
     于是背题模式里点两次「隐藏答案」后按钮写回「显示答案」、答案却还在屏上 ——
     按了没用。现在隐藏 = 置 true，任何自动显示规则都要先过这一关；换题/交卷清零。 */
  answerHiddenOverride: false,
  mobileTagsOpen: false,
  tagPanelQuestionId: 0,
  /* 错题本筛选。`review` 默认 "all"：错题本的第一诉求是「我错过的题在哪」，
     默认串「只看到期」会把刚答错的题全滤掉（stage 0 的到期日是答错**次日**，
     今天答错的今天永远不到期）→ 列表空、请求都不发，用户说「错题看不了」。
     「到期」作为可选筛选保留在下拉里。 */
  wrongFilters: { review: "all", status: "active", minCount: "1", period: "all" },
  tagFilter: "",
  answerCardPage: 0,
  answerCardPageSize: 50,
  verifyMode: "paper",
  shuffledOrder: null,
  shuffleKey: "",
  adminRows: [],
  adminFailedCount: 0,
  adminView: "users",
  adminBankQuery: "",
  adminBankCategory: "",
  adminBankSubcategory: "",
  /* 「更新」页签里一级分类的折叠表。**默认全折叠**：664 门课全展开会渲染 674 个按钮、
     页面高 26 万像素（见 ensureAdminBankCollapsedInit）。 */
  adminBankCollapsed: {},
  adminBankCollapsedInit: false,
  adminBankStatus: "",
  adminUpdateCheck: {},
  adminCourseAssign: null,
  adminBankUpdateMode: "",
  adminBankUpdateModePromise: null,
  adminBankLastReport: null,
  adminBankLastReportAt: "",
  // 正在进行的拉取（可同时多门课）：courseId -> {courseId, courseName, startedAtLocal, stageLabel,
  // current, total, detail, percent, queued, unknown, elapsedSeconds, done, ok, error, finishedAtLocal}
  adminBankPulls: {},
  adminDataStatus: null,
  adminBackups: null,
  adminBackupsError: "",
  adminBackupBusy: "",
  adminCleanAdsResult: "",
  adminEditorCourseId: 0,
  adminEditorChapterId: 0,
  adminEditorChapters: [],
  adminEditorChaptersCourseId: 0,
  adminEditorQuery: "",
  adminEditorCorrectionOnly: false,
  adminEditorResults: [],
  adminEditorSelectedId: 0,
  adminEditorDetail: null,
  adminEditorBusy: "",
  adminEditorAutoLoadKey: "",
  exam: null,
  examTimer: null,
  examConfig: null,
  /* 组卷进行中（点了「开始组卷」→ 卷子还没生成出来）。
     这段时间 state.exam 仍是 null、state.questions 还是空，
     交卷入口必须拦掉（见 submitPaper / isExamGenerating）。 */
  examGenerating: false,
  /* 这次组卷是**哪个科目**发起的。切到别的科目后旧组卷已经不会写 state 了
     （见 beginExamFlow），它的「组卷中」标记也不该继续把新科目的「开始组卷」禁掉。 */
  examGeneratingCourseId: 0,
  /* 组卷 / 续考的**代际**编号：迟到的异步回包靠它作废，见 beginExamFlow。 */
  examGenerationId: 0,
  correctionQuestion: null,
  expandedChapters: new Set(),
  examExpandedChapters: new Set(),
  examSelectedChapters: null,
  /* 组卷章节里被**显式取消**的子树（含该节点自身）。
     「父章勾选 = 整棵子树入卷」是既有语义，要保留；但用户手动取消的子章必须能从
     展开结果里减掉 —— 否则 normalizeExamChapterIds 会把「祖先已选中」的子项剔除，
     父 id 仍然进卷，被取消章的题又会回到卷子里。 */
  examExcludedChapters: new Set(),
  analysisExpandedChapters: new Set(),
  chapterAutoExpanded: false,
  smartPracticeFreshStart: false,
  /* ⑤ 切科目时记下上一轮智能练习属于哪个科目，空态用它说清原因；
     打开新题单 / 离开智能练习时清掉。 */
  smartLeftCourseName: "",
  smartLeftCourseId: 0,
  practiceContextCollapsed: null,
  printAfterRender: false,
  lastAnimatedQuestionId: 0,
  lastMarkedQuestionId: 0,
  lastActivityAt: 0,   // 上一次答题时间（内存值，算学习时长用，见 recordPracticeActivity）
  /* ── 题单读取生命周期（UI-05）─────────────────────────────────────────
     一条主线：**屏上这份 state.questions 到底属于哪个模块/科目**。
     · questionContext：成功写进 state.questions 的那一次是 mode|courseId；
       与当前 mode/courseId 不一致 = 屏上是**别的模块**的旧题单，进入新模块时必须
       先收起来（不给「新页签旧数据」的机会）。
     · questionListAt / questionListSource：那次成功取数的**时间与条件**
       （章节/题型/题量），保留旧题单时用它写清「屏上这份是什么、什么时候的」。
     · questionLoadState：{status: loading|ready|error, mode, courseId, error}，
       loading 画明确占位、error 画常驻可重试错误态。
     · questionListStale：同一模块内刷新失败、旧题单被保留时置位，
       页面顶部常驻横幅 + 收掉按题单生效的写操作。 */
  questionContext: "",
  questionListAt: "",
  questionListSource: null,
  questionLoadState: null,
  questionListStale: null,
};

const $ = (id) => document.getElementById(id);
/* 本标签页的随机身份。草稿按「账号 + 标签页 + 草稿ID」分别落盘，
   同账号两个标签页各存一份草稿时互不覆盖（见 stashPendingSaveDraft / ST-03）。 */
const TAB_ID = Math.random().toString(36).slice(2, 10);
const SESSION_USER_KEY = "yunxi-session-user";
const LAST_USER_KEY = "yunxi-current-user";
/* 会话令牌的本地副本。服务端登录时会同时下发 cookie 与 body 里的 token：
   手机浏览器/内置 WebView 的"云加速"代理会吞掉 Set-Cookie，只靠 cookie 就会
   「登录成功却立刻提示登录已失效」。这里存一份，后续请求用 X-Session-Token 头带上。 */
const SESSION_TOKEN_KEY = "yunxi-session-token";
const REVIEW_INTERVAL_DAYS = [1, 2, 4, 7, 15, 30];
/* 智能训练选题预算（2026-09-26 重构）。
   旧实现是「装填后截断」：错题段没有上限，错题超过 60 道时整张卷子 100% 是错题、
   新题配额恒为 0，卡片上写的「混合」是假的。现在改成「先占位再装填」：
   弱项段各有上限，未做题有保底量，保底先扣出来，弱项再多也挤不掉。 */
const SMART_PRACTICE_SIZE = 60;
const SMART_PRACTICE_FLOOR_NEW = 24;      // 60 题里保证 24 道没做过的
const SMART_QUOTA = { due: 12, repeated: 10, weak: 12 };
const SPRINT_PRACTICE_SIZE = 100;
const SPRINT_PRACTICE_FLOOR_NEW = 40;
const SPRINT_QUOTA = { wrong: 25, weak: 35 };
/* 「薄弱」判定共用的最小样本量。**只此一处**，两个判定方都必须引用它：
     · getWeakChapterRows()：一行章至少要确认这么多道客观题才配上榜；
     · chapterMastery()：确认数不足时直接判「薄弱」（样本不够 = 还看不出掌握）。
   旧实现是 3 / 5 两个值，3 和 4 道确认的章会「上榜却又被别处判成薄弱」，同屏两套口径。
   为什么是 5 不是 3：3 道全错即 0%，必然霸榜，且 3 样本的正确率置信区间宽到没有意义。
   ⚠️ 它同时是 getWeakChapterRows() 的**上榜门槛**，所以提高会让「薄弱章」变少 ——
   3 个生成器（今日强化段③ / 考前冲刺段② / 今日计划）拿的是它的 top-N，样本不足的章
   不再参与，题池会略微缩小。这是「口径统一」的必然代价，**没有**给生成器另加过滤。 */
const WEAK_MIN_VERIFIED = 5;
/* 弱项类题的轮换冷却：N 天内出现过的降到候选队尾（不是硬排除，
   池子不够时仍然会被用上，不会出现「凑不满」）。 */
const TRAINING_COOLDOWN_DAYS = 3;
/* 同类题型练习（原名「相似错题重练」，名不副实）：种子数 / 每个种子补几道 /
   判定为「同类」所需的最少关键词命中数。
   实测 56 道里有 48 道不是错题 —— 它是「以错题为样本扩展同类题」，不是错题重做。
   名字和文案已经改成说实话的版本，行为一行没动（这个功能本身是有用的）。
   阈值 2 是权衡：设 1 等于没判（「万元」「2018」这种高频词命中就收），
   设 3 以上在短题干上会一个都匹配不到。 */
const SIMILAR_SEED_COUNT = 12;
const SIMILAR_PER_SEED = 6;
const SIMILAR_MIN_KEYWORD_HITS = 2;
/* 错题本「开始训练」的单轮题量 = **当前筛选结果的全部**（见 startWrongBookPractice）。
   这里曾经有个 20 道的上限常量（WRONG_BOOK_PRACTICE_SIZE），已经去掉：
   按钮上写了几道，这一轮就该练几道 —— 筛出 32 道只发 20 道，等于按钮上的数在说谎。
   与「今日错题复习」不同：这里**不看记忆周期**，到期与否都进池 ——
   stage 0 的到期日是答错次日，只出到期题的话今天刚错的题今天一道都练不了。 */
const DEFAULT_TAG_LABELS = ["计算量大", "易错题", "坑题", "重要", "待复盘"];
const FAVORITE_GROUPS = ["公式", "易混点", "考前速看", "老师提醒"];

/* ── 配色模式：浅色 / 暗色 / 护眼 / 墨水屏 ─────────────────────────────
   真正的调色在 style.css 的 [data-theme="dark"|"eye"|"ink"] 令牌块里；这里只负责
   读写偏好、同步按钮状态、通知需要重绘的地方。
   首屏防白闪靠 index.html <head> 的内联脚本（必须排在样式表之前）。

   ink（墨水屏）不是「换颜色」而是换一套表达方式：只有灰阶、没有玻璃、没有动画，
   语义靠「实心 / 描边」而不是色相。详见 style.css 里的说明。 */
const THEME_KEY = "yunxi-theme";
const THEME_NAMES = ["light", "dark", "eye", "ink"];
const THEME_AUTO = "auto";   // 跟随系统（S13）

function currentTheme() {
  const theme = document.documentElement.getAttribute("data-theme");
  return THEME_NAMES.includes(theme) ? theme : "light";
}

/* 用户的**选择**（可能是 "auto"），与 currentTheme() 不同 —— 后者永远是实际生效的那套配色。
   data-theme 放实际配色（CSS 令牌块认它），data-theme-choice 放用户选择（按钮高亮认它）。 */
function currentThemeChoice() {
  const choice = document.documentElement.getAttribute("data-theme-choice");
  if (choice === THEME_AUTO) return THEME_AUTO;
  return THEME_NAMES.includes(choice) ? choice : "light";
}

function systemTheme() {
  return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

function resolveTheme(choice) {
  return choice === THEME_AUTO ? systemTheme() : (THEME_NAMES.includes(choice) ? choice : "light");
}

// 读取当前主题下的令牌值（画布绘图用：它拿不到 CSS 变量，只能算出来）
function themeToken(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

function applyTheme(name, options = {}) {
  const choice = name === THEME_AUTO || THEME_NAMES.includes(name) ? name : "light";
  const next = resolveTheme(choice);
  document.documentElement.setAttribute("data-theme", next);
  document.documentElement.setAttribute("data-theme-choice", choice);
  if (options.persist !== false) {
    try {
      localStorage.setItem(THEME_KEY, choice);
    } catch (err) {
      // 隐私模式下写不了，忽略：本次会话仍然生效
    }
  }
  syncThemeSwitchUi(choice);
  // 学习看板的折线图是 canvas 画的，主题换了必须重画
  if (typeof renderProgressTrend === "function") {
    try {
      renderProgressTrend();
    } catch (err) {
      console.warn("trend redraw after theme change failed", err);
    }
  }
  return next;
}

function syncThemeSwitchUi(choice = currentThemeChoice()) {
  document.querySelectorAll("[data-theme-set]").forEach((btn) => {
    const on = btn.dataset.themeSet === choice;
    btn.classList.toggle("active", on);
    btn.setAttribute("aria-pressed", on ? "true" : "false");
  });
}

function initTheme() {
  /* 首屏内联脚本只能定下 data-theme（防白闪），data-theme-choice 在这里补。 */
  let saved = "light";
  try {
    saved = localStorage.getItem(THEME_KEY) || "light";
  } catch (err) { /* 隐私模式 */ }
  const choice = saved === THEME_AUTO || THEME_NAMES.includes(saved) ? saved : "light";
  document.documentElement.setAttribute("data-theme-choice", choice);
  syncThemeSwitchUi(choice);
  document.querySelectorAll("[data-theme-set]").forEach((btn) => {
    btn.onclick = () => applyTheme(btn.dataset.themeSet);
  });
  // 同一个浏览器开了多个标签页时保持一致
  window.addEventListener("storage", (event) => {
    if (event.key === THEME_KEY && event.newValue) applyTheme(event.newValue, { persist: false });
  });
  // 选了「跟随系统」时，系统配色一变就跟着换
  if (window.matchMedia) {
    const media = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => {
      if (currentThemeChoice() === THEME_AUTO) applyTheme(THEME_AUTO, { persist: false });
    };
    if (media.addEventListener) media.addEventListener("change", onChange);
    else if (media.addListener) media.addListener(onChange);   // 老 Safari
  }
}

function setText(id, value) {
  const el = $(id);
  if (el) el.textContent = value;
}

/* 科目名在两处显示：内容区的 #courseTitle 与手机端顶栏的 #navCourseTitle
   （手机端隐藏内容区那份，见 style.css）。文案必须同步，否则顶栏会一直停在
   初始的「请选择科目」。 */
function setCourseTitle(value) {
  setText("courseTitle", value);
  setText("navCourseTitle", value);
}

async function api(path, options) {
  let res;
  const opts = { ...(options || {}) };
  const sessionToken = localStorage.getItem(SESSION_TOKEN_KEY);
  if (sessionToken) {
    opts.headers = { ...(opts.headers || {}), "X-Session-Token": sessionToken };
  }
  try {
    res = await fetch(path, opts);
  } catch (err) {
    throw new Error("网络连接失败，请检查服务是否已启动");
  }
  /* 304 不是错误：调用方带了 If-None-Match 且服务端内容没变。
     只有显式声明 allowNotModified 的调用方才会走到这里 —— 其余接口收到 304 仍按失败处理。 */
  if (res.status === 304 && opts.allowNotModified) return { notModified: true };
  if (!res.ok) {
    const payload = await res.json().catch(() => ({ error: res.statusText }));
    /* `error` 字段历史上出现过**嵌套对象**（后端 SafeError 曾返回 {ok,error}），
       直接 new Error(对象) 会让 toast 显示 "[object Object]"，用户完全看不懂。
       这里做类型防御：字符串直接用；对象则取它自己的 error / message。
       ⚠️ 还有第三种历史形态：响应体是**裸 JSON 字符串**（后端 `Results.Json(SafeError(ex), 400)`
       → `400 | "题目不存在"`）。那时 payload 本身就是字符串，取 `payload.error` 得到 undefined，
       于是落到 `res.statusText` → 用户只看到 "Bad Request"，看不到真实原因。
       后端已统一改成 `{ok:false,error:…}`，这里同时兜住裸字符串（老版本后端 / 未覆盖到的端点）。 */
    const raw = typeof payload === "string" ? payload : payload?.error;
    const text = typeof raw === "string"
      ? raw
      : (raw && typeof raw === "object" && typeof raw.error === "string")
        ? raw.error
        : (typeof payload?.message === "string" ? payload.message : res.statusText);
    const error = new Error(text);
    error.status = res.status;
    error.code = payload?.code || "";
    /* 会话失效统一处理。只对业务接口生效 —— /api/auth/* 的 401 是「密码不对」，不是会话失效。 */
    if (res.status === 401 && !/^\/api\/auth\//.test(path)) handleSessionExpired();
    throw error;
  }
  return res.json();
}

/* 会话失效（cookie 过期 / 被清）后的统一处理。
   以前只在保存时弹一句「保存失败：登录已失效，请重新登录」，页面**仍停在应用内**，
   用户继续操作只会一直失败、也没有出路。这里统一：提示 → 可选导出备份 → 回登录页。 */
let sessionExpiredAt = 0;
function handleSessionExpired() {
  const now = Date.now();
  if (now - sessionExpiredAt < 8000) return;   // 并发请求只处理一次
  sessionExpiredAt = now;
  const hasUnsaved = !!state.storageDirty;
  if (hasUnsaved && confirm("登录已失效，需要重新登录。\n\n本地还有未保存的改动。\n点「确定」先导出备份，点「取消」直接返回登录页。")) {
    exportLocalBackup();
  }
  clearTimeout(state.saveTimer);
  state.saveTimer = null;
  stopExamTimer();
  state.storageDirty = false;
  setStorageReadFailed(false, "");
  setSaveStatus("idle");
  localStorage.removeItem(SESSION_USER_KEY);
  localStorage.removeItem(SESSION_TOKEN_KEY);
  toast("登录已失效，请重新登录");
  state.user = "";
  state.currentCourse = null;
  state.questions = [];
  state.answers = {};
  state.submitted = false;
  document.body.classList.remove("is-admin", "admin-view", "picker-open", "mobile-actions-open", "mobile-tools-open");
  showLogin();
}

/* 把当前内存里的数据导出成 JSON 备份（会话失效 / 保存失败时给用户的兜底）。 */
function exportLocalBackup() {
  try {
    const payload = { exportedAt: nowText(), user: state.user, storage: state.storage };
    const blob = new Blob([JSON.stringify(payload, null, 2)], { type: "application/json;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `yunxi-backup-${state.user || "user"}-${Date.now()}.json`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 4000);
  } catch (_err) {
    /* 导出失败不阻塞返回登录页 */
  }
}

function debounce(fn, delay = 250) {
  let timer;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), delay);
  };
}

const refreshAdminBankTableDebounced = debounce(() => refreshAdminBankTable(), 180);

function shuffleItems(items) {
  const result = items.slice();
  for (let i = result.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [result[i], result[j]] = [result[j], result[i]];
  }
  return result;
}

/* 题图路径改写 ────────────────────────────────────────────────────────────
   金考典原始题库里 `<img src>` 是**本地路径**，两种形式（实测 4995 张图）：

     C:\Users\Administrator\AppData\Roaming\jinkaodian\data\20\1509065_1.jpg
     data/23/18145350_1.png

   两者都过不了 `isSafeUrl`（只放行 http(s)/ / ./ assets/ / data:image），
   `src` 会被删掉 —— 结果就是**所有题图都显示不出来**。

   后端把图片按课程挂在 `/assets/<课程id>/<文件名>`（见 Program.cs 的
   `app.MapGet("/assets/{**relative}")`），所以这里把两种形式都改写过去。 */
const ASSET_LOCAL_PATH = /[\\/]?data[\\/](\d+)[\\/]([^\\/]+)$/i;

function normalizeAssetSrc(value) {
  const text = String(value || "").trim();
  if (!text) return "";
  // 已经是 web URL 的直接放过
  if (/^(?:https?:|data:|\/\/|\/|\.\/)/i.test(text)) return text;
  const match = ASSET_LOCAL_PATH.exec(text);
  if (!match) return text;   // 认不出来就原样交给 isSafeUrl 判
  return `/assets/${match[1]}/${encodeURIComponent(match[2])}`;
}

function sanitizeRichHtml(html) {
  if (!html) return "";
  const allowedTags = new Set([
    "A", "B", "BLOCKQUOTE", "BR", "CODE", "DEL", "DIV", "EM", "FONT", "H1", "H2", "H3", "H4",
    "H5", "H6", "HR", "I", "IMG", "LI", "MARK", "OL", "P", "PRE", "S", "SMALL", "SPAN", "STRONG",
    "SUB", "SUP", "TABLE", "TBODY", "TD", "TFOOT", "TH", "THEAD", "TR", "U", "UL",
  ]);
  const globalAttrs = new Set(["title"]);
  const tagAttrs = {
    A: new Set(["href", "target", "rel"]),
    FONT: new Set(["color", "size"]),
    IMG: new Set(["src", "alt", "width", "height", "loading"]),
    TD: new Set(["colspan", "rowspan", "align", "valign"]),
    TH: new Set(["colspan", "rowspan", "align", "valign"]),
  };
  const isSafeUrl = (value, image = false) => {
    const text = String(value || "").trim();
    if (!text) return false;
    if (/^(?:https?:|\/|\.\/|assets\/)/i.test(text)) return true;
    return image && /^data:image\/(?:png|jpe?g|gif|webp);base64,/i.test(text);
  };
  const template = document.createElement("template");
  template.innerHTML = String(html);
  for (const node of [...template.content.querySelectorAll("*")]) {
    if (!allowedTags.has(node.tagName)) {
      if (["SCRIPT", "STYLE", "IFRAME", "OBJECT", "EMBED", "FORM", "SVG", "MATH"].includes(node.tagName)) node.remove();
      else node.replaceWith(...node.childNodes);
      continue;
    }
    for (const attr of [...node.attributes]) {
      const name = attr.name.toLowerCase();
      const safeClass = name === "class" && /^[a-zA-Z0-9_\- ]+$/.test(attr.value);
      const permitted = globalAttrs.has(name) || tagAttrs[node.tagName]?.has(name);
      if (name.startsWith("data-") || (!permitted && !safeClass) || name.startsWith("on") || name === "style") node.removeAttribute(attr.name);
    }
    if (node.tagName === "A") {
      if (!isSafeUrl(node.getAttribute("href"))) node.removeAttribute("href");
      if (node.getAttribute("target") === "_blank") node.setAttribute("rel", "noopener noreferrer");
    }
    if (node.tagName === "IMG") {
      // 先改写本地路径（否则下面 isSafeUrl 会把 src 删掉，图就没了）
      const src = normalizeAssetSrc(node.getAttribute("src"));
      if (src && isSafeUrl(src, true)) node.setAttribute("src", src);
      else node.removeAttribute("src");
      node.setAttribute("loading", "lazy");
    }
  }
  return template.innerHTML;
}

function addLazyLoading(html) {
  return sanitizeRichHtml(html);
}

function isSubjective(q) {
  return !(q?.options || []).length;
}

function isMultiChoice(q) {
  const typeText = String(q?.type || "");
  if (/多选|多项|不定项/.test(typeText)) return true;
  if (/单选|判断/.test(typeText)) return false;
  return normalizeAnswer(q?.answer).length > 1;
}

function shouldAutoVerifyInstant(q) {
  return currentVerifyMode() === "instant" && !isSubjective(q) && !isMultiChoice(q);
}

function normalizeAnswer(value) {
  const cleaned = String(value || "").trim().replace(/[、,，\s]/g, "");
  /* 多字符答案（多选 / 不定项）按字符**排序后**再比较。
     题库里存在未排序的正确答案（实测 "BED" / "ABED" / "BCED"），
     而 chooseOption 写用户答案时会 sort() —— 不排序就会「用户全对却判错」，
     考试计分也会少给（全对只得 0.5×项数），还会把这题永久记进错题本。
     排序对 hasAnswer（只看长度）与 calculateQuestionScore（用 Set）都无影响。 */
  return cleaned.length > 1 ? Array.from(cleaned).sort().join("") : cleaned;
}

function hasAnswer(questionId) {
  return normalizeAnswer(state.answers[questionId]).length > 0;
}

function currentAnswerSnapshot(questionId) {
  return normalizeAnswer(state.answers[questionId]);
}

function activeSearchTerm() {
  return ($("questionSearch")?.value || "").trim();
}

function getSearchMatchIndexes() {
  const term = activeSearchTerm().toLowerCase();
  if (!term) return [];
  return state.questions
    .map((item, index) => ({ item, index }))
    .filter(({ item }) => {
      const text = stripText(item.title || item.detail?.stem || item.detail?.extraQuestion || "");
      return text.toLowerCase().includes(term);
    })
    .map(({ index }) => index);
}

function isAnswerCorrect(q) {
  const mine = normalizeAnswer(state.answers[q.id]);
  const right = normalizeAnswer(q.answer);
  return mine === right;
}

function startOfToday() {
  const d = new Date();
  d.setHours(0, 0, 0, 0);
  return d;
}

function addDays(date, days) {
  const d = new Date(date.getTime());
  d.setDate(d.getDate() + days);
  return d;
}

function reviewStage(record) {
  return Math.max(0, Math.min(REVIEW_INTERVAL_DAYS.length - 1, Number(record?.stage || 0)));
}

function reviewDueDate(record) {
  const base = parseDate(record?.lastReviewAt || record?.wrongAt || record?.at) || new Date();
  return addDays(base, REVIEW_INTERVAL_DAYS[reviewStage(record)]);
}

function isReviewDue(record) {
  if (record?.resolved) return false;
  return reviewDueDate(record).getTime() <= startOfToday().getTime() + 86399999;
}

function ensureWrongReviewFields(record = {}, q = null) {
  const now = nowText();
  if (q) {
    record.courseId = q.courseId;
    /* chapterId 必须存下来：同类题型练习（原「相似错题重练」）要靠它判「同章节」。
       以前只存了 chapterName，导致 chapterId 比较恒为 false，同章节判定全靠字符串相等。 */
    record.chapterId = Number(q.chapterId || 0);
    record.chapterName = q.chapterName;
    record.type = q.type;
    record.title = stripText(q.stem).slice(0, 120);
  }
  record.wrongAt ||= record.at || now;
  record.lastReviewAt ||= record.at || record.wrongAt;
  record.stage = reviewStage(record);
  /* 不预置 1：markResult 的「答错」分支要做 count+1，预置 1 会让第一次答错就变成 2
     （「错误次数 ≥2」筛选因此把只错一次的题也算成反复错）。缺省值读的时候按 1 处理。 */
  record.count = Number(record.count || 0);
  record.resolved = !!record.resolved;
  return record;
}

function tagLabels() {
  const labels = new Set(Array.isArray(state.storage.tagLabels) ? state.storage.tagLabels : []);
  Object.keys(state.storage.tags || {}).forEach((label) => labels.add(label));
  DEFAULT_TAG_LABELS.forEach((label) => labels.add(label));
  return [...labels].filter(Boolean).sort((a, b) => a.localeCompare(b, "zh-Hans-CN"));
}

function questionTags(questionId) {
  const qid = Number(questionId);
  return tagLabels().filter((label) => (state.storage.tags?.[label] || []).map(Number).includes(qid));
}

function getExamRule(course = state.currentCourse) {
  const name = course?.name || "";
  if (/案例/.test(name)) {
    return {
      name: "案例分析模拟卷",
      durationMinutes: 240,
      totalScore: 120,
      autoScoreNote: "主观题需人工核对，客观题自动计分",
      parts: [
        { label: "案例/问答题", typeIds: [3, 4], count: 5, score: 20 },
        { label: "不定项选择题", typeIds: [6], count: 20, score: 1 },
      ],
    };
  }
  if (/安装/.test(name) && /计量|技术/.test(name)) {
    return {
      name: "安装计量模拟卷",
      durationMinutes: 150,
      totalScore: 100,
      autoScoreNote: "单选每题 1 分，多选/不定项每题 1.5 分",
      parts: [
        { label: "单选题", typeIds: [0], count: 40, score: 1 },
        { label: "多选题", typeIds: [1], count: 20, score: 1.5 },
        { label: "不定项选择题", typeIds: [6], count: 20, score: 1.5 },
      ],
    };
  }
  return {
    name: "标准模拟卷",
    durationMinutes: 150,
    totalScore: 100,
    autoScoreNote: "单选每题 1 分，多选每题 2 分",
    parts: [
      { label: "单选题", typeIds: [0], count: 60, score: 1 },
      { label: "多选题", typeIds: [1], count: 20, score: 2 },
    ],
  };
}

function normalizeExamParts(rule) {
  return rule.parts.map((part) => {
    const available = state.types
      .filter((type) => part.typeIds.includes(Number(type.id)) && Number(type.questionCount || 0) > 0)
      .map((type) => Number(type.id));
    return { ...part, availableTypeIds: available };
  }).filter((part) => part.availableTypeIds.length);
}

function allocateExamCounts(part, chapters) {
  const chapterList = chapters.filter((chapter) => Number(chapter.questionCount || 0) > 0);
  if (!chapterList.length) return [{ chapterId: 0, count: part.count }];
  const total = chapterList.reduce((sum, chapter) => sum + Number(chapter.questionCount || 0), 0);
  const allocations = chapterList.map((chapter) => {
    const exact = part.count * Number(chapter.questionCount || 0) / Math.max(1, total);
    return { chapterId: chapter.id, count: Math.floor(exact), rest: exact % 1 };
  });
  let assigned = allocations.reduce((sum, item) => sum + item.count, 0);
  allocations.sort((a, b) => b.rest - a.rest);
  for (let i = 0; assigned < part.count && allocations.length; i++, assigned++) {
    allocations[i % allocations.length].count++;
  }
  return allocations.filter((item) => item.count > 0);
}

function selectedChapterIds(chapter = state.currentChapter) {
  if (!chapter) return [];
  const excluded = state.examExcludedChapters;
  const ids = new Set([Number(chapter.id)]);
  const visit = (node) => {
    (node.children || []).forEach((child) => {
      const childId = Number(child.id);
      /* 被显式取消的子章：连它自己的子树一起从展开结果里减掉。
         「父选中 = 整棵子树入卷」的语义保留，但用户取消过的那一支不再入卷 ——
         否则 normalizeExamChapterIds 把「祖先已选中」的子项一剔除，父 id 照样进卷，
         被取消章的题又会被抽回来。 */
      if (excluded && excluded.size && excluded.has(childId)) return;
      ids.add(childId);
      visit(child);
    });
  };
  const treeNode = state.chapterTreeIndex?.get(Number(chapter.id));
  if (treeNode) visit(treeNode);
  return Array.from(ids).filter(Boolean);
}

/* chapterPathForId 的缓存。选题逻辑会对整个题库逐题调它
   （一次生成是几万到几十万次调用），而 findChapterPath 是递归线性扫描，
   不缓存的话每次生成/每次 renderTraining 都会卡主线程。
   renderChapters 每次都会重建 roots 数组，所以用数组身份做失效判断就够。 */
let chapterPathCache = new Map();
let chapterPathCacheRoots = null;

function chapterPathForId(chapterId) {
  const id = Number(chapterId || 0);
  if (!id || !state.chapterTreeRoots) return [];
  if (chapterPathCacheRoots !== state.chapterTreeRoots) {
    chapterPathCache = new Map();
    chapterPathCacheRoots = state.chapterTreeRoots;
  }
  if (chapterPathCache.has(id)) return chapterPathCache.get(id);
  const path = findChapterPath(state.chapterTreeRoots, id);
  chapterPathCache.set(id, path);
  return path;
}

/* 「课本章」的名字 = chapterPathForId 前两层里最深那一层的名字。
   题库的 coursechapter 是四层：卷册(grade0) → 章(grade1) → 节(grade2) → 目(grade3)，
   同一个课本的章在每个卷册里都有一份**独立的** chapter 记录 ——
   实测课程 20 的「第二章 建设工程计价原理、方法及计价依据」被拆成 5 个 chapter id
   （章节练习 / 章节真题 / 章节密训 / 章节强化 / 章节精选），合计 1085 题，
   按 id 判「同章节」一次最多只能看到其中 356 题，漏掉 67%。
   只有章名（「第X章 …」）是跨卷册稳定的，所以判同章节必须按名字，不能按 id。 */
function textbookChapterName(chapterId) {
  const path = chapterPathForId(chapterId).slice(0, 2);
  const node = path[1] || path[0];
  /* 归一化必须和后端 ChapterMapStore.NormalizeChapterName 一致：
     空白（含全角空格 U+3000）压成一个半角空格再 trim。
     题库里同一个课本的章在不同卷册里空格写法不同 ——
     实测课程 570 同时有「第二章　安装工程计量」和「第二章 安装工程计量」，
     不归一化就会被当成两个章，一题多章统计虚高，外挂映射的键也会对不上。 */
  return normalizeChapterName(node?.name);
}

function normalizeChapterName(name) {
  return String(name || "").replace(/\s+/g, " ").trim();
}

/* 外挂章节映射（后端 ChapterMapStore 生成，落 data/chapter-map.json）。
   整卷类卷册（历年真题 / 模考 / 预测 …）的题在题库里没有章节归属，靠这张表补：
   键是 subjectId，值是课本章名数组（同一道题可能考察多个章节，所以是数组）。
   取不到就留空表，选题逻辑自动回落到「题自己的章节」。 */
async function loadChapterMap() {
  try {
    const payload = await api("/api/chapter-map");
    const chapters = Array.isArray(payload?.chapters) ? payload.chapters : [];
    const map = new Map();
    Object.entries(payload?.map || {}).forEach(([id, indices]) => {
      const names = (indices || []).map((index) => chapters[Number(index)]).filter(Boolean);
      if (names.length) map.set(Number(id), names);
    });
    state.chapterMap = map;
    return map;
  } catch (err) {
    console.warn("chapter map load failed", err);
    state.chapterMap = new Map();
    return state.chapterMap;
  }
}

/* 整卷型卷册的 id 集合：该卷册下**没有任何题挂到「节」**（chapterPath 长度 < 3）。
   历年真题 / 模考 / 预测 这类卷册整卷就是一个节点，题直接挂在卷子上，没有节。
   缓存按题目数组的身份失效（换科目或重新拉题就会重算）。 */
let volumeTypeCache = null;
let volumeTypeCacheSource = null;
function volumeTypeVolumeIds(items) {
  if (volumeTypeCacheSource === items && volumeTypeCache) return volumeTypeCache;
  const withSections = new Set();
  const volumes = new Set();
  (items || []).forEach((item) => {
    const path = chapterPathForId(item.chapterId);
    if (path.length < 2) return;
    const volumeId = Number(path[0].id);
    volumes.add(volumeId);
    if (path.length >= 3) withSections.add(volumeId);
  });
  const result = new Set([...volumes].filter((id) => !withSections.has(id)));
  volumeTypeCache = result;
  volumeTypeCacheSource = items;
  return result;
}

/* 一道题所属的课本章名集合。来源优先级：
     ① 外挂映射 —— 整卷题在题库里没有章节归属，只能靠它补；
     ② 题自己挂在哪个章 —— 章节型卷册天然有；
     ③ 都没有 → 空数组，不参与章节统计。
   一题多章是正常的（同一道题可能考察多个章节），所以返回数组而不是单值。

   ② 必须排除整卷型卷册：那些卷册里「题挂在章上」的那个「章」其实是**卷子名**
   （「2009年一级造价工程师考试《安装计量》真题」），不是课本的章。
   不排除的话每份真题卷都会变成一行假的「薄弱章节」。 */
function textbookChapterKeys(item, items = null) {
  const mapped = state.chapterMap?.get(Number(item?.id));
  if (mapped && mapped.length) return [...new Set(mapped)];
  const path = chapterPathForId(item?.chapterId);
  if (path.length >= 2) {
    const pool = items || (state.analysisQuestions.length ? state.analysisQuestions : state.questions);
    if (!volumeTypeVolumeIds(pool).has(Number(path[0].id))) {
      const own = normalizeChapterName(path[1].name);
      if (own) return [own];
    }
    return [];
  }
  /* 章节树还没加载：回落到题自带的 chapterName（精度差，但总比没有强） */
  const fallback = normalizeChapterName(item?.chapterName);
  return fallback ? [fallback] : [];
}

function applyChapterParams(params, chapter = state.currentChapter) {
  const ids = selectedChapterIds(chapter);
  if (!ids.length) return;
  if (ids.length === 1) params.set("chapterId", String(ids[0]));
  else params.set("chapterIds", ids.join(","));
}

function chapterQuestionCount(chapter) {
  const node = state.chapterTreeIndex?.get(Number(chapter.id)) || chapter;
  return node.children ? chapterTotalCount(node) : Number(chapter.questionCount || 0);
}

function effectiveQuestionLimit() {
  const selected = Number($("limitSelect").value) || 80;
  if (!state.currentChapter) return selected;
  return Math.max(selected, chapterQuestionCount(state.currentChapter));
}

/* 题量 / 题序是「题单窗口」的一部分，必须跟存档一起持久化。
   不持久化时，刷新后 limit 回落成 DOM 默认的 80：上次停在「400 题」窗口里第 151 题的人，
   新题单里根本没有那道题（findStartIndex 找不到 → 只能回第 1 题）。
   老存档没有这两个字段 → 回落到与 index.html 默认值一致的行为（80 / 章节顺序）。 */
const QUESTION_LIMIT_OPTIONS = ["80", "160", "400", "1000"];
const QUESTION_ORDER_OPTIONS = ["", "random"];

function savedQuestionWindow() {
  const settings = state.storage?.settings || {};
  const limit = String(settings.questionLimit ?? "");
  const order = String(settings.questionOrder ?? "");
  return {
    limit: QUESTION_LIMIT_OPTIONS.includes(limit) ? limit : "80",
    order: QUESTION_ORDER_OPTIONS.includes(order) ? order : "",
  };
}

/* 在 restoreLastCourse 之前把上次的题量/题序放回选择器：
   loadQuestions 的题单窗口直接读这两个控件的值。 */
function applyQuestionWindow() {
  const { limit, order } = savedQuestionWindow();
  if ($("limitSelect")) $("limitSelect").value = limit;
  if ($("orderSelect")) $("orderSelect").value = order;
}

function persistQuestionWindow() {
  if (!state.user) return;
  state.storage.settings ||= {};
  state.storage.settings.questionLimit = $("limitSelect") ? $("limitSelect").value : "80";
  state.storage.settings.questionOrder = $("orderSelect") ? $("orderSelect").value : "";
  scheduleSave();
}

function chapterDepth(chapter) {
  const path = state.chapterTreeRoots ? findChapterPath(state.chapterTreeRoots, chapter.id) : [];
  return path.length || Number(chapter.grade || 1);
}

function examChapterGroups() {
  const roots = state.chapterTreeRoots?.length ? state.chapterTreeRoots : buildChapterTree(state.chapters);
  const rows = [];
  const visit = (nodes, depth = 1) => {
    nodes.forEach((node) => {
      const total = chapterTotalCount(node);
      if (total > 0 && depth <= 2) {
        rows.push({
          ...node,
          questionCount: total,
          examLevel: depth,
        });
      }
      if (depth < 2) visit(node.children || [], depth + 1);
    });
  };
  visit(roots);
  if (rows.length) return rows;
  const leaves = state.chapters.filter((chapter) => Number(chapter.questionCount || 0) > 0);
  return leaves.map((chapter) => ({
    ...chapter,
    questionCount: chapterQuestionCount(chapter),
    examLevel: chapterDepth(chapter),
  }));
}

function normalizeExamChapterIds(ids) {
  const selected = new Set((ids || []).map(Number).filter(Boolean));
  if (!selected.size) return [];
  return Array.from(selected).filter((id) => {
    const path = chapterPathForId(id);
    return !path.slice(0, -1).some((node) => selected.has(Number(node.id)));
  });
}

function examChaptersFromIds(ids) {
  return (ids || [])
    .map((id) => state.chapterTreeIndex?.get(Number(id)) || state.chapters.find((chapter) => Number(chapter.id) === Number(id)))
    .filter(Boolean)
    .map((chapter) => ({
      ...chapter,
      questionCount: examEffectiveQuestionCount(chapter),
    }));
}

/* 组卷用的题量：与 selectedChapterIds 同一口径 —— 被显式取消的子树不算进来，
   否则父章会按「含被取消子章」的题量多分到题，再靠兜底填充去别处凑。 */
function examEffectiveQuestionCount(chapter) {
  const excluded = state.examExcludedChapters;
  const node = state.chapterTreeIndex?.get(Number(chapter.id)) || chapter;
  let total = Number(node.questionCount || 0);
  for (const child of node.children || []) {
    const childId = Number(child.id);
    if (excluded && excluded.size && excluded.has(childId)) continue;
    total += examEffectiveQuestionCount(child);
  }
  return total;
}

/* 每题分值。优先级：
     1. state.exam.scoreMap —— 模拟考场组卷时按题型显式设的分值，最权威
     2. q.score             —— 题库自带的 iscore，100% 的题都有，
                               且和真题卷面完全吻合（实测造价管理真题
                               60×1 + 20×2 = 100 分，正是卷面满分）
     3. 按题型的兜底值       —— 老数据 / 字段缺失时用

   以前只有第 1 和第 3 条，第 3 条把主观题算成 0 分 ——
   案例真题那 8 道每题 20 分的题，交卷显示「0/0 分」。 */
function getQuestionScore(q) {
  if (state.exam?.scoreMap?.[q.id] != null) return Number(state.exam.scoreMap[q.id]);
  // 判据是「字段有没有值」，不是「值 > 0」——
  // 题库里真的有 31 道 iscore='0.0' 的题（其中 18 道单选）。
  // 按 > 0 判断会把这 31 道当成「题库没给分值」，套上兜底的 1 分。
  const raw = q?.score;
  if (raw != null && String(raw).trim() !== '') {
    const bankScore = Number(raw);
    if (Number.isFinite(bankScore)) return bankScore;
  }
  if (isSubjective(q)) return 0;
  // 问答/案例（3、4）题库里大多是 20 分一道的大题。
  // iscore 缺失时给 0（等于不计入总分），比给 1 分合理得多。
  if ([3, 4].includes(Number(q.subjectType))) return 0;
  if (Number(q.subjectType) === 1) return 2;
  if (Number(q.subjectType) === 6) return 1.5;
  return 1;
}

function calculateQuestionScore(q) {
  const maxScore = getQuestionScore(q);
  if (!hasAnswer(q.id) || isSubjective(q)) return 0;
  const mine = normalizeAnswer(state.answers[q.id]);
  const right = normalizeAnswer(q.answer);
  if (mine === right) return maxScore;
  const type = Number(q.subjectType || 0);
  if (type !== 1 && type !== 6) return 0;
  const rightSet = new Set(right.split(""));
  const mineSet = new Set(mine.split(""));
  for (const label of mineSet) {
    if (!rightSet.has(label)) return 0;
  }
  return Math.min(maxScore, mineSet.size * 0.5);
}

/* 考试时长上限（分钟）。600 = 10 小时，比任何真实卷子都宽松得多，
   但挡得住「填 999999 → 状态栏渲染成 16666:38:59」这种明显不是本意的输入。
   下限 1 分钟：0 / 负数 / 空值一律不启动（旧实现 Math.max(1, …) 静默改成 1 分钟）。 */
const EXAM_DURATION_MAX_MINUTES = 600;

function getExamConfigFromForm(rule) {
  const name = ($("examNameInput")?.value || rule.name).trim() || rule.name;
  /* 时长当场校验，不合法就返回 null 让调用方**不启动** ——
     旧实现 Math.max(1, Number(…)) 把 0 / -5 静默改成 1 分钟开考
     （输入框里还显示着 0，用户不会知道自己在考一份 1 分钟的卷子），
     999999 也照收。空值（Number("") === 0）同样按「至少 1 分钟」拦下。 */
  const rawDuration = Number($("examDurationInput")?.value);
  if (!Number.isFinite(rawDuration) || rawDuration < 1) {
    toast("考试时间至少 1 分钟");
    return null;
  }
  if (rawDuration > EXAM_DURATION_MAX_MINUTES) {
    toast(`考试时间最多 ${EXAM_DURATION_MAX_MINUTES} 分钟（10 小时）`);
    return null;
  }
  const duration = rawDuration;
  const selectedParts = rule.parts
    .map((part, index) => {
      const checked = $("examPart" + index)?.checked !== false;
      const count = Math.max(0, Number($("examPartCount" + index)?.value || part.count));
      return checked && count ? { ...part, count } : null;
    })
    .filter(Boolean);
  const checkedChapters = Array.from(document.querySelectorAll("[data-exam-chapter]:checked"))
    .map((input) => Number(input.value))
    .filter(Boolean);
  const totalScore = selectedParts.reduce((sum, part) => sum + part.count * part.score, 0);
  return {
    ...rule,
    name,
    durationMinutes: duration,
    parts: selectedParts,
    totalScore,
    selectedChapterIds: normalizeExamChapterIds(checkedChapters),
  };
}

function selectableExamChapters() {
  return examChapterGroups();
}

/* 某个题型当前**真正能出多少题** = min(题型池, 已选章节的有效题量)。
   题型池来自 /api/types 的 questionCount（服务端权威数字）；
   章节题量用 examEffectiveQuestionCount（已扣掉被显式取消的子章）。
   只用来给题量输入框设 max + 就地提示，**不静默改用户填的数字** ——
   超出部分组卷时本来就抽不出来，满分按实际卷子重算（见 generateExamPaper）。 */
function examPartAvailableCount(part) {
  /* availableTypeIds 只有 normalizeExamParts 处理过的对象才有；没有就用原始 typeIds
     （题量为 0 的题型本来也出不了题，求和时按 >0 过滤，两种入口结果一致）。 */
  const typeIds = (part.availableTypeIds && part.availableTypeIds.length) ? part.availableTypeIds : (part.typeIds || []);
  const typeCount = typeIds.reduce((sum, typeId) => {
    const type = (state.types || []).find((item) => Number(item.id) === Number(typeId) && Number(item.questionCount || 0) > 0);
    return sum + Number(type?.questionCount || 0);
  }, 0);
  const selected = normalizeExamChapterIds(Array.from(state.examSelectedChapters || []));
  const chapterCount = selected.reduce((sum, chapterId) => {
    const node = state.chapterTreeIndex?.get(Number(chapterId));
    return sum + (node ? examEffectiveQuestionCount(node) : 0);
  }, 0);
  if (!typeCount) return chapterCount;
  if (!chapterCount) return typeCount;
  return Math.min(typeCount, chapterCount);
}

function renderExamChapterTree() {
  const roots = (state.chapterTreeRoots?.length ? state.chapterTreeRoots : buildChapterTree(state.chapters))
    .filter((node) => chapterTotalCount(node) > 0);
  if (!state.examSelectedChapters) {
    state.examSelectedChapters = new Set();
    const mark = (node) => {
      if (chapterTotalCount(node) > 0) state.examSelectedChapters.add(Number(node.id));
      (node.children || []).forEach(mark);
    };
    roots.forEach(mark);
  }
  if (!roots.length) return `<span class="muted">当前科目暂无可选择章节，将按全部题库组卷。</span>`;
  return roots.map((node) => renderExamChapterNode(node, 1)).join("");
}

function renderExamChapterNode(chapter, level) {
  const total = chapterTotalCount(chapter);
  /* 不再按层级截断：旧实现 level>2 直接返回空串，三级及更深的章在组卷界面上
     **根本渲染不出来**，用户没法单独排除它们（只能连整棵子树一起取消）。 */
  if (total <= 0) return "";
  const hasChildren = (chapter.children || []).some((child) => chapterTotalCount(child) > 0);
  const expanded = state.examExpandedChapters.has(Number(chapter.id));
  /* 「父章勾选 + 子章取消」在收起的树里**看不出来**：父章 checked 仍为 true，
     题量小字写的还是整棵子树的量（实测 1305 → 有效 1080 却毫无视觉差别）。
     所以：子树里有被排除的章 → 半选态 + 小字追加「（已排除 N 节）」。
     indeterminate 只能通过 DOM 属性设置（innerHTML 写不出来），这里只留标记，
     由 bindExamChapterTree 补上。 */
  const excludedInSubtree = childChapterIds(chapter)
    .filter((childId) => state.examExcludedChapters?.has(Number(childId))).length;
  const indeterminate = state.examSelectedChapters.has(Number(chapter.id)) && excludedInSubtree > 0;
  return `
    <div class="exam-chapter-node level-${level}">
      <div class="exam-chapter-row">
        <button class="exam-chapter-toggle ${expanded ? "expanded" : "collapsed"}" type="button" data-exam-toggle="${chapter.id}" ${hasChildren ? "" : "disabled"}>${hasChildren ? (expanded ? "−" : "+") : ""}</button>
        <label title="${escapeHtml(chapter.name)}">
          <input type="checkbox" data-exam-chapter value="${chapter.id}" data-exam-parent="${hasChildren ? "1" : "0"}" ${state.examSelectedChapters.has(Number(chapter.id)) ? "checked" : ""}${indeterminate ? ' data-exam-indeterminate="1"' : ""}>
          <span>${escapeHtml(chapter.name)}</span>
          <small>${total}题${indeterminate ? `（已排除 ${excludedInSubtree} 节）` : ""}</small>
        </label>
      </div>
      ${hasChildren && expanded ? `<div class="exam-chapter-children">${chapter.children.map((child) => renderExamChapterNode(child, level + 1)).join("")}</div>` : ""}
    </div>
  `;
}

/* 勾选变化后**就地**刷新祖先链的「半选 + 已排除 N 节」显示。
   不整棵重绘：.exam-chapter-list 自己是个滚动容器（max-height + overflow），
   每点一次都 innerHTML 重绘会把 scrollTop 打回 0 —— 而这个列表通常要滚很深
   才点到目标章。 */
function syncExamChapterAncestors(chapterId) {
  const path = chapterPathForId(Number(chapterId)) || [];
  path.slice(0, -1).reverse().forEach((node) => {
    const id = Number(node.id);
    const input = document.querySelector(`[data-exam-chapter][value="${id}"]`);
    if (!input) return;
    const excluded = childChapterIds(node)
      .filter((childId) => state.examExcludedChapters?.has(Number(childId))).length;
    const indeterminate = !!input.checked && excluded > 0;
    input.indeterminate = indeterminate;
    const small = input.closest("label")?.querySelector("small");
    if (small) small.textContent = `${chapterTotalCount(node)}题${indeterminate ? `（已排除 ${excluded} 节）` : ""}`;
  });
}

function bindExamChapterTree() {
  document.querySelectorAll("[data-exam-toggle]").forEach((btn) => {
    btn.onclick = () => {
      const id = Number(btn.dataset.examToggle || 0);
      if (!id) return;
      if (state.examExpandedChapters.has(id)) state.examExpandedChapters.delete(id);
      else state.examExpandedChapters.add(id);
      const host = document.querySelector(".exam-chapter-list");
      if (host) host.innerHTML = renderExamChapterTree();
      bindExamChapterTree();
    };
  });
  document.querySelectorAll("[data-exam-chapter]").forEach((input) => {
    /* 半选态：innerHTML 渲染时只能写 data 标记，这里落到真正的 indeterminate。 */
    if (input.dataset.examIndeterminate === "1") input.indeterminate = true;
    input.onchange = () => {
      const id = Number(input.value || 0);
      const node = state.chapterTreeIndex?.get(id);
      state.examSelectedChapters ||= new Set();
      state.examExcludedChapters ||= new Set();
      /* 勾选/取消都向整棵子树传播，并且**同时**维护 excluded：
         取消 = 显式排除（要从父章展开里减掉），勾选 = 撤销排除。
         只维护 selected 的话，「父勾、子取消」丢不掉这条信息：
         normalizeExamChapterIds 会把「祖先已选中」的子项剔掉，父 id 仍然入卷。 */
      const touched = [id].concat(node ? childChapterIds(node) : []);
      touched.forEach((chapterId) => {
        if (input.checked) {
          state.examSelectedChapters.add(chapterId);
          state.examExcludedChapters.delete(chapterId);
        } else {
          state.examSelectedChapters.delete(chapterId);
          state.examExcludedChapters.add(chapterId);
        }
        const child = document.querySelector(`[data-exam-chapter][value="${chapterId}"]`);
        if (child) {
          child.checked = input.checked;
          /* 勾上父章 = 整棵子树重新全选，之前的半选态要一起清掉。 */
          if (input.checked) child.indeterminate = false;
        }
      });
      /* 被取消/勾上的章会改变**祖先**的「有没有被排除的子章」，就地更新它们的
         半选态与「已排除 N 节」，否则要退出重进考场页才看得到差别。 */
      syncExamChapterAncestors(id);
    };
  });
}

function wrongRecordMatchesFilter(id, item, courseStore, filtersOverride = null) {
  ensureWrongReviewFields(item);
  const filters = filtersOverride || state.wrongFilters || {};
  const active = !!courseStore.wrong?.[id];
  const status = filters.status || "active";
  if ((filters.review || "due") === "due" && !isReviewDue(item)) return false;
  if (filters.review === "future" && isReviewDue(item)) return false;
  if (status === "active" && !active) return false;
  if (status === "resolved" && active) return false;
  const minCount = Number(filters.minCount || 1);
  if (minCount > 1 && Number(item?.count || 1) < minCount) return false;
  const at = parseDate(item?.at);
  const now = Date.now();
  if (filters.period === "7" && (!at || now - at.getTime() > 7 * 86400000)) return false;
  if (filters.period === "30" && (!at || now - at.getTime() > 30 * 86400000)) return false;
  return true;
}

function getWrongRecordsForCurrentCourse(courseStore = userCourseStore()) {
  const records = new Map();
  /* 顶层错题记录的 courseId 允许缺失：老数据（以及早期版本写下的记录）没有这个字段，
     用严格相等过滤会把它们全部丢掉，再从 courseStore.wrong 合成一条
     {at:"",wrongAt:"",lastReviewAt:"",stage:0,count:1} 的 stub ——
     真实的 stage / count / lastReviewAt 全部归零，SRS 永远算不到期。 */
  const currentCourseId = Number(state.currentCourse?.id || 0);
  Object.entries(state.storage.wrong || {})
    .filter(([, item]) => !item?.courseId || Number(item.courseId) === currentCourseId)
    .forEach(([id, item]) => records.set(String(id), item || {}));
  Object.keys(courseStore.wrong || {}).forEach((id) => {
    if (!records.has(String(id))) {
      records.set(String(id), {
        courseId: state.currentCourse?.id,
        at: "",
        wrongAt: "",
        lastReviewAt: "",
        stage: 0,
        count: 1,
        resolved: false,
      });
    }
  });
  return Array.from(records.entries()).map(([id, item]) => [id, ensureWrongReviewFields(item)])
    .sort((a, b) => reviewDueDate(a[1]).getTime() - reviewDueDate(b[1]).getTime());
}

function ensureMobileControls() {
  ensureVerifyModeControl();

  if (!$("mobileActionsBtn") && $("noteBtn")) {
    const btn = document.createElement("button");
    btn.id = "mobileActionsBtn";
    btn.className = "mobile-actions-btn";
    btn.type = "button";
    btn.textContent = "操作";
    $("noteBtn").parentNode.insertBefore(btn, $("noteBtn"));
  }

  if (!$("mobileToolsBtn")) {
    const btn = document.createElement("button");
    btn.id = "mobileToolsBtn";
    btn.className = "mobile-tools-btn";
    btn.type = "button";
    btn.textContent = "工具";
    // ⚠️ 必须插在头像（`.account-box`）**之前**，顶栏顺序才是「… 工具 头像」。
    // 这里原来以 `[data-action="refresh"]` 作锚点；那个按钮已经搬进 .toolbar
    // （手机「工具」面板里的「重新读取题库」），再用它当锚点会把「工具」插到工具栏里。
    const anchor = document.querySelector(".main-nav .account-box");
    const nav = document.querySelector(".main-nav");
    if (anchor) anchor.parentNode.insertBefore(btn, anchor);
    else nav?.appendChild(btn);
  }

  if (!$("answerCardCollapseBtn")) {
    const btn = document.createElement("button");
    btn.id = "answerCardCollapseBtn";
    btn.className = "answer-card-collapse-btn";
    btn.type = "button";
    btn.textContent = "收起答题卡";
    btn.setAttribute("aria-expanded", "true");
    const footer = document.querySelector(".answer-card-wrap");
    footer?.insertBefore(btn, footer.firstChild);
  }

  if (!$("pickerDoneBtn")) {
    const btn = document.createElement("button");
    btn.id = "pickerDoneBtn";
    btn.className = "picker-done-btn";
    btn.type = "button";
    btn.textContent = "进入做题";
    btn.title = "收起题库并回到答题界面";
    const pane = document.querySelector(".course-pane");
    pane?.appendChild(btn);
  }
}

function ensureVerifyModeControl() {
  const submitBtn = $("submitBtn");
  if (!$("verifyModeBtn")) {
    const btn = document.createElement("button");
    btn.id = "verifyModeBtn";
    btn.type = "button";
    btn.className = "verify-mode-btn";
    btn.title = "切换刷题验证方式";
    submitBtn?.parentNode.insertBefore(btn, submitBtn);
  }

  if (!$("confirmAnswerBtn")) {
    const confirmBtn = document.createElement("button");
    confirmBtn.id = "confirmAnswerBtn";
    confirmBtn.type = "button";
    confirmBtn.className = "confirm-answer-btn hidden";
    confirmBtn.textContent = "确认答案";
    confirmBtn.title = "确认当前题并显示答案";
    /* 「确认答案」放在「下一题」**右边**。
       旧位置是 nextBtn 之前 → 桌面按钮行成了「上一题 / 确认答案 / 下一题」，
       把不可撤销的判定动作夹在两个翻页键中间，翻页时极易误点。
       手机端是 flex 等分（按钮个数自适应），顺序变了也不会掉行。 */
    const nextBtn = $("nextBtn");
    nextBtn?.insertAdjacentElement("afterend", confirmBtn);
  }
}

/* 顶栏账号下拉菜单：点头像开合，点外面 / Esc 关闭。
   主题按钮点完**不**关闭菜单（方便连着试三种配色）。 */
function initAccountMenu() {
  const box = document.querySelector(".account-box");
  const btn = $("accountMenuBtn");
  const menu = $("accountMenu");
  if (!box || !btn || !menu) return;
  const setOpen = (open) => {
    box.classList.toggle("open", !!open);
    menu.classList.toggle("hidden", !open);
    btn.setAttribute("aria-expanded", String(!!open));
  };
  setOpen(false);
  btn.onclick = (e) => {
    e.stopPropagation();
    setOpen(menu.classList.contains("hidden"));
  };
  document.addEventListener("click", (e) => {
    if (box.classList.contains("open") && !box.contains(e.target)) setOpen(false);
  });
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") setOpen(false);
  });
  /* 保存状态在「有补存不了的草稿」时就是入口：开页不再弹模态，用户点这里才打开冲突框
     （见 openPendingConflict）。没有待处理草稿时它是纯展示，点不动。 */
  $("saveStatusLabel")?.addEventListener("click", (e) => {
    if (!state.pendingConflict) return;
    e.stopPropagation();
    setOpen(false);
    openPendingConflict();
  });
}

function setMobileActions(open) {
  state.mobileActionsOpen = !!open;
  if (state.mobileActionsOpen) {
    state.mobileToolsOpen = false;
  }
  document.body.classList.toggle("mobile-actions-open", state.mobileActionsOpen);
  document.body.classList.toggle("mobile-tools-open", state.mobileToolsOpen);
  if ($("mobileActionsBtn")) {
    $("mobileActionsBtn").textContent = state.mobileActionsOpen ? "收起" : "操作";
    $("mobileActionsBtn").setAttribute("aria-expanded", String(state.mobileActionsOpen));
  }
  if ($("mobileToolsBtn")) {
    $("mobileToolsBtn").textContent = state.mobileToolsOpen ? "收起" : "工具";
    $("mobileToolsBtn").setAttribute("aria-expanded", String(state.mobileToolsOpen));
  }
  syncAnswerCardSpace();   // 卡片可能刚被收起，预留空间要跟着更新
}

function setMobileTools(open) {
  state.mobileToolsOpen = !!open;
  if (state.mobileToolsOpen) {
    state.mobileActionsOpen = false;
    /* 矮屏上「工具面板 + 展开的答题卡」会把读题区挤没。
       实测 360×640：顶栏 57 + 工具面板 208 + 按钮行 36 + 答题卡 294 + 标签栏 64
       = 659px > 640px，题干只剩 53px，等于看不到题。
       工具面板是「我要做操作」的临时浮层，这时答题卡不是主角，直接收起。
       （「操作 / 工具」本来就已经互斥，这里只是把同一条规则延伸到答题卡。） */
    if (!state.answerCardCollapsed && toolsSqueezeQuestion()) {
      state.answerCardUserTouched = true;   // 别让 applyResponsiveAnswerCard 又展开回去
      setAnswerCardCollapsed(true);
    }
  }
  document.body.classList.toggle("mobile-actions-open", state.mobileActionsOpen);
  document.body.classList.toggle("mobile-tools-open", state.mobileToolsOpen);
  if ($("mobileActionsBtn")) {
    $("mobileActionsBtn").textContent = state.mobileActionsOpen ? "收起" : "操作";
    $("mobileActionsBtn").setAttribute("aria-expanded", String(state.mobileActionsOpen));
  }
  if ($("mobileToolsBtn")) {
    $("mobileToolsBtn").textContent = state.mobileToolsOpen ? "收起" : "工具";
    $("mobileToolsBtn").setAttribute("aria-expanded", String(state.mobileToolsOpen));
  }
  syncAnswerCardSpace();   // 卡片可能刚被收起，预留空间要跟着更新
}

/* 展开工具面板后，读题区还剩多少？低于 MIN_QUESTION_PX 就算「挤到一起」。
   固定开销：顶栏 57 + 工具面板 208（10 个按钮两列）+ 按钮行 36 + 标签栏 64
             + 展开的答题卡 46vh（上限 460px，和 CSS 里的 max-height 对齐）。 */
function toolsSqueezeQuestion() {
  const MIN_QUESTION_PX = 180;
  const h = window.innerHeight || document.documentElement.clientHeight || 0;
  if (!h) return false;
  const cardH = Math.min(h * 0.46, 460);
  const overhead = 57 + 208 + 36 + 64 + cardH;
  return h - overhead < MIN_QUESTION_PX;
}

function shouldAutoCollapseAnswerCard() {
  // 默认收起。答题卡是「跳题用的导航」，不是正文；它一展开就要吃掉
  // 150~460px（宽度越窄格子换行越多、越高），而读题区才是主角。
  // 只在很高的屏幕上（≥1200px）才默认展开 —— 那时卡片只占约 8% 高度，
  // 读题区仍能拿到 ~74%。
  const h = window.innerHeight || document.documentElement.clientHeight || 0;
  return h < 1200;
}

function applyResponsiveAnswerCard() {
  if (!state.answerCardUserTouched) {
    setAnswerCardCollapsed(shouldAutoCollapseAnswerCard());
  }
}

function setAnswerCardCollapsed(collapsed) {
  state.answerCardCollapsed = !!collapsed;
  document.body.classList.toggle("answer-card-collapsed", state.answerCardCollapsed);
  if ($("answerCardCollapseBtn")) {
    $("answerCardCollapseBtn").textContent = state.answerCardCollapsed ? "展开答题卡" : "收起答题卡";
    $("answerCardCollapseBtn").setAttribute("aria-expanded", String(!state.answerCardCollapsed));
  }
}

// 答题卡在 ≤1440px 是 `position: fixed` 的底部覆盖层，脱流后不占位置，
// 所以题目区得自己预留出「卡片高度」的底部空间，否则 .question-actions
// （上一题 / 确认答案 / 下一题 / 笔记 / 收藏本题 / 纠错）会被卡片压住、点不到。
// 卡片高度随宽度变化极大（收起 38~44px；展开 162~462px），写死值必然在某个宽度失效，
// 因此这里把**实测高度**写进 CSS 变量 --answer-card-h，由 style.css 消费。
function syncAnswerCardSpace() {
  const card = document.querySelector(".answer-card-wrap");
  const root = document.documentElement;
  if (!card) {
    root.style.removeProperty("--answer-card-h");
    return;
  }
  const h = Math.round(card.getBoundingClientRect().height) + "px";
  if (root.style.getPropertyValue("--answer-card-h") !== h) {
    root.style.setProperty("--answer-card-h", h);
  }
}

function setMobileTagsOpen(open) {
  state.mobileTagsOpen = !!open;
  const panel = $("questionTagPanel");
  if (panel) {
    panel.classList.toggle("open", state.mobileTagsOpen);
    const btn = $("tagPanelToggleBtn");
    if (btn) btn.setAttribute("aria-expanded", String(state.mobileTagsOpen));
  }
}

/* 标签面板是**就地展开**（不是弹窗），所以点外面不关 —— 关了反而碍事：
   用户正一边看题一边打标签，点一下题目就把面板收了很烦。
   只保留 Esc 关闭。 */
function bindTagPanelDismiss() {
  document.addEventListener("keydown", (event) => {
    if (event.key !== "Escape" || !state.mobileTagsOpen) return;
    event.preventDefault();
    event.stopPropagation();
    setMobileTagsOpen(false);
  }, true);
}

function updateFullscreenState() {
  const btn = $("fullscreenBtn");
  if (!btn) return;
  const active = !!document.fullscreenElement;
  btn.textContent = active ? "收起" : "全屏";
  btn.classList.toggle("active", active);
  btn.setAttribute("aria-expanded", String(active));
}

async function toggleFullscreen() {
  if (document.fullscreenElement) {
    await document.exitFullscreen();
    updateFullscreenState();
    return;
  }
  await document.documentElement.requestFullscreen();
  updateFullscreenState();
}

async function init() {
  initTheme();
  bindAccountMenuActions();
  ensureMobileControls();
  const shouldCollapseCard = window.innerWidth <= 760
    || (window.innerWidth >= 761 && window.innerWidth < 1280 && window.innerHeight < 950)
    || (window.innerWidth >= 1280 && window.innerHeight < 900);
  applyResponsiveAnswerCard();
  updateFullscreenState();
  // 恢复登录：问服务端「这张 cookie 是谁」，不再依赖公开的用户名列表。
  // 停用 / 已删除的账号由服务端直接作废会话，前端不必再自己比对名单。
  const session = await api("/api/auth/session").catch(() => null);
  if (session && session.ok && session.user && !session.mustChangePassword) {
    try {
      await enterApp(session.user, { rememberSession: true });
      return;
    } catch (err) {
      toast(err.message);
    }
  }
  if (!session || !session.ok) localStorage.removeItem(SESSION_USER_KEY);
  showLogin();
}

async function loadUsers() {
  // 账号清单是管理员专用接口（未登录 401、非管理员 403）
  state.users = await api("/api/admin/users");
  renderUsers();
}

function renderUsers() {
  setText("accountLabel", `账号：${state.user || "未登录"}`);
  setText("userName", state.user || "未登录");
  document.body.classList.toggle("is-admin", (state.user || "").toLowerCase() === "admin");
  updateSaveStatusUi();
}

function showLogin() {
  $("appShell").classList.add("hidden");
  $("loginView").classList.remove("hidden");
  $("loginPassword").value = "";
  $("newPassword").value = "";
  $("confirmPassword").value = "";
  $("changePasswordBox").classList.add("hidden");
  state.pendingPasswordUser = "";
  state.pendingOldPassword = "";
  const lastUser = localStorage.getItem(LAST_USER_KEY) || "";
  $("loginUserSelect").value = lastUser;
  $("loginUserSelect").focus();
}

async function login() {
  const user = $("loginUserSelect").value.trim();
  const password = $("loginPassword").value;
  if (!user) {
    toast("请输入账号");
    $("loginUserSelect").focus();
    return;
  }
  if (!password) {
    toast("请输入密码");
    $("loginPassword").focus();
    return;
  }
  const result = await api("/api/auth/login", {
    method: "POST",
    headers: { "Content-Type": "application/json; charset=utf-8" },
    body: JSON.stringify({ user, password }),
  });
  // 先落令牌再进应用：enterApp 会立刻打 /api/user/load，那一下就得带上凭据。
  if (result.token) localStorage.setItem(SESSION_TOKEN_KEY, result.token);
  if (result.mustChangePassword) {
    state.pendingPasswordUser = result.user;
    state.pendingOldPassword = password;
    $("changePasswordBox").classList.remove("hidden");
    $("newPassword").focus();
    toast("首次登录，请修改密码");
    return;
  }
  await enterApp(result.user);
}

async function changePassword() {
  if (!state.pendingPasswordUser) {
    toast("请先登录");
    return;
  }
  const newPassword = $("newPassword").value;
  const confirmPassword = $("confirmPassword").value;
  if (newPassword.length < 6) {
    toast("新密码至少 6 位");
    return;
  }
  if (newPassword !== confirmPassword) {
    toast("两次输入的新密码不一致");
    return;
  }
  const changed = await api("/api/auth/change-password", {
    method: "POST",
    headers: { "Content-Type": "application/json; charset=utf-8" },
    body: JSON.stringify({
      user: state.pendingPasswordUser,
      oldPassword: state.pendingOldPassword,
      newPassword,
    }),
  });
  // 改密会吊销该用户全部旧会话并重签一张：旧令牌必须换掉，否则改完密码马上又被踢出去。
  if (changed.token) localStorage.setItem(SESSION_TOKEN_KEY, changed.token);
  await enterApp(state.pendingPasswordUser);
  toast("密码已修改");
}

async function enterApp(user, options = {}) {
  if (options.rememberSession !== false) localStorage.setItem(SESSION_USER_KEY, user);
  /* tolerateFailure：服务端读用户数据失败时**照样进应用**，但只读
     （顶部常驻横幅 + 不保存）。以前会让登录整个失败，用户连导出都做不到；
     更早的版本则是把读失败当成空账号，直接把真实进度覆盖掉。 */
  await loadUserData(user, { tolerateFailure: true });
  /* 上次关页时没存上的改动（sendBeacon 兜底失败，见 flushPendingSaveBeacon）。
     必须在 applyQuestionWindow / restoreLastCourse 之前接回来，否则界面先按
     服务器那份旧数据渲染一遍，再被替换掉。 */
  applyPendingSaveDraft();
  // 题量/题序是题单窗口的一部分，要在 restoreLastCourse → loadQuestions 之前落位
  applyQuestionWindow();
  $("loginView").classList.add("hidden");
  $("appShell").classList.remove("hidden");
  document.body.classList.remove("admin-view");
  setMobileActions(false);
  setMobileTools(false);
  $("adminDashboard").classList.add("hidden");
  $("questionView").classList.remove("hidden");
  state.storage.profile.lastLoginAt = nowText();
  if (!isAdmin()) {
    /* 刷新/重新登录后回到上次所在的模式（章节与题号由 restoreLastCourse 恢复）。
       以前无条件回 training，用户刷到一半刷新就得重新选章节。 */
    const savedMode = state.storage.settings?.lastMode;
    state.mode = RESTORABLE_MODES.includes(savedMode) ? savedMode : "training";
  }
  await loadCourses();
  /* 老包里的 questionChapter（qid→chapterId）摘到本地缓存并置空 ——
     必须放在 loadCourses 之后：缓存要记题库版本（changedAt），而版本号来自课程列表。 */
  migrateQuestionChapterToLocalCache();
  // 外挂章节映射是选题逻辑的输入，尽量在进入应用时取好；取不到也不挡进入（回落成空表）。
  await loadChapterMap();
  if (isAdmin()) {
    // 账号清单改成了管理员专用接口，进面板前先取一次（失败不该挡住进入应用）
    await loadUsers().catch((err) => {
      console.warn("user list load failed", err);
      state.users = [];
    });
    renderAdminDashboard();
  } else {
    await restoreLastCourse();
  }
  /* ⚠️ 这里**刻意没有** scheduleSave()。
     旧实现无条件存一次（当时是为了落 profile.lastLoginAt），后果是每次打开/刷新
     都回写整包：服务器 revision 前进 → 之前打开的标签页保存时 409；
     而且 326KB 的包（questionChapter 占 99.8%）每次开页都完整 POST 一次。
     现在只有**真有改动**的流程才 markStorageDirty（lastLoginAt 这类只改内存的
     字段会随下一次真实改动一起落盘）。 */
}

async function logout() {
  clearUserDataCache();
  /* 保存失败不能静默：以前是 `.catch(() => {})`，结果「答题后点退出」会把本地未保存的改动
     无声丢掉（多标签页场景服务器会返回 409）。现在改为提示，并由用户决定是否继续退出。

     ⚠️ 但只在「本地**确实有**未保存改动」时才打扰用户：
     同一账号开了多个标签页/设备时，另一个先保存过 → 本页 revision 过期 → 409，
     此时退出本身是安全的，弹冲突框反而会挡住退出流程。 */
  const hadUnsaved = !!state.storageDirty;
  try {
    await saveUserData();
  } catch (err) {
    if (hadUnsaved) {
      handleUserSaveError(err);
      if (err?.status === 409 || err?.code === "user_data_conflict") return;   // 交给冲突对话框
      if (!confirm("本地改动还没保存成功，确定仍要退出吗？\n\n点「取消」可留在页面重试或先导出备份。")) return;
    }
  }
  await api("/api/auth/logout", { method: "POST" }).catch(() => {});
  stopExamTimer();
  localStorage.removeItem(SESSION_USER_KEY);
  localStorage.removeItem(SESSION_TOKEN_KEY);
  clearTimeout(state.saveTimer);
  state.saveTimer = null;
  state.storageDirty = false;
  setSaveStatus("idle");
  state.user = "";
  /* 换账号：在途保存的响应不能落到下一个账号头上（世代比对里 user 那一项也管这事）。 */
  state.loadGeneration = (state.loadGeneration || 0) + 1;
  state.mutationVersion = 0;
  state.saveChain = null;
  state.storageInheritedDraft = null;
  state.conflictLocalSnapshot = null;
  state.conflictDraft = null;
  state.conflictDraftId = "";
  state.conflictDrafts = null;
  state.questionRequestId = (state.questionRequestId || 0) + 1;
  state.typeRequestId = (state.typeRequestId || 0) + 1;
  state.modeTransitionId = (state.modeTransitionId || 0) + 1;
  state.submitToken = (state.submitToken || 0) + 1;
  state.submitting = false;
  state.currentCourse = null;
  state.questions = [];
  state.answers = {};
  document.body.classList.remove("is-admin", "admin-view", "picker-open", "mobile-actions-open", "mobile-tools-open");
  showLogin();
}

/* 读用户数据。
   ⚠️ 读失败**不等于**「这个账号没有数据」：服务端读失败会回 5xx（见 /api/user/load），
   这里必须把它和「首次用户（200 + 空数据 + revision:""）」分开处理。
   旧实现把 5xx 直接抛给调用方 → 登录页报错；更早的实现把读失败当成空数据 →
   下一次保存把用户真实进度整包覆盖（实测 supa.json 2651 → 700 字节）。
   现在：只在 tolerateFailure 时进**只读态**（能看、能导出，但不保存）；其它错误照旧上抛。 */
/* 本机数据缓存：**只**用来给 `/api/user/load` 做条件请求 —— 服务端说「还是这一版」
   （revision 相同 = 落盘全文逐字节相同）时直接复用，省掉那 ~1MB 整包。
   它不参与任何写入判断：写缓存失败（localStorage 配额满）就当没有缓存，功能不受影响；
   缓存过期也无害 —— 校验器对不上就是一次普通全量读取。 */
const USER_DATA_CACHE_KEY = "yt.userDataCache.v1";
let userDataCacheTimer = 0;

function readUserDataCache(user) {
  try {
    const raw = localStorage.getItem(USER_DATA_CACHE_KEY);
    if (!raw) return null;
    const cache = JSON.parse(raw);
    if (!cache || cache.user !== user || typeof cache.body !== "string" || !cache.revision) return null;
    return cache;
  } catch (_err) {
    return null;
  }
}

function clearUserDataCache() {
  clearTimeout(userDataCacheTimer);
  userDataCacheTimer = 0;
  try { localStorage.removeItem(USER_DATA_CACHE_KEY); } catch (_err) { /* 清不掉也无害 */ }
}

function writeUserDataCacheNow() {
  clearTimeout(userDataCacheTimer);
  userDataCacheTimer = 0;
  if (!state.user || !state.storageRevision || !state.syncedBody || state.storageDirty) return;
  try {
    localStorage.setItem(USER_DATA_CACHE_KEY, JSON.stringify({
      user: state.user,
      revision: state.storageRevision,
      body: state.syncedBody,
      at: Date.now(),
    }));
  } catch (_err) {
    /* 配额被草稿占满时优先保住草稿：缓存删掉，下次开页大不了重下一遍。 */
    try { localStorage.removeItem(USER_DATA_CACHE_KEY); } catch (_err2) { /* 忽略 */ }
  }
}

/* 保存很频繁（每答一题一次），别每次都序列化 1MB 写进 localStorage：
   攒 3 秒再写。缓存本身是「可能过期」的，晚写只会少省一次下载，不会读到错数据。 */
function scheduleUserDataCacheWrite() {
  clearTimeout(userDataCacheTimer);
  userDataCacheTimer = setTimeout(writeUserDataCacheNow, 3000);
}

async function loadUserData(user, options = {}) {
  /* 换代：这次读数据之后回来的、上一次读数据之前发出的保存响应，一律按过期处理
     （它们带的 revision/账号上下文已经不对了，命中由 saveUserData 的世代比对完成）。 */
  state.loadGeneration = (state.loadGeneration || 0) + 1;
  state.mutationVersion = 0;
  state.saveChain = null;
  state.storageInheritedDraft = null;
  state.draftConfirmations = [];
  state.conflictDraftId = "";
  state.conflictDrafts = null;
  state.pendingConflict = null;
  const generation = state.loadGeneration;
  const loadUrl = `/api/user/load?user=${encodeURIComponent(user)}`;
  /* 本机缓存里就是上次从服务端读到的那份内容：带上它的 revision 做条件请求。
     同一账号换了设备/别的标签页保存过 → revision 对不上 → 正常回全量，不吃亏。 */
  const cache = state.storageDirty ? null : readUserDataCache(user);
  const condOpts = cache ? { headers: { "If-None-Match": `"${cache.revision}"` }, allowNotModified: true } : undefined;
  let res;
  try {
    let savesBefore = state.saveSuccessSeq;
    res = await api(loadUrl, condOpts);
    /* 读的过程中本页可能有保存落地：服务端那份 revision 已经前进，
       而这次读到的是保存**之前**的快照。采用它会把 storageRevision 回退，
       紧接着任何一次保存都会撞 409（用户看到的是「刚选完加载服务器数据，
       状态条又卡在保存中，草稿还在」）。快照过期就重读，直到没有新保存落地 —— 见下面那段。 */

    /* 一次重读不够：慢网下保存会一笔接一笔落地（每笔都可能早于本次读的快照），
       于是重读本身也可能读到旧值 —— 断点就在这里（e2e 实测 revision 被拽回旧值 → 下一次保存 409）。
       现在按 saveSuccessSeq 逐轮重读，直到「读完这一轮期间没有新保存落地」，最多 3 轮。 */
    for (let attempt = 0; attempt < 3 && state.saveSuccessSeq !== savesBefore; attempt++) { savesBefore = state.saveSuccessSeq;
      res = await api(loadUrl, condOpts);
    }
    if (res && res.notModified) {
      /* 服务端：还是你手里那一版。用缓存直接渲染，这 ~1MB 不用再下一遍。
         缓存坏了（损坏/被别的版本改过）就退回一次无条件全量读取，不让用户卡住。 */
      let cachedData = null;
      try { cachedData = JSON.parse(cache.body); } catch (_err) { cachedData = null; }
      res = cachedData ? { ok: true, user, data: cachedData, revision: cache.revision } : await api(loadUrl);
    }
  } catch (err) {
    const readFailed = !err?.status || err.status >= 500;
    setStorageReadFailed(readFailed, err?.message || "");
    if (readFailed && options.tolerateFailure) {
      /* 只读态：空基线只为让界面能渲染；storageDirty 保持 false，不会去回写。 */
      state.user = user;
      state.storage = normalizeStorage({});
      state.storageRevision = "";
      state.storageDirty = false;
      setSaveStatus("idle");
      state.zoom = 1;
      localStorage.setItem(LAST_USER_KEY, state.user);
      renderUsers();
      return res;
    }
    throw err;
  }
  if (generation !== state.loadGeneration) return res;   // 期间又 load 过（切账号/恢复冲突）
  setStorageReadFailed(false, "");
  state.user = res.user;
  state.storage = normalizeStorage(res.data || {});
  state.storageRevision = res.revision || "";
  /* 这份内容就是服务端现在的内容 → 记下来当增量保存的基线（键序由 normalizeStorage 钉死，
     所以和之后 JSON.stringify(state.storage) 的口径一致）。 */
  state.syncedBody = JSON.stringify(state.storage);
  state.storageDirty = false;
  setSaveStatus("idle");
  state.zoom = normalizeZoom(state.storage.settings?.zoom);
  localStorage.setItem(LAST_USER_KEY, state.user);
  /* 数据取回来了，这正是判定「上次 beacon/补存到底成没成」的时机：
     内容与服务端一致的那条草稿才算确认，然后**按 ID** 定向删（ST-02）。
     别的账号的草稿这里一概不读、不动。 */
  migrateAndReconcileDrafts();
  if (refreshDailyTrainingState()) {
    // Day rolled over while data was offline: keep local dirty so today's clean slate persists.
    scheduleSave();
  }
  renderUsers();
  /* 记下这份内容，下次开页（或再点「加载服务器数据」）就能靠 304 省掉整包。 */
  writeUserDataCacheNow();
  return res;
}

/* 只读态的开关 + 顶部横幅。横幅是**常驻**的（不是 toast）：
   用户可能在只读态里做很久，几秒就消失的提示等于没提示。 */
function setStorageReadFailed(failed, message) {
  const next = !!failed;
  const changed = state.storageReadFailed !== next;
  state.storageReadFailed = next;
  state.storageReadErrorMessage = next ? (message || "") : "";
  if (next && changed) {
    /* 定时器里可能已经挂了一次保存，必须撤掉：它会带着空基线去打服务端。 */
    clearTimeout(state.saveTimer);
    state.saveTimer = null;
    state.storageDirty = false;
  }
  updateStorageReadOnlyBanner();
  updateSaveStatusUi();   // 「已同步 / 只读 · 未保存」两态切换要立刻反映到顶栏和账号菜单
}

function updateStorageReadOnlyBanner() {
  const banner = $("readOnlyBanner");
  if (!banner) return;
  banner.classList.toggle("hidden", !state.storageReadFailed);
}

function normalizeZoom(value) {
  const zoom = Number(value || 1);
  if (!Number.isFinite(zoom)) return 1;
  return Math.max(0.8, Math.min(1.5, zoom));
}

function normalizeStorage(data) {
  const tags = data.tags && typeof data.tags === "object" ? data.tags : {};
  const tagLabels = [...new Set([...(Array.isArray(data.tagLabels) ? data.tagLabels : Object.keys(tags || {})), ...DEFAULT_TAG_LABELS])];
  const plans = data.plans && typeof data.plans === "object" ? data.plans : {};
  const examDrafts = data.examDrafts && typeof data.examDrafts === "object" ? data.examDrafts : {};
  if (data.plan?.courseId && !plans[String(data.plan.courseId)]) plans[String(data.plan.courseId)] = data.plan;
  if (data.examDraft?.courseId && !examDrafts[String(data.examDraft.courseId)]) examDrafts[String(data.examDraft.courseId)] = data.examDraft;
  return {
    profile: data.profile || {},
    courses: data.courses || {},
    wrong: data.wrong || {},
    favorite: data.favorite || {},
    notes: data.notes || {},
    corrections: Array.isArray(data.corrections) ? data.corrections : [],
    history: Array.isArray(data.history) ? data.history : [],
    examHistory: Array.isArray(data.examHistory) ? data.examHistory : [],
    examDraft: null,
    examDrafts,
    dailyReports: Array.isArray(data.dailyReports) ? data.dailyReports : [],
    dailyActivity: data.dailyActivity && typeof data.dailyActivity === "object" ? data.dailyActivity : {},
    confidence: data.confidence && typeof data.confidence === "object" ? data.confidence : {},
    wrongReasons: data.wrongReasons && typeof data.wrongReasons === "object" ? data.wrongReasons : {},
    /* smartPractice 已删除：它是「当前可继续题单」的第二个指针，和
       activeTrainingSessionId 生命周期不同 → 同屏两个「继续」各指一条题单。
       现在唯一事实来源是 trainingSessions + activeTrainingSessionId。
       老存档里的这个字段在这里被**丢弃**（不迁移）：它指向的会话本来就在
       trainingSessions 里，迁移只会把两个指针的分歧带进来。
       注意 data.smartPractice 里没有会话列表里没有的信息，丢弃不丢数据。 */
    trainingSessions: Array.isArray(data.trainingSessions) ? data.trainingSessions : [],
    activeTrainingSessionId: data.activeTrainingSessionId || "",
    tags,
    tagLabels,
    settings: data.settings || {},
    plan: data.plan || null,
    plans,
  };
}

function userCourseStore(courseId = state.currentCourse?.id) {
  const key = String(courseId || "global");
  state.storage.courses[key] ||= { answers: {}, done: {}, correct: {}, wrong: {}, verified: {}, lastSubjectId: 0 };
  state.storage.courses[key].verified ||= {};
  return state.storage.courses[key];
}

function peekCourseStore(courseId = state.currentCourse?.id) {
  return state.storage.courses[String(courseId || "global")] || { answers: {}, done: {}, correct: {}, wrong: {}, verified: {}, lastSubjectId: 0 };
}

/* 一道题「累计做过」的判据 = done ∪ correct ∪ wrong。
   为什么不能只看 done：老数据里大量题目只有 correct/wrong 而没有 done ——
     · 考场交卷旧实现只写 correct/wrong（本文件 submitPaper 已修）；
     · 导入 / 从旧版本同步过来的包同样只带 correct/wrong。
   只认 done 会让这些题在「新题」（countNeverDone）里被当成从没做过再推一遍，
   在「薄弱章节」里永远不出现。这是**有意的修正**：老用户的薄弱章节会因此变多。 */
function isQuestionDone(courseStore, id) {
  if (!courseStore || id == null) return false;
  return !!(courseStore.done?.[id] || courseStore.correct?.[id] || courseStore.wrong?.[id]);
}

/* 同一判据的集合版：给需要遍历全部已做题的地方用（章节树的「已做」归集）。 */
function doneQuestionIds(courseStore) {
  const ids = new Set();
  for (const key of ["done", "correct", "wrong"]) {
    const store = courseStore?.[key];
    if (!store || typeof store !== "object") continue;
    for (const id of Object.keys(store)) if (store[id]) ids.add(id);
  }
  return ids;
}

function setSaveStatus(status) {
  state.saveStatus = status || "idle";
  updateSaveStatusUi();
}

function updateSaveStatusUi() {
  const status = state.user ? (state.saveStatus || "idle") : "idle";
  const map = {
    idle: "已同步",
    dirty: "未保存",
    saving: "保存中…",
    error: "保存失败",
  };
  /* 只读态：状态文案必须说清楚「不是已同步」，否则用户看到「已同步」以为存上了。 */
  const pendingCount = state.pendingConflict
    ? ((state.pendingConflict.drafts || []).length || 1)
    : 0;
  const label = state.storageReadFailed
    ? "只读 · 未保存"
    : (pendingCount ? `未补存 ${pendingCount} 份 · 点击处理` : (map[status] || map.idle));
  /* 顶栏账号头像上的状态角标。
     同步状态原本只写在账号下拉菜单里（#saveStatusLabel），菜单一关宽度就是 0 ——
     409 之后按「暂时保留本地」时，界面上唯一的「保存失败」提示在看不见的地方。
     角标是同一个 state.saveStatus 的第二个投递点，红点/同步点由 CSS 画。 */
  const avatar = $("accountMenuBtn");
  if (avatar) {
    avatar.classList.toggle("has-error", status === "error" || state.storageReadFailed);
    avatar.classList.toggle("is-syncing", !state.storageReadFailed && (status === "dirty" || status === "saving"));
    avatar.dataset.sync = state.storageReadFailed ? "error" : status;
    avatar.title = state.user ? `账号 · ${label}` : "账号";
  }
  const el = $("saveStatusLabel");
  if (!el) return;
  el.classList.toggle("is-actionable", pendingCount > 0);
  if (pendingCount) el.title = "点这里处理没补存的本地改动";
  else el.removeAttribute("title");
  if (!state.user) {
    el.textContent = "";
    el.dataset.status = "idle";
    return;
  }
  el.textContent = label;
  el.dataset.status = state.storageReadFailed ? "error" : (state.saveStatus || "idle");
}

function markStorageDirty() {
  /* 代际自增必须在**每一次** storage 变更时发生：在途保存靠它判断
     「响应回来时 storage 是否又变过」（ST-05）。 */
  state.mutationVersion = (state.mutationVersion || 0) + 1;
  state.storageDirty = true;
  if (state.saveStatus !== "saving") setSaveStatus("dirty");
}

function scheduleSave() {
  if (!state.user) return;
  /* 只读态（服务端读用户数据失败）下**一次都不发**。
     基线是空的，发出去就是拿空包覆盖用户的真实进度。 */
  if (state.storageReadFailed) return;
  markStorageDirty();
  clearTimeout(state.saveTimer);
  state.saveTimer = setTimeout(() => {
    state.saveTimer = null;
    saveUserData().catch(handleUserSaveError);
  }, 450);
}

/* 隐藏页/关页时兜底保存失败的落点：整包写进 localStorage，
   下次开页提示并补存（见 applyPendingSaveDraft）。

   ── 为什么是「集合」而不是一个键 ──────────────────────────────
   旧实现只有一个 `yunxi-pending-save` 槽，谁后写谁覆盖：
     · alpha 的未存笔记被 beta 登录时的 apply 直接 clear（删别人的数据）；
     · 同账号标签页 A 存了 notes101=tabA，B 一存 tabB 就把 A 盖掉；
     · 补存成功的回调无条件 clear，会把期间别的标签页写进来的新草稿一起删。
   现在每个草稿占**独立键** `yunxi-save-draft:<账号>:<标签页>:<草稿ID>`，
   索引键只存 ID 列表；清理一律按 DraftId 定向删（compare-and-delete），
   别人的账号一律**跳过不删**（ST-02/03/04 的公共地基）。 */
const PENDING_SAVE_KEY = "yunxi-pending-save";        // 旧单槽：只读，用来迁移
const DRAFT_INDEX_KEY = "yunxi-save-drafts";
const DRAFT_PREFIX = "yunxi-save-draft:";
/* localStorage 的配额通常 ~5MB，而整包 storage 在重题库上能到 300KB+。
   配额满了 setItem 会抛 → 新草稿写不进去。清理顺序：
   ① 「已确认」的（服务端就是这份内容，见 markDraftConfirmed）；② 同一个 tab 自己的旧草稿。
   绝不动别的账号、别的标签页的草稿 —— 那些是它们唯一的恢复点。 */
const DRAFT_INDEX_MAX = 8;
/* sendBeacon 的 body 上限（浏览器规范 64KB，取 60KB 留余量）。超过它 sendBeacon
   返回 false 且一个字节都不发，见 flushPendingSaveBeacon。 */
const BEACON_MAX_BYTES = 60 * 1024;

function normalizeDraftUser(user) {
  return String(user || "").trim().toLowerCase();
}

/* 草稿 ID = user + tab + 随机。TAB_ID 是页面级随机值，
   所以同账号两个标签页各自的草稿天然是两条独立条目。 */
function makeDraftId(user) {
  return [
    encodeURIComponent(normalizeDraftUser(user) || "anon"),
    TAB_ID,
    Math.random().toString(36).slice(2, 8) + Date.now().toString(36).slice(-4),
  ].join(".");
}

function draftKeyFor(id) {
  return DRAFT_PREFIX + String(id);
}

function readDraftIndex() {
  try {
    const raw = localStorage.getItem(DRAFT_INDEX_KEY);
    const list = raw ? JSON.parse(raw) : null;
    return Array.isArray(list) ? list.filter((row) => row && typeof row.id === "string") : [];
  } catch (_err) {
    return [];
  }
}

function writeDraftIndex(list) {
  try { localStorage.setItem(DRAFT_INDEX_KEY, JSON.stringify(list)); } catch (_err) { /* 索引丢了草稿仍在 */ }
}

function readDraftRecord(id) {
  try {
    const raw = localStorage.getItem(draftKeyFor(id));
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    return parsed && typeof parsed === "object" && parsed.storage ? parsed : null;
  } catch (_err) {
    return null;
  }
}

/* 定向清理：只删「这次明确要删的那一条」。
   期间别的标签页/别的账号写进来的新草稿不受影响（旧实现的整槽 removeItem 会连坐）。 */
function removeDraftById(id) {
  if (!id) return false;
  try { localStorage.removeItem(draftKeyFor(id)); } catch (_err) { /* 删不掉不影响使用 */ }
  writeDraftIndex(readDraftIndex().filter((row) => row.id !== id));
  return true;
}

/* 删掉**本标签页**所有草稿（只在确认服务端已收下本页这份内容后调用）。 */
function removeOwnDrafts(user) {
  const me = normalizeDraftUser(user || state.user);
  for (const row of readDraftIndex()) {
    if (normalizeDraftUser(row.user) === me && row.tab === TAB_ID) removeDraftById(row.id);
  }
}

function draftSummaryFor(entry) {
  return { id: entry.id, user: entry.user, tab: entry.tab, at: entry.at, reason: entry.reason, revision: entry.revision };
}

/* 写一份草稿。user 默认当前账号；只读态写草稿是安全的
   （基线本就空着，落盘只是留个恢复点，不会被当成「已同步」）。 */
function stashPendingSaveDraft(reason, options = {}) {
  const user = options.user || state.user;
  if (!user) return false;
  const id = options.id || makeDraftId(user);
  const record = {
    id,
    user,
    tab: TAB_ID,
    dirty: options.dirty !== false,
    revision: options.revision !== undefined ? (options.revision || "") : (state.storageRevision || ""),
    reason: reason || "",
    at: nowText(),
    storage: options.storage || state.storage,
  };
  // 最多保留 8 条：① 已确认的 ② 本标签页自己的旧草稿，之后才轮到别的标签页/账号的旧草稿
  //   （那条只可能删到 7 条以前写下的、既没确认也没同步的，概率极低；这条注释就是它的说明）。
  let index = readDraftIndex().filter((row) => row.id !== id);
  while (index.length >= DRAFT_INDEX_MAX) {
    index = dropOneDraftForSpace(index);
    if (index === null) return false;
  }
  index.push(draftSummaryFor(record));
  writeDraftIndex(index);
  try {
    localStorage.setItem(draftKeyFor(id), JSON.stringify(record));
  } catch (err) {
    // 写正文失败 → 刚挂上的索引条目要摘掉，否则索引指向空条目
    writeDraftIndex(readDraftIndex().filter((row) => row.id !== id));
    console.warn("未保存改动落地失败", err);
    return false;
  }
  return id;
}

/* 返回去掉一个之后的索引；无可删则返回 null。 */
function dropOneDraftForSpace(index) {
  const next = [...index];
  const take = (pick) => {
    const at = next.findIndex(pick);
    if (at < 0) return null;
    const [row] = next.splice(at, 1);
    return row;
  };
  const drop = (row) => {
    try { localStorage.removeItem(draftKeyFor(row.id)); } catch (_err) { /* 忽略 */ }
  };
  const confirmed = take((row) => isDraftConfirmed(row.id, row.user));
  if (confirmed) { drop(confirmed); return next; }
  const mine = take((row) => row.tab === TAB_ID);
  if (mine) { drop(mine); return next; }
  const me = normalizeDraftUser(state.user);
  const stale = take((row) => normalizeDraftUser(row.user) === me);
  if (stale) { drop(stale); return next; }
  // 全是别的账号的：宁可写不下这一份，也不删别人的未保存数据
  return null;
}

/* 读草稿：默认只读**当前账号**的，按写入时间倒序（最新的先拿来补存）。
   includeOtherUsers 走导出/清理入口。 */
function readPendingSaveDrafts(options = {}) {
  const me = normalizeDraftUser(state.user);
  /* 默认**只**读当前账号的：草稿隔离的第一道闸就是这里 ——
     别的账号的条目连读都不读，后面的清理自然碰不到它们。 */
  const includeAll = !!options.includeOtherUsers;
  const rows = [];
  const index = readDraftIndex();
  for (const row of index) {
    if (!includeAll && normalizeDraftUser(row.user) !== me) continue;
    const record = readDraftRecord(row.id);
    if (!record) continue;   // 索引有、正文没了：跳过（索引会在下次写入时收敛）
    rows.push(record);
  }
  const ordered = rows.sort((a, b) => String(b.at || "").localeCompare(String(a.at || "")));
  return options.order === "oldest" ? ordered.reverse() : ordered;
}

/* 旧单槽迁移：读出来当一条草稿，正文转写到新键，然后**按 ID 定向**删掉旧槽。
   迁移只认「当前账号」的那条：槽里是别人的账号就原样留着，等他自己登回来。 */
function migrateLegacyPendingDraft() {
  let legacy = null;
  try {
    const raw = localStorage.getItem(PENDING_SAVE_KEY);
    if (!raw) return false;
    legacy = JSON.parse(raw);
  } catch (_err) {
    try { localStorage.removeItem(PENDING_SAVE_KEY); } catch (_e2) { /* 损坏的旧槽 */ }
    return false;
  }
  if (!legacy || typeof legacy !== "object" || !legacy.storage) {
    try { localStorage.removeItem(PENDING_SAVE_KEY); } catch (_e3) { /* 损坏的旧槽 */ }
    return false;
  }
  const me = normalizeDraftUser(state.user);
  if (normalizeDraftUser(legacy.user) !== me) return false;   // 别人的草稿：留着
  const id = stashPendingSaveDraft(legacy.reason || "migrated", {
    user: legacy.user, revision: legacy.revision, storage: legacy.storage,
  });
  if (id) {
    try { localStorage.removeItem(PENDING_SAVE_KEY); } catch (_err) { /* 下次再迁 */ }
  }
  return !!id;
}

/* ⚠️ 兼容壳：老探针/老代码可能还在找「那一个」草稿。
   只回答**当前账号 + 本标签页**最新的一条，别的账号一概不回。 */
function readPendingSaveDraft() {
  const mine = readPendingSaveDrafts().filter((draft) => draft.tab === TAB_ID);
  return mine.length ? mine[0] : null;
}

/* ⚠️ 兼容壳：**只给当前探针/内联调用用**。
   传 draftId 才是定向删（生产路径一律走 removeDraftById）；
   不传就当作「明确放弃本页当前账号的草稿」，且**不碰别的账号**。 */
function clearPendingSaveDraft(draftId) {
  if (draftId) return removeDraftById(draftId);
  for (const draft of readPendingSaveDrafts()) removeDraftById(draft.id);
  return true;
}

/* 拿「已确认」清单去对草稿：内容与服务端**完全一致**的那条才算确认，
   然后按 ID 定向删。这就是「beacon 只算排队，成功与否由下次 load 判定」的落点
   （ST-02）。没有凭证就什么都不删。 */
function reconcileConfirmedDrafts() {
  if (!state.user || state.storageReadFailed) return false;
  const json = JSON.stringify(state.storage);
  let changed = false;
  const additions = [];
  for (const record of readPendingSaveDrafts()) {
    let same = false;
    try { same = JSON.stringify(record.storage) === json; } catch (_err) { same = false; }
    if (!same) continue;
    if (!getDraftConfirmations().some((item) => item.id === record.id)) additions.push(draftSummaryFor(record));
    removeDraftById(record.id);
    changed = true;
  }
  if (additions.length) pushDraftConfirmations(additions);
  return changed;
}

/* 数据已经取回来了 → 把旧槽迁进集合，再对一遍「已确认」清单。 */
function migrateAndReconcileDrafts() {
  migrateLegacyPendingDraft();
  reconcileConfirmedDrafts();
}

function getDraftConfirmations() {
  return Array.isArray(state.draftConfirmations) ? state.draftConfirmations : [];
}

function pushDraftConfirmations(list) {
  state.draftConfirmations = getDraftConfirmations().concat(list).slice(-16);
}

/* 「这份内容服务端已经收下了」的凭据：记 ID（给草稿反查用）+ 内容哈希（给导入去重/兼容用）。 */
function markDraftConfirmed(ids, storages) {
  const additions = [];
  (ids || []).forEach((id, i) => {
    if (!id) return;
    additions.push({ id, user: state.user, digest: storageDigest((storages || [])[i]), at: nowText() });
    removeDraftById(id);
  });
  if (additions.length) pushDraftConfirmations(additions);
  return additions;
}

function isDraftConfirmed(id, user) {
  const me = normalizeDraftUser(user || state.user);
  return getDraftConfirmations().some((item) => item.id === id && normalizeDraftUser(item.user) === me);
}

function storageDigest(storage) {
  try { return digestText(JSON.stringify(storage)); } catch (_err) { return ""; }
}

function digestText(text) {
  const str = String(text || "");
  let h1 = 0x811c9dc5;
  let h2 = 0x01000193;
  for (let i = 0; i < str.length; i += 1) {
    const code = str.charCodeAt(i);
    h1 = Math.imul(h1 ^ code, 16777619) >>> 0;
    h2 = Math.imul(h2 + code, 2246822519) >>> 0;
  }
  return h1.toString(16).padStart(8, "0") + h2.toString(16).padStart(8, "0");
}

/* 补存时**只贴**当次那份草稿的内容。
   旧实现 `state.storage = draft.storage` 会把用户此刻正在编辑的（可能更新的）
   storage 换回旧草稿，等于把编辑动作抹掉。 */
function pickDraftContent(record) {
  if (!record) return;
  state.storage = normalizeStorage(record.storage);
  state.storageDirty = true;
  state.storageInheritedDraft = { id: record.id, at: record.at || "", user: record.user || "" };
  setSaveStatus("dirty");
}

/* 开页时把上次没存上的改动接回来。三种情况：
     ① 草稿内容 = 服务器那份（别的标签页后来存成功了）→ 直接删掉这条，不打扰用户；
     ② 服务器还停在同一 revision → 当场补存（数据找回来了，按 ID 定向删）；
     ③ 服务器已经前进（别的标签页/设备写过）→ 不覆盖，把草稿摆进「数据冲突」
        对话框让用户自己决定（导出备份 / 保留本地 / 加载服务器最新数据）。
   为什么不无条件覆盖：那会把别人的新改动盖掉。
   为什么跳过别的账号而不是清掉：隔离身份时删掉的是**别人唯一的未保存数据**。 */
function applyPendingSaveDraft() {
  /* 只读态：基线是空的，把草稿贴上来也存不下去，只会让用户以为改动还在。
     草稿**留着**（不清），下次读成功时再走正常补存。 */
  if (state.storageReadFailed) return false;
  migrateLegacyPendingDraft();
  const drafts = readPendingSaveDrafts().filter((row) => row.dirty);
  if (!drafts.length) return false;
  const newest = drafts[0];
  let same = false;
  try { same = JSON.stringify(newest.storage) === JSON.stringify(state.storage); } catch (_err) { same = false; }
  if (same) {
    markDraftConfirmed([newest.id], [newest.storage]);
    return false;
  }
  if (String(newest.revision || "") !== String(state.storageRevision || "")) {
    /* 服务器已经前进，草稿的 revision 永久过期，不能再拿它补存。
       ⚠️ 但**开页时不弹模态**：旧实现每次开页都弹同一个「数据冲突」框，
       用户点「关闭」清不掉、直接刷新也清不掉 —— 表现为「刷新一次弹一次」
       （实测：答一题后立刻刷新，之后每次刷新都弹，且不再有任何网络请求）。
       现在只把草稿挂到账号菜单里的「未补存」入口，用户想处理时点一下就是同一个冲突框。 */
    state.pendingConflict = { drafts, newest };
    setSaveStatus("error");
    toast(drafts.length > 1
      ? `本机有 ${drafts.length} 份改动没补存（服务器已更新），点账号菜单里的「未补存」处理`
      : `上次有 1 份改动没补存（服务器已更新），点账号菜单里的「未补存」处理`);
    return false;
  }
  pickDraftContent(newest);
  toast(`上次有未保存的改动（${newest.at || ""}），正在补存…`);
  saveUserData().then(() => {
    /* 草稿的删除在 saveUserData 成功分支里按 ID 定向做（那里才知道这次发的是哪一份）。 */
    toast("上次未保存的改动已补存");
  }).catch((err) => {
    /* 补存失败：草稿留着，下次开页再试；冲突照走冲突对话框。 */
    handleUserSaveError(err);
  });
  return true;
}

/* 开页时不打扰用户，但草稿必须还能处理 —— 这是那个「未补存」入口的落点。
   点账号菜单里的保存状态就走到这里，弹的是同一个冲突框（导出 / 保留本地 / 加载服务器）。 */
function openPendingConflict() {
  const pending = state.pendingConflict;
  const newest = pending?.newest;
  if (!newest) return false;
  const drafts = pending.drafts || [newest];
  openConflictDialog(
    {
      message: drafts.length > 1
        ? `本机有 ${drafts.length} 份未保存的改动（最新一份在 ${newest.at || "时间未知"}），但服务器数据已在其他页面更新，没有自动补存。可导出这份草稿备份，或加载服务器最新数据。`
        : `上次有未保存的改动（${newest.at || "时间未知"}），但服务器数据已在其他页面更新，没有自动补存。可导出这份草稿备份，或加载服务器最新数据。`,
    },
    { user: newest.user, revision: newest.revision || "", storage: newest.storage, at: newest.at || "" },
    { draftId: newest.id, count: drafts.length, drafts },
  );
  return true;
}

/* 重算「有没有还挂着没补存的草稿」。冲突框关掉后要重算一次：
   用户点了「加载服务器最新数据」→ 草稿没了 → 入口跟着消失；
   只点了「关闭」→ 草稿还在 → 入口留着，随时能再打开。 */
function refreshPendingConflict() {
  if (!state.user || state.storageReadFailed) {
    state.pendingConflict = null;
    return;
  }
  const stale = readPendingSaveDrafts().filter((row) => row.dirty
    && String(row.revision || "") !== String(state.storageRevision || ""));
  state.pendingConflict = stale.length ? { drafts: stale, newest: stale[0] } : null;
  /* 挂上时走 setSaveStatus（顺带刷 UI）；**清掉时也必须刷**，否则状态条会一直停在
     上一次的「未补存 N 份 · 点击处理」——草稿其实已经删干净了，用户却以为还没处理完
     （e2e 实测：drafts=[] 但文案仍是「未补存 2 份」）。 */
  if (state.pendingConflict) setSaveStatus("error");
  else updateSaveStatusUi();
}

function flushPendingSaveBeacon() {
  if (!state.user || !state.storageDirty) return false;
  /* 只读态下 sendBeacon 更危险：它不看响应，发出去就当作已保存。 */
  if (state.storageReadFailed) return false;
  /* ⚠️ sendBeacon 返回 true **只表示浏览器把请求排进了队列**，既不代表服务端
     提交成功，也拿不到新 revision。旧实现在 ok 为真时立刻
     dirty=false / status=idle / 清掉草稿 —— 隐藏页最常见的链路正是
     「普通保存 409 → 再发一个带同样旧 revision 的 beacon」，
     于是即便随后 409/401/500，本机一个恢复点都不剩（ST-02）。
     现在的顺序：先落草稿（带唯一 ID）再发；ok 为真也不清草稿、不认定同步，
     成功与否由下一次 load / save 的结果判定（见 reconcileConfirmedDrafts）。 */
  let draftId = "";
  let body = "";
  try {
    body = JSON.stringify(state.storage);
    draftId = stashPendingSaveDraft("beacon");   // 先落盘，再排队
  } catch (err) {
    console.warn("未保存改动落地失败", err);
  }
  try {
    /* revision 为空时**不能**省掉前置条件：服务端会把「无前置条件」当成放行，
       于是空基线直接盖掉真实数据。空 revision 用标准 HTTP 的「只在不存在时创建」
       （If-None-Match: *），sendBeacon 发不了自定义头，所以带上 expectEmpty=1。 */
    const revision = state.storageRevision
      ? `&revision=${encodeURIComponent(state.storageRevision)}`
      : "&expectEmpty=1";
    const blob = new Blob([body], { type: "application/json; charset=utf-8" });
    /* 实测（真实账号 1MB 左右）：sendBeacon 的 body 上限是 64KB，超了浏览器**直接返回
       false 且根本不发**。旧实现照样调用 → 每次「改完就刷新」都落一条
       beacon_rejected 草稿，而那次保存压根没出去，草稿的 revision 从此永久过期 →
       下次开页必弹「数据冲突」，且用户不点「加载服务器最新数据」就永远弹。
       大包干脆不发：草稿本身就是恢复点，下一次开页按 revision/内容补存
       （见 applyPendingSaveDraft）。 */
    if (blob.size > BEACON_MAX_BYTES) {
      if (draftId) stashPendingSaveDraft(`unload_toobig:${blob.size}b`, { id: draftId });
      return false;
    }
    const ok = navigator.sendBeacon(`/api/user/save?user=${encodeURIComponent(state.user)}${revision}`, blob);
    if (ok) {
      /* 草稿**留着**、dirty 字段**留着**：这份草稿是「已排队未确认」，
         下次开页按内容/revision 确认后再定向清理。 */
      if (draftId) state.pendingBeaconDraftId = draftId;
      return true;
    }
    /* sendBeacon 有 64KB 的 body 上限（超了直接返回 false），页面正在卸载时也可能发不出去。
       旧实现到这里只返回 false 就完了 —— 保存状态写着「未保存」，可页面已经关了，
       用户根本看不到，改动就这么无声丢掉。草稿已经在上面落好了，这里只补上原因。 */
    if (draftId) stashPendingSaveDraft(`beacon_rejected:${blob.size}b`, { id: draftId });
    return false;
  } catch (err) {
    if (draftId) stashPendingSaveDraft("beacon_error:" + (err?.message || ""), { id: draftId });
    return false;
  }
}

/* 保存请求体压缩：整包用户数据线上实测约 1MB（1,006,794 字节），上传侧一直没有压缩，
   国内移动网络下这一趟要好几秒。CompressionStream('gzip') 在 Chrome 80+ / Safari 16.4+ /
   Firefox 113+ 可用，同样的数据能压到约 1/10。
   服务端 UseRequestDecompression 负责还原，端点读到的仍是原始 JSON，
   revision 依旧按原文算 —— 协议语义不变。老浏览器 / 压不动时不发这个头，行为与以前一致。 */
const SAVE_COMPRESS_MIN_BYTES = 32 * 1024;

async function encodeSaveBody(text) {
  if (typeof CompressionStream !== "function") return { body: text, encoding: "" };
  const bytes = new TextEncoder().encode(text);
  if (bytes.byteLength < SAVE_COMPRESS_MIN_BYTES) return { body: text, encoding: "" };
  try {
    const packed = new Uint8Array(await new Response(
      new Blob([bytes]).stream().pipeThrough(new CompressionStream("gzip")),
    ).arrayBuffer());
    /* 压不动（小包 / 已高度随机的数据）就别多发一个头，按原文发。 */
    if (packed.byteLength >= bytes.byteLength) return { body: text, encoding: "" };
    return { body: packed, encoding: "gzip" };
  } catch {
    return { body: text, encoding: "" };
  }
}

/* ===== 增量保存 =====
   线上实测：整包 1,006,956 字节（gzip 106KB），而「答一道题」真正变的只有几百字节
   （真实数据上实测 4 条 op、gzip 167 字节）。整包上传在移动网络下是几百毫秒的等待，
   增量后几乎是瞬时的。
   契约：POST /api/user/patch {base, ops:[{p,v}|{p,d}|{p,app}]}，base 必须等于服务端当前
   revision（否则同样 409）；服务端在同一把用户锁内合并后原子写回，revision 仍按落盘全文算。
   任何一步不成立（没有基线 / 结构可疑 / 服务端不认）就退回整包保存 —— 增量只是快路径，
   绝不能因为算错 diff 而丢数据。 */
const PATCH_MIN_FULL_BYTES = 8 * 1024;   // 整包本身就很小时不值得走增量
const PATCH_MAX_OPS = 2000;
const PATCH_MAX_BYTES = 512 * 1024;
/* 服务端不认增量（老版本 404 / 合并被拒 400）：本次就退回整包，并且以后别再试。 */
let patchUnavailable = false;

function isPlainObject(value) {
  return !!value && typeof value === "object" && !Array.isArray(value);
}

function jsonSameValue(a, b) {
  if (a === b) return true;
  try {
    return JSON.stringify(a) === JSON.stringify(b);
  } catch {
    return false;
  }
}

/* 把 base → cur 的差异收集成 ops。path 用 / 分段，键里出现 / 就没法表达（置 unsafe 退回整包）。 */
function collectPatchOps(base, cur, path, out) {
  if (out.unsafe || out.ops.length > PATCH_MAX_OPS) return;
  if (Array.isArray(base) && Array.isArray(cur)) {
    /* 数组只认「前缀不变 + 尾部追加」这一种增量（history / trainingSessions 都是追加型），
       其余情况整段替换 —— 猜错数组语义是最容易丢数据的地方。 */
    let prefix = true;
    const n = Math.min(base.length, cur.length);
    for (let i = 0; i < n; i++) {
      if (!jsonSameValue(base[i], cur[i])) { prefix = false; break; }
    }
    if (prefix) {
      if (cur.length > base.length) out.ops.push({ p: path, app: cur.slice(base.length) });
      else if (cur.length < base.length) out.ops.push({ p: path, v: cur });
      return;
    }
    out.ops.push({ p: path, v: cur });
    return;
  }
  if (isPlainObject(base) && isPlainObject(cur)) {
    for (const key of Object.keys(cur)) {
      if (key.includes("/")) { out.unsafe = true; return; }
      if (jsonSameValue(base[key], cur[key])) continue;
      const child = `${path}/${key}`;
      if (!(key in base)) out.ops.push({ p: child, v: cur[key] });
      else collectPatchOps(base[key], cur[key], child, out);
    }
    for (const key of Object.keys(base)) {
      if (key.includes("/")) { out.unsafe = true; return; }
      if (!(key in cur)) out.ops.push({ p: `${path}/${key}`, d: 1 });
    }
    return;
  }
  if (!jsonSameValue(base, cur)) out.ops.push({ p: path, v: cur });
}

/* 返回增量请求体文本；返回 "" 表示「这次老老实实发整包」。 */
function buildPatchPayload(payload, fullText) {
  if (patchUnavailable) return "";
  if (!state.storageRevision || !state.syncedBody) return "";
  if (fullText.length < PATCH_MIN_FULL_BYTES) return "";
  let base;
  try {
    base = JSON.parse(state.syncedBody);
  } catch {
    return "";
  }
  if (!isPlainObject(base)) return "";
  const out = { ops: [], unsafe: false };
  try {
    collectPatchOps(base, payload, "", out);
  } catch {
    return "";
  }
  if (out.unsafe || out.ops.length === 0 || out.ops.length > PATCH_MAX_OPS) return "";
  let text;
  try {
    text = JSON.stringify({ base: state.storageRevision, ops: out.ops });
  } catch {
    return "";
  }
  /* 增量不比整包明显小就没意义：整包还有 gzip，而且少走一条服务端合并路径。 */
  if (text.length > PATCH_MAX_BYTES || text.length * 2 > fullText.length) return "";
  return text;
}

/* 发保存请求。patchText 非空且成功就走增量；服务端不认就当场退回整包（同一次保存内重试）。 */
async function postUserSave(fullText, patchText) {
  const usePatch = !!patchText;
  const headers = { "Content-Type": "application/json; charset=utf-8" };
  let url;
  if (usePatch) {
    url = `/api/user/patch?user=${encodeURIComponent(state.user)}`;
    headers["If-Match"] = `"${state.storageRevision}"`;
  } else {
    url = `/api/user/save?user=${encodeURIComponent(state.user)}`;
    /* 前置条件**始终**带：revision 为空时用 If-None-Match: *（「只在服务端还没有数据时创建」），
       不能再省成「无前置条件」—— 服务端在「文件非空但客户端 revision 为空」时按 409 处理，
       这正是那条「读失败 → 空包覆盖真实进度」的路径的兜底。 */
    if (state.storageRevision) headers["If-Match"] = `"${state.storageRevision}"`;
    else headers["If-None-Match"] = "*";
  }
  /* 大包走 gzip（见 encodeSaveBody）：1MB → 约 100KB，服务端透明解压。 */
  const encoded = await encodeSaveBody(usePatch ? patchText : fullText);
  if (encoded.encoding) headers["Content-Encoding"] = encoded.encoding;
  try {
    return await api(url, { method: "POST", headers, body: encoded.body });
  } catch (err) {
    const status = err?.status || 0;
    /* 409 是冲突，绝不能在这里吞掉或改发整包（那会把冲突盖过去）。 */
    if (usePatch && status !== 409 && (status === 400 || status === 404 || status === 405 || status === 501)) {
      patchUnavailable = true;
      return postUserSave(fullText, "");
    }
    throw err;
  }
}

function saveUserData() {
  if (!state.user) return Promise.resolve();
  /* 只读态：直接返回失败，不发请求（调度侧已拦，这里是第二道闸）。 */
  if (state.storageReadFailed) {
    return Promise.reject(new Error("数据读取失败，本次改动不会保存，请刷新重试"));
  }
  const run = async () => {
    setSaveStatus("saving");
    /* 发送时就把这次请求的「上下文」钉死：账号、加载世代、变更代际、payload。
       响应回来时先比对 —— 对不上说明这段时间里 storage 又变过（ST-05）。 */
    const context = {
      user: state.user,
      generation: state.loadGeneration,
      mutation: state.mutationVersion,
      payload: state.storage,
      inherited: state.storageInheritedDraft || null,
    };
    /* 整包文本只序列化一次：既要给 gzip 用，也要给增量 diff 比大小用。 */
    let fullText = "";
    try {
      fullText = JSON.stringify(context.payload);
    } catch (err) {
      throw err;
    }
    const patchText = buildPatchPayload(context.payload, fullText);
    const result = await postUserSave(fullText, patchText).catch((err) => {
      /* 把「这次被拒的是哪份内容」挂到错误上：409 之后要先拿它跟服务端对账，
         内容一致就说明是本机另一处已经写进去了，不该弹冲突框（见 handleUserSaveError）。 */
      if (err && typeof err === "object") {
        err.savePayload = context.payload;
        err.saveDraftId = context.inherited?.id || "";
        /* 用代际判断「这期间 storage 有没有再变过」：payload 是同一个对象引用，
           就地改字段时 === 照样成立，不能拿来判断。 */
        err.saveMutation = context.mutation;
        err.saveGeneration = context.generation;
        err.saveUser = context.user;
      }
      throw err;
    });
    state.storageRevision = result.revision || state.storageRevision;
    /* 服务端现在持有的就是这份内容 → 更新增量基线（发的是整包还是增量都一样）。 */
    state.syncedBody = fullText;
    /* 服务端已经是这份内容了：给「在途的 load 快照是否过期」打个记号（见 loadUserData）。 */
    state.saveSuccessSeq += 1;
    /* 顺手把本机缓存推到这一版（攒 3 秒再写，别每答一题就写 1MB）。 */
    scheduleUserDataCacheWrite();
    /* 这次请求确实被服务端收下了 —— 它发出去的那份内容（= 某条草稿的内容）
       现在可以按 ID 定向删；导入去重也认这个凭据。 */
    markDraftConfirmed([context.inherited?.id], [context.payload]);
    if (context.inherited?.id) state.storageInheritedDraft = null;
    /* ⚠️ 「请求发出之后」storage 又变过（mutationVersion 前进）或换了账号/重载过
       （loadGeneration 前进）→ **不能**把这个响应当成本次内容的同步结果：
       旧实现无条件 dirty=false / status=idle，于是 v2 被标成已同步，
       此刻关页 beforeunload 清掉 450ms 定时器、beacon 又因 dirty=false 直接 return
       → v2 没有任何请求也没有草稿（ST-05）。现在保持 dirty 并排队补存。 */
    const stale = context.generation !== state.loadGeneration
      || context.user !== state.user
      || context.mutation !== state.mutationVersion;
    if (stale) {
      state.storageDirty = true;
      setSaveStatus("dirty");
      scheduleSave();
      return result;
    }
    state.storageDirty = false;
    setSaveStatus("idle");
    /* 本页唯一一份草稿对应的内容刚刚被服务端收下 → 定向删掉它（compare-and-delete）。
       别的标签页/别的账号的草稿一律不动：它们可能这期间又写过新内容。 */
    if (context.inherited?.id) removeDraftById(context.inherited.id);
    /* 草稿被定向删掉了，入口要跟着消失（否则「未补存 1 份」会挂在那里骗人）。 */
    refreshPendingConflict();
    return result;
  };
  state.saveChain = (state.saveChain || Promise.resolve()).catch(() => {}).then(run);
  return state.saveChain;
}

/* 把一份 storage 序列化成「可逐字节比对」的字符串：键序由 normalizeStorage 钉死。
   profile.lastLoginAt 每次开页都会被改成 nowText()，跟「内容有没有同步」无关，
   比对前抹掉 —— 否则永远判成不一致，等于没比。 */
function comparableStorageJson(data) {
  let json = "";
  try {
    json = JSON.stringify(normalizeStorage(data || {}));
  } catch (_err) {
    return "";
  }
  return json.replace(/"lastLoginAt":("[^"]*"|null)/g, '"lastLoginAt":""');
}

/* 409 之后先跟服务端对账，再决定要不要打扰用户。
   服务端现在那份内容如果**就是**本页刚想存的那份，说明这次 409 来自本机自己：
   上一页在卸载前发出去的那次保存（或 beacon）已经把同一份内容写进去了。
   这不是「别的设备改过」，静默对齐即可 —— 旧实现到这里直接弹冲突框，
   于是「改完立刻刷新」几乎必弹一次。 */
async function adoptServerIfSameAs(payload) {
  if (!state.user || !payload || state.storageReadFailed) return false;
  let res = null;
  try {
    res = await api(`/api/user/load?user=${encodeURIComponent(state.user)}`);
  } catch (_err) {
    return false;
  }
  if (!res || res.ok === false) return false;
  const serverJson = comparableStorageJson(res.data);
  if (!serverJson || serverJson !== comparableStorageJson(payload)) return false;
  state.storageRevision = res.revision || state.storageRevision;
  return true;
}

async function handleUserSaveError(err) {
  setSaveStatus("error");
  state.storageDirty = true;
  const now = Date.now();
  if (err?.status === 409 || err?.code === "user_data_conflict") {
    if (await adoptServerIfSameAs(err?.savePayload)) {
      /* 服务器上已经是这份内容了。storage 在这期间没再变过 → 就当这次保存成功；
         变过 → 保留 dirty，用刚对上的 revision 再存一次。 */
      const stale = err.saveMutation !== state.mutationVersion
        || err.saveGeneration !== state.loadGeneration
        || err.saveUser !== state.user;
      if (!stale) {
        state.storageDirty = false;
        setSaveStatus("idle");
      } else {
        setSaveStatus("dirty");
        scheduleSave();
      }
      reconcileConfirmedDrafts();
      if (err?.saveDraftId) removeDraftById(err.saveDraftId);
      refreshPendingConflict();
      return;
    }
    /* 普通 409 的本地改动此前只活在内存里（旧实现），关页/刷新就没了。
       openConflictDialog 会在这一支顺手把它落成一条**独立持久草稿**（ST-04）。 */
    openConflictDialog(err, null, { reason: "save_conflict" });
    return;
  }
  if (now - Number(state.saveErrorShownAt || 0) < 5000) return;
  state.saveErrorShownAt = now;
  toast(`保存失败：${err?.message || "请检查网络和磁盘空间"}`);
}

function summarizeStorage(data = {}) {
  const courses = Object.values(data.courses || {});
  const done = courses.reduce((sum, course) => sum + Object.keys(course?.done || {}).length, 0);
  const wrong = Object.keys(data.wrong || {}).length;
  const favorite = Object.keys(data.favorite || {}).length;
  const notes = Object.keys(data.notes || {}).length;
  const examDrafts = Object.keys(data.examDrafts || {}).length + (data.examDraft ? 1 : 0);
  return { done, wrong, favorite, notes, examDrafts };
}

function openConflictDialog(err, snapshot = null, options = {}) {
  if (state.conflictOpen) {
    /* 已经有一次没处理完的冲突。**不能**就这么 return：这次的新本地数据（普通 409 时
       它就只在内存里）会连草稿都没落下。至少把它落成独立草稿，用户不会两头都丢。 */
    if (!options.draftId) stashPendingSaveDraft(options.reason || "conflict_while_open");
    return;
  }
  state.conflictOpen = true;
  /* snapshot 给「开页时发现上次没存上的草稿」用：那会儿 state.storage 是服务器那份，
     本地草稿另有其人（见 applyPendingSaveDraft）。 */
  const source = snapshot || {
    user: state.user,
    revision: state.storageRevision || "",
    storage: JSON.parse(JSON.stringify(state.storage || {})),
    at: nowText(),
  };
  state.conflictLocalSnapshot = source;
  /* 「保留本地」要能落到持久草稿上（ST-04），所以这里先记住：
     这份本地数据该以哪条草稿的形式留在本机、以及它的 baseRevision。
     - 有 draftId（开页时恢复的草稿/在途保存的草稿）→ 就直接用那条草稿的 ID，不另起一份；
     - 没有（普通 409，本地改动只在内存里）→ 立刻现写一条草稿，别等着用户点「保留」时才发现丢。 */
  const draftId = options.draftId
    || source.draftId
    || stashPendingSaveDraft(options.reason || "conflict", {
      user: source.user,
      revision: source.revision,
      storage: source.storage,
    })
    || "";
  state.conflictDraftId = draftId;
  state.conflictDraft = { baseRevision: source.revision || "", source: options.reason || "conflict" };
  state.conflictGeneration = state.mutationVersion;
  if (options.count) state.conflictDraftCount = options.count;
  if (options.drafts) state.conflictDrafts = options.drafts;
  const modal = $("conflictModal");
  if (!modal) {
    toast("数据已在其他页面更新，请刷新后继续");
    return;
  }
  const local = summarizeStorage(state.conflictLocalSnapshot.storage);
  $("conflictMessage").textContent = err?.message || "当前账号的数据已在其他页面更新。为避免覆盖，请选择如何处理本地未保存改动。";
  $("conflictSummary").innerHTML = `
    <div class="conflict-metric"><b>${local.done}</b><span>本页已做题标记</span></div>
    <div class="conflict-metric"><b>${local.wrong}</b><span>错题</span></div>
    <div class="conflict-metric"><b>${local.favorite}</b><span>收藏</span></div>
    <div class="conflict-metric"><b>${local.notes}</b><span>笔记</span></div>
    <div class="conflict-metric"><b>${local.examDrafts}</b><span>考试草稿</span></div>
  `;
  modal.classList.remove("hidden");
  setSaveStatus("error");
}

function closeConflictDialog() {
  state.conflictOpen = false;
  /* ⚠️ 必须连冲突上下文一起清：`conflictDraftId` 是「这次弹窗对应哪条草稿」的口径，
     留着上一次的 ID 会让下一次「保留本地」把别的账号/别的标签页的草稿当成自己的
     （实测：alpha 的草稿被 inktop 的 keepLocalConflictDraft 接管并改写成 alpha 的内容）。 */
  state.conflictLocalSnapshot = null;
  state.conflictDraft = null;
  state.conflictDraftId = "";
  state.conflictDrafts = null;
  $("conflictModal")?.classList.add("hidden");
  /* 关掉框不等于问题解决：草稿还在就重新挂上「未补存」入口（见 openPendingConflict）。 */
  refreshPendingConflict();
}

/* 导出/恢复入口共用一个「当前这份本地草稿是哪一条」的口径（ST-03/04 的共同地基）。 */
function currentConflictDraftRecords() {
  if (Array.isArray(state.conflictDrafts) && state.conflictDrafts.length) {
    const live = state.conflictDrafts.filter((draft) => readDraftRecord(draft.id));
    if (live.length) return live;
    return state.conflictDrafts;
  }
  const one = state.conflictDraftId ? readDraftRecord(state.conflictDraftId) : null;
  if (one) return [one];
  return [];
}

function exportConflictLocalDraft() {
  const snapshot = state.conflictLocalSnapshot;
  if (!snapshot) {
    toast("没有可导出的本地草稿");
    return;
  }
  /* 把本机**所有**当前账号的未保存草稿一起导出（不只是弹窗里这一条）：
     「保留本地」类的操作要让用户拿得到那份数据，导出是它唯一的出口。 */
  const drafts = currentConflictDraftRecords().length
    ? currentConflictDraftRecords()
    : readPendingSaveDrafts();
  const payload = {
    type: "yunxi-local-draft",
    exportedAt: nowText(),
    user: snapshot.user,
    baseRevision: snapshot.revision,
    storage: snapshot.storage,
    drafts: drafts.map((draft) => ({
      id: draft.id,
      tab: draft.tab,
      at: draft.at,
      reason: draft.reason,
      baseRevision: draft.revision,
      storage: draft.storage,
    })),
  };
  const blob = new Blob([JSON.stringify(payload, null, 2)], { type: "application/json;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  const stamp = new Date().toISOString().replace(/[:.]/g, "-");
  a.href = url;
  a.download = `yunxi-local-draft-${snapshot.user || "user"}-${stamp}.json`;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  toast("本地草稿已导出");
}

async function reloadServerUserDataFromConflict() {
  if (!state.user) return;
  const snapshot = state.conflictLocalSnapshot;
  /* ⚠️ 必须在 loadUserData 之前把草稿清单取出来：loadUserData 会重置
     state.conflictDrafts / conflictDraftId（那两个字段描述的是「旧基线上的冲突」），
     取晚了就是空清单 —— 于是「加载服务器最新数据」从来没删掉过草稿，
     这草稿下次开页又被 applyPendingSaveDraft 翻出来，表现为「怎么点都清不掉」。 */
  const records = currentConflictDraftRecords();
  try {
    await loadUserData(state.user);
    state.storageDirty = false;
    setSaveStatus("idle");
    /* 用户选了「以服务器为准」→ 这次是**明确放弃**，本页那条草稿可以定向删。
       别的账号、别的标签页的草稿不碰。 */
    for (const record of records) removeDraftById(record.id);
    closeConflictDialog();
    state.conflictDrafts = null;
    state.conflictDraftId = "";
    if (isAdmin()) {
      await renderAdminDashboard();
    } else if (state.currentCourse) {
      await loadQuestions();
      if (state.currentIndex >= 0) await loadCurrentQuestion().catch((err) => toast(err.message));
      else renderAll();
    } else {
      await restoreLastCourse();
    }
    toast(snapshot ? "已加载服务器最新数据，本地草稿可随时导出备份" : "已加载服务器最新数据");
  } catch (err) {
    toast(err?.message || "加载服务器数据失败");
  }
}

/* 「继续编辑本地」：snapshot 必须**成为当前编辑状态**，而不是只躺在内存里。
   旧实现只 close + dirty=true + 删草稿 —— state.storage 还是服务器那份，
   弹窗一关（甚至刷个新）「保留的本地」就没了，之后保存保存的也是服务器那份（ST-04）。
   两条硬性要求：
     ① 明确保留的 snapshot 写成**独立持久草稿**（留着，不删）；
     ② 用 snapshot 当编辑状态，并**保留原 baseRevision** —— 不能悄悄换成服务器 revision
        去覆盖；revision 对不上时保存自然走 409 → 再弹一次冲突，这是有意的。 */
function keepLocalConflictDraft() {
  /* ⚠️ 先把要用的东西**全部**取出来再关弹窗：closeConflictDialog 会把
     conflictLocalSnapshot 一并清掉（那是必须的，见那里的注释），
     先关再读就会读到「没有 snapshot」→ 回落到 state.storage（= 服务器那份），
     于是「保留本地」实际保存的是服务器数据（这正是 ST-04 最实质的那个 bug）。 */
  const draftId = state.conflictDraftId;
  const snapshot = state.conflictLocalSnapshot;
  const baseRevision = state.conflictDraft?.baseRevision ?? (snapshot?.revision || "");
  const conflictGeneration = state.conflictGeneration;
  closeConflictDialog();
  /* 保留 = 把 snapshot 落成本页当前编辑状态。用户点完之后紧接着的改动（mutationVersion 前进）
     说明这份 snapshot 已经被更新了，那就不要再覆盖回去 —— 只把草稿留住。 */
  const snapshotIsStale = conflictGeneration !== state.mutationVersion;
  const keptStorage = snapshot ? JSON.parse(JSON.stringify(snapshot.storage)) : null;
  const keptAt = snapshot?.at || "";
  if (!snapshotIsStale && keptStorage) {
    state.storage = keptStorage;
    state.storageRevision = baseRevision;
    state.storageInheritedDraft = draftId ? { id: draftId, at: keptAt, user: state.user } : null;
    /* 草稿正文必须是**被保留的那份本地数据**，不是服务器那份。
       普通 409 时 openConflictDialog 已经把 state.storage（本地）写进草稿了，
       但「开页恢复草稿 → 冲突 → 保留」这条路上草稿正文仍是原始草稿内容，
       这里统一重写一遍，保证「保留的就是草稿里存的那份」。 */
    if (draftId) {
      stashPendingSaveDraft(state.conflictDraft?.source || "kept_local", {
        id: draftId, revision: baseRevision, storage: keptStorage,
      });
    }
  }
  state.storageDirty = true;
  setSaveStatus("error");
  /* 草稿**留着**（就是上面那条 draftId）——它是刷新后唯一能把本地改动接回来的东西。
     ⚠️ 这里原来还会 scheduleDeferredConflictSave()：1.2 秒后再写一条
     `conflict_retry` 草稿。那条草稿正文是整包（真实账号 1MB），既把 localStorage
     配额吃掉一半，又因为排在索引最前面，下一次开页必然又被当成「最新一份」再处理一遍。
     它的 revision 和 draftId 那条完全一样，留着没有任何额外信息，已删除。 */
  toast("已保留本地改动，暂不覆盖服务器。刷新后仍可恢复，建议先导出草稿");
}

/* 死代码清理：这里原来有一个 `switchUser(name)`（189 行前的位置）。
   它没有任何调用者，而且不调 `/api/auth/logout` —— 将来谁把它接上，服务端会话就留在那里，
   换个人用同一台机器还是上一个账号的身份。真需要「切账号」就直接走 logout()。 */

async function loadCourses() {
  $("courseList").innerHTML = `<div class="muted">正在读取科目...</div>`;
  const q = encodeURIComponent($("courseSearch").value.trim());
  state.courses = await api(`/api/courses?available=1&q=${q}`);
  renderCourses();
}

async function loadAdminCourses() {
  state.adminCourses = await api("/api/courses");
}

async function restoreLastCourse() {
  const lastCourseId = Number(state.storage.profile.lastCourseId || 0);
  const course = state.courses.find((item) => item.id === lastCourseId) || state.courses[0];
  setCoursePicker(false);
  if (course) await selectCourse(course, { restoreChapter: true });
}

function setCoursePicker(open) {
  state.pickerOpen = !!open;
  document.body.classList.toggle("picker-open", state.pickerOpen);
  if (state.pickerOpen) setMobileTools(false);
  // 科目名本身就是开关（手机端在顶栏、桌面端在内容区），把状态同步给读屏
  for (const id of ["navCourseTitle", "courseTitle"]) {
    const el = $(id);
    if (el) el.setAttribute("aria-expanded", String(state.pickerOpen));
  }
  updatePickerHint();
}

/* 点科目名 = 开关科目面板。
   管理看板下没有科目可选（`body.admin-view .course-pane` 是 display:none），忽略。 */
function toggleCoursePicker() {
  if (document.body.classList.contains("admin-view")) return;
  setCoursePicker(!state.pickerOpen);
}

/* 「进题库时要不要自动弹面板」（S-35）。
   桌面端维持原样：面板是并排的一栏，不挡题，开着反而方便。
   手机端只在**还没选科目**时弹（那时必须选）；已经有科目就不拦人 ——
   要换科目点顶栏的科目名（toggleCoursePicker），那是明确的意图。
   不用「本次会话弹过一次」这种一次性标志：它会让「第一次其实没选科目」
   和「第二次又弹一遍」同时发生，规则越绕越难解释。 */
function shouldAutoOpenCoursePicker() {
  if (!isPhoneViewport()) return true;
  return !state.currentCourse;
}

function updatePickerHint() {
  const btn = $("pickerDoneBtn");
  if (!btn) return;
  const selected = state.currentChapter ? `已选：${state.currentChapter.name}` : state.currentCourse ? "已选：全部章节" : "请选择科目和章节";
  btn.textContent = state.currentCourse ? "进入做题" : "选择题库后进入做题";
  btn.dataset.hint = selected;
  btn.disabled = !state.currentCourse;
}

function isAdmin() {
  return (state.user || "").toLowerCase() === "admin";
}

function renderCourses() {
  $("courseList").innerHTML = "";
  if (!state.courses.length) {
    $("courseList").innerHTML = `<div class="muted">没有找到科目</div>`;
    return;
  }
  for (const course of state.courses) {
    const stats = getCourseStats(course.id);
    const btn = document.createElement("button");
    const stale = isCourseStale(course.changedAt);
    btn.className = "course-item" + (state.currentCourse?.id === course.id ? " active" : "") + (stale ? " stale" : "");
    btn.innerHTML = `
      <span>${escapeHtml(course.name)}</span>
      <small>${escapeHtml(course.category || "")} ${escapeHtml(course.subcategory || "")} · ${course.questionCount}题 · 已做${stats.done} · 更新${escapeHtml(formatRelativeTime(course.changedAt))}</small>
    `;
    btn.onclick = () => selectCourse(course);
    $("courseList").appendChild(btn);
  }
  updatePickerHint();
}

async function selectCourse(course, options = {}) {
  /* 末尾那次 scheduleSave 只在**真的有改动**时才做。
     旧实现无条件存一次，而 selectCourse 在每次开页都会被 restoreLastCourse 调到 ——
     于是「单纯刷新一下」也会 POST 整包 → 服务器 revision 前进 →
     之前打开的标签页保存时 409（实测改前 reload 一次 = 1 个 POST、revision 前进）。
     注意最后一次改动是 profile.lastChapterId 的写回（见下面 restoreChapterId 分支），
     所以快照必须取在函数开头。 */
  const storageBefore = JSON.stringify(state.storage);
  const transitionId = (state.modeTransitionId || 0) + 1;
  state.modeTransitionId = transitionId;
  stopExamTimer();
  state.exam = null;
  if (state.currentCourse?.id !== course.id) {
    state.analysisQuestions = [];
    state.analysisCourseId = 0;
    state.examExpandedChapters = new Set();
    state.examSelectedChapters = null;
    state.examExcludedChapters = new Set();
    state.analysisExpandedChapters = new Set();
  }
  const restoreChapterId = options.restoreChapter ? Number(state.storage.profile.lastChapterId || 0) : 0;
  state.currentCourse = course;
  state.questionRequestId = (state.questionRequestId || 0) + 1;
  state.typeRequestId = (state.typeRequestId || 0) + 1;
  state.modeTransitionId = transitionId;
  state.submitToken = (state.submitToken || 0) + 1;
  state.submitting = false;
  state.answerCardUserTouched = false;
  /* 换科目：上一轮的训练会话属于别的科目，指针必须让位。
     ⑤ 记下「上一轮属于哪个科目」——空态要用它说清原因（旧实现是让用户
     「换章节、题型或清空搜索条件」，跟刚切科目毫无关系）。
     题单本身留在会话列表里，切回去还能从看板「继续」。 */
  const activeSession = activeTrainingSession();
  if (activeSession && Number(activeSession.courseId || 0) !== Number(course.id)) {
    state.storage.activeTrainingSessionId = "";
    state.smartPracticeFreshStart = false;
    /* 只在「人还停在智能练习里」时留提示，否则会污染下一次进入的空态文案。 */
    if (state.mode === "smart") {
      state.smartLeftCourseName = activeSession.courseName || state.currentCourse?.name || "";
      state.smartLeftCourseId = Number(activeSession.courseId || 0);
    }
  }
  state.currentChapter = null;
  state.currentIndex = -1;
  state.answers = { ...userCourseStore(course.id).answers };
  state.submitted = false;
  state.answerVisible = false;
  state.storage.profile.lastCourseId = course.id;
  state.storage.profile.lastCourseName = course.name;
  state.storage.profile.lastChapterId = 0;
  setCourseTitle(course.name);
  const courseLabel = [course.category, course.subcategory].filter(Boolean).join(" ");
  setText("courseMeta", `${courseLabel ? `${courseLabel} · ` : ""}${course.questionCount}题 · 更新 ${formatRelativeTime(course.changedAt)}`);
  renderCourses();
  /* 章节读取失败**不再**中断整条流程（旧实现会静默卡在「正在读取章节...」）：
     面板已由 loadChapters 渲染成错误态 + 重试入口，这里补一条 toast 并把后面的
     题型/题单继续做完 —— 章节挂了也还能做题。 */
  await loadChapters().catch((err) => toast("章节读取失败：" + err.message));
  if (transitionId !== state.modeTransitionId) return;
  if (restoreChapterId) {
    const chapter = state.chapters.find((item) => item.id === restoreChapterId);
    if (chapter) {
      state.currentChapter = chapter;
      /* 恢复成功后必须把章节**写回存档**：上面为了「换科目不继承上一科目的章节」
         刚把 lastChapterId 清成 0，这里不写回的话，本次 boot 结尾的 scheduleSave
         会把 0 同步到服务器 —— 表现就是「刷一次还有章节，再刷一次只剩全部章节」，
         上次停的那道题也不在新题单里（findStartIndex 找不到 → 回第 1 题）。 */
      state.storage.profile.lastChapterId = chapter.id;
    }
    renderChapters();
  }
  if (!await loadTypes()) return;
  if (transitionId !== state.modeTransitionId) return;
  await loadQuestions().catch((err) => toast(err.message));
  if (transitionId !== state.modeTransitionId) return;
  if (!isAdmin() && options.closePicker) setCoursePicker(false);
  updatePickerHint();
  if (JSON.stringify(state.storage) !== storageBefore) scheduleSave();
  /* 换科目时如果人还在「模拟考场」，必须重新进一次考场首页。
     旧实现只把题型/题单换成新科目的，页面就从组卷页退回**普通练习题**
     （「第 1 / 80 题」），而导航仍高亮「模拟考场」、工具栏仍挂着「提交答卷」——
     学员点一下就是一份 0/0 分的假成绩单（见 submitPaper 的守卫）。 */
  if (state.mode === "exam") await enterExamHome(transitionId);
}

/* 最近一次**成功**读到章节的科目 id。切科目失败时用它判断
   「当前章节是不是别的科目的」，别把上一个科目的 chapter 留在界面上。 */
let chaptersCourseId = 0;
/* 非空 = 章节读取失败，面板要显示错误态 + 重试入口。
   用标志位而不是「只在 catch 里画一次」：失败后别的流程还会调 renderChapters()
   （例如 backfillQuestionChapters 的回调），空树会被画成「全部章节 (0/0)」，
   错误态和重试按钮就被冲掉了。 */
let chaptersLoadError = "";

async function loadChapters() {
  const courseId = state.currentCourse?.id;
  if (courseId == null) return;
  const previousCourseId = chaptersCourseId;
  $("chapterList").innerHTML = `<div class="muted">正在读取章节...</div>`;
  let chapters;
  try {
    chapters = await api(`/api/chapters?courseId=${courseId}`);
  } catch (err) {
    /* 旧实现没有 try/catch：失败时 selectCourse 的后半段（题型/题单/提示）全部不执行，
       面板永久停在「正在读取章节...」，同时 state.chapters / chapterTreeRoots /
       chapterTreeIndex / chapterDoneIndex 还是**上一个科目的**数据 ——
       界面既没有错误提示也没有重试入口，只能刷新页面。 */
    if (Number(state.currentCourse?.id) !== Number(courseId)) return;
    state.chapters = [];
    state.chapterTreeRoots = [];
    state.chapterTreeIndex = new Map();
    state.chapterDoneIndex = new Map();
    state.expandedChapters = new Set();
    state.chapterAutoExpanded = false;
    chapterPathCache = new Map();
    chapterPathCacheRoots = null;
    if (Number(previousCourseId) !== Number(courseId)) {
      state.currentChapter = null;
      state.storage.profile.lastChapterId = 0;
    }
    chaptersCourseId = 0;
    chaptersLoadError = err?.message || "未知错误";
    renderChapterLoadError(chaptersLoadError);
    throw err;
  }
  if (Number(state.currentCourse?.id) !== Number(courseId)) return;
  chaptersCourseId = Number(courseId);
  chaptersLoadError = "";
  state.chapters = chapters;
  state.expandedChapters = new Set();
  state.chapterAutoExpanded = false;
  /* 题库版本（course.changedAt）和本地映射缓存对不上时重拉 —— 题库更新后
     学员端的章节映射不刷新就是这个原因。不 await：别挡住章节树渲染。 */
  backfillQuestionChapters(courseId).catch(() => { /* 补不上就算了，翻题也会慢慢攒 */ });
  renderChapters();
}

/* 章节读取失败时的面板：一句人话 + 重试入口（复用 .chapter-item 的列表样式）。 */
function renderChapterLoadError(message) {
  const box = $("chapterList");
  if (!box) return;
  box.innerHTML = "";
  const hint = document.createElement("div");
  hint.className = "muted";
  hint.textContent = `章节读取失败：${message}`;
  const retry = document.createElement("button");
  retry.type = "button";
  retry.className = "chapter-item";
  retry.innerHTML = `<span>重新读取章节</span><small>(点这里重试)</small>`;
  retry.onclick = () => loadChapters().catch((err) => toast("章节读取失败：" + err.message));
  box.appendChild(hint);
  box.appendChild(retry);
  updatePickerHint();
}

/* 题库被整体替换（上传 / 拉取更新）后，章节相关状态必须一起清干净。
   旧实现只清 chapters/types/questions，chapterTreeRoots / chapterTreeIndex /
   chapterDoneIndex / currentChapter / profile.lastChapterId 和章节面板 DOM 全部残留：
   树是旧库、当前章节指向旧 id、题单清 0 之后点「下一题」零请求，界面等于死掉。 */
function resetChapterState(options = {}) {
  state.chapters = [];
  state.chapterTreeRoots = [];
  state.chapterTreeIndex = new Map();
  state.chapterDoneIndex = new Map();
  state.expandedChapters = new Set();
  state.chapterAutoExpanded = false;
  state.currentChapter = null;
  state.currentIndex = -1;
  state.storage.profile.lastChapterId = 0;
  chaptersCourseId = 0;
  chaptersLoadError = "";
  chapterPathCache = new Map();
  chapterPathCacheRoots = null;
  if (options.placeholder !== false) {
    const box = $("chapterList");
    if (box) box.innerHTML = `<div class="muted">正在读取章节...</div>`;
  }
}

/* 整体替换题库后刷新章节树：当前科目还在的话重新读一次（树/统计/面板 DOM 全部重建）。 */
async function reloadChaptersAfterBankChange() {
  if (!state.currentCourse) {
    const box = $("chapterList");
    if (box) box.innerHTML = `<div class="muted">请选择科目</div>`;
    return;
  }
  await loadChapters().catch((err) => toast("章节读取失败：" + err.message));
}

function renderChapters() {
  /* 上次读取失败、而且树是空的 → 保持错误态，别画成「全部章节 (0/0)」。
     （失败后 backfillQuestionChapters 等流程还会回调 renderChapters） */
  if (chaptersLoadError && !state.chapters.length) {
    renderChapterLoadError(chaptersLoadError);
    return;
  }
  $("chapterList").innerHTML = "";
  const courseStore = userCourseStore();

  /* 先建树再算「全部章节」的总数 —— state.chapters 是**扁平**的，
     每个节点上没有 children，直接拿它调 chapterTotalCount 会炸
     （chapterTotalCount 要递归 children）。 */
  const roots = buildChapterTree(state.chapters);
  state.chapterTreeRoots = roots;
  state.chapterDoneIndex = buildChapterDoneIndex(roots, courseStore);

  const all = document.createElement("button");
  all.className = "chapter-item" + (!state.currentChapter ? " active" : "");
  const allTotal = roots.reduce((sum, chapter) => sum + chapterTotalCount(chapter), 0);
  /* 分子和分母必须同口径：分母是「章节树上归集的题量」，分子就只能数
     **能映射到树上的**已做题（buildChapterDoneIndex 的结果）。
     旧实现用 Object.keys(done).length（所有已做题），映射不到的题只进「全部章节」的分子，
     于是「全部章节」和各子章节的合计对不上（长期表现为问题 4）。 */
  const allDone = roots.reduce((sum, chapter) => sum + (state.chapterDoneIndex.get(Number(chapter.id)) || 0), 0);
  all.innerHTML = `<span>全部章节</span><small>(${allDone}/${allTotal})</small>`;
  all.onclick = () => selectChapter(null);
  $("chapterList").appendChild(all);

  state.chapterTreeIndex = new Map();
  const indexTree = (items) => items.forEach((item) => {
    state.chapterTreeIndex.set(Number(item.id), item);
    indexTree(item.children || []);
  });
  indexTree(roots);
  ensureChapterExpansion(roots);
  renderChapterNodes(roots, $("chapterList"));
  updatePickerHint();
}

/* 答完题后刷新章节树的「已做/总数」。
   旧实现完全靠 /api/question-chapters 的异步响应**顺带**触发一次 renderChapters，
   而那个回填每门课只跑一次：之后答题再也不会重渲染，面板永远停在旧数字
   （实测同一秒内 done=6，界面还写「全部章节(0/9155)」）。
   节流是为了别每选一个选项就重建整棵树的 DOM。 */
let chapterRefreshTimer = null;
function scheduleChapterRefresh(delay = 400) {
  if (chapterRefreshTimer) return;
  chapterRefreshTimer = setTimeout(() => {
    chapterRefreshTimer = null;
    if (!state.chapters.length || !$("chapterList")) return;
    renderChapters();
  }, delay);
}

function buildChapterTree(chapters) {
  const nodes = chapters.map((chapter) => ({ ...chapter, children: [] }));
  const roots = [];
  for (const node of nodes) {
    const parent = nodes
      .filter((item) =>
        item !== node &&
        item.type === node.type &&
        item.grade === node.grade - 1 &&
        node.code &&
        item.code &&
        node.code.startsWith(item.code) &&
        item.code.length < node.code.length
      )
      .sort((a, b) => b.code.length - a.code.length)[0];
    if (parent) parent.children.push(node);
    else roots.push(node);
  }
  const sortTree = (items) => {
    items.sort((a, b) => String(a.code || "").localeCompare(String(b.code || ""), "zh-CN"));
    items.forEach((item) => sortTree(item.children));
  };
  sortTree(roots);
  return roots;
}

function ensureChapterExpansion(roots) {
  /* 恢复上次章节时，把它所在的**整条祖先链**展开。
     旧实现只自动展开 roots[0]：切回上次的三级章（属于第 2 个根）时，
     那一行根本没进 DOM（不是滚出视口），而 pickerHint 还写着「已选：第一节 概述」——
     用户看到的是「说选了却一行都不高亮」。 */
  const currentId = Number(state.currentChapter?.id || 0);
  if (currentId) {
    const path = findChapterPath(roots, currentId);
    if (path.length > 1) {
      path.slice(0, -1).forEach((node) => state.expandedChapters.add(Number(node.id)));
      state.chapterAutoExpanded = true;
      return;
    }
  }
  if (!state.chapterAutoExpanded && !state.expandedChapters.size && roots[0]) {
    state.expandedChapters.add(roots[0].id);
    state.chapterAutoExpanded = true;
  }
}

function findChapterPath(nodes, id, path = []) {
  for (const node of nodes) {
    const nextPath = path.concat(node);
    if (node.id === id) return nextPath;
    const found = findChapterPath(node.children, id, nextPath);
    if (found.length) return found;
  }
  return [];
}

function chapterTotalCount(chapter) {
  return Number(chapter.questionCount || 0) + chapter.children.reduce((sum, child) => sum + chapterTotalCount(child), 0);
}

/* ── 章节树的「已做」统计：qid → chapterId 映射（**纯本地缓存**）────────
   courseStore.done 只记了题目 id，不知道它属于哪一章，所以需要一份
   qid → chapterId 的映射来把「已做」归到章上。

   ⚠️ 这份映射以前存在 state.storage.courses[cid].questionChapter 里、跟着用户数据
   同步。2026-10 改成纯本地缓存（localStorage: yunxi-qcmap-<courseId>）。为什么：
     ① 它是 /api/question-chapters 的**只读派生物**，却占满了同步包 ——
        实测空账号只碰 2 门课，state.storage 就有 325,833 B，其中 99.8% 是它
        （course20 163,449 B/9155 条、course21 161,511 B/9035 条）；
     ② 同步包一大，sendBeacon 的 64KB body 上限永远顶穿（实测 325,877 B → 返回 false），
        隐藏页兜底保存**永远失败**；
     ③ 每次开页/翻题都回写整包 → 服务器 revision 前进 → 开第二个标签页必 409。
   缓存与题库版本挂钩（bankChangedAt ← /api/courses 的 changedAt）：
   版本不一致就重拉，顺手修掉「题库更新后学员端章节映射不刷新」。 */
const QC_MAP_VERSION = 1;
const QC_MAP_PREFIX = "yunxi-qcmap-";

function questionChapterCacheKey(courseId) {
  return `${QC_MAP_PREFIX}${Number(courseId || 0)}`;
}

/* 进程内再存一份：章节树每次重绘都要读这个映射，160KB 的 JSON 不能反复 parse。 */
function questionChapterCache(courseId, options = {}) {
  const id = Number(courseId || 0);
  state.qcMaps ||= new Map();
  if (!id) return { version: QC_MAP_VERSION, bankChangedAt: "", filled: false, map: {} };
  if (!options.reload && state.qcMaps.has(id)) return state.qcMaps.get(id);
  let record = null;
  try {
    const raw = localStorage.getItem(questionChapterCacheKey(id));
    const parsed = raw ? JSON.parse(raw) : null;
    if (parsed && typeof parsed === "object" && parsed.map && typeof parsed.map === "object") {
      record = {
        version: Number(parsed.version || 0),
        bankChangedAt: String(parsed.bankChangedAt || ""),
        filled: !!parsed.filled,
        map: parsed.map,
      };
    }
  } catch (err) {
    console.warn("章节映射缓存读取失败，按空表处理", err);
  }
  record ||= { version: QC_MAP_VERSION, bankChangedAt: "", filled: false, map: {} };
  state.qcMaps.set(id, record);
  return record;
}

function writeQuestionChapterCache(courseId, record) {
  const id = Number(courseId || 0);
  if (!id || !record) return;
  record.version = QC_MAP_VERSION;
  state.qcMaps ||= new Map();
  state.qcMaps.set(id, record);
  try {
    localStorage.setItem(questionChapterCacheKey(id), JSON.stringify({
      version: record.version,
      bankChangedAt: record.bankChangedAt || "",
      /* filled 必须跟着缓存走、不能留在同步包里：留在服务器上的话，
         换一台设备打开时「以为自己已经补过映射了」而本地缓存是空的，
         章节树的「已做」就永远是 0。 */
      filled: !!record.filled,
      map: record.map,
    }));
  } catch (err) {
    /* 配额满 / 隐私模式：退回内存缓存，本次会话照用，只是下次开页要重拉 */
    console.warn("章节映射缓存写入失败", err);
  }
}

function courseBankChangedAt(courseId) {
  const id = Number(courseId || 0);
  const course = (state.courses || []).find((item) => Number(item.id) === id);
  return String(course?.changedAt || "");
}

/* 题库版本变了 → 缓存作废。
   任一侧拿不到版本号时**不**判过期：不能因为「暂时读不到」就把用户的映射当废纸重拉。 */
function questionChapterCacheStale(courseId) {
  const cache = questionChapterCache(courseId);
  const current = courseBankChangedAt(courseId);
  if (!cache.bankChangedAt || !current) return false;
  return cache.bankChangedAt !== current;
}

/* 列表接口每道题都带 chapterId，顺手记进本地缓存。
   只在真有增删时写 localStorage；**不再 scheduleSave()** —— 映射不进同步包了，
   所以「打开页面就回写整包」这条链在这里断掉。 */
function rememberQuestionChapters(items, courseId = state.currentCourse?.id) {
  if (!Array.isArray(items) || !items.length) return;
  const id = Number(courseId || 0);
  if (!id) return;
  const cache = questionChapterCache(id);
  let changed = false;
  for (const item of items) {
    const qid = Number(item?.id || 0);
    const cid = Number(item?.chapterId || 0);
    if (!qid || !cid) continue;
    if (cache.map[qid] !== cid) { cache.map[qid] = cid; changed = true; }
  }
  if (!changed) return;
  const current = courseBankChangedAt(id);
  if (current) cache.bankChangedAt = current;
  writeQuestionChapterCache(id, cache);
}

/* 一次性把整门课的 qid → chapterId 补全。
   老用户第一次打开时映射是空的，章节树的「已做」会全是 0；
   靠翻题慢慢攒太慢（得一道道点过去）。接口只回 [qid, cid, ...] 的扁平数组，
   一门课几千题也就百来 KB，比拉完整题目列表小一个数量级。
   每门课只补一次（缓存里的 filled），题库版本变了再补一次。 */
async function backfillQuestionChapters(courseId) {
  const id = Number(courseId || 0);
  if (!id) return;
  const cache = questionChapterCache(id);
  if (cache.filled && !questionChapterCacheStale(id)) return;
  const res = await api(`/api/question-chapters?courseId=${id}`);
  const pairs = Array.isArray(res?.pairs) ? res.pairs : [];
  /* 拉取期间 rememberQuestionChapters 可能又写了新条目 → 重读一份最新的，别覆盖掉。 */
  const fresh = questionChapterCache(id, { reload: true });
  fresh.map = {};
  for (let i = 0; i + 1 < pairs.length; i += 2) fresh.map[pairs[i]] = pairs[i + 1];
  fresh.filled = true;
  fresh.bankChangedAt = courseBankChangedAt(id) || fresh.bankChangedAt || "";
  writeQuestionChapterCache(id, fresh);
  if (Number(state.currentCourse?.id) === id) renderChapters();
}

/* 老数据迁移：questionChapter 以前跟着用户数据同步，现在改成纯本地缓存。
   首次加载时把它摘到本地缓存，并把同步包里的字段**置空**（保留字段名，
   免得还在跑旧版本页面的标签页写回来时结构对不上）；
   questionChapterFilled 直接从同步包里删掉（理由见 writeQuestionChapterCache）。
   迁移会标脏一次（必须的：服务器上那份 326KB 的老包得瘦下来），之后不再回写。 */
function migrateQuestionChapterToLocalCache() {
  const courses = state.storage?.courses || {};
  let dirty = false;
  for (const [key, store] of Object.entries(courses)) {
    if (!store || typeof store !== "object") continue;
    const legacy = store.questionChapter;
    if (legacy && typeof legacy === "object" && Object.keys(legacy).length) {
      const cache = questionChapterCache(key, { reload: true });
      if (!Object.keys(cache.map).length) {
        /* 本地还没有这份映射：整份接过来。
           filled 一律给 false（不认老包的 questionChapterFilled），理由见下面。 */
        cache.map = { ...legacy };
        cache.filled = false;
      } else {
        /* 本地已有映射（这台机器上补过）：只补缺，不覆盖 */
        for (const [qid, cid] of Object.entries(legacy)) if (cache.map[qid] == null) cache.map[qid] = cid;
      }
      /* 迁移进来的映射是**旧包的产物**，无从判断它对应哪一版题库 ——
         统一标成「未补全」，让下一次 backfill 带着当前 changedAt 验证一遍。
         代价只有升级后首次打开多拉一次 /api/question-chapters（~160KB/门课），
         换来的是「题库更新后学员端章节映射不刷新」这条老问题对所有老用户也一并修好。 */
      writeQuestionChapterCache(key, cache);
      store.questionChapter = {};
      dirty = true;
    }
    if (store.questionChapterFilled !== undefined) {
      delete store.questionChapterFilled;
      dirty = true;
    }
  }
  if (dirty) scheduleSave();
  return dirty;
}

/* 整棵树的「已做」数：把已做的题按映射归到章，再逐级累加到父章。
   一次算完整棵树 —— 每个节点各遍历一遍的话，树一大就明显卡。
   「已做」同样用 done ∪ correct ∪ wrong：只认 done 的话，老数据里
   「做过但只有 correct/wrong」的题在章节树上一个数都不算。 */
function buildChapterDoneIndex(roots, courseStore = userCourseStore(), courseId = state.currentCourse?.id) {
  const map = questionChapterCache(courseId).map;
  const direct = new Map();
  for (const qid of doneQuestionIds(courseStore)) {
    const cid = Number(map[qid] || 0);
    if (!cid) continue;
    direct.set(cid, (direct.get(cid) || 0) + 1);
  }
  const totals = new Map();
  const walk = (node) => {
    let n = direct.get(Number(node.id)) || 0;
    for (const child of node.children || []) n += walk(child);
    totals.set(Number(node.id), n);
    return n;
  };
  roots.forEach(walk);
  return totals;
}

function childChapterIds(chapter) {
  const ids = [];
  const visit = (node) => {
    (node.children || []).forEach((child) => {
      ids.push(Number(child.id));
      visit(child);
    });
  };
  visit(chapter);
  return ids.filter(Boolean);
}

function renderChapterNodes(nodes, container) {
  for (const chapter of nodes) {
    const row = document.createElement("div");
    const hasChildren = chapter.children.length > 0;
    const expanded = state.expandedChapters.has(chapter.id);
    row.className = "chapter-row" + (hasChildren ? " has-children" : " leaf") + (state.currentChapter?.id === chapter.id ? " active" : "");
    row.style.setProperty("--indent", `${Math.min(chapter.grade, 5) * 14}px`);

    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = `chapter-toggle ${expanded ? "expanded" : "collapsed"}`;
    toggle.textContent = hasChildren ? (expanded ? "−" : "+") : "";
    toggle.disabled = !hasChildren;
    toggle.title = hasChildren ? (expanded ? "收起子章节" : "展开子章节") : "";
    const toggleChapter = (event) => {
      event.stopPropagation();
      if (expanded) state.expandedChapters.delete(chapter.id);
      else state.expandedChapters.add(chapter.id);
      renderChapters();
    };
    toggle.onclick = toggleChapter;

    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "chapter-item";
    const chapterDone = state.chapterDoneIndex?.get(Number(chapter.id)) || 0;
    btn.innerHTML = `<span>${escapeHtml(chapter.name)}</span><small>(${chapterDone}/${chapterTotalCount(chapter)})</small>`;
    if (hasChildren) btn.title = "点击加载本目录及全部子目录题目，左侧按钮展开或收起";
    btn.onclick = () => {
      selectChapter(chapter);
    };

    row.appendChild(toggle);
    row.appendChild(btn);
    container.appendChild(row);
    if (hasChildren && expanded) renderChapterNodes(chapter.children, container);
  }
}

async function selectChapter(chapter) {
  const transitionId = (state.modeTransitionId || 0) + 1;
  state.modeTransitionId = transitionId;
  state.questionRequestId = (state.questionRequestId || 0) + 1;
  state.submitToken = (state.submitToken || 0) + 1;
  state.submitting = false;
  if (state.mode === "smart") state.mode = "practice";
  state.currentChapter = chapter;
  state.currentIndex = -1;
  state.submitted = false;
  state.answerVisible = false;
  state.storage.profile.lastChapterId = chapter ? chapter.id : 0;
  renderChapters();
  if (!await loadTypes()) return;
  if (transitionId !== state.modeTransitionId) return;
  await loadQuestions();
  if (transitionId !== state.modeTransitionId) return;
  updatePickerHint();
  /* 手机端选完章节自动收起面板：面板占满整屏，不收就等于「点了一下什么也没发生」。
     （761px 以上不关：那里是并排的一栏，用户的下一步很可能是继续挑题型。） */
  if (isPhoneViewport()) setCoursePicker(false);
  scheduleSave();
}

async function loadTypes(options = {}) {
  if (!state.currentCourse) return false;
  const requestId = (state.typeRequestId || 0) + 1;
  state.typeRequestId = requestId;
  const courseId = Number(state.currentCourse.id);
  const params = new URLSearchParams({ courseId });
  if (!options.ignoreChapter && !isIdListMode()) applyChapterParams(params);
  const types = await api(`/api/types?${params}`);
  if (requestId !== state.typeRequestId || Number(state.currentCourse?.id || 0) !== courseId) return false;
  state.types = types;
  $("typeSelect").innerHTML = `<option value="">全部题型</option>` + state.types.map((t) =>
    `<option value="${t.id}">${escapeHtml(t.name || "题型")} (${t.questionCount})</option>`
  ).join("");
  ensureTagFilterControl();
  renderTagFilterOptions();
  return true;
}

function ensureTagFilterControl() {
  if ($("tagSelect")) return;
  /* 可见 label（S-25）。这个下拉原本只有一个 title="按标签筛选"：
     读屏读不到、手机点不出 tooltip，而且它躺在默认收起的搜索面板里 ——
     等于「标签筛选」这个功能在界面上没有名字。
     包一层 <label> 同时解决可见文案和可访问名（原生 label 关联）。 */
  const field = document.createElement("label");
  field.className = "tag-filter-field";
  const caption = document.createElement("span");
  caption.textContent = "标签筛选";
  const select = document.createElement("select");
  select.id = "tagSelect";
  select.title = "按标签筛选";
  select.onchange = () => {
    state.tagFilter = select.value;
    loadQuestions().catch((err) => toast(err.message));
  };
  field.appendChild(caption);
  field.appendChild(select);
  $("typeSelect").insertAdjacentElement("afterend", field);
}

/* ── 题单读取生命周期（UI-05）──────────────────────────────────────────
   旧实现只有一条线：切 mode → 请求 → 失败就 toast。请求成功前 state.questions 和
   DOM 全是上一个模块的，于是实测「阻断 /api/questions 后 practice → favorite」
   会得到：页签写着收藏、屏上还是题库那 80 题和同一道题干，只有一个几秒的 toast。
   用户完全可能就这么在「收藏」里作答了非收藏题。
   现在：进入模块先收起旧题单 + 明确加载占位；失败给常驻可重试的错误态；
   同一模块内刷新失败而保留旧题单时，顶部常驻「上次缓存，非本次结果」横幅，
   并收掉按题单生效的写操作（见 renderVerifyModeControls / blockWriteOnStaleList）。 */

const QUESTION_PLACEHOLDER_MODES = ["practice", "wrong", "favorite"];

function modeDisplayName(mode = state.mode) {
  return {
    practice: "考试题库",
    wrong: "错题本",
    favorite: "收藏试题",
    exam: "模拟考场",
    smart: "智能训练",
    training: "智能训练",
    progress: "学习看板",
  }[mode] || "当前模块";
}

function questionContextKey(mode = state.mode, courseId = state.currentCourse?.id) {
  return `${mode}|${Number(courseId || 0)}`;
}

/* 题型下拉里的**干净**文案：选项自带题量（如「多选题 (2428)」），
   写进提示语里太吵（用户不需要在空态里看题库统计）。 */
function selectedTypeLabel() {
  const select = $("typeSelect");
  if (!select) return "";
  const raw = (select.selectedOptions?.[0]?.textContent || "").trim();
  return raw.replace(/\s*[（(]\s*\d+\s*[)）]\s*$/, "").trim();
}

/* 屏上那份 state.questions 和「现在要去的模块/科目」不是一回事。 */
function questionContextChanged(mode = state.mode, courseId = state.currentCourse?.id) {
  return !!state.questionContext && state.questionContext !== questionContextKey(mode, courseId);
}

/* 只记「身份」（模块|科目 + 成功时间 + 取数条件），不动读取状态与陈旧标记。
   考场的卷子走这一条：它不是 loadQuestions 的结果，没有「读取中/读取失败」可言，
   但同样需要一份身份，切回「题库」时才会被认成换了模块（见 markQuestionListLoaded）。 */
function markQuestionListLoadedIdentity() {
  if (!state.questions.length) {
    state.questionContext = "";
    state.questionListAt = "";
    state.questionListSource = null;
    return;
  }
  state.questionContext = questionContextKey();
  state.questionListAt = nowText();
  state.questionListSource = {
    modeName: modeDisplayName(),
    courseName: state.currentCourse?.name || "",
    chapter: state.currentChapter?.name || "全部章节",
    type: selectedTypeLabel() || "全部题型",
    count: state.questions.length,
  };
}

/* 取数成功：连同「这份题单的身份」（成功时间 + 取数条件）一起记下，
   失败保留它时才有东西可写。 */
function markQuestionListLoaded(items) {
  state.questions = Array.isArray(items) ? items : [];
  state.questionLoadState = {
    status: "ready",
    mode: state.mode,
    courseId: Number(state.currentCourse?.id || 0),
    error: "",
  };
  state.questionListStale = null;
  markQuestionListLoadedIdentity();
  updateQuestionStaleBanner();
}

/* 请求失败：没旧题单可留 → 常驻错误态；留着旧题单 → 显式标成陈旧。 */
function failQuestionLoad(err, keptList = false) {
  state.questionLoadState = {
    status: "error",
    mode: state.mode,
    courseId: Number(state.currentCourse?.id || 0),
    error: (err && err.message) || "读取失败",
    at: nowText(),
  };
  state.questionListStale = keptList && state.questions.length
    ? {
        ...(state.questionListSource || {}),
        modeName: modeDisplayName(),
        count: state.questions.length,
        at: state.questionListAt || "未知时间",
        reason: state.questionLoadState.error,
      }
    : null;
  updateQuestionStaleBanner();
}

/* 「重试读取」的唯一实现：顶部陈旧横幅和加载失败空态各有一个按钮，
   原来各写一份一模一样的 loadQuestions().catch(...)，合并到这里。 */
function retryQuestionLoad() {
  return loadQuestions().catch((err) => toast(err.message));
}

/* 顶部常驻横幅：写清「这是上次缓存、什么条件、什么时候」。不给关闭按钮 ——
   这不是「提示一下就好」的状态：屏上的内容可能已经不是当前筛选的结果。 */
function updateQuestionStaleBanner() {
  const el = $("questionStaleBanner");
  if (!el) return;
  const stale = state.questionListStale;
  if (!stale) {
    el.classList.add("hidden");
    el.innerHTML = "";
    return;
  }
  el.classList.remove("hidden");
  el.innerHTML = `
    <strong>上面是上次缓存的内容，不是本次「${escapeHtml(stale.modeName || modeDisplayName())}」的读取结果</strong>
    <span>读取失败：${escapeHtml(stale.reason || "网络错误")}。屏上这 ${Number(stale.count || 0)} 题来自上次成功加载（${escapeHtml(stale.at || "未知时间")} · ${escapeHtml(stale.chapter || "全部章节")} · ${escapeHtml(stale.type || "全部题型")}），当前筛选条件可能已经变了。「提交答卷 / 清空本题单 / 显示答案 / 打印」等按题单生效的操作已暂时停用。</span>
    <button type="button" id="questionStaleRetryBtn">重试读取</button>
  `;
  const btn = $("questionStaleRetryBtn");
  if (btn) btn.onclick = retryQuestionLoad;
}

/* 屏上是陈旧题单时，按**整份题单**生效的写操作一律拦掉。
   单题作答不禁用：答案按题目 id 记录，不依赖题单构成（陈旧只影响「这题在不在
   当前筛选里」）。批量动作（交卷/清空）才是在猜题单，必须挡住。 */
function blockWriteOnStaleList() {
  if (!state.questionListStale) return false;
  toast("屏上是上次缓存的题单（本次读取失败），先点上方「重试读取」再操作");
  return true;
}

/* 读取中 / 读取失败的占位与错误态。返回 true = 已经接管 #emptyState，
   调用方不要再画 buildEmptyMessage() 那套「当前范围没有题目」。 */
function renderQuestionLoadState() {
  const st = state.questionLoadState;
  if (!st || st.status === "ready") return false;
  if (!QUESTION_PLACEHOLDER_MODES.includes(state.mode)) return false;
  const label = modeDisplayName(st.mode);
  $("questionBody").classList.add("hidden");
  const empty = $("emptyState");
  if (!empty) return false;
  empty.classList.remove("hidden");
  if (st.status === "loading") {
    empty.innerHTML = `<strong>正在加载「${escapeHtml(label)}」的题单…</strong>
      <span>${st.dropped
        ? `上一个模块（${escapeHtml(st.dropped)}）的内容已收起，避免和本次结果混在一起。`
        : "正在读取当前科目与筛选条件下的题目。"}</span>`;
    return true;
  }
  empty.innerHTML = `<strong>「${escapeHtml(label)}」的题单没读出来</strong>
    <span>${escapeHtml(st.error || "网络连接失败")}</span>
    <span>上一个模块或上一次筛选的题单已经收起，避免你在不确定的题单上作答。</span>
    <div class="empty-actions">
      <button type="button" id="questionLoadRetryBtn" class="primary-action">重试读取</button>
      ${state.mode === "practice" ? "" : '<button type="button" id="emptyBackToPracticeBtn">返回考试题库</button>'}
    </div>`;
  const retry = $("questionLoadRetryBtn");
  if (retry) retry.onclick = retryQuestionLoad;
  bindEmptyStateActions();
  return true;
}

/* 进模块（或换科目）的第一时间就把上一个模块的题单收起来。
   必须在 loadTypes 之前调用：loadTypes 也是网络请求，等它回来再收就已经把
   「新页签旧数据」露出来了。已在同一模块内重复点（含再点当前页签）不动。 */
function beginQuestionModuleEntry() {
  if (!QUESTION_PLACEHOLDER_MODES.includes(state.mode)) return;
  if (!questionContextChanged()) return;
  const dropped = state.questions.length
    ? `「${modeDisplayName(state.questionContext.split("|")[0])}」的 ${state.questions.length} 题`
    : "";
  state.questions = [];
  state.currentIndex = -1;
  state.questionListStale = null;
  state.questionLoadState = {
    status: "loading",
    mode: state.mode,
    courseId: Number(state.currentCourse?.id || 0),
    startedAt: Date.now(),
    error: "",
    dropped,
  };
  renderAll();
}

async function loadQuestions() {
  const requestId = (state.questionRequestId || 0) + 1;
  state.questionRequestId = requestId;
  if (!state.currentCourse) return;
  if (state.mode === "training") {
    setPanelPage(true);
    state.questions = [];
    state.currentIndex = -1;
    state.answerVisible = false;
    state.answerCardPage = 0;
    /* 训练/看板没有「题单」这个对象：陈旧标记与读取状态一并清掉（横幅收起来）。 */
    state.questionLoadState = null;
    state.questionListStale = null;
    updateQuestionStaleBanner();
    ensureAnalysisQuestionsInBackground();
    renderAll();
    return;
  }
  if (state.mode === "progress") {
    setPanelPage(true);
    state.questions = [];
    state.currentIndex = -1;
    state.answerVisible = false;
    state.answerCardPage = 0;
    state.questionLoadState = null;
    state.questionListStale = null;
    updateQuestionStaleBanner();
    await loadAnalysisQuestions();
    if (requestId !== state.questionRequestId) return;
    renderAll();
    return;
  }
  setPanelPage(false);
  if (state.mode === "exam" && state.exam && !state.submitted) {
    renderSearchMatches();
    return;
  }
  /* 换模块 / 换科目：屏上那份属于别处的题单先收掉，并给加载占位（见上面 UI-05 注释）。
     同一模块内的刷新（换章节/题型/题量）保留当前题单，失败时由 failQuestionLoad
     标成「上次缓存」而不是白屏。 */
  const contextChanged = questionContextChanged();
  if (contextChanged) {
    state.questions = [];
    state.currentIndex = -1;
    state.questionListStale = null;
  }
  const keptList = !contextChanged && state.questions.length > 0;
  state.questionLoadState = {
    status: "loading",
    mode: state.mode,
    courseId: Number(state.currentCourse?.id || 0),
    startedAt: Date.now(),
    error: "",
    dropped: "",
  };
  if (keptList) updateQuestionStaleBanner();   // 重新读取 → 先撤掉旧的陈旧横幅
  else renderAll();                            // 占位由 renderAll → renderQuestionLoadState 画
  state.answerCardPage = 0;
  const orderValue = $("orderSelect").value;
  const courseStore = userCourseStore();
  const effectiveLimit = effectiveQuestionLimit();
  const params = new URLSearchParams({
    courseId: state.currentCourse.id,
    limit: String(effectiveLimit),
  });
  /* 错题本 / 收藏夹模式下**不叠加章节筛选**：这两个模式的题目集由 ids 决定，
     而残留的 state.currentChapter（练习停在别的章节）会以 AND 的形式带上 chapterId，
     后端 chapterId 与 ids 是 AND 关系 → 错题不在那个章节就返回空数组，
     界面显示「当前无待复习错题」，而角标还写着「到期待复习(N)」。 */
  if (!isIdListMode()) applyChapterParams(params);
  if ($("typeSelect").value) params.set("typeId", $("typeSelect").value);
  if (orderValue && orderValue !== "random") params.set("order", orderValue);
  /* 题内搜索**不再走后端**：`q` 以前会随 loadQuestions 发给 /api/questions，
     于是「改题型/标签/题量」时也顺带把题单按搜索词过滤掉，而用户看到的只是
     「格子整块重绘」。现在搜索词只参与本地匹配（highlightSearchTerm /
     getSearchMatchIndexes / #searchMatchPanel），题单的构成只由题型+标签+题量决定。 */
  const idFilter = getModeQuestionIds(courseStore);
  if (state.mode !== "exam") state.answers = { ...currentPracticeAnswerStore() };
  const tagIds = getTagFilterIds();
  const mergedIds = intersectIdFilters(idFilter, tagIds);
  if (mergedIds) {
    if (!mergedIds.length) {
      /* 「这个集合本来就是空的」（比如 0 收藏、0 道到期错题）：这是一次**成功**的读取，
         空态由 buildEmptyMessage 按模块讲原因，不当错误处理。 */
      state.currentIndex = -1;
      state.answerCardPage = 0;
      markQuestionListLoaded([]);
      renderAll();
      return;
    }
    params.set("limit", String(Math.max(mergedIds.length, effectiveLimit)));
  }

  const requestedCourseId = Number(state.currentCourse?.id);
  /* ids 走查询串，必须分批：URL 一过 Kestrel 的请求行上限（默认 8192 字节，
     约 850 个 8 位 id）服务端直接回 414、响应体为空，而 state.questions 会保留上一次
     的题单、DOM 残留上一页，且没有任何提示。导出路径一直是按 100/批的，这里漏了。 */
  let items;
  try {
    items = await fetchQuestionsByIds(params, mergedIds, () =>
      requestId !== state.questionRequestId || Number(state.currentCourse?.id) !== requestedCourseId);
  } catch (err) {
    /* 迟到的失败不许画到新模块上：这轮请求如果已经被下一次导航/换科目作废，直接退出。 */
    if (requestId !== state.questionRequestId || Number(state.currentCourse?.id) !== requestedCourseId) return;
    failQuestionLoad(err, keptList);
    renderAll();          // 常驻错误态（没旧题单）/ 陈旧横幅 + 工具栏收敛（有旧题单）
    throw err;            // 调用方照旧 toast，但这不再是唯一的提示
  }
  if (requestId !== state.questionRequestId || Number(state.currentCourse?.id) !== requestedCourseId) return;
  // 记下 qid → chapterId，章节树的「已做/总数」靠它（纯本地缓存，不进同步包）
  rememberQuestionChapters(items, state.currentCourse.id);
  // 老用户的映射是空的，异步补一次（不阻塞渲染）；题库版本变了也会在这里重拉
  backfillQuestionChapters(state.currentCourse.id).catch(() => { /* 补不上就算了，翻题也会慢慢攒 */ });
  /* 题单由会话冻结时（智能训练 / 错题训练）按**冻结顺序**排：ids 就是这一轮
     的出题顺序，而服务端会按章节/题序重排；错题训练旧实现只冻结了「取哪些题」，
     顺序仍是服务端顺序，续做时上次那题会跳到别的页码。 */
  if (mergedIds && frozenSessionIdsForCurrentMode().length) items = orderItemsByIds(items, mergedIds);
  const shouldShuffle = orderValue === "random" || state.mode === "exam";
  /* ⚠️ shuffleKey 里**没有**搜索词：搜索只做本地匹配，不再参与取题
     （上面 `q` 那条注释）。旧实现在这里带 `q`，等于每敲一个字就换一次洗牌种子、
     整份随机题单重排一遍 —— 那是搜索「整块重绘」的另一半原因。 */
  const shuffleKey = JSON.stringify({
    courseId: state.currentCourse.id,
    chapterId: state.currentChapter?.id || 0,
    typeId: $("typeSelect").value || "",
    limit: effectiveLimit,
    mode: state.mode,
  });
  if (shouldShuffle) {
    if (state.shuffleKey !== shuffleKey || !Array.isArray(state.shuffledOrder)) {
      items = shuffleItems(items);
      state.shuffledOrder = items.map((item) => item.id);
      state.shuffleKey = shuffleKey;
    } else {
      const order = new Map(state.shuffledOrder.map((id, index) => [id, index]));
      items = items.slice().sort((a, b) => (order.get(a.id) ?? Number.MAX_SAFE_INTEGER) - (order.get(b.id) ?? Number.MAX_SAFE_INTEGER));
      const known = new Set(state.shuffledOrder);
      const missing = shuffleItems(items.filter((item) => !known.has(item.id)));
      if (missing.length) {
        state.shuffledOrder = state.shuffledOrder.concat(missing.map((item) => item.id));
        const byId = new Map(items.concat(missing).map((item) => [item.id, item]));
        items = state.shuffledOrder.map((id) => byId.get(id)).filter(Boolean);
      }
    }
  } else {
    state.shuffledOrder = null;
    state.shuffleKey = "";
  }

  if (requestId !== state.questionRequestId) return;
  markQuestionListLoaded(items);
  const activeSession = activeTrainingSession();
  const sessionMatchesMode = activeSession && (state.mode === "smart" || activeSession.mode === state.mode);
  state.currentIndex = state.mode === "smart" && state.smartPracticeFreshStart
    ? 0
    : sessionMatchesMode
      ? findTrainingStartIndex(items, activeSession, courseStore.lastSubjectId)
      : findStartIndex(items, courseStore.lastSubjectId);
  state.smartPracticeFreshStart = false;
  state.answerVisible = false;
  state.answerCardPage = Math.max(0, Math.floor(Math.max(0, state.currentIndex) / state.answerCardPageSize));
  renderAll();
  if (state.currentIndex >= 0 && state.mode !== "progress") await loadCurrentQuestion().catch((err) => toast(err.message));
}

async function loadAnalysisQuestions() {
  if (!state.currentCourse) return;
  const courseId = Number(state.currentCourse.id);
  if (state.analysisCourseId === courseId && state.analysisQuestions.length) return;
  const params = new URLSearchParams({
    courseId,
    limit: "30000",
  });
  const analysisQuestions = await api(`/api/questions?${params}`);
  if (Number(state.currentCourse?.id || 0) !== Number(courseId)) return;
  state.analysisQuestions = analysisQuestions;
  state.analysisCourseId = Number(courseId);
}

function ensureAnalysisQuestionsInBackground() {
  if (!state.currentCourse) return;
  const courseId = Number(state.currentCourse.id);
  if (state.analysisCourseId === courseId && state.analysisQuestions.length) return;
  if (state.analysisLoadingCourseId === courseId) return;
  state.analysisLoadingCourseId = courseId;
  loadAnalysisQuestions()
    .then(() => {
      if (state.mode === "training" && Number(state.currentCourse?.id || 0) === courseId) renderAll();
    })
    .catch((err) => {
      console.warn("analysis preload failed", err);
      if (state.mode === "training") toast("题库分析数据加载失败：" + err.message);
    })
    .finally(() => {
      if (state.analysisLoadingCourseId === courseId) state.analysisLoadingCourseId = 0;
    });
}

/* 错题本 / 收藏夹：题目集由 id 列表决定，章节/标签这类「范围筛选」不参与。
   叠上去只会把结果算成空集（chapterId 与 ids 在后端是 AND）。 */
function isIdListMode() {
  return state.mode === "wrong" || state.mode === "favorite";
}

function getTagFilterIds() {
  if (isIdListMode()) return null;
  const label = state.tagFilter || $("tagSelect")?.value || "";
  if (!label) return null;
  return (state.storage.tags?.[label] || []).map(Number).filter(Boolean);
}

/* 按 ids 拉题。id 超过一批时**分批**请求再合并（沿用导出路径的 100/批）：
   ids 全拼进查询串会顶穿服务端的请求行上限（Kestrel 默认 8192 字节）→ HTTP 414。
   合并时按批次顺序拼接并按 id 去重，保持原来「一批返回什么顺序就是什么顺序」的语义。 */
const QUESTION_IDS_BATCH_SIZE = 100;
async function fetchQuestionsByIds(params, mergedIds, isStale = null) {
  if (!mergedIds || mergedIds.length <= QUESTION_IDS_BATCH_SIZE) {
    if (mergedIds) params.set("ids", mergedIds.join(","));
    const items = await api(`/api/questions?${params}`);
    return items || [];
  }
  const items = [];
  const seen = new Set();
  for (let i = 0; i < mergedIds.length; i += QUESTION_IDS_BATCH_SIZE) {
    if (isStale && isStale()) return items;
    const batch = mergedIds.slice(i, i + QUESTION_IDS_BATCH_SIZE);
    const batchParams = new URLSearchParams(params);
    batchParams.set("ids", batch.join(","));
    batchParams.set("limit", String(batch.length));
    const part = await api(`/api/questions?${batchParams}`);
    for (const item of part || []) {
      const id = Number(item?.id || 0);
      if (!id || seen.has(id)) continue;
      seen.add(id);
      items.push(item);
    }
  }
  return items;
}

function intersectIdFilters(a, b) {
  if (!a && !b) return null;
  if (!a) return b || [];
  if (!b) return a || [];
  const set = new Set(b.map(Number));
  return a.map(Number).filter((id) => set.has(id));
}

function orderItemsByIds(items, ids) {
  const order = new Map((ids || []).map((id, index) => [Number(id), index]));
  return items.slice().sort((a, b) => (order.get(Number(a.id)) ?? Number.MAX_SAFE_INTEGER) - (order.get(Number(b.id)) ?? Number.MAX_SAFE_INTEGER));
}

/* 当前题单是不是「会话冻结」的，冻结的 ids 原样返回（按冻结顺序）。
   smart（智能训练）与 wrong（错题训练）都在内 —— 取题路径与排序必须同判据，
   否则会出现「取的是冻结 ids、排的是别处算出来的顺序」。纯读，不写数据。 */
function frozenSessionIdsForCurrentMode() {
  if (state.mode === "smart") return getSmartPracticeIds();
  if (state.mode === "wrong") {
    const session = activeTrainingSession();
    if (session?.mode === "wrong") return normalizeSessionIds(session.ids);
  }
  return [];
}

function getModeQuestionIds(courseStore) {
  if (state.mode === "smart") return getSmartPracticeIds();
  if (state.mode === "wrong") {
    /* 正在一轮错题训练里（指针指着一条 mode === "wrong" 的会话）：
       题单就是这条会话启动时冻结的 ids，**按冻结顺序**原样发下去，
       不再和「错题页当前筛选」求交集。
       旧实现两条都做，于是：答对一道 stage4 的题（毕业、courseStore.wrong 被删）
       之后刷新，实际题单从 32 掉到 31，而会话里 total 还是 32；
       切到「错误次数≥3」（列表剩 4 题）更会直接把这一轮砍成 4 题。
       冻结的必须同时是统计分母**和**取题范围。
       浏览错题本（指针为空，进错题本时会 clear）仍按筛选实时算 ——
       「我现在的错题还剩哪些」要如实反映已解决，那是另一种状态（见 UI-01 的取舍）。 */
    const frozen = frozenSessionIdsForCurrentMode();
    if (frozen.length) return frozen;
    return getWrongRecordsForCurrentCourse(courseStore)
      .filter(([id, item]) => wrongRecordMatchesFilter(id, item, courseStore))
      .map(([id]) => Number(id))
      .filter(Boolean);
  }
  if (state.mode === "favorite") {
    /* 与 renderProgress 的「收藏题（本科目）」KPI / buildFavoriteGroupRows 同口径。 */
    return favoriteIdsForCurrentCourse();
  }
  return null;
}

/* 当前智能练习的题单 = **指针指向的那条会话自己的 ids**。
   ⚠️ 这是 loadQuestions 的取数路径，必须只读。

   旧实现在这里写了数据：续不上（canContinueSmartPractice 要求 dayKey === 今天）
   就现场 buildSmartPracticeIds + saveSmartPracticeSession 现造一条 60 题的
   「智能推荐」并存盘。后果实测（点看板上一条昨天的 77 题会话）：
     · 被点的那条仍是 0/77、status=active，什么都没发生
     · state.questions 换成新建的 smart-…「智能推荐」60 题
     · toast 还说「已继续同类题型练习」（撒谎）
     · 会话数 1 → 2，连点几次 5→6→7 全是「进行中」
   根因就是「一个读列表的 getter 在写数据」，而且它被 getModeQuestionIds ← loadQuestions
   这条纯渲染路径调用，任何一次列表刷新都可能悄悄建会话。

   生成动作现在只发生在显式入口：startSmartPractice / startXxxPractice /
   continueTrainingSession（见那边的注释）。续不上就返回空数组，
   由 loadQuestions 给出正确的空态，而不是偷偷换一条题单。 */
function getSmartPracticeIds() {
  const session = activeTrainingSession();
  if (!session) return [];
  return normalizeSessionIds(session.ids);
}

/* ── 智能训练轮换 ──────────────────────────────────────────────────────
   为什么需要它：题库默认题序是 order by ichaptertype, cchaptercode, iindex, isubjectid，
   而真题卷行的 ichaptertype 是 NULL（SQLite 里 NULL 排最前）、历年真题里 2014 年编号最小，
   所以「题序最前面那一大段」永远是 2014 真题。用 slice(0, n) 抓题，
   每天的今日强化/考前冲刺都会从 2014 真题开始，看着就是「怎么全是 14 年真题」。

   另一个问题是旧实现没有任何跨天记忆：候选池不变、规则不变 → 每天生成的题单完全一样，
   唯一的变量是「做对了才会被移出池」，等于「只有做对才换题」。
   staleRank 用「最久没出现」排序解决它。

   数据来源是已有的两个存储字段，不新增 schema、不改导出格式：
     - state.storage.trainingSessions（≤80 个会话，含 ids / createdAt / dayKey）→ 生成过的题
     - state.storage.dailyActivity（保留 90 天，courses[cid].questions）→ 实际做过的题 */

/* 稳定散列：同一道题永远得到同一个 32 位数。
   给「从没见过」的题打散顺序——它们 staleness 全相等，如果保持题库原序，
   取前 N 道又会退回「全是 2014 真题」。刻意不用 Math.random：同一天内多次生成要一致。 */
function stableHash(id) {
  let h = (2166136261 ^ Number(id || 0)) >>> 0;
  h = Math.imul(h ^ (h >>> 13), 16777619) >>> 0;
  return (h ^ (h >>> 16)) >>> 0;
}

/* 某科目下每道题「最近一次出现在训练单或做题记录里」的日期（YYYY-MM-DD）。 */
function buildSeenIndex(courseId = state.currentCourse?.id) {
  const cid = Number(courseId || 0);
  const seen = new Map();
  const mark = (id, day) => {
    const n = Number(id);
    if (!n || !day) return;
    const prev = seen.get(n);
    if (!prev || day > prev) seen.set(n, day);
  };
  (state.storage.trainingSessions || []).forEach((session) => {
    if (Number(session.courseId || 0) !== cid) return;
    const day = session.dayKey || dateKey(parseDate(session.createdAt) || new Date());
    normalizeSessionIds(session.ids).forEach((id) => mark(id, day));
  });
  Object.entries(state.storage.dailyActivity || {}).forEach(([day, bucket]) => {
    const questions = bucket?.courses?.[String(cid)]?.questions;
    if (questions) Object.keys(questions).forEach((id) => mark(id, day));
  });
  return seen;
}

function daysSinceDayKey(day) {
  const d = parseDate(day);
  if (!d) return Infinity;
  d.setHours(0, 0, 0, 0);
  return Math.round((startOfToday().getTime() - d.getTime()) / 86400000);
}

function isInCooldown(id, seen, days = TRAINING_COOLDOWN_DAYS) {
  const day = seen.get(Number(id));
  if (!day) return false;
  return daysSinceDayKey(day) < days;
}

/* 候选排序器：从没出现过的排最前（同档按 stableHash 打散），其次是出现得最久远的。 */
function staleRank(seen) {
  return (a, b) => {
    const av = seen.get(Number(a));
    const bv = seen.get(Number(b));
    if (av !== bv) {
      if (!av) return -1;
      if (!bv) return 1;
      return av < bv ? -1 : 1;   // dayKey 是 YYYY-MM-DD，字符串序即时间序
    }
    return stableHash(a) - stableHash(b);
  };
}

/* 统一的候选排序：冷却期内的整体降到队尾，其余按 primary（默认 staleRank）排。 */
function orderCandidates(candidates, seen, primary = null) {
  const cmp = primary || staleRank(seen);
  return [...new Set((candidates || []).map(Number).filter(Boolean))].sort((a, b) => {
    const ca = isInCooldown(a, seen) ? 1 : 0;
    const cb = isInCooldown(b, seen) ? 1 : 0;
    return ca - cb || cmp(a, b);
  });
}

/* 今天「已经做过」的题 —— 同一天内不重复出题的依据。
   注意是「做过」，不是「生成过」：旧实现读的是 trainingSessions 里记录的题单 ids，
   那是「生成过」的口径，会把只看过一眼、一道没做的题单也算成已练。
   后果是「生成 60 道 → 一道没做 → 进今日错题复习」会被告知「都已经练过了」，
   而实际上根本没练过。
   数据源用 dailyActivity 里今天这个科目的 questions（记答案时写入，见 recordPracticeActivity）。
   跨天轮换（buildSeenIndex）仍然按「生成过 + 做过」算，两者职责不同：
   当天去重看「有没有练」，跨天轮换看「有没有见过」。 */
function todayServedIds(courseId = state.currentCourse?.id) {
  const cid = Number(courseId || 0);
  const questions = state.storage.dailyActivity?.[todayKey()]?.courses?.[String(cid)]?.questions;
  const out = new Set();
  if (questions) Object.keys(questions).forEach((id) => out.add(Number(id)));
  return out;
}

/* 未做题按二级章节轮转取：每轮每章最多 1 道。
   这是「训练其他」的关键一步——不做的话 24 道新题可能全砸在同一个章节里。
   排除条件用 isQuestionDone（done ∪ correct ∪ wrong）：正常答题路径下
   错题/对题一定也是 done，但导入的旧数据和**旧版考场交卷**只写 correct/wrong，
   三样都判才稳，否则「新题」里会混进早就做过的题。 */
function takeFreshByChapter(items, courseStore, quota, blocked, seen, randomize = false) {
  if (quota <= 0) return [];
  const groups = new Map();
  items.forEach((item) => {
    const id = Number(item.id);
    if (!id || blocked.has(id) || isQuestionDone(courseStore, id)) return;
    const path = chapterPathForId(item.chapterId).slice(0, 2);
    const key = Number(path[path.length - 1]?.id || item.chapterId || 0);
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(id);
  });
  const cmp = staleRank(seen);
  const buckets = [...groups.values()].map((list) => randomize ? shuffleItems(list) : list.sort(cmp));
  const out = [];
  for (let round = 0; out.length < quota; round++) {
    let progressed = false;
    for (const bucket of buckets) {
      if (out.length >= quota) break;
      if (round < bucket.length) {
        out.push(bucket[round]);
        progressed = true;
      }
    }
    if (!progressed) break;
  }
  return out;
}

/* ── 组题单的三个共用零件 ────────────────────────────────────────────
   「今日强化」(buildSmartPracticePlan) 和「考前冲刺」(buildSprintPracticePlan)
   原来各写一份去重器 / 薄弱章段 / 新题段，逻辑逐字相同，只有配额常量不同。
   抽到这里，改轮换策略或去重口径时只改一处。 */

/* 去重器：push 保证一批内不重复，take 额外跳过「今天已经出现过」的题。 */
function createPracticePicker(servedToday) {
  const ids = [];
  const picked = new Set();
  const push = (id) => {
    const n = Number(id);
    if (!n || picked.has(n)) return false;
    picked.add(n);
    ids.push(n);
    return true;
  };
  const take = (ordered, quota) => {
    let added = 0;
    for (const raw of ordered || []) {
      if (added >= quota) break;
      const n = Number(raw);
      if (picked.has(n) || servedToday.has(n)) continue;
      if (push(n)) added++;
    }
    return added;
  };
  return { ids, picked, push, take };
}

/* 「薄弱章节」段：前 N 个弱章各分一份配额，章内按跨天轮换排序。
   按**课本章名**筛（跨卷册、一题多章 —— 见 textbookChapterKeys），
   并排除已答对的题（没做对的才值得再练）。 */
function takeWeakChapterIds({ allItems, courseStore, weakChapters, quota, take, seen }) {
  const perChapter = Math.max(1, Math.ceil(quota / Math.max(1, weakChapters.length)));
  let added = 0;
  weakChapters.forEach((chapter) => {
    if (added >= quota) return;
    const candidates = allItems
      .filter((item) => textbookChapterKeys(item).includes(chapter.key))
      .filter((item) => !courseStore.correct?.[item.id])
      .map((item) => item.id);
    added += take(orderCandidates(candidates, seen), Math.min(perChapter, quota - added));
  });
  return added;
}

/* 「未做题保底」段：先扣出保底量，再按剩余空间继续取（新题不再是余数）。
   按二级章节轮转，避免整批新题砸在同一个章节。 */
function takeFreshIds({ allItems, courseStore, quota, picked, servedToday, seen, randomize, take }) {
  const pool = takeFreshByChapter(
    allItems,
    courseStore,
    quota,
    new Set([...picked, ...servedToday]),
    seen,
    randomize,
  );
  return take(pool, quota);
}

/* 今日强化的选题预算表。返回 { ids, counts }，counts 给卡片文案用。
   装填顺序：到期复习 → 反复错 → 薄弱章节 → 未做题保底 → 余量填充。
   每一段都有上限，且任何一段装不满都不会浪费位置（后面的段顺延吃余量）。 */
function buildSmartPracticePlan(courseStore = userCourseStore(), options = {}) {
  const randomize = !!options.randomize;
  const allItems = state.analysisQuestions.length ? state.analysisQuestions : state.questions;
  const seen = buildSeenIndex();
  const servedToday = todayServedIds();
  const { ids, picked, take } = createPracticePicker(servedToday);

  const records = getWrongRecordsForCurrentCourse(courseStore);
  const cmpStale = staleRank(seen);
  const counts = { due: 0, repeated: 0, weak: 0, fresh: 0, neverDone: 0 };

  /* ① 到期复习：到期日升序（最该复习的先来），同一天到期的按「最久没出现」轮换。 */
  const dueList = records
    .filter(([, item]) => isReviewDue(item))
    .sort((a, b) => (reviewDueDate(a[1]).getTime() - reviewDueDate(b[1]).getTime()) || cmpStale(a[0], b[0]))
    .map(([id]) => id);
  counts.due = take(orderCandidates(dueList, seen, cmpStale), SMART_QUOTA.due);

  /* ② 反复错：count≥2 且未 resolved。
     旧实现这里只判 `isReviewDue || count≥2`，漏了 resolved，已经复习完结的题还会被捞回来。 */
  const repeatedList = records
    .filter(([, item]) => !item.resolved && Number(item.count || 0) >= 2)
    .map(([id]) => id);
  counts.repeated = take(orderCandidates(repeatedList, seen), SMART_QUOTA.repeated);

  /* ③ 薄弱章节：top5 章均分配额，章内按轮换排序。
     旧实现是章内 slice(0,10) 取题序最前 10 道，薄弱章节不变则那 10 道永远不变。 */
  counts.weak = takeWeakChapterIds({
    allItems,
    courseStore,
    weakChapters: getWeakChapterRows(courseStore, allItems).slice(0, 5),
    quota: SMART_QUOTA.weak,
    take,
    seen,
  });

  /* ④ 未做题保底 */
  const freshQuota = Math.max(SMART_PRACTICE_FLOOR_NEW, SMART_PRACTICE_SIZE - ids.length);
  counts.fresh = takeFreshIds({ allItems, courseStore, quota: freshQuota, picked, servedToday, seen, randomize, take });

  /* ⑤ 余量填充：题库做完了（未做题取尽）时用剩余弱项补满，不留下短题单。 */
  if (ids.length < SMART_PRACTICE_SIZE) {
    counts.weak += take(
      orderCandidates([
        ...repeatedList,
        ...records.map(([id]) => id),
        ...Object.keys(courseStore.done || {}).filter((id) => !courseStore.correct?.[id]),
      ], seen),
      SMART_PRACTICE_SIZE - ids.length,
    );
  }

  const planIds = ids.slice(0, SMART_PRACTICE_SIZE);
  counts.neverDone = countNeverDone(planIds, courseStore);
  return { ids: planIds, counts };
}

/* 「新题」的真实口径 = 累计从没答过（done / correct / wrong 里都没有，见 isQuestionDone）。
   为什么不能用段④的 fresh 当新题数：段①到期、段②反复错都一定是做过的题，
   但段③「薄弱章节」是从「没做对」的题里取的，里面大量是从没答过的 ——
   实测同一轮 60 题里 54 题从没答过，卡片却写「新题42」，用户会低估新题量。
   段④的 fresh 保留原义（只数保底那一段），另给一个 neverDone 专供卡片文案。 */
function countNeverDone(ids, courseStore) {
  return (ids || []).filter((id) => !isQuestionDone(courseStore, id)).length;
}

function buildSmartPracticeIds(courseStore = userCourseStore(), options = {}) {
  return buildSmartPracticePlan(courseStore, options).ids;
}

function normalizeSessionIds(ids) {
  return [...new Set((ids || []).map(Number).filter(Boolean))];
}

function trainingSessionId(type = "training") {
  return `${type}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

function getTrainingSessionsForCourse(courseId = state.currentCourse?.id) {
  const id = Number(courseId || 0);
  return (state.storage.trainingSessions || [])
    .filter((item) => Number(item.courseId || 0) === id)
    .sort((a, b) => String(b.updatedAt || b.createdAt || "").localeCompare(String(a.updatedAt || a.createdAt || "")));
}

function activeTrainingSession() {
  const id = state.storage.activeTrainingSessionId || "";
  if (!id) return null;
  return (state.storage.trainingSessions || []).find((item) => item.id === id) || null;
}

function isBatchScopedTrainingSession(session) {
  if (!session) return false;
  /* 错题训练（session.mode === "wrong"，见 startWrongBookPractice / startDueWrongReview）
     也是「批量训练单」，同样要自带作答作用域：题池里全是用户**之前答过并答错**的题，
     courseStore.answers / verified 里躺着上一次的作答 —— 拿它们当本轮的作答，
     用户还没答就被判分、答案直接被标出来（见 activeWrongPracticeSession）。
     不计 answerScopeVersion：这个字段是给老 smart 会话做迁移用的，错题会话没有历史包袱。 */
  if (session.mode === "wrong") return true;
  return session.mode === "smart" && Number(session.answerScopeVersion || 0) >= 1;
}

function ensureSmartTrainingSessionState(session, reset = false) {
  if (!session || session.mode !== "smart") return session;
  if (reset) {
    session.answerScopeVersion = 1;
    session.answers = {};
    session.verifiedAnswers = {};
    session.results = {};
    session.currentIndex = 0;
    session.lastSubjectId = 0;
    session.status = "active";
    session.completedAt = "";
    return session;
  }
  if (!isBatchScopedTrainingSession(session)) {
    // Completed/expired legacy sessions keep their historical aggregate fields.
    // An unfinished legacy session is migrated only when it is resumed.
    if (session.status === "completed" || session.status === "expired") return session;
    const courseStore = peekCourseStore(session.courseId);
    const ids = normalizeSessionIds(session.ids);
    session.answers = {};
    session.verifiedAnswers = {};
    session.results = {};
    ids.forEach((id) => {
      const answer = courseStore.answers?.[id];
      const verified = courseStore.verified?.[id];
      if (normalizeAnswer(answer)) session.answers[id] = answer;
      if (verified) session.verifiedAnswers[id] = verified;
      if (verified && (courseStore.correct?.[id] || courseStore.wrong?.[id])) {
        session.results[id] = {
          answer: normalizeAnswer(answer),
          correct: !!courseStore.correct?.[id],
          at: verified.at || session.updatedAt || session.createdAt || nowText(),
        };
      }
    });
    session.answerScopeVersion = 1;
    if (state.user) scheduleSave();
  } else {
    session.answers ||= {};
    session.verifiedAnswers ||= {};
    session.results ||= {};
  }
  return session;
}

function activeSmartTrainingSession() {
  const session = activeTrainingSession();
  if (!session || state.mode !== "smart" || Number(session.courseId || 0) !== Number(state.currentCourse?.id || 0)) return null;
  const ensured = ensureSmartTrainingSessionState(session);
  ensured.answers ||= {};
  ensured.verifiedAnswers ||= {};
  ensured.results ||= {};
  return ensured;
}

/* 错题训练会话（「开始训练」/「今日错题复习」）：本轮作答与判定结果都记在**会话自己**身上。
   为什么必须这么做：错题的定义就是「用户之前答过并答错了」，
   所以 courseStore.answers[qid] / courseStore.verified[qid] 里躺着上一次的作答与判定
   —— 直接拿它们渲染，进训练的第一眼就是「上次选的选项被预选中 + 正确答案被标出来 +
   答案/解析区展开」，用户没答就被判了分。这不是练。
   作用域划在会话上（和智能训练同一套机制）之后：
     · 本轮还没答过的题 = 干净的一轮（没有任何提示）
     · 本轮答过的题 = 显示判定结果（退出再进能续做）
   注意与 isWrongBookTrainingSession 的分工：那个只管「强制立即反馈」，
   按上一轮的约定只认 type === "wrong"；这里要覆盖所有 wrong 训练会话（含 type === "due"）。 */
function activeWrongPracticeSession() {
  if (state.mode !== "wrong") return null;
  const session = activeTrainingSession();
  if (!session
    || session.mode !== "wrong"
    || Number(session.courseId || 0) !== Number(state.currentCourse?.id || 0)) return null;
  session.answers ||= {};
  session.verifiedAnswers ||= {};
  session.results ||= {};
  return session;
}

/* 当前「本轮作答」属于哪个会话（智能训练 / 错题训练）；没有会话就是科目级的 courseStore。 */
function activeScopedAnswerSession() {
  return activeSmartTrainingSession() || activeWrongPracticeSession();
}

/* 当前这一轮该读/写哪一份「作答」与「判定」。
   训练轮（智能训练 / 错题训练）用**会话自己**的一份，普通练习用 courseStore 的。
   两份存储的键名不同（会话是 answers / verifiedAnswers），所以传一对键名进来，
   而不是写两个只差字段名的函数。 */
function currentScopedStore(sessionKey, courseKey) {
  const session = activeScopedAnswerSession();
  if (session) return session[sessionKey];
  const courseStore = userCourseStore();
  courseStore[courseKey] ||= {};
  return courseStore[courseKey];
}

function currentPracticeAnswerStore() {
  return currentScopedStore("answers", "answers");
}

function currentPracticeVerifiedStore() {
  return currentScopedStore("verifiedAnswers", "verified");
}

/* ── 训练会话：终态 + 「继续上次」的唯一判据 ────────────────────────────
   为什么要重做：以前有**两个指针**指向「当前可继续的题单」——
   state.storage.activeTrainingSessionId 和 state.storage.smartPractice.sessionId。
   两者生命周期不同（退出智能练习清两个、点导航离开只清第一个、生成新题单都写），
   于是同屏能出现两个都能点的「继续」各指一条题单：
     hero「继续今日强化」→ 今日强化的 60 题 A（按 type 反查出来的一条）
     「继续上次：同类题型练习」→ 指针里的 75 题 B
   现在只有一个事实来源：**会话列表**。
     · activeTrainingSessionId —— 「我现在正在哪一轮里」，退出就清，语义不变
     · 没有别的指针了（smartPractice 已删除，见 normalizeStorage 里的丢弃兜底）
     · 「继续上次」= 未完成会话里最近更新的那条（**不限今天**），由
       resumableTrainingSession() 统一回答。hero 的「继续今日强化」、学习看板的
       「智能练习」卡、历史行的「继续」都调它，不可能再各说各话。

   终态三态：active（还能接着做）/ completed（本轮题全部确认过，见
   syncTrainingSessionStats）/ abandoned（超过 SESSION_ABANDON_DAYS 天没动过）。
   "expired" 是历史数据里旧的终态（rolled_to_next_day / 手动过期），一并按终态读。 */
const SESSION_ABANDON_DAYS = 7;
const TRAINING_SESSION_TERMINAL = ["completed", "abandoned", "expired"];

function isTrainingSessionOpen(session) {
  return !!session && !TRAINING_SESSION_TERMINAL.includes(session.status);
}

/* 会话「最后一次被动过」的时间。updatedAt 由 upsertTrainingSession /
   syncCurrentTrainingSession 维护（做题、切题、退出都会刷）。 */
function sessionLastTouchedAt(session) {
  return parseDate(session?.updatedAt) || parseDate(session?.createdAt) || null;
}

/* 「继续上次」的**唯一**判据。范围：当前科目 ∩ 未完成 ∩ 有题，
   取最近更新的一条（getTrainingSessionsForCourse 已按 updatedAt desc 排好）。
   ⚠️ **不限今天**（本批产品决策）：旧实现只认 dayKey === 今天，于是昨天/前天
   留下的半份题单在训练页和学习看板都点不到 —— 只能翻「个人训练看板」那一行
   找，用户的原话是「继续上次就该是上次」。终态（completed / abandoned /
   expired）仍按 isTrainingSessionOpen 排除；真正废弃由 SESSION_ABANDON_DAYS
   在 sweepTrainingSessionStates 里收口。
   返回的会话不一定是今天的 → 按钮/提示要用 sessionResumeDayText 带上日期。

   options.mode / options.type 用来把「可续」收窄到某一族会话。错题本面板要的是
   「上一轮**错题本**训练」，不能把「今日错题复习」（type:"due"，mode 同样是 "wrong"）
   也算进来 —— 两个入口的题池不同（错题本 = 当前筛选结果，错题复习 = 只到期），
   混在一起会让错题本的「继续上轮」续到一条题池完全不同的会话上（实测：做完
   今日错题复习后，错题本显示「继续上轮（已答 0/3）」并指向 due 会话）。 */
function resumableTrainingSession(courseId = state.currentCourse?.id, options = {}) {
  const wantMode = options.mode || "";
  const wantType = options.type || "";
  for (const session of getTrainingSessionsForCourse(courseId)) {
    if (wantMode && session.mode !== wantMode) continue;
    if (wantType && session.type !== wantType) continue;
    if (!normalizeSessionIds(session.ids).length) continue;
    if (!isTrainingSessionOpen(session)) continue;
    /* syncTrainingSessionStats 可能顺手把它标成 completed（本轮题全确认完了），
       那它就不再「可续」—— 继续往下找，别把已完成的当可续的返回。 */
    syncTrainingSessionStats(session);
    if (isTrainingSessionOpen(session)) return session;
  }
  return null;
}

/* 「继续上次」那条会话的日期（不是今天的才有值，今天返回空串）。
   用途：按钮/卡片/toast 上写「继续上次（10 月 9 日）」而非光秃秃的「继续上次」——
   否则用户以为是刚才那一条，点下去却回到了三天前的题单。

   ⚠️ 用 **dayKey**（这条题单属于哪一天），**不用 updatedAt**：updatedAt 会在
   点「继续」的瞬间被刷成今天，于是按钮上写「（10 月 9 日）」、点下去 toast 立刻
   改口成「（今天）」—— 实测就是这么翻的。dayKey 只由 upsertTrainingSession 保留，
   续考不会改它，前后说法一致。 */
function sessionResumeDayText(session) {
  if (!session || isSessionFromToday(session)) return "";
  const key = String(sessionDayKey(session) || "");
  /* "YYYY-MM-DD" 直接给 new Date 会按 UTC 解析，东八区以外的时区会差一天；
     补上 00:00:00 走本地时间。 */
  const date = (/^\d{4}-\d{2}-\d{2}$/.test(key) ? parseDate(`${key} 00:00:00`) : null)
    || parseDate(session.createdAt)
    || parseDate(session.updatedAt);
  if (!date) return "";
  return `${date.getMonth() + 1} 月 ${date.getDate()} 日`;
}

/* 会话终态收敛。旧实现只处理 activeTrainingSessionId 指向的那一条 ——
   而指针一退出就被 clearActiveTrainingSession() 清空，别的会话于是永远停在
   active：实测跨天 3 条仍全部「进行中」、expiredReason 为空，再造一条就是 4 条。
   这里扫**全部**会话（跨科目），把
     ① 本轮题全部确认过的 → completed（判据在 syncTrainingSessionStats 里）
     ② 超过 SESSION_ABANDON_DAYS 天没动过的 → abandoned（带原因，可追溯）
   收敛到一处，任何入口 refreshDailyTrainingState() 都会顺带跑一遍。 */
function sweepTrainingSessionStates(now = Date.now()) {
  let changed = false;
  for (const session of state.storage.trainingSessions || []) {
    if (!isTrainingSessionOpen(session)) continue;
    syncTrainingSessionStats(session);
    if (!isTrainingSessionOpen(session)) { changed = true; continue; }
    const last = sessionLastTouchedAt(session);
    const idleDays = last ? (now - last.getTime()) / 86400000 : Infinity;
    if (idleDays > SESSION_ABANDON_DAYS) {
      session.status = "abandoned";
      session.abandonedAt ||= nowText();
      session.abandonedReason = session.abandonedReason || `idle_over_${SESSION_ABANDON_DAYS}d`;
      changed = true;
    }
  }
  return changed;
}

/* 会话行右侧的按钮文案与可点性，和 isTrainingSessionOpen 同一判据 ——
   否则会出现「按钮能点，点下去提示『该训练已结束』」。 */
function trainingSessionButton(session) {
  const total = Number(session?.total || session?.ids?.length || 0);
  const done = Number(session?.done || 0);
  if (!isTrainingSessionOpen(session) || !total || done >= total) {
    return {
      label: session?.status === "completed" ? "已完成" : "已结束",
      disabled: true,
    };
  }
  return { label: "继续", disabled: false };
}

function resetExpiredSmartPracticeView() {
  if (state.mode !== "smart") return;
  state.mode = "training";
  state.questions = [];
  state.currentIndex = -1;
  state.answers = {};
  state.submitted = false;
  state.answerVisible = false;
  state.answerCardPage = 0;
  state.smartPracticeFreshStart = false;
}

function sessionDayKey(session) {
  if (!session) return "";
  if (session.dayKey) return String(session.dayKey);
  if (session.planKey) return String(session.planKey);
  return dateKey(parseDate(session.createdAt) || parseDate(session.updatedAt) || new Date());
}

function isSessionFromToday(session) {
  return !!session && sessionDayKey(session) === todayKey();
}

/* ⚠️ 已不用于「继续上次」：它只按 type 反查会话，不看用户点的是哪条 ——
   正是「hero 说继续今日强化、点下去续的却是另一条题单」的成因。
   新代码请用 resumableTrainingSession()（唯一判据）。
   保留是因为它仍被审计探针 (_co3-probe.js) 读取，别再往应用逻辑里加调用。 */
function findActiveTrainingSessionByType(type, courseId = state.currentCourse?.id, options = {}) {
  const onlyToday = options.onlyToday !== false;
  const sessions = getTrainingSessionsForCourse(courseId);
  return sessions.find((session) => {
    if (session.type !== type) return false;
    if (onlyToday && !isSessionFromToday(session)) return false;
    syncTrainingSessionStats(session);
    return isTrainingSessionOpen(session) && normalizeSessionIds(session.ids).length;
  }) || null;
}

function refreshDailyTrainingState() {
  const key = todayKey();
  let changed = false;
  /* 会话终态统一在这里收敛：completed（本轮题全确认完）/ abandoned
     （SESSION_ABANDON_DAYS 天没动过）。扫的是**全部**会话，不是指针那一条 ——
     旧实现只看指针，而指针一退出就被清空，所以会话永远停在「进行中」。 */
  if (sweepTrainingSessionStates()) changed = true;
  /* 旧的「跨天就把指针那条标 expired」不再做了：一天的间隔不该终止一轮训练，
     用户第二天回来接着做才是对的（真正废弃由 SESSION_ABANDON_DAYS 判）。
     这里只处理「指针指向的这条已经进了终态」——比如题全部确认完了。 */
  const active = activeTrainingSession();
  if (active && !isTrainingSessionOpen(active)) {
    const expiredCurrentSmart = state.mode === "smart" && active.mode === "smart";
    state.storage.activeTrainingSessionId = "";
    changed = true;
    if (expiredCurrentSmart) resetExpiredSmartPracticeView();
  }
  // Keep dailyActivity keyed by date; ensure today bucket exists for stable UI.
  state.storage.dailyActivity ||= {};
  if (!state.storage.dailyActivity[key]) {
    state.storage.dailyActivity[key] = { courses: {}, total: 0, correct: 0, verified: 0, updatedAt: nowText() };
    /* ⚠️ 只读态**不算**成「有改动」。开页时这个桶必然不存在 → 旧实现恒返回 true →
       loadUserData 当场就 scheduleSave 回写一次；读失败时那就是拿空包覆盖真实进度
       （实测 supa.json 2651 → 700 字节）。桶仍然建出来，界面要它稳定。 */
    if (!state.storageReadFailed) changed = true;
  }
  return changed;
}

function upsertTrainingSession(session, activate = true) {
  state.storage.trainingSessions ||= [];
  const ids = normalizeSessionIds(session.ids);
  const now = nowText();
  const oldIndex = state.storage.trainingSessions.findIndex((item) => item.id === session.id);
  const old = oldIndex >= 0 ? state.storage.trainingSessions[oldIndex] : {};
  const next = {
    ...old,
    ...session,
    ids,
    total: ids.length,
    dayKey: session.dayKey || old.dayKey || todayKey(),
    createdAt: old.createdAt || session.createdAt || now,
    updatedAt: now,
    status: session.status || old.status || "active",
  };
  if (oldIndex >= 0) state.storage.trainingSessions.splice(oldIndex, 1);
  state.storage.trainingSessions.unshift(next);
  state.storage.trainingSessions = state.storage.trainingSessions.slice(0, 80);
  if (activate) state.storage.activeTrainingSessionId = next.id;
  syncTrainingSessionStats(next);
  scheduleSave();
  return next;
}

function syncTrainingSessionStats(session = activeTrainingSession()) {
  if (!session) return null;
  if (session.mode === "smart" && !isBatchScopedTrainingSession(session) && isTrainingSessionOpen(session)) {
    ensureSmartTrainingSessionState(session);
  }
  const courseStore = peekCourseStore(session.courseId || state.currentCourse?.id);
  const ids = normalizeSessionIds(session.ids);
  const batchScoped = isBatchScopedTrainingSession(session);
  const answerStore = batchScoped ? (session.answers || {}) : (courseStore.answers || {});
  const verifiedStore = batchScoped ? (session.verifiedAnswers || {}) : (courseStore.verified || {});
  const resultStore = batchScoped ? (session.results || {}) : (courseStore.correct || {});
  const done = ids.filter((id) => batchScoped
    ? normalizeAnswer(answerStore[id]).length > 0
    : courseStore.done?.[id] || normalizeAnswer(answerStore[id]).length > 0).length;
  const verifiedIds = new Set(ids.filter((id) => {
    const record = verifiedStore[id];
    if (!record) return false;
    return record === true || typeof record !== "object" || !("answer" in record)
      || record.answer === normalizeAnswer(answerStore[id]);
  }).map(String));
  const verified = verifiedIds.size;
  /* 分子必须落在分母里：本轮的 confirmed 记录会被 resetQuestionVerification 删掉
     （改了答案），而 results 里的判定还在 —— 各数各的会算出 >100% 的「平均正确率」。 */
  const correct = ids.filter((id) => verifiedIds.has(String(id)) && (batchScoped
    ? resultStore[id] === true || resultStore[id]?.correct === true
    : courseStore.correct?.[id])).length;
  const wrong = ids.filter((id) => batchScoped
    ? resultStore[id] === false || resultStore[id]?.correct === false
    : courseStore.wrong?.[id] || state.storage.wrong?.[id]).length;
  session.total = ids.length;
  session.done = done;
  session.verified = verified;
  session.correct = correct;
  session.wrong = wrong;
  session.rate = confirmedRate(correct, verified);
  if (isTrainingSessionOpen(session) && verified >= ids.length && ids.length) {
    session.status = "completed";
    session.completedAt ||= nowText();
    if (session.type === "plan" && Number(session.courseId || 0) === Number(state.currentCourse?.id || 0)) {
      const plan = ensureReviewPlan(session.courseId);
      plan.dailyLog ||= {};
      const key = session.planKey || dateKey(parseDate(session.createdAt) || new Date());
      plan.dailyLog[key] ||= {
        newDone: Number(session.taskCounts?.new || 0),
        reviewDone: Number(session.taskCounts?.review || 0) + Number(session.taskCounts?.carryover || 0),
        weakDone: Number(session.taskCounts?.weak || 0),
        at: session.completedAt,
      };
    }
  }
  return session;
}

function syncCurrentTrainingSession(questionId = 0) {
  const session = activeTrainingSession();
  if (!session || Number(session.courseId || 0) !== Number(state.currentCourse?.id || 0)) return;
  session.updatedAt = nowText();
  if (isBatchScopedTrainingSession(session) && state.mode === "smart") session.answers = { ...state.answers };
  if (questionId) session.lastSubjectId = Number(questionId);
  session.currentIndex = Math.max(0, state.currentIndex);
  syncTrainingSessionStats(session);
}

function clearActiveTrainingSession() {
  state.storage.activeTrainingSessionId = "";
  scheduleSave();
}

/* 切页时结束当前训练会话，但**先**把「做到第几题」落盘：
   session.currentIndex / lastSubjectId 只在退出与卸载时同步，
   直接 clear 的话，「退出再进」会回到第 1 题（进度看着像丢了）。
   另外中途去别的页（练习 / 收藏）会刷新 courseStore.lastSubjectId，
   不先同步就会拿别的页面的题号去找起始位置，找不到就退回第 1 题。 */
function leaveActiveTrainingSession() {
  syncCurrentTrainingSession();
  clearActiveTrainingSession();
}

/* 点「错题本」时的页面筛选复位：错题本的第一诉求是「我错过的题在哪」，
   带着上一次的「复习 = 到期」进来会把今天刚错的题全滤掉 ——
   用户点「错题本」，看不到自己刚做错的题。
   只复位「复习」：它最容易被会话污染（且后果最严重）；
   「状态 / 错误次数 / 记忆周期」是用户在错题本里主动选的，保留。 */
function resetWrongBookPageFilter() {
  state.wrongFilters = { ...(state.wrongFilters || {}), review: "all" };
}

/* 显式生成入口。**唯一**会新建/改写训练单的函数 —— 读路径（getSmartPracticeIds）
   一律不碰它，这是 ④「点继续被静默换成另一条题单」的根因修法。
   upsertTrainingSession 会把 activeTrainingSessionId 指到新会话上，
   指针之外不写第二份「当前题单」（smartPractice 已删除）。 */
function saveSmartPracticeSession(ids, sourceTitle = "智能推荐", options = {}) {
  const cleanIds = normalizeSessionIds(ids);
  const session = upsertTrainingSession({
    id: options.sessionId || trainingSessionId(options.type || "smart"),
    type: options.type || "smart",
    mode: "smart",
    sourceTitle,
    courseId: state.currentCourse?.id || 0,
    courseName: state.currentCourse?.name || "",
    ids: cleanIds,
    answerScopeVersion: 1,
    answers: {},
    verifiedAnswers: {},
    results: {},
    currentIndex: 0,
    lastSubjectId: 0,
  });
  ensureSmartTrainingSessionState(session, true);
  syncTrainingSessionStats(session);
  // A newly generated batch must never inherit the previous round's
  // runtime reveal state, even when the user starts it immediately after submit.
  state.answers = {};
  state.submitted = false;
  state.answerVisible = false;
  state.answerCardPage = 0;
  state.smartPracticeFreshStart = true;
  scheduleSave();
  return session;
}

/* 新建一条智能训练题单并进入智能练习视图。
   6 个入口（收藏分组 / 信心·错因 / 今日强化 / 薄弱章节 / 同类题 / 考前冲刺）原来各写一遍
   「resetGeneratedPracticeFilters → saveSmartPracticeSession → state.mode="smart" →
   setCoursePicker(false) → loadQuestions」，而「清 smartLeftCourseName」只有 3 处写了：
   切过科目后再从收藏/同类题/冲刺入口开新题单，空态会错说「上一轮属于「X」科目」
   （见 buildEmptyMessage）。统一到这里，少一处遗漏就少一句错话。 */
async function enterSmartPractice({ ids, sourceTitle, type, limit = 0, extra = null, toastText = "" }) {
  const list = limit > 0 ? ids.slice(0, limit) : ids;
  resetGeneratedPracticeFilters();
  const session = saveSmartPracticeSession(list, sourceTitle, { type });
  if (extra) extra(session);
  state.mode = "smart";
  state.smartLeftCourseName = "";
  state.smartLeftCourseId = 0;
  setCoursePicker(false);
  await loadQuestions();
  if (toastText) toast(toastText);
  return session;
}

function findStartIndex(items, subjectId) {
  if (!items.length) return -1;
  const index = items.findIndex((item) => item.id === subjectId);
  return index >= 0 ? index : 0;
}

function findTrainingStartIndex(items, session, fallbackSubjectId = 0) {
  if (!items.length) return -1;
  const subjectId = Number(session?.lastSubjectId || fallbackSubjectId || 0);
  if (subjectId) {
    const bySubject = items.findIndex((item) => Number(item.id) === subjectId);
    if (bySubject >= 0) return bySubject;
  }
  const index = Number(session?.currentIndex);
  if (Number.isFinite(index) && index >= 0 && index < items.length) return index;
  return findStartIndex(items, fallbackSubjectId);
}

async function loadCurrentQuestion() {
  /* 翻题/换章节/切科目都会走这里，而它最终会 renderQuestion() 重写 #noteEditor。
     挂起的那份笔记必须**先**落库，否则正在输入的内容会被下一次渲染冲掉
     （实测：输入后立刻点下一题，qA 什么都没存到）。
     pendingNote 自带题号，所以这里不用关心 index 有没有已经变过。 */
  flushPendingNote();
  const requestId = (state.questionRequestId || 0) + 1;
  state.questionRequestId = requestId;
  const questions = state.questions;
  const index = state.currentIndex;
  const mode = state.mode;
  const courseId = Number(state.currentCourse?.id || 0);
  const item = questions[index];
  if (!item) return;
  const questionKey = `${courseId}:${mode}:${index}:${Number(item.id || 0)}`;
  const questionChanged = state.lastLoadedQuestionKey !== questionKey || state.lastLoadedQuestions !== questions;
  if (!item.detail) {
    const detail = await api(`/api/question?id=${item.id}`);
    if (requestId !== state.questionRequestId) return;
    item.detail = detail;
  }
  if (
    requestId !== state.questionRequestId
    || state.questions !== questions
    || state.currentIndex !== index
    || state.mode !== mode
    || Number(state.currentCourse?.id || 0) !== courseId
    || Number(questions[index]?.id) !== Number(item.id)
  ) return;
  if (!item.detail) return;
  if (mode === "exam") saveExamDraft();
  else {
    /* lastSubjectId = 「上次看到哪一题」，只有真的换题才值得回写整包。
       旧实现在这里无条件 scheduleSave()，而开页路径是
       restoreLastCourse → selectCourse → loadQuestions → loadCurrentQuestion，
       于是**单纯刷新一次**就会 POST 整包 → 服务器 revision 前进 →
       之前打开的标签页保存时 409（实测 reload 一次 = 1 POST、revision 前进）。 */
    const courseStore = userCourseStore();
    if (Number(courseStore.lastSubjectId || 0) !== Number(item.id)) {
      courseStore.lastSubjectId = item.id;
      scheduleSave();
    }
    syncCurrentTrainingSession(item.id);
  }
  state.answerVisible = state.submitted || isQuestionVerified(item.id);
  state.answerHiddenOverride = false;   // 换题清掉「手动隐藏」，别把上一题的意图带过来
  renderQuestion();
  state.lastLoadedQuestionKey = questionKey;
  state.lastLoadedQuestions = questions;
  if (questionChanged) scrollQuestionViewToTop();
  prefetchAroundCurrent();
}

/* ── 题目预取 ──────────────────────────────────────────────────────────────
   每翻一题打一次 /api/question，弱网下会明显卡顿。这里提前把前后几题取回来。

   开销控制（墨水屏设备性能差，必须克制）：
     · 只预取详情 JSON；图片单独处理（见 warmImageCache）
     · 并发上限 2，往前最多 PREFETCH_AHEAD 题、往回 1 题
     · 低配设备自动缩窗口：deviceMemory ≤ 2 或 hardwareConcurrency ≤ 4 → 只预取 1 题
       且不预热图片
     · 省流量模式（navigator.connection.saveData）直接不预取
     · 用 requestIdleCallback 发起，不和首屏渲染抢主线程
     · 换题单/换课程用「代次号」作废旧结果。不 abort 已发出的请求 ——
       那要多写一套错误分支，而按代次丢弃结果效果一样
     · 预取失败静默，真正翻到那题会正常重试
   ────────────────────────────────────────────────────────────────────── */
const PREFETCH_AHEAD = 3;
const PREFETCH_CONCURRENCY = 2;
const PREFETCH_MAX_IMAGES = 4;
const prefetchState = { generation: 0, inflight: 0, queue: [] };

function prefetchAheadCount() {
  try {
    if (navigator.connection?.saveData) return 0;
    const mem = Number(navigator.deviceMemory || 0);
    const cores = Number(navigator.hardwareConcurrency || 0);
    if ((mem > 0 && mem <= 2) || (cores > 0 && cores <= 4)) return 1;
  } catch (e) { /* 老浏览器没这些 API，按默认来 */ }
  return PREFETCH_AHEAD;
}

function pumpPrefetch() {
  while (prefetchState.inflight < PREFETCH_CONCURRENCY && prefetchState.queue.length) {
    const job = prefetchState.queue.shift();
    if (job.generation !== prefetchState.generation || job.item.detail) continue;
    prefetchState.inflight += 1;
    api(`/api/question?id=${job.item.id}`)
      .then((detail) => {
        if (job.generation !== prefetchState.generation) return;   // 已经换题单了，丢掉
        job.item.detail = detail;
        if (job.warmImages) warmImageCache(detail);
      })
      .catch(() => { /* 预取失败静默；真翻到那题会重新请求 */ })
      .finally(() => { prefetchState.inflight -= 1; pumpPrefetch(); });
  }
}

/* 把下一题的图塞进浏览器 HTTP 缓存 —— 不保留引用，避免白占内存。
   这样翻过去时 <img> 直接命中缓存，不会先白一块再出图。 */
function warmImageCache(detail) {
  const html = [detail?.stem, detail?.extraQuestion, detail?.description].filter(Boolean).join(" ");
  const re = /<img[^>]*\bsrc="([^"]+)"/gi;
  const srcs = [];
  let m;
  while ((m = re.exec(html)) !== null && srcs.length < PREFETCH_MAX_IMAGES) srcs.push(m[1]);
  for (const src of srcs) {
    const img = new Image();
    img.decoding = "async";
    img.src = src;
  }
}

function prefetchAroundCurrent() {
  const ahead = prefetchAheadCount();
  const generation = prefetchState.generation + 1;
  prefetchState.generation = generation;
  prefetchState.queue.length = 0;          // 旧的待办作废
  if (ahead <= 0) return;
  const questions = state.questions;
  if (!questions?.length) return;
  const picks = [];
  for (let d = 1; d <= ahead; d += 1) {
    const item = questions[state.currentIndex + d];
    // 只给「下一题」预热图片；低配设备（ahead===1）连图也不预热
    if (item && !item.detail) picks.push({ item, generation, warmImages: d === 1 && ahead > 1 });
  }
  const prev = questions[state.currentIndex - 1];
  if (prev && !prev.detail) picks.push({ item: prev, generation, warmImages: false });
  if (!picks.length) return;
  prefetchState.queue = picks;
  if (typeof requestIdleCallback === "function") requestIdleCallback(pumpPrefetch, { timeout: 1500 });
  else setTimeout(pumpPrefetch, 150);
}

function renderAll() {
  renderMode();
  renderExamStatus();
  renderWrongFilters();
  renderPracticeContextPanel();
  if (state.mode === "training") {
    renderTraining();
    return;
  }
  if (state.mode === "progress") {
    renderProgress();
    return;
  }
  renderAnswerCard();
  renderSearchMatches();
  updateStats();
  if (!state.questions.length) {
    $("questionBody").classList.add("hidden");
    $("emptyState").classList.remove("hidden");
    /* 读取中 / 读取失败优先：这两种状态下画「当前范围没有题目，可以换章节…」
       等于把「没读出来」说成「本来就没有」（见 renderQuestionLoadState）。 */
    if (!renderQuestionLoadState()) {
      const message = buildEmptyMessage();
      $("emptyState").innerHTML = `<strong>${message[0]}</strong><span>${message[1]}</span>${message[2] || ""}`;
      bindEmptyStateActions();
    }
  }
}

/* 「返回考试题库」的唯一实现。三个入口共用：
   · 训练页工具行的 #trainingPracticeBtn
   · 空态/读取失败态里的 #emptyBackToPracticeBtn
   · 错题本等模块内的「回题库」按钮
   原来这几处各写一份，且都**不递增 modeTransitionId**（在途请求回来照样写屏）、
   也不统一 setPanelPage。现在一律走 switchMode。 */
function backToPractice() {
  return switchMode("practice");
}

/* 空态里也可能有操作按钮（比如错题本「一道都没到期」时的「立即训练全部 N 道」、
   智能练习空态里的「开始今日强化」）。innerHTML 每渲染一次都会重建按钮，所以每次都要重新绑。 */
function bindEmptyStateActions() {
  const wrongBtn = $("emptyWrongStartBtn");
  if (wrongBtn) wrongBtn.onclick = () => startWrongBookPractice({ ignoreReview: true }).catch((err) => toast(err.message));
  const smartBtn = $("emptyStartSmartBtn");
  if (smartBtn) smartBtn.onclick = () => startSmartPractice().catch((err) => toast(err.message));
  /* 收藏空态 / 题单读取失败的错误态共用这个出口：回考试题库。 */
  const backBtn = $("emptyBackToPracticeBtn");
  if (backBtn) backBtn.onclick = backToPractice;
  /* 收藏有内容、但被「题型」筛空时的出口：清掉题型筛选再读一次
     （收藏是 id 列表模式，章节/标签不参与，能挡住它的只有题型）。 */
  const clearTypeBtn = $("emptyClearTypeBtn");
  if (clearTypeBtn) {
    clearTypeBtn.onclick = async () => {
      if ($("typeSelect")) $("typeSelect").value = "";
      await loadQuestions().catch((err) => toast(err.message));
    };
  }
}

/* 空状态文案。错题本里「列表是空的」有四种完全不同的原因，说错一种用户就会以为
   错题没记上：
     ① 一道错题都没有；
     ② 有错题，但「复习/状态/错误次数」筛选把它们的挡光了（旧文案一律说
        「当前无待复习错题」，于是出现「面板写着到期待复习(1)，列表说没有」的自相矛盾）；
     ③ 当前复习任务（trainingSession.ids）里已经没有未做的题；
     ④ 就是没题。
   这里把 ①②③ 分开讲清楚。 */
function daysUntilDate(date) {
  /* 按「日历天」算，并减掉今天**已经过去**的时间：
     旧实现用 startOfToday() 做基准再 ceil，明天下午到期的题会被说成「2 天后」。 */
  const day = new Date(date.getTime());
  day.setHours(0, 0, 0, 0);
  return Math.max(1, Math.round((day.getTime() - startOfToday().getTime()) / 86400000));
}

function daysUntilReview(record) {
  return daysUntilDate(reviewDueDate(record));
}

function buildEmptyMessage() {
  /* ⑤ 智能练习的空列表有自己的一套原因，说错一种用户就会以为题库坏了：
       ① 「续不上」——本轮题单的会话不在了 / 指针被清了；
       ② 「切了科目」——上一轮题单属于**别的科目**，题单不会跟着过来。
     实测（真实点击科目器）：写的是「当前范围没有题目 / 可以换章节、题型或清空搜索条件」——
     用户刚切了科目，跟章节/题型毫无关系，照着做只会更迷糊。 */
  if (state.mode === "smart") {
    const courseName = state.currentCourse?.name || "当前科目";
    const leftName = state.smartLeftCourseName;
    if (leftName) {
      return [
        `「${leftName}」的智能练习不属于当前科目`,
        `你切换到了「${courseName}」。智能练习的题单按科目生成，上一轮属于「${leftName}」，`
          + `切回那个科目就能从学习看板继续；在本科目则要新开一轮。`,
        `<button type="button" id="emptyStartSmartBtn" class="primary-action">开始「${escapeHtml(courseName)}」的今日强化</button>`,
      ];
    }
    return [
      "本次智能练习没有题目",
      `当前科目（${courseName}）还没有可继续的题单。`,
      `<button type="button" id="emptyStartSmartBtn" class="primary-action">开始「${escapeHtml(courseName)}」的今日强化</button>`,
    ];
  }
  /* ── 收藏夹的空态必须讲收藏自己的两件事（UI-10）─────────────────────
     旧实现和普通练习共用一句「当前范围没有题目，可以换章节、题型或清空搜索条件」，
     但收藏**不叠章节**（见 isIdListMode / getTagFilterIds：章节/标签在 id 列表模式下
     根本不参与取数），题目内搜索也不改题单（搜索只做本地匹配）——让用户去换章节、
     清搜索只会白忙。实测 fresh 账号 0 收藏时写的正是这一句。
     能真正把收藏页清空的只有：① 本科目一道收藏都没有；② 题型筛选把收藏挡光了。
     两句话都要写明**科目范围**，否则用户会以为别科目的收藏丢了。 */
  if (state.mode === "favorite") {
    const courseName = state.currentCourse?.name || "当前科目";
    const favIds = favoriteIdsForCurrentCourse();
    const typeName = $("typeSelect")?.value ? selectedTypeLabel() : "";
    if (!favIds.length) {
      const allCourses = Object.keys(state.storage.favorite || {}).length;
      return [
        `「${courseName}」还没有收藏题`,
        `做题时点题目下方的「收藏本题」，这里就会出现。收藏按科目归档，这一页只列「${courseName}」`
          + (allCourses ? `；其它科目还有 ${allCourses} 道收藏，切到对应科目才能看到。` : "。"),
        '<button type="button" id="emptyBackToPracticeBtn" class="primary-action">返回考试题库</button>',
      ];
    }
    if (typeName) {
      return [
        `${favIds.length} 道收藏都被题型筛选「${typeName}」挡住了`,
        `「${courseName}」共有 ${favIds.length} 道收藏，但没有一道属于「${typeName}」。`
          + `收藏页不叠章节和搜索条件，清掉题型筛选就能看到全部收藏。`,
        '<button type="button" id="emptyClearTypeBtn" class="primary-action">清空题型筛选</button>',
      ];
    }
    /* 有记录、没筛选，却一道都没取回来：题库更新后题被删除/停用，或服务端返回空。
       别再让用户去换章节 —— 说清可能的原因和下一步。 */
    return [
      `「${courseName}」的 ${favIds.length} 道收藏一道都没取回来`,
      "这些题可能在题库更新时被删除或停用。可以点工具栏「重新读取题库」再试，"
        + "或到学习看板确认「收藏题（本科目）」的数字。",
      '<button type="button" id="emptyBackToPracticeBtn" class="primary-action">返回考试题库</button>',
    ];
  }
  if (state.mode !== "wrong") {
    return ["当前范围没有题目", "可以换章节、题型或清空搜索条件。"];
  }
  const filters = state.wrongFilters || {};
  const review = filters.review || "all";
  const courseStore = userCourseStore();
  const all = getWrongRecordsForCurrentCourse(courseStore);
  if (!all.length) {
    return ["错题本里还没有错题", "答错的题会自动收进这里；也可以在题目上手动加入错题。"];
  }
  const pending = all.filter(([, item]) => !item.resolved && !isReviewDue(item));
  if (review === "due" && pending.length) {
    const soonest = Math.min(...pending.map(([, item]) => reviewDueDate(item).getTime()));
    const days = daysUntilDate(new Date(soonest));
    /* 这是最伤人的一格：错题本里明明有题，却一道都点不开（旧文案只说「切到全部」
       就完了，等于让用户自己绕）。复习周期本来就是建议，直接给一个入口：
       忽略「复习」这一项，按其余筛选组一轮训练 —— 未到期的也练。 */
    const pool = all
      .filter(([id, item]) =>
        wrongRecordMatchesFilter(id, item, courseStore, { ...filters, review: "all" }))
      /* 与 buildWrongBookPracticeIds 同口径：只认数字 id。
         这里的 N 就是点下去实际会发的题数（题量不截断，全部发）。 */
      .filter(([id]) => Number(id));
    return [
      `当前无「到期」待复习错题，但有 ${all.length} 道错题`,
      `其中 ${pending.length} 道都还没到复习时间，最近一批 ${days} 天后到期。`
        + (pool.length ? `复习周期只是建议，现在就能练这 ${pool.length} 道（含未到期）。` : "把上方「复习」切到「全部」现在就能看。"),
      pool.length
        ? `<button type="button" id="emptyWrongStartBtn" class="primary-action">立即训练全部 ${pool.length} 道</button>`
        : "",
    ];
  }
  if (review === "future" && all.every(([, item]) => isReviewDue(item))) {
    return [
      `${all.length} 道错题已经到期，被「复习 = 未到期」挡住了`,
      "把上方「复习」切到「全部」或「到期」即可看到。",
    ];
  }
  if ((filters.status || "active") === "active" && !all.some(([id]) => !!courseStore.wrong?.[id])) {
    return [
      `${all.length} 道错题都已标记为「已解决」`,
      "把上方「状态」切到「已解决」或「全部」即可看到。",
    ];
  }
  if ((filters.status || "active") === "resolved" && all.some(([id]) => !!courseStore.wrong?.[id])) {
    return [
      "错题都还没解决，被「状态 = 已解决」挡住了",
      "把上方「状态」切回「未解决」或「全部」。",
    ];
  }
  const minCount = Number(filters.minCount || 1);
  if (minCount > 1 && !all.some(([, item]) => Number(item.count || 1) >= minCount)) {
    return [
      `没有错够 ${minCount} 次的题`,
      `当前 ${all.length} 道错题都只错了一次；把「错误次数」切回「全部次数」即可看到。`,
    ];
  }
  const session = activeTrainingSession();
  if (session?.mode === "wrong") {
    return [
      "本次复习任务的题都已经做完了",
      "可以回「训练」开始一轮新的复习，或把上方筛选放宽。",
    ];
  }
  return ["当前范围没有题目", "可以换题型或清空搜索条件。"];
}

function setPanelPage(active) {
  const enabled = !!active;
  document.body.classList.toggle("panel-page", enabled);
  const questionBody = $("questionBody");
  const emptyState = $("emptyState");
  const sidePanel = $("questionSidePanel");
  if (enabled) {
    questionBody?.classList.add("hidden");
    questionBody?.classList.remove("exam-case-split");
    emptyState?.classList.remove("hidden");
    sidePanel?.classList.add("hidden");
  } else {
    sidePanel?.classList.remove("hidden");
  }
}

function ensureQuestionSidePanel() {
  const body = $("questionBody");
  if (!body) return null;
  let panel = $("questionSidePanel");
  if (!panel) {
    panel = document.createElement("aside");
    panel.id = "questionSidePanel";
    panel.className = "question-side-panel";
    body.appendChild(panel);
  }
  ["questionTagPanel", "answerBox", "noteEditor"].forEach((id) => {
    const el = $(id);
    if (el && el.parentNode !== panel) panel.appendChild(el);
  });
  panel.classList.remove("hidden");
  return panel;
}

function isMobileViewport() {
  const width = window.innerWidth || document.documentElement.clientWidth || 0;
  const coarsePointer = !!window.matchMedia?.("(pointer: coarse)")?.matches
    || Number(window.navigator?.maxTouchPoints || 0) > 0;
  return width <= 1280 || coarsePointer;
}

/* 手机端断点（≤760px），和 style.css 里那一整套 `@media (max-width: 760px)` 对齐。
   ⚠️ 判断「面板会不会铺满整屏 / 底部标签栏在不在」这类**布局**问题时用它，
   不要用 isMobileViewport()：那个把 ≤1280px 和粗指针都算进来（它服务的是
   答题卡、预取、触摸手势那类「小屏或触摸设备」的判断），
   761–1280px 上面板是并排的一栏、根本不挡题。 */
function isPhoneViewport() {
  return window.matchMedia?.("(max-width: 760px)")?.matches ?? false;
}

function scrollQuestionViewToTop(behavior = "auto") {
  const view = $("questionView");
  if (!isMobileViewport() || !view) return;
  const reset = () => {
    view.scrollTop = 0;
    view.scrollLeft = 0;
    view.scrollTo?.({ top: 0, left: 0, behavior });
  };
  reset();
  window.requestAnimationFrame?.(reset);
  window.setTimeout?.(reset, 0);
}

function renderQuestion() {
  const item = state.questions[state.currentIndex];
  if (!item || !item.detail) return;
  /* 兜底：本函数下面会重写 #noteEditor，正在输入的那份先落库。
     （翻题的主路径已经在 loadCurrentQuestion 里刷过一次，这里是给
     「显示答案」「切验证模式」「交卷」这类直接重绘的入口收口。） */
  flushPendingNote();
  const q = item.detail;
  const revealAnswer = shouldRevealCurrentAnswer(q);
  const questionBody = $("questionBody");
  setPanelPage(false);
  renderPracticeContextPanel();
  $("emptyState").classList.add("hidden");
  questionBody.classList.remove("hidden");
  questionBody.classList.toggle("exam-case-split", state.mode === "exam" && /案例|问答|主观/.test(`${q.type || ""}${state.currentCourse?.name || ""}`) && !!q.extraQuestion);
  $("questionView").style.setProperty("--zoom", state.zoom);
  setText("questionNo", `第 ${state.currentIndex + 1} / ${state.questions.length} 题`);
  setText("questionType", `${q.type || "题目"}${state.mode === "exam" ? ` · ${getQuestionScore(q)}分` : ""}`);
  setText("questionChapter", q.chapterName || "");
  $("stem").innerHTML = highlightSearchTerm(addLazyLoading(q.stem));
  $("extraQuestion").innerHTML = highlightSearchTerm(addLazyLoading(q.extraQuestion));
  setText("answerText", q.answer || "");
  $("description").innerHTML = q.description ? `<b>试题解析：</b>${highlightSearchTerm(addLazyLoading(q.description))}` : "";
  $("answerBox").classList.toggle("hidden", !revealAnswer);
  /* 按钮文案跟着**实际可见性**走（不再看 state.answerVisible）：
     背题模式下答案框是可见的，而 answerVisible 可能是 false，
     旧条件会把按钮写成「显示答案」——按钮和它对应的框自相矛盾。 */
  setText("answerToggleBtn", revealAnswer ? "隐藏答案" : "显示答案");
  setText("favoriteBtn", isFavorite(q.id) ? "取消收藏" : "收藏本题");
  setText("wrongBtn", state.mode === "wrong" ? "移出错题" : "纠错");
  $("noteEditor").value = getNote(q.id);
  refreshNoteButton();
  renderOptions(q);
  ensureQuestionSidePanel();
  renderQuestionTags(q);
  renderCorrectionBadge(q);
  ensureQuestionSidePanel();
  renderExamStatus();
  renderVerifyModeControls();
  syncQuestionNavButtons();
  renderSearchMatches();
  renderAnswerCard();
  updateStats();
  animateQuestionEntry(q.id);
}

function animateQuestionEntry(questionId) {
  const body = $("questionBody");
  if (!body || !questionId || state.lastAnimatedQuestionId === questionId) return;
  state.lastAnimatedQuestionId = questionId;
  body.classList.remove("question-enter");
  void body.offsetWidth;
  body.classList.add("question-enter");
}

/* 纠错闭环（S12）：提交过纠错的题目在题号栏旁显示角标。
   此前用户提交后完全看不到结果（管理端能标记处理，但用户侧毫无反馈）。 */
function renderCorrectionBadge(q) {
  const record = (state.storage.corrections || [])
    .find((item) => Number(item.questionId) === Number(q.id) && item.status !== "deleted");
  let badge = $("correctionBadge");
  if (!record) {
    badge?.remove();
    return;
  }
  if (!badge) {
    badge = document.createElement("span");
    badge.id = "correctionBadge";
    badge.className = "correction-badge";
    const anchor = $("questionChapter");
    if (anchor && anchor.parentNode) anchor.parentNode.insertBefore(badge, anchor.nextSibling);
    else $("questionView")?.appendChild(badge);
  }
  badge.textContent = record.status === "resolved" ? "纠错已处理" : "已提交纠错";
  badge.title = `${record.type || "纠错"}：${record.note || ""}`;
  badge.dataset.status = record.status === "resolved" ? "resolved" : "open";
}

function renderQuestionTags(q) {
  let panel = $("questionTagPanel");
  if (!panel) {
    panel = document.createElement("div");
    panel.id = "questionTagPanel";
    panel.className = "question-tag-panel";
    $("answerBox").parentNode.insertBefore(panel, $("answerBox"));
  }
  const currentTags = questionTags(q.id);
  const confidence = state.storage.confidence?.[q.id] || "";
  const wrongReason = questionWrongReason(q.id);
  const favoriteGroup = favoriteGroupOf(q.id);
  const tagSummaryParts = [
    ...currentTags,
    confidence ? `信心：${confidence}` : "",
    wrongReason ? `错因：${wrongReason}` : "",
    favoriteGroup ? `收藏：${favoriteGroup}` : "",
  ].filter(Boolean);
  const tagSummary = tagSummaryParts.length ? tagSummaryParts.join(" · ") : "未添加标注";
  const keepOpen = Number(state.tagPanelQuestionId || 0) === Number(q.id) && state.mobileTagsOpen;
  state.tagPanelQuestionId = Number(q.id || 0);
  const renderToggleGroup = (items, attr, activeValue = "") => items.map((label) => {
    const escaped = escapeHtml(label);
    const isActive = Array.isArray(activeValue) ? activeValue.includes(label) : activeValue === label;
    return `<button type="button" class="quick-mark ${isActive ? "active" : ""}" ${attr}="${escaped}">${escaped}</button>`;
  }).join("");
  panel.innerHTML = `
    <button id="tagPanelToggleBtn" class="tag-panel-toggle" type="button" aria-expanded="false">
      <span>标签</span>
      <small>${escapeHtml(tagSummary)}</small>
    </button>
    <div class="tag-panel-body">
      <div class="tag-section">
        <strong>标签</strong>
        <div class="tag-button-grid">
          ${tagLabels().map((label) => {
            const active = currentTags.includes(label);
            const custom = !DEFAULT_TAG_LABELS.includes(label);
            return `<span class="tag-chip-wrap">`
              + `<button type="button" class="quick-mark ${active ? "active" : ""}" data-toggle-tag="${escapeHtml(label)}">${escapeHtml(label)}</button>`
              // 自定义标签多一个小 × 用来彻底删掉这个标签（默认标签不给删）
              + (custom ? `<button type="button" class="tag-chip-del" data-delete-tag-label="${escapeHtml(label)}" title="删除这个标签" aria-label="删除标签 ${escapeHtml(label)}">×</button>` : "")
              + `</span>`;
          }).join("")}
        </div>
        <div class="tag-add-row">
          <input id="tagAddInput" type="text" maxlength="12" placeholder="新建标签，回车添加" autocomplete="off">
          <button id="tagAddBtn" type="button">添加</button>
        </div>
      </div>
      <div class="tag-section">
        <strong>信心</strong>
        <div class="tag-button-grid">${renderToggleGroup(["确定", "不确定", "蒙的"], "data-confidence", confidence)}</div>
      </div>
      <div class="tag-section">
        <strong>错因</strong>
        <div class="tag-button-grid">${renderToggleGroup(["概念不清", "计算错误", "审题错误", "记忆错误", "选项陷阱"], "data-wrong-reason", wrongReason)}</div>
      </div>
      <div class="tag-section">
        <strong>收藏夹</strong>
        <div class="tag-button-grid">${renderToggleGroup(FAVORITE_GROUPS, "data-favorite-group", favoriteGroup)}</div>
      </div>
    </div>
  `;
  setMobileTagsOpen(keepOpen);
  $("tagPanelToggleBtn").onclick = () => setMobileTagsOpen(!state.mobileTagsOpen);
  panel.querySelectorAll("[data-toggle-tag]").forEach((btn) => {
    btn.onclick = () => toggleQuestionTag(q.id, btn.dataset.toggleTag);
  });
  // 新建标签：回车或点「添加」都行。输入框里打字不会被全局快捷键抢（isTypingTarget 挡着）
  const tagInput = $("tagAddInput");
  const submitNewTag = () => {
    const label = (tagInput?.value || "").trim();
    if (!label) {
      toast("请输入标签名");
      tagInput?.focus();
      return;
    }
    if (label.length > 12) {
      toast("标签名最多 12 个字");
      return;
    }
    addQuestionTag(q.id, label);   // 内部会 scheduleSave + renderQuestion
  };
  if (tagInput) {
    tagInput.onkeydown = (event) => {
      if (event.key !== "Enter") return;
      event.preventDefault();
      event.stopPropagation();
      submitNewTag();
    };
  }
  if ($("tagAddBtn")) $("tagAddBtn").onclick = submitNewTag;
  panel.querySelectorAll("[data-delete-tag-label]").forEach((btn) => {
    btn.onclick = () => deleteTagLabel(btn.dataset.deleteTagLabel);
  });
  panel.querySelectorAll("[data-confidence]").forEach((btn) => {
    btn.onclick = () => setQuestionConfidence(q.id, btn.dataset.confidence);
  });
  panel.querySelectorAll("[data-wrong-reason]").forEach((btn) => {
    btn.onclick = () => setQuestionWrongReason(q, btn.dataset.wrongReason);
  });
  panel.querySelectorAll("[data-favorite-group]").forEach((btn) => {
    btn.onclick = () => setFavoriteGroup(q, btn.dataset.favoriteGroup);
  });
}

function setQuestionConfidence(questionId, value) {
  state.storage.confidence ||= {};
  if (state.storage.confidence[questionId] === value) delete state.storage.confidence[questionId];
  else state.storage.confidence[questionId] = value;
  scheduleSave();
  renderQuestionTags(state.questions[state.currentIndex]?.detail || { id: questionId });
}

function setQuestionWrongReason(q, value) {
  if (!q) return;
  state.storage.wrongReasons ||= {};
  const current = questionWrongReason(q.id);
  if (current === value) state.storage.wrongReasons[q.id] = "";
  else state.storage.wrongReasons[q.id] = value;
  scheduleSave();
  renderQuestionTags(q);
  renderAnswerCardPage({ updateNav: false });
}

function questionWrongReason(questionId) {
  if (state.storage.wrongReasons && Object.prototype.hasOwnProperty.call(state.storage.wrongReasons, questionId)) {
    return state.storage.wrongReasons[questionId] || "";
  }
  return state.storage.wrongReasons?.[questionId] || state.storage.wrong?.[questionId]?.reason || "";
}

function favoriteGroupOf(questionId) {
  return state.storage.favorite?.[questionId]?.group || "";
}

function ensureFavoriteRecord(q, group = "") {
  state.storage.favorite ||= {};
  state.storage.favorite[q.id] ||= {
    courseId: q.courseId,
    chapterName: q.chapterName,
    type: q.type,
    title: stripText(q.stem).slice(0, 120),
    at: nowText(),
  };
  if (group) state.storage.favorite[q.id].group = group;
  return state.storage.favorite[q.id];
}

function setFavoriteGroup(q, group) {
  if (!q || !group) return;
  const record = ensureFavoriteRecord(q, group);
  if (record.group === group && record.groupToggledAt) {
    record.group = "";
    record.groupToggledAt = "";
  } else {
    record.group = group;
    record.groupToggledAt = nowText();
  }
  scheduleSave();
  renderQuestion();
  toast(record.group ? `已加入${record.group}` : "已取消收藏分组");
}

/* 死代码清理：这里原来有一个 `applyNoteTemplate(questionId)` + 上面的 `NOTE_TEMPLATE`
   常量。全项目没有调用者，界面上也没有入口（笔记编辑框是一个裸 <textarea>）——
   与其留一个永远点不到的「已套用笔记模板」toast，不如删掉；将来要做就在
   笔记编辑框旁边正经加一个入口，别让这段代码继续漂着。 */

function addQuestionTag(questionId, rawLabel) {
  const label = String(rawLabel || "").trim();
  if (!label) return;
  state.storage.tags ||= {};
  state.storage.tagLabels ||= [];
  state.storage.tags[label] ||= [];
  const qid = Number(questionId);
  if (!state.storage.tags[label].map(Number).includes(qid)) state.storage.tags[label].push(qid);
  if (!state.storage.tagLabels.includes(label)) state.storage.tagLabels.push(label);
  scheduleSave();
  renderQuestion();
  renderTagFilterOptions();
}

function toggleQuestionTag(questionId, rawLabel) {
  const label = String(rawLabel || "").replace(/\s*×$/, "").trim();
  if (!label) return;
  /* 标签 chip 点完会 renderQuestion() 重写笔记框 —— 正在输入的那份先落库，
     否则点一下 chip 就丢字（实测过）。 */
  flushPendingNote();
  if (questionTags(questionId).includes(label)) removeQuestionTag(questionId, label);
  else addQuestionTag(questionId, label);
}

function removeQuestionTag(questionId, label) {
  if (!label || !state.storage.tags?.[label]) return;
  const qid = Number(questionId);
  state.storage.tags[label] = state.storage.tags[label].filter((id) => Number(id) !== qid);
  scheduleSave();
  renderQuestion();
  renderTagFilterOptions();
}

/* 彻底删掉一个自定义标签（连同它在所有题目上的标记）。
   默认标签（DEFAULT_TAG_LABELS）不给删 —— 它们是内置的分类，删了会让筛选下拉变空。
   删之前确认一下：这个操作会波及所有题，不只是当前这道。 */
function deleteTagLabel(rawLabel) {
  const label = String(rawLabel || "").trim();
  if (!label || DEFAULT_TAG_LABELS.includes(label)) return;
  const count = (state.storage.tags?.[label] || []).length;
  const detail = count ? `\n\n有 ${count} 道题用了这个标签，会一并去掉。` : "";
  if (!confirm(`删除标签「${label}」？${detail}`)) return;
  if (state.storage.tags) delete state.storage.tags[label];
  state.storage.tagLabels = (state.storage.tagLabels || []).filter((item) => item !== label);
  scheduleSave();
  renderQuestion();
  renderTagFilterOptions();
  toast(`已删除标签「${label}」`);
}

function renderTagFilterOptions() {
  const select = $("tagSelect");
  if (!select) return;
  const previous = state.tagFilter || select.value || "";
  select.innerHTML = `<option value="">全部标签</option>` + tagLabels().map((label) =>
    `<option value="${escapeHtml(label)}" ${label === previous ? "selected" : ""}>${escapeHtml(label)}</option>`
  ).join("");
  select.value = previous;
}

function renderWrongFilters() {
  let panel = $("wrongFilterPanel");
  if (state.mode !== "wrong" || isAdmin()) {
    panel?.remove();
    return;
  }
  /* 错题「训练中」不画筛选面板。训练和浏览同为 mode="wrong"，旧实现只看 mode，
     于是点「开始训练/继续上轮」后这整块（两个大按钮 + 4 组下拉 + 导出）还留在
     .question-pane 的网格里 —— 面板没有 grid-row，掉进隐式第 6 行，移动端实测
     390×844 吃掉读题区下方 296px（题干只剩 246px、选项伸进操作区底下被盖住），
     桌面 1440×900 也占 149px。训练中的会话信息由 renderPracticeContextPanel
     顶上的「错题本训练」条承担（含退出按钮），筛选只属于「浏览错题本」这个形态。
     判据与 activeWrongPracticeSession 一致：有活跃指针且 mode==="wrong"。 */
  if (activeWrongPracticeSession()) {
    panel?.remove();
    return;
  }
  if (!panel) {
    panel = document.createElement("div");
    panel.id = "wrongFilterPanel";
    panel.className = "wrong-filter-panel";
    $("questionView").parentNode.insertBefore(panel, $("questionView"));
  }
  const filters = state.wrongFilters || {};
  const courseStore = userCourseStore();
  const allRecords = getWrongRecordsForCurrentCourse(courseStore);
  const activeCount = allRecords.filter(([id]) => !!courseStore.wrong?.[id]).length;
  const resolvedCount = allRecords.length - activeCount;
  const dueCount = allRecords.filter(([, item]) => isReviewDue(item) && !item.resolved).length;
  /* 当前筛选结果 = 「开始训练」的题池，与错题本列表同一套口径（wrongRecordMatchesFilter），
     所以按钮上的数就是列表里的数。**包含未到期的** —— 这是这个入口存在的意义
     （stage 0 的到期日是答错次日，只出到期题的话今天刚错的题今天练不了）。
     题量**不截断**：筛选结果多少道就练多少道（旧的 20 道上限已去掉，
     见 startWrongBookPractice）。`Number(id)` 这一层要和 buildWrongBookPracticeIds /
     getModeQuestionIds 保持一致 —— 它们只认数字 id，否则按钮算 32 道、实际只发 31 道。 */
  const pool = allRecords
    .filter(([id, item]) => wrongRecordMatchesFilter(id, item, courseStore))
    .filter(([id]) => Number(id));
  const poolNotDue = pool.filter(([, item]) => !isReviewDue(item)).length;
  const startLabel = `开始训练（${pool.length} 道${poolNotDue ? "·含未到期" : ""}）`;
  /* 还有没做完的一轮时，「继续上轮」和「按筛选新建」必须是**两个按钮、两个动作**。
     旧实现只有一个按钮，数量按当前筛选算、点下去却续了旧会话 ——
     实测：切到「错误次数≥3」（列表 4 题）后按钮写「开始训练 4 道」，
     点进去仍是那条 32 题的旧题单。两个数字说的是两件事，只能由两个入口承担。 */
  const openSession = wrongBookSessionForCourse();
  const newBatchLabel = `按筛选新建（${pool.length} 道${poolNotDue ? "·含未到期" : ""}）`;
  const newBatchTitle = pool.length
    ? `按当前筛选冻结一轮新的 ${pool.length} 道错题（上一轮的记录会保留在训练记录里）`
    : "当前筛选没有错题，换个筛选条件再试";
  const startButtons = openSession
    ? `<button type="button" id="wrongResumeBtn" class="wrong-start-btn primary-action"
            title="继续上轮错题训练：${openSession.total} 题，已答 ${openSession.done} 题（题单在启动时冻结，不随筛选变化）">继续上轮（已答 ${openSession.done}/${openSession.total}）</button>
    <button type="button" id="wrongStartBtn" class="wrong-start-btn primary-action secondary"
            ${pool.length ? "" : "disabled aria-disabled=\"true\""}
            title="${escapeHtml(newBatchTitle)}">${escapeHtml(newBatchLabel)}</button>`
    : `<button type="button" id="wrongStartBtn" class="wrong-start-btn primary-action"
            ${pool.length ? "" : "disabled aria-disabled=\"true\""}
            title="${pool.length
              ? `把当前筛选出的 ${pool.length} 道错题全部练一遍（${poolNotDue ? `含 ${poolNotDue} 道还没到复习周期的，` : ""}单选/判断题点了选项立即出对错）`
              : "当前筛选没有错题，换个筛选条件再试"}">${escapeHtml(startLabel)}</button>`;
  panel.innerHTML = `
    ${startButtons}
    ${openSession
      ? `<p class="wrong-filter-hint">本轮题单已冻结（${openSession.total} 题，已答 ${openSession.done} 题）· 下面这些筛选只作用于「按筛选新建」</p>`
      : ""}
    <label>
      <span>复习</span>
      <select id="wrongReviewFilter">
        <option value="due" ${(filters.review || "due") === "due" ? "selected" : ""}>到期待复习(${dueCount})</option>
        <option value="future" ${filters.review === "future" ? "selected" : ""}>未到期</option>
        <option value="all" ${filters.review === "all" ? "selected" : ""}>全部</option>
      </select>
    </label>
    <label>
      <span>状态</span>
      <select id="wrongStatusFilter">
        <option value="active" ${filters.status === "active" ? "selected" : ""}>未解决(${activeCount})</option>
        <option value="resolved" ${filters.status === "resolved" ? "selected" : ""}>已解决(${resolvedCount})</option>
        <option value="all" ${filters.status === "all" ? "selected" : ""}>全部(${allRecords.length})</option>
      </select>
    </label>
    <label>
      <span>错误次数</span>
      <select id="wrongCountFilter">
        <option value="1" ${filters.minCount === "1" ? "selected" : ""}>全部次数</option>
        <option value="2" ${filters.minCount === "2" ? "selected" : ""}>2次及以上</option>
        <option value="3" ${filters.minCount === "3" ? "selected" : ""}>3次及以上</option>
      </select>
    </label>
    <label>
      <span>记忆周期</span>
      <select id="wrongPeriodFilter">
        <option value="all" ${filters.period === "all" ? "selected" : ""}>全部</option>
        <option value="7" ${filters.period === "7" ? "selected" : ""}>近7天</option>
        <option value="30" ${filters.period === "30" ? "selected" : ""}>近30天</option>
      </select>
    </label>
    <button type="button" id="exportWrongBtn" class="wrong-export-btn primary-action secondary compact"
            title="把全部错题（不受当前筛选限制）导出为可打印页面，可另存为 PDF">导出全部错题</button>
  `;
  const reload = debounce(async () => {
    state.wrongFilters = {
      review: $("wrongReviewFilter").value,
      status: $("wrongStatusFilter").value,
      minCount: $("wrongCountFilter").value,
      period: $("wrongPeriodFilter").value,
    };
    await loadQuestions();
  }, 80);
  $("wrongReviewFilter").onchange = reload;
  $("wrongStatusFilter").onchange = reload;
  $("wrongCountFilter").onchange = reload;
  $("wrongPeriodFilter").onchange = reload;
  const startBtn = $("wrongStartBtn");
  if (startBtn) {
    /* 有旧会话时这个按钮是「按筛选新建」：走 options.newBatch，**不复用**旧会话。 */
    startBtn.onclick = () => startWrongBookPractice(openSession ? { newBatch: true } : {})
      .catch((err) => toast(err.message));
  }
  const resumeBtn = $("wrongResumeBtn");
  if (resumeBtn && openSession) {
    resumeBtn.onclick = () => continueTrainingSession(openSession.id).catch((err) => toast(err.message));
  }
  const exportBtn = $("exportWrongBtn");
  if (exportBtn) exportBtn.onclick = () => exportAllWrongQuestions().catch((err) => toast(err.message));
}

/* 错题导出（S10）：把**全部**错题（不受当前筛选/分页限制）拉全后走打印管线，
   浏览器「另存为 PDF」即可存档。此前只能打印当前题目集。 */
async function exportAllWrongQuestions() {
  if (!state.currentCourse) return;
  const records = getWrongRecordsForCurrentCourse(userCourseStore());
  if (!records.length) {
    toast("错题本里还没有题目");
    return;
  }
  const ids = records.map(([id]) => Number(id)).filter(Boolean);
  toast(`正在准备导出 ${ids.length} 道错题`);
  const items = [];
  const batchSize = 100;   // /api/questions 的 ids 走查询串，分批避免 URL 过长
  for (let i = 0; i < ids.length; i += batchSize) {
    const batch = ids.slice(i, i + batchSize);
    const params = new URLSearchParams({
      courseId: state.currentCourse.id,
      ids: batch.join(","),
      limit: String(batch.length),
    });
    const part = await api(`/api/questions?${params}`);
    items.push(...part);
  }
  if (!items.length) {
    toast("没有可导出的题目");
    return;
  }
  const details = await loadPrintQuestionDetails(items);
  if (!details.length) {
    toast("没有可导出的题目");
    return;
  }
  renderPrintView(details, `错题导出（${details.length} 题）`, true);
}

function renderPracticeContextPanel() {
  let panel = $("practiceContextPanel");
  const activeSession = activeTrainingSession();
  const showPanel = state.mode === "smart" || (state.mode === "wrong" && activeSession?.mode === "wrong");
  if (!showPanel || isAdmin() || !state.currentCourse) {
    panel?.remove();
    return;
  }
  if (!panel) {
    panel = document.createElement("div");
    panel.id = "practiceContextPanel";
    panel.className = "practice-context-panel";
    $("questionView").parentNode.insertBefore(panel, $("questionView"));
  }
  const done = state.questions.filter((item) => hasAnswer(item.id)).length;
  const verified = state.questions.filter((item) => isQuestionVerified(item.id)).length;
  /* 标题 / 生成时间 / 科目全部取自**当前会话**（唯一事实来源）。
     旧实现先读 smartPractice 指针再退回会话，两个指针不同步时，
     面板上写的会是另一条题单的名字。 */
  const session = syncTrainingSessionStats(activeSession);
  const title = session?.sourceTitle || "智能练习";
  const createdAt = session?.createdAt || "";
  const rate = session ? Number(session.rate || 0) : confirmedRate(0, 0);
  panel.innerHTML = `
    <div>
      <span>${escapeHtml(title)}</span>
      <b>${state.questions.length} 题 · 已做 ${session?.done ?? done} · 已确认 ${session?.verified ?? verified} · 本轮正确率 ${rate}%</b>
      <small>${escapeHtml(session?.courseName || state.currentCourse.name || "")}${createdAt ? ` · 生成于 ${escapeHtml(createdAt)}` : ""}</small>
    </div>
    <button class="practice-context-toggle" id="practiceContextToggleBtn" type="button">${isPracticeContextCollapsed() ? "展开" : "收起"}</button>
    <div class="practice-context-actions">
      ${state.mode === "smart" ? `<button type="button" id="regenerateSmartBtn">重新生成</button>` : ""}
      <button type="button" id="exitSmartBtn">${state.mode === "smart" ? "退出智能练习" : "退出本次复习"}</button>
    </div>
  `;
  panel.classList.toggle("collapsed", isPracticeContextCollapsed());
  panel.classList.toggle("compact-touch", isMobileViewport());
  $("practiceContextToggleBtn").onclick = () => {
    state.practiceContextCollapsed = !isPracticeContextCollapsed();
    renderPracticeContextPanel();
  };
  if ($("regenerateSmartBtn")) $("regenerateSmartBtn").onclick = () => startSmartPractice({ force: true }).catch((err) => toast(err.message));
  $("exitSmartBtn").onclick = () => exitTrainingSession().catch((err) => toast(err.message));
}

function isPracticeContextCollapsed() {
  if (state.practiceContextCollapsed === null) {
    const w = window.innerWidth || document.documentElement.clientWidth || 0;
    const h = window.innerHeight || document.documentElement.clientHeight || 0;
    return isMobileViewport() || (w > 1280 && h < 900);
  }
  return !!state.practiceContextCollapsed;
}

function renderSearchMatches() {
  const panel = $("searchMatchPanel");
  if (!panel) return;
  const term = activeSearchTerm();
  const matches = getSearchMatchIndexes();
  panel.classList.toggle("hidden", !term);
  if (!term) {
    panel.innerHTML = "";
    return;
  }
  if (!matches.length) {
    panel.innerHTML = `<span>没有找到匹配题目</span>`;
    return;
  }
  const position = Math.max(1, matches.indexOf(state.currentIndex) + 1);
  panel.innerHTML = `
    <span>搜索到 <b>${matches.length}</b> 条匹配</span>
    <button id="prevMatchBtn" type="button">上一处匹配</button>
    <button id="nextMatchBtn" type="button">下一处匹配</button>
    <span>${position}/${matches.length}</span>
  `;
  $("prevMatchBtn").onclick = () => moveSearchMatch(-1).catch((err) => toast(err.message));
  $("nextMatchBtn").onclick = () => moveSearchMatch(1).catch((err) => toast(err.message));
}

async function moveSearchMatch(delta) {
  const matches = getSearchMatchIndexes();
  if (!matches.length) return;
  let pos = matches.indexOf(state.currentIndex);
  if (pos < 0) pos = 0;
  else pos = (pos + delta + matches.length) % matches.length;
  state.currentIndex = matches[pos];
  state.answerCardPage = Math.floor(state.currentIndex / state.answerCardPageSize);
  await loadCurrentQuestion().catch((err) => toast(err.message));
}

function highlightSearchTerm(html) {
  const term = activeSearchTerm();
  if (!term) return html || "";
  const template = document.createElement("template");
  template.innerHTML = html || "";
  const walker = document.createTreeWalker(template.content, NodeFilter.SHOW_TEXT);
  const nodes = [];
  while (walker.nextNode()) {
    const node = walker.currentNode;
    const parent = node.parentElement;
    if (!parent || ["SCRIPT", "STYLE", "MARK"].includes(parent.tagName)) continue;
    if ((node.nodeValue || "").toLowerCase().includes(term.toLowerCase())) nodes.push(node);
  }
  const pattern = new RegExp(escapeRegExp(term), "gi");
  for (const node of nodes) {
    const text = node.nodeValue || "";
    const frag = document.createDocumentFragment();
    let last = 0;
    text.replace(pattern, (match, offset) => {
      if (offset > last) frag.appendChild(document.createTextNode(text.slice(last, offset)));
      const mark = document.createElement("mark");
      mark.className = "search-highlight";
      mark.textContent = match;
      frag.appendChild(mark);
      last = offset + match.length;
      return match;
    });
    if (last < text.length) frag.appendChild(document.createTextNode(text.slice(last)));
    node.parentNode.replaceChild(frag, node);
  }
  return template.innerHTML;
}

function renderOptions(q) {
  $("options").innerHTML = "";
  $("options").classList.remove("hidden");
  if (isSubjective(q)) {
    const input = document.createElement("textarea");
    input.id = "subjectiveInput";
    input.className = "subjective-input";
    input.placeholder = "请输入本题答案";
    input.value = state.answers[q.id] || "";
    input.oninput = () => saveSubjectiveAnswer(q, input.value);
    $("options").appendChild(input);
    return;
  }
  const selected = new Set((state.answers[q.id] || "").split("").filter(Boolean));
  const answerText = String(q.answer || "");
  const revealed = shouldRevealCurrentAnswer(q);
  /* 「漏选」只在**多选题**上标，而且要求这一轮真的作答过：
     · 单选题只有一个正确答案，用户没选它就是「答错」—— 把那个正确答案标成
       「漏选」等于说「你漏了一个」，是误导（用户点名不要）。
     · 一个选项都没选（背题模式 / 交卷时这题空着）时也谈不上「漏」，
       全标上漏选会让人以为答错了。 */
  const multi = isMultiChoice(q);
  const attempted = selected.size > 0;
  for (const option of q.options || []) {
    const el = document.createElement("button");
    el.className = "option";
    el.dataset.label = option.label;
    const chosen = selected.has(option.label);
    const isAnswer = revealed && answerText.includes(option.label);
    if (chosen) el.classList.add("selected");
    if (isAnswer) el.classList.add("correct");
    if (chosen && revealed && !answerText.includes(option.label)) el.classList.add("wrong");
    /* 正确答案里用户没选的那些：.correct 之外再挂一个 .missed。
       改前 `.option.correct` 写在 `.option.selected` 之后，两者同时存在时
       视觉完全一样 —— 多选答案 ABC、用户只选 A，B/C（漏选）和 A（选对）
       都是同一块绿，用户看不出漏了哪几个（错题本共用这个 renderOptions，
       所以那里也一样看不出来）。 */
    if (isAnswer && !chosen && multi && attempted) el.classList.add("missed");
    /* 文字徽标：墨水屏主题靠边框/填充区分状态，颜色不可靠，必须有一句话说得清
       「已选」还是「漏选」。只给多选题挂（见上）。 */
    const badge = revealed && multi && isAnswer
      ? (chosen ? "已选" : (attempted ? "漏选" : ""))
      : "";
    el.innerHTML = `<b>${escapeHtml(option.label)}</b><span>${escapeHtml(option.text)}`
      + `${badge ? `<em class="option-badge${badge === "漏选" ? " is-missed" : ""}">${badge}</em>` : ""}</span>`;
    el.onclick = () => chooseOption(q, option.label);
    $("options").appendChild(el);
  }
}

function printScopeLabel() {
  if (state.currentChapter) return `当前章节：${state.currentChapter.name} · ${state.questions.length} 题`;
  if (state.currentCourse) return `当前题单：${state.currentCourse.name} · ${state.questions.length} 题`;
  return `当前题单 · ${state.questions.length} 题`;
}

function openPrintDialog() {
  if (!state.questions.length || state.currentIndex < 0) {
    toast("当前没有可打印的题目");
    return;
  }
  $("printScopeHint").textContent = `${printScopeLabel()}。请选择打印当前题，或打印当前章节/筛选后的全部题目。`;
  setText("printAllTitle", state.currentChapter ? "所选章节全部" : "当前题单全部");
  setText("printAllDesc", state.currentChapter ? "打印所选章节及子章节的全部题" : "打印当前科目/筛选结果中的所有题");
  $("printAnswerCheck").checked = state.answerVisible || state.submitted || currentVerifyMode() === "review";
  $("printModal").classList.remove("hidden");
}

function closePrintDialog() {
  $("printModal").classList.add("hidden");
}

async function printQuestions(scope) {
  if (!state.questions.length || state.currentIndex < 0) {
    toast("当前没有可打印的题目");
    return;
  }
  closePrintDialog();
  const items = scope === "all" ? state.questions : [state.questions[state.currentIndex]];
  const label = scope === "all" ? printScopeLabel() : `当前题：第 ${state.currentIndex + 1} 题`;
  toast(scope === "all" ? `正在准备打印 ${items.length} 题` : "正在准备打印当前题");
  const details = await loadPrintQuestionDetails(items);
  if (!details.length) {
    toast("没有可打印的题目");
    return;
  }
  renderPrintView(details, label, $("printAnswerCheck")?.checked);
}

async function loadPrintQuestionDetails(items) {
  const result = new Array(items.length);
  let cursor = 0;
  const workers = Array.from({ length: Math.min(8, items.length) }, async () => {
    while (cursor < items.length) {
      const index = cursor++;
      const item = items[index];
      item.detail = item.detail || await api(`/api/question?id=${item.id}`);
      result[index] = item.detail;
    }
  });
  await Promise.all(workers);
  return result.filter(Boolean);
}

function renderPrintView(questions, scopeLabel, includeAnswers = false) {
  const view = ensurePrintView();
  view.innerHTML = `
    <div class="print-document">
      <header class="print-document-head">
        <strong>云习题库</strong>
        <span>${escapeHtml(scopeLabel)} · ${escapeHtml(state.currentCourse?.name || "")}</span>
      </header>
      ${questions.map((q, index) => renderPrintQuestion(q, index + 1, questions.length)).join("")}
      ${includeAnswers ? renderPrintAnswerSection(questions) : ""}
    </div>
  `;
  view.classList.remove("hidden");
  document.body.classList.add("print-mode");
  state.printAfterRender = true;
  requestAnimationFrame(() => {
    if (!state.printAfterRender) return;
    hideToast();
    window.print();
  });
}

function renderPrintQuestion(q, index, total) {
  const options = isSubjective(q)
    ? `<div class="print-subjective-lines"><span></span><span></span><span></span></div>`
    : `<div class="print-options">${(q.options || []).map((option) => `
        <div class="print-option"><b>${escapeHtml(option.label)}</b><span>${escapeHtml(option.text)}</span></div>
      `).join("")}</div>`;
  return `
    <article class="print-question">
      <div class="print-question-head">
        <b>第 ${index} / ${total} 题</b>
        <span>${escapeHtml(q.type || "题目")}</span>
        <em>${escapeHtml(q.chapterName || "")}</em>
      </div>
      <div class="print-stem">${addLazyLoading(q.stem)}</div>
      ${q.extraQuestion ? `<div class="print-extra">${addLazyLoading(q.extraQuestion)}</div>` : ""}
      ${options}
    </article>
  `;
}

function renderPrintAnswerSection(questions) {
  return `
    <section class="print-answer-section">
      <h2>答案与解析</h2>
      ${questions.map((q, index) => `
        <article class="print-answer-item">
          <div class="print-answer-title">
            <b>第 ${index + 1} 题</b>
            <span>${escapeHtml(q.type || "题目")}</span>
            <em>正确答案：${escapeHtml(q.answer || "见解析")}</em>
          </div>
          ${q.description ? `<div class="print-description"><b>试题解析：</b>${addLazyLoading(q.description)}</div>` : ""}
        </article>
      `).join("")}
    </section>
  `;
}

function ensurePrintView() {
  let view = $("printView");
  if (!view) {
    // cleanupPrintView 会把整个节点移除；打印完不该在 DOM 里留一个空壳。
    view = document.createElement("section");
    view.id = "printView";
    view.className = "print-view hidden";
    document.body.appendChild(view);
  }
  return view;
}

function cleanupPrintView() {
  state.printAfterRender = false;
  document.body.classList.remove("print-mode");
  $("printView")?.remove();
}

function saveSubjectiveAnswer(q, answer) {
  /* 交卷后不能再改答案：成绩、答题卡、考试历史都在交卷那一刻定格了，
     旧实现却照样写 state.answers 并刷新选中态 —— 界面看着改了，分数不变，
     用户以为改对了分。练习模式不受影响（那边交卷后仍可改写并重判）。 */
  if (state.mode === "exam" && state.submitted) {
    toast("已交卷，不能再修改答案");
    return;
  }
  const previous = state.answers[q.id] || "";
  const wasFilled = !!normalizeAnswer(previous);
  const isFilled = !!normalizeAnswer(answer);
  state.answers[q.id] = answer;
  if (state.mode === "exam") {
    if (wasFilled !== isFilled) state.lastMarkedQuestionId = q.id;
    saveExamDraft();
    renderVerifyModeControls();
    renderAnswerCardPage({ updateNav: false });
    updateStats();
    return;
  }
  const courseStore = userCourseStore();
  const answerStore = currentPracticeAnswerStore();
  answerStore[q.id] = answer;
  courseStore.answers ||= {};
  courseStore.answers[q.id] = answer;
  /* 只有答案**真的变了**才撤销上一次的确认判定。
     旧实现无条件撤销：立即反馈下再点一次同一个选项（答案没变）也会先删掉
     verified 再重新判定 —— 于是「一次复习」被记成两次，stage 连跳两档。 */
  if (answer !== previous) resetQuestionVerification(q.id);
  if (normalizeAnswer(answer)) courseStore.done[q.id] = true;
  else delete courseStore.done[q.id];
  if (wasFilled !== isFilled) state.lastMarkedQuestionId = q.id;
  if (normalizeAnswer(answer)) recordPracticeActivity(q);
  if (state.submitted && normalizeAnswer(answer) && !isSubjective(q)) markResult(q);
  if (shouldAutoVerifyInstant(q) && normalizeAnswer(answer)) {
    verifyQuestionResult(q);
    state.answerVisible = true;
  }
  scheduleSave();
  scheduleChapterRefresh();
  renderVerifyModeControls();
  renderAnswerCardPage({ updateNav: false });
  updateStats();
}

function chooseOption(q, label) {
  /* 同 saveSubjectiveAnswer：交卷后考场里的选项必须冻结（实测 B→A 能改选中态，
     但 score 不变、答题卡和历史都不动）。 */
  if (state.mode === "exam" && state.submitted) {
    toast("已交卷，不能再修改答案");
    return;
  }
  const previous = state.answers[q.id] || "";
  const selected = new Set((state.answers[q.id] || "").split("").filter(Boolean));
  const multi = isMultiChoice(q);
  if (!multi) selected.clear();
  selected.has(label) ? selected.delete(label) : selected.add(label);
  const answer = [...selected].sort().join("");
  state.answers[q.id] = answer;
  if (state.mode === "exam") {
    if (answer !== previous) state.lastMarkedQuestionId = q.id;
    saveExamDraft();
    renderQuestion();
    return;
  }
  const courseStore = userCourseStore();
  const answerStore = currentPracticeAnswerStore();
  answerStore[q.id] = answer;
  courseStore.answers ||= {};
  courseStore.answers[q.id] = answer;
  /* 同 saveSubjectiveAnswer：答案没变就不撤销确认（多选题取消再选回同一个、
     或单选重复点同一项都会被当成「没变」）—— 否则同一份答案会被判两次。 */
  if (answer !== previous) resetQuestionVerification(q.id);
  if (answer) courseStore.done[q.id] = true;
  else delete courseStore.done[q.id];
  if (answer !== previous) state.lastMarkedQuestionId = q.id;
  if (answer) recordPracticeActivity(q);
  if (state.submitted && state.answers[q.id]) markResult(q);
  if (answer && shouldAutoVerifyInstant(q)) {
    verifyQuestionResult(q);
    state.answerVisible = true;
  }
  scheduleSave();
  scheduleChapterRefresh();
  renderQuestion();
}

/* ── 判定记账表：SRS 的幂等键 ────────────────────────────────────────────
   markResult 会推进 SRS（stage±1 / count±1），而它被三条入口反复调到：
   「点选项 → 立即反馈自动判定」「点确认答案」「提交答卷」。同一次判定被记
   多遍，一次复习就能跨好几档、甚至直接毕业。实测（改前）：
     · 立即反馈里连点两次同一个 A（答案没变）→ stage 0→1→2、count 3→2→1
     · 练习交卷后改选错项 → chooseOption 里两次 markResult → count 4→6
     · 练习交卷后再点一次「提交答卷」→ stage 3→4、history 1→2

   幂等键 = {scopeId, qid, answerSnapshot}：
     · scopeId —— 「本轮作答作用域」。有训练会话（智能训练 / 错题训练）时是
       `session:<会话 id>`（续做、刷新都还在同一轮）；没有会话（练习 / 收藏 /
       错题本浏览 / 考场）时是 `round:<模式>:<科目>:<日期>`。
       为什么要带它：SRS 的档位间隔是 [1,2,4,7,15,30] 天，**同一天里**对同一道题
       重复同一个答案不构成新的复习事件；跨天（或换到另一轮训练）再答对才算。
       不带 scope 的话「一道题一辈子只推进一次」，SRS 就等于废掉。
     · qid —— 每题只留**最近一次已记账**的判定。
     · answerSnapshot —— 改选 = 新快照 = **新的练习事件**（用户确实重新作答了，
       对错与 SRS 都该跟着变）；同一个快照在同一轮里判定多少次都只记一次。

   为什么落盘（courseStore.resultLog）而不是只放内存：练习交卷 → 刷新 → 再交卷
   这条路径上内存表已经没了，而 verified 记录会被 resetQuestionVerification 删掉，
   靠它当账本挡不住重复交卷。 */
const RESULT_LOG_LIMIT = 2000;

function currentAnswerScopeId(courseId = state.currentCourse?.id) {
  const session = activeScopedAnswerSession();
  if (session?.id) return `session:${session.id}`;
  return `round:${state.mode || "practice"}:${Number(courseId || 0)}:${todayKey()}`;
}

/* 这个 {scope, 题号, 答案快照} 是不是已经记过账了（记过就不再推进 SRS）。 */
function resultAlreadyRecorded(courseStore, q, scopeId) {
  const entry = courseStore?.resultLog?.[q.id];
  if (!entry) return false;
  return entry.scope === scopeId && entry.answer === currentAnswerSnapshot(q.id);
}

function recordResultEntry(courseStore, q, scopeId) {
  if (!courseStore) return;
  courseStore.resultLog ||= {};
  courseStore.resultLog[q.id] = { scope: scopeId, answer: currentAnswerSnapshot(q.id), at: nowText() };
  trimResultLog(courseStore);
}

/* 账本每题一条，正常上限就是科目题量；但 round scope 里带日期，换天/换模式会
   不断产生新条目，所以还是设个上限：超了就把最老的裁掉（单轮训练远达不到这个量级，
   被裁掉的只会是很久以前、不会再被判定的轮次）。 */
function trimResultLog(courseStore) {
  const log = courseStore?.resultLog;
  if (!log) return;
  const keys = Object.keys(log);
  if (keys.length <= RESULT_LOG_LIMIT) return;
  keys.sort((a, b) => String(log[a]?.at || "").localeCompare(String(log[b]?.at || "")));
  keys.slice(0, keys.length - RESULT_LOG_LIMIT).forEach((id) => delete log[id]);
}

function markResult(q) {
  const courseStore = userCourseStore();
  const isRight = isAnswerCorrect(q);
  /* 幂等：本轮（scopeId）里这道题的这个答案快照只记一次账（见上面的注释）。 */
  const scopeId = currentAnswerScopeId();
  const logged = resultAlreadyRecorded(courseStore, q, scopeId);
  /* 会话作用域的题单（智能训练 / 错题训练）：对错记进会话自己的 results，
     否则 syncTrainingSessionStats 从会话里数不到正确率（错题训练会恒显示 0%）。 */
  const session = activeScopedAnswerSession();
  if (session) {
    session.results[q.id] = {
      answer: currentAnswerSnapshot(q.id),
      correct: isRight,
      at: nowText(),
    };
  }
  if (isRight) {
    /* 这道题在答对**之前**是否属于本课程的错题本 —— 下面 `delete courseStore.wrong[q.id]`
       之后就看不出来了。非错题训练模式靠它判断「要不要给这道题做 SRS 推进」：
       不在错题本里的题（新题顺手做对）绝不能被写进错题本。 */
    const wasWrongTracked = !!courseStore.wrong?.[q.id];
    courseStore.correct[q.id] = true;
    delete courseStore.wrong[q.id];
    /* `logged` = 本轮这道题的这个答案快照**已经推进过 SRS** 了：
       下面的「在不在错题本里」照旧同步（那是幂等的），但复习档位不再推第二次。 */
    if (state.mode === "wrong") {
      if (logged) {
        /* 已记账：只把归属摆正（courseStore.wrong 上面已经删过了），不动 stage/count。 */
        if (state.storage.wrong[q.id]?.resolved) delete courseStore.wrong[q.id];
        else if (state.storage.wrong[q.id]) courseStore.wrong[q.id] = true;
      } else {
        const record = ensureWrongReviewFields(state.storage.wrong[q.id] || {}, q);
        record.courseId ||= state.currentCourse?.id;
        record.lastReviewAt = nowText();
        record.stage = Math.min(REVIEW_INTERVAL_DAYS.length - 1, reviewStage(record) + 1);
        record.resolved = record.stage >= REVIEW_INTERVAL_DAYS.length - 1;
        record.resolvedAt = record.resolved ? nowText() : "";
        /* 答对一次就减一次「错了几次」的计数。count 只增不减的话 count≥2 会永久成立，
           选题时永远把它当反复错处理。 */
        record.count = record.resolved ? 1 : Math.max(1, Number(record.count || 1) - 1);
        state.storage.wrong[q.id] = record;
        if (record.resolved) delete courseStore.wrong[q.id];
        else courseStore.wrong[q.id] = true;
      }
    } else if (wasWrongTracked || state.storage.wrong[q.id]) {
      /* 练习 / 智能训练 / 考场答对**错题本里的题**：与错题训练走同一套 SRS 推进
         （stage+1、练满 5 档才 resolved、count 递减、lastReviewAt 刷新）。
         旧实现这条分支是无条件 `resolved = true` 直接毕业，而且 stage 完全不动 ——
         用户只是想在练习里做一道题、顺手做对了，这道题就从错题本里消失、
         [1,2,4,7,15,30] 整条复习曲线被跳掉，看起来像「错题被吃掉了」。
         唯一的例外是**已经毕业**的记录：它不因为一次答对又倒回「未解决」重进错题本。
         必须留这个例外 —— 旧数据里大量存在 resolved=true 但 stage<5 的记录
         （正是旧版这条分支写出来的：毕业时不动 stage），直接套 SRS 会把它们全部打回错题本。 */
      const record = ensureWrongReviewFields(state.storage.wrong[q.id] || {}, q);
      if (logged) {
        /* 已记账：不改档位/计数，只补 courseId（老记录缺它会被错题本丢掉）。 */
        record.courseId ||= state.currentCourse?.id;
        state.storage.wrong[q.id] = record;
        if (record.resolved) delete courseStore.wrong[q.id];
        else courseStore.wrong[q.id] = true;
      } else {
        record.courseId ||= state.currentCourse?.id;
        const alreadyResolved = !!record.resolved;
        record.lastReviewAt = nowText();
        record.stage = Math.min(REVIEW_INTERVAL_DAYS.length - 1, reviewStage(record) + 1);
        record.resolved = alreadyResolved || record.stage >= REVIEW_INTERVAL_DAYS.length - 1;
        record.resolvedAt = record.resolved ? (record.resolvedAt || nowText()) : "";
        /* 同错题训练：答对一次减一次「错了几次」。 */
        record.count = record.resolved ? 1 : Math.max(1, Number(record.count || 1) - 1);
        state.storage.wrong[q.id] = record;
        if (record.resolved) delete courseStore.wrong[q.id];
        else courseStore.wrong[q.id] = true;
      }
    } else {
      /* 从来没错过的题：答对只是「答对」，不产生错题记录。 */
      delete courseStore.wrong[q.id];
    }
  } else {
    delete courseStore.correct[q.id];
    courseStore.wrong[q.id] = true;
    const record = ensureWrongReviewFields(state.storage.wrong[q.id] || {}, q);
    /* 落盘前补上 courseId：缺了它，下一次读错题本时这条记录会被旧版的严格相等过滤丢掉，
       只能从 courseStore.wrong 合成 stub，stage/count/lastReviewAt 全丢 → 永远不到期。 */
    record.courseId ||= state.currentCourse?.id;
    if (logged) {
      /* 已记账：同一个答案快照不再加一次「错了几次」。 */
      state.storage.wrong[q.id] = record;
    } else {
      const wasResolved = !!record.resolved;
      record.at = nowText();
      record.wrongAt = nowText();
      record.lastReviewAt = nowText();
      /* 答错回退一档，不清零：旧实现 stage = 0 会把已经练到 15 天间隔的题一次手滑打回
         1 天间隔，等于前面的复习全部作废。真正「毕业」过的题（resolved）才重新从 0 开始。 */
      record.stage = wasResolved ? 0 : Math.max(0, reviewStage(record) - 1);
      record.count = Number(record.count || 0) + 1;
      record.resolved = false;
      record.resolvedAt = "";
      state.storage.wrong[q.id] = record;
    }
  }
  /* 记账：只有真的推进过（或本来就没记过）才写 ---- 写进去的是这一轮的幂等依据。 */
  if (!logged) recordResultEntry(courseStore, q, scopeId);
  syncCurrentTrainingSession(q.id);
}

function recordPracticeActivity(q, options = {}) {
  if (!q || state.mode === "exam" || isAdmin()) return;
  const key = todayKey();
  state.storage.dailyActivity ||= {};
  const day = state.storage.dailyActivity[key] ||= { courses: {}, total: 0, correct: 0, verified: 0, updatedAt: nowText() };
  day.courses ||= {};
  const courseId = String(q.courseId || state.currentCourse?.id || "global");
  const course = day.courses[courseId] ||= {
    courseId: Number(q.courseId || state.currentCourse?.id || 0),
    courseName: state.currentCourse?.name || "",
    questions: {},
    total: 0,
    correct: 0,
    verified: 0,
  };
  course.questions ||= {};
  /* 学习时长：按「相邻两次答题的间隔」累计，单次上限 5 分钟
     （页面挂着不动不该算成学习时间）。lastActivityAt 是内存值，刷新后从 0 开始，
     所以刷新后的第一次答题不计时长。 */
  const nowMs = Date.now();
  const gap = state.lastActivityAt ? nowMs - state.lastActivityAt : 0;
  state.lastActivityAt = nowMs;
  if (gap > 0) {
    const add = Math.min(gap, 5 * 60000);
    day.durationMs = Number(day.durationMs || 0) + add;
    course.durationMs = Number(course.durationMs || 0) + add;
  }
  const id = String(q.id);
  const previous = course.questions[id] || {};
  const answer = currentAnswerSnapshot(q.id);
  course.questions[id] = {
    id: q.id,
    chapterId: q.chapterId,
    chapterName: q.chapterName,
    type: q.type,
    answer,
    answeredAt: previous.answeredAt || nowText(),
    verifiedAt: options.verified ? nowText() : previous.verifiedAt || "",
    correct: options.verified && !isSubjective(q) ? (hasAnswer(q.id) && isAnswerCorrect(q)) : previous.correct,
    subjective: isSubjective(q),
  };
  recomputeDailyActivity(day);
  trimDailyActivity();
  day.updatedAt = nowText();
  syncCurrentTrainingSession(q.id);
}

function recomputeDailyActivity(day) {
  let total = 0;
  let correct = 0;
  let verified = 0;
  Object.values(day.courses || {}).forEach((course) => {
    const rows = Object.values(course.questions || {});
    const scoredRows = rows.filter((item) => !item.subjective);
    course.total = rows.length;
    course.correct = scoredRows.filter((item) => item.correct === true).length;
    course.verified = scoredRows.filter((item) => item.verifiedAt).length;
    total += course.total;
    correct += course.correct;
    verified += course.verified;
  });
  day.total = total;
  day.correct = correct;
  day.verified = verified;
}

function trimDailyActivity() {
  const entries = Object.entries(state.storage.dailyActivity || {}).sort((a, b) => b[0].localeCompare(a[0]));
  state.storage.dailyActivity = Object.fromEntries(entries.slice(0, 90));
}

function renderAnswerCard() {
  $("answerCard").innerHTML = "";
  const total = state.questions.length;
  if (!total) return;
  const pageSize = state.answerCardPageSize;
  const activePage = Math.max(0, Math.floor(Math.max(0, state.currentIndex) / pageSize));
  if (activePage !== state.answerCardPage) state.answerCardPage = activePage;
  const pageCount = Math.ceil(total / pageSize);
  const pageNav = document.createElement("div");
  pageNav.className = "card-page-nav";
  renderAnswerCardNav(pageNav, pageCount);
  const pageContent = document.createElement("div");
  pageContent.id = "cardPageContent";
  pageContent.className = "card-page-content";
  $("answerCard").appendChild(pageNav);
  $("answerCard").appendChild(pageContent);
  renderAnswerCardPage({ updateNav: false });
}

function renderAnswerCardNav(container, pageCount) {
  container.innerHTML = "";
  if (!pageCount) return;
  const current = Math.min(pageCount - 1, Math.max(0, state.answerCardPage));
  const isMobile = window.matchMedia("(max-width: 760px)").matches;
  addCardPageButton(container, Math.max(0, current - 1), "上一页", current === 0);
  if (isMobile) {
    addCardPageButton(container, current, `第 ${current + 1} / ${pageCount} 页`, true);
    addCardPageButton(container, Math.min(pageCount - 1, current + 1), "下一页", current >= pageCount - 1);
    return;
  }
  if (pageCount <= 9) {
    for (let page = 0; page < pageCount; page++) addCardPageButton(container, page);
  } else {
    const pages = new Set([0, pageCount - 1]);
    for (let page = Math.max(0, current - 2); page <= Math.min(pageCount - 1, current + 2); page++) pages.add(page);
    [...pages].sort((a, b) => a - b).forEach((page, index, arr) => {
      if (index && page - arr[index - 1] > 1) {
        const gap = document.createElement("span");
        gap.className = "card-page-gap";
        gap.textContent = "...";
        container.appendChild(gap);
      }
      addCardPageButton(container, page);
    });
  }
  addCardPageButton(container, Math.min(pageCount - 1, current + 1), "下一页", current >= pageCount - 1);
}

function addCardPageButton(container, page, label, disabled = false) {
  const start = page * state.answerCardPageSize + 1;
  const end = Math.min(state.questions.length, (page + 1) * state.answerCardPageSize);
  const btn = document.createElement("button");
  btn.type = "button";
  const isRangeButton = !label;
  const isStatusLabel = !!label && disabled && /^第\s*\d+\s*\//.test(label);
  btn.className = "card-page-btn"
    + (isRangeButton && page === state.answerCardPage ? " active" : "")
    + (isStatusLabel ? " page-status" : "");
  btn.dataset.page = page;
  btn.dataset.role = isRangeButton ? "range" : isStatusLabel ? "status" : "nav";
  btn.textContent = label || `${start}-${end}`;
  btn.disabled = disabled;
  btn.onclick = () => {
    state.answerCardPage = page;
    renderAnswerCardPage();
  };
  container.appendChild(btn);
}

/* 答题卡格子与统计行的**唯一**判据。为什么要有它：
   格子按「已作答」染色（黄），统计行却数「有对错结果」（correct + wrong）——
   统一验证 / 模拟考场里答完一题，格子黄了、统计行还写「已完成 0 题」，
   两个数字当着用户的面打架。现在两处都调它，不可能再分叉。

   返回值 → 格子类名：
     "correct" 绿（判定过，对）｜ "wrong" 红（判定过，错）
     "done"    黄（已作答、还没判定：统一验证未交卷 / 考场中）
     "todo"    中性（交卷了但这题没作答 —— 扫漏题时能和「做错」区分开）
     "none"    中性（没答过）
   统计行口径：「已作答」= correct / wrong / done；todo 与 none 都算「未作答」。

   判定**优先用已落盘的记录**，不要靠 item.detail：
     · courseStore.correct / wrong —— 本课程
     · state.storage.wrong[qid]    —— 错题本（跨课程，resolved 表示已经练会）
   item.detail 只有当前题才有，用 isAnswerCorrect(item.detail) 判的话，
   「已作答但 detail 没加载」的题一个类都拿不到 → 格子上显示成「未做」
   （用户报的「已做的题在答题卡上全是未做」就是这个原因）。

   ⚠️ 错题训练进行中（wrongSession）例外：「在错题本里」对本轮毫无信息量 ——
   整份题单都是错题，按它染就是全红。这一轮改看**本轮**的结果：
   本轮答对=绿、本轮答错=红、本轮没答=中性。 */
function answerCardCellState(item, courseStore = userCourseStore(), wrongSession = activeWrongPracticeSession()) {
  const wrongRecord = state.storage.wrong?.[item.id];
  const roundResult = wrongSession?.results?.[item.id];
  const roundAnswered = !!normalizeAnswer(wrongSession?.answers?.[item.id]);
  /* 会话 results 里既有对象 {answer, correct, at}，也可能有迁移期留下的布尔值
     （口径与 syncTrainingSessionStats 一致，别只认一种）。 */
  const recordedRight = wrongSession
    ? (roundResult === true || roundResult?.correct === true)
    : (!!courseStore.correct?.[item.id] || !!wrongRecord?.resolved);
  const recordedWrong = wrongSession
    ? (roundResult === false || roundResult?.correct === false)
    : (!!courseStore.wrong?.[item.id] || (!!wrongRecord && !wrongRecord.resolved));
  const answered = wrongSession ? roundAnswered : hasAnswer(item.id);
  if (state.submitted && !answered) return "todo";
  if (recordedRight) return "correct";
  if (recordedWrong) return "wrong";
  return answered ? "done" : "none";
}

function renderAnswerCardPage(options = {}) {
  const updateNav = options.updateNav !== false;
  const content = $("cardPageContent");
  if (!content) return;
  const pageCount = Math.ceil(state.questions.length / state.answerCardPageSize);
  const nav = document.querySelector(".card-page-nav");
  if (nav && updateNav) renderAnswerCardNav(nav, pageCount);
  if (nav && !updateNav) {
    nav.querySelectorAll(".card-page-btn").forEach((el) => {
      el.classList.toggle("active", el.dataset.role === "range" && Number(el.dataset.page) === state.answerCardPage);
    });
  }
  content.innerHTML = "";
  const pageSize = state.answerCardPageSize;
  const startIndex = state.answerCardPage * pageSize;
  const endIndex = Math.min(state.questions.length, startIndex + pageSize);
  const matched = new Set(getSearchMatchIndexes());
  const courseStore = userCourseStore();
  /* 错题训练进行中（会话作用域，见 activeWrongPracticeSession）：答题卡按**本轮**的
     作答与判定染色。判据必须是本轮的 answers / results —— 「这题在错题本里」不等于
     「本轮答错了」，按后者染会让整张卡一进训练就全红，用户以为全做错了。
     翻看错题本（mode === "wrong" 但没有训练会话）/ 普通练习 / 智能训练 / 考场维持原样：
     那些场景「这题是错题 / 之前做错过」本身就是要表达的信息。 */
  const wrongSession = activeWrongPracticeSession();
  for (let index = startIndex; index < endIndex; index++) {
    const item = state.questions[index];
    const btn = document.createElement("button");
    btn.textContent = index + 1;
    btn.className = "card-cell";
    btn.dataset.questionId = item.id;
    if (index === state.currentIndex) btn.classList.add("current");
    if (Number(item.id) === Number(state.lastMarkedQuestionId)) btn.classList.add("just-marked");
    if (matched.has(index)) btn.classList.add("search-match");
    /* 染色判据在 answerCardCellState 里，和统计行（updateStats）**同一个函数** ——
       见那边的注释。这里只负责把状态翻成类名。 */
    const cellState = answerCardCellState(item, courseStore, wrongSession);
    if (cellState !== "none") btn.classList.add(cellState);
    btn.onclick = async () => {
      const transitionId = (state.modeTransitionId || 0) + 1;
      state.modeTransitionId = transitionId;
      state.currentIndex = index;
      state.answerCardPage = Math.floor(index / state.answerCardPageSize);
      renderAnswerCardPage({ updateNav: false });
      await loadCurrentQuestion().catch((err) => toast(err.message));
      if (transitionId !== state.modeTransitionId) return;
    };
    content.appendChild(btn);
  }
  if (state.lastMarkedQuestionId) {
    clearTimeout(renderAnswerCardPage.markTimer);
    renderAnswerCardPage.markTimer = setTimeout(() => {
      document.querySelectorAll(".card-cell.just-marked").forEach((el) => el.classList.remove("just-marked"));
    }, 380);
    state.lastMarkedQuestionId = 0;
  }
  content.querySelector(".card-cell.current")?.scrollIntoView({ block: "nearest", inline: "nearest" });
}

function updateStats() {
  const total = state.questions.length;
  const courseStore = userCourseStore();
  /* 口径与答题卡格子完全一致 —— 两边都调 answerCardCellState（见那边的注释）。
     旧实现数的是「有对错结果」（correct + wrong），而格子按「已作答」染色：
     统一验证 / 考场里答完一题，格子黄了、这里还写「已完成 0 题」。
     文案也一起改成「已作答 / 未作答」：旧文案写「已完成」却按「答过」算，
     用户按字面理解成「做完且判过对错」，怎么数都对不上。 */
  const wrongSession = activeWrongPracticeSession();
  const answered = state.questions
    .filter((q) => {
      const cell = answerCardCellState(q, courseStore, wrongSession);
      return cell !== "none" && cell !== "todo";
    })
    .length;
  setText("totalCount", total);
  setText("doneCount", answered);
  setText("todoCount", Math.max(0, total - answered));
  // 答题卡收起时那条细栏显示进度（原先只显示「答题卡已收起」，白占一行高度）
  const card = document.querySelector(".answer-card-wrap");
  if (card) card.dataset.summary = total ? `已作答 ${answered} / ${total} 题` : "答题卡";
  updateWrongBadge();
}

/* 刷新后能安全回到的导航模式（P-9 / S8）。
   考试有草稿机制、智能训练有会话状态，各有专门的恢复入口，不走这条路径。 */
const RESTORABLE_MODES = ["practice", "wrong", "favorite", "progress", "training"];

function persistLastMode() {
  if (!state.user || isAdmin() || !state.storage.settings) return;
  const next = RESTORABLE_MODES.includes(state.mode) ? state.mode : "";
  if ((state.storage.settings.lastMode || "") === next) return;
  state.storage.settings.lastMode = next;
  scheduleSave();
}

function renderMode() {
  const visualMode = state.mode === "smart" ? "training" : state.mode;
  document.querySelectorAll(".nav-item[data-mode]").forEach((btn) => {
    btn.classList.toggle("active", btn.dataset.mode === visualMode);
  });
  document.body.dataset.mode = state.mode;
  scheduleNavIndicator();
  renderVerifyModeControls();
  persistLastMode();
}

function ensureNavIndicator() {
  const nav = document.querySelector(".main-nav");
  if (!nav) return null;
  let indicator = nav.querySelector(".nav-indicator");
  if (!indicator) {
    indicator = document.createElement("span");
    indicator.className = "nav-indicator";
    indicator.setAttribute("aria-hidden", "true");
    nav.appendChild(indicator);
  }
  return indicator;
}

function updateNavIndicator() {
  const nav = document.querySelector(".main-nav");
  const indicator = ensureNavIndicator();
  if (!nav || !indicator || document.body.classList.contains("admin-view")) {
    indicator?.classList.remove("ready");
    return;
  }
  const active = nav.querySelector(".nav-item.active");
  if (!active || active.offsetParent === null) {
    indicator.classList.remove("ready");
    return;
  }
  const navRect = nav.getBoundingClientRect();
  const activeRect = active.getBoundingClientRect();
  indicator.style.setProperty("--nav-indicator-x", `${activeRect.left - navRect.left + 10}px`);
  indicator.style.setProperty("--nav-indicator-y", `${activeRect.top - navRect.top + activeRect.height - 7}px`);
  indicator.style.setProperty("--nav-indicator-w", `${Math.max(24, activeRect.width - 20)}px`);
  indicator.classList.add("ready");
}

function scheduleNavIndicator() {
  cancelAnimationFrame(scheduleNavIndicator.raf);
  scheduleNavIndicator.raf = requestAnimationFrame(updateNavIndicator);
}

/* 错题本训练（session.type === "wrong"）期间强制「立即反馈」。
   题池全是**已经做错过的题**，这一轮的目的就是当场把它们做对一遍 ——
   统一验证下点选项没有任何反馈，等于白练（用户点完不知道对错，也不会重看解析）。
   边界只看「当前是不是那条错题训练会话」，不改「今日错题复习」等其它 wrong 会话的行为。
   ⚠️ 只影响运行时 state.verifyMode（由 renderVerifyModeControls 写入），
   **不写** state.storage.settings.verifyMode —— 那是用户偏好、会同步到服务器，
   见上面考试那条注释踩过的坑。 */
function isWrongBookTrainingSession() {
  if (state.mode !== "wrong") return false;
  const session = activeTrainingSession();
  return !!session
    && session.type === "wrong"
    && session.mode === "wrong"
    && Number(session.courseId || 0) === Number(state.currentCourse?.id || 0);
}

function currentVerifyMode() {
  /* 考试期间一律按「交卷后统一验证」处理 —— 由**模式**决定，而不是去改用户的偏好。
     旧实现在 startExamPaper 里把 settings.verifyMode 改成 "paper" 并 scheduleSave()，
     但全文件没有任何地方恢复 → 用户考一次试后，之后**所有练习**被永久改成「统一验证」，
     而且已经同步到服务器（换设备也是错的）。见 docs/VERSIONS.md。 */
  if (state.mode === "exam") return "paper";
  if (isWrongBookTrainingSession()) return "instant";
  const stored = state.storage.settings?.verifyMode;
  if (stored === "single") return "instant";
  return ["paper", "instant", "review"].includes(stored) ? stored : "paper";
}

function isQuestionVerified(questionId) {
  if (state.mode === "exam") return false;
  const verified = currentPracticeVerifiedStore()[questionId];
  if (!verified) return false;
  if (verified === true) return true;
  if (typeof verified === "object" && "answer" in verified) {
    return verified.answer === currentAnswerSnapshot(questionId);
  }
  return false;
}

function shouldRevealCurrentAnswer(q) {
  /* 显式覆盖位优先：用户在背题模式/已判定题目上点了「隐藏答案」，
     这条规则（恒真）不能再把答案顶回来 —— 否则按钮按了没用。 */
  if (state.answerHiddenOverride) return false;
  return !!(state.answerVisible || state.submitted || currentVerifyMode() === "review" || isQuestionVerified(q?.id));
}

/* 组卷进行中：工具栏的「提交答卷」要禁掉。
   这段窗口里 state.exam 还是 null（renderExamHome 刚置空）、state.questions 也是空，
   点交卷本来就没有对象 —— 旧实现在这里做出了一张 0/0 分的假成绩单。 */
function isExamGenerating() {
  if (state.mode !== "exam" || !state.examGenerating) return false;
  /* 组卷是**按科目**进行的：挂在科目 1 的那次组卷已经不会再写 state 了
     （见 beginExamFlow），它的标记只管科目 1 —— 否则切到科目 2 后
     「开始组卷」和「交卷」会被一个跟自己无关的旧请求一直禁用着。 */
  return !state.examGeneratingCourseId
    || Number(state.examGeneratingCourseId) === Number(state.currentCourse?.id || 0);
}

/* 「离开页面时暂停计时」是用户选项，默认关 = 真限时。
   默认状态下 visibilitychange / beforeunload / 切模块**都不调** pauseExamTimer：
   草稿里存的是 endsAt，续考时按真实时间算剩余（见 resumeExamDraft），
   所以切标签页、刷新、切模块时间都照走，到点照样自动交卷。
   旧实现无条件暂停 → 「150 分钟」是假的：切走一次就能无限暂停。 */
function pauseExamOnLeave() {
  return !!state.storage.settings?.pauseExamOnLeave;
}

/* 「离开考场」的唯一实现。切模块 / 切到后台 / 刷新关页三处做的是同一件事：
   勾了「离开页面时暂停计时」才暂停，然后存草稿。**先暂停再存草稿** ——
   顺序反了草稿里就没有 pausedAt，续考时会把离开的这段时间也算成考试时间。
   默认（未勾）时间照走：草稿存的是 endsAt，续考按真实时间算剩余。
   （「回到前台」那条不能并进来：它要 resumeExamTimer / syncExamTimer。） */
function suspendExam(reason) {
  if (state.mode !== "exam" || !state.exam || state.submitted) return false;
  if (pauseExamOnLeave()) pauseExamTimer(reason);
  saveExamDraft();
  return true;
}

function renderVerifyModeControls() {
  state.verifyMode = currentVerifyMode();
  const isExam = state.mode === "exam";
  const modeLabels = {
    paper: "统一验证",
    instant: "立即反馈",
    review: "背题模式",
  };
  const modeBtn = $("verifyModeBtn");
  if (modeBtn) {
    /* 错题训练期间验证方式是强制的：按钮藏起来（留着能点但点了没效果，比藏起来更糟）。 */
    const forced = isWrongBookTrainingSession();
    modeBtn.classList.toggle("hidden", isExam || isAdmin() || forced);
    modeBtn.classList.toggle("active", state.verifyMode !== "paper");
    modeBtn.textContent = modeLabels[state.verifyMode] || modeLabels.paper;
    modeBtn.setAttribute("aria-pressed", String(state.verifyMode !== "paper"));
    modeBtn.title = {
      paper: "考试式练习：提交答卷后统一显示对错",
      instant: "立即反馈：单选/判断选择后反馈，多选选完后点确认答案",
      review: "背题模式：先看答案，再做信心、错因和笔记标记",
    }[state.verifyMode] || "";
  }
  const confirmBtn = $("confirmAnswerBtn");
  if (confirmBtn) {
    const q = state.questions[state.currentIndex]?.detail;
    const needsConfirm = !!q && state.verifyMode === "instant" && !isExam && !isAdmin() && (isMultiChoice(q) || isSubjective(q));
    const verified = needsConfirm && isQuestionVerified(q.id);
    confirmBtn.classList.toggle("hidden", !needsConfirm);
    confirmBtn.disabled = !!verified;
    confirmBtn.textContent = verified ? "已确认" : "确认答案";
    confirmBtn.title = isSubjective(q)
      ? "确认后显示参考答案，主观题需人工核对"
      : "选完全部答案后确认并显示结果";
  }
  const submitBtn = $("submitBtn");
  if (submitBtn) {
    const generating = isExamGenerating();
    /* 两个分支的文案必须真的不同：旧实现写成 `exam ? "提交答卷" : "提交答卷"`，
       三元毫无意义。考场里那个按钮的动作是**交卷**（confirm 弹的就是「确定交卷吗」），
       练习里是「提交答卷」。 */
    submitBtn.textContent = state.mode === "exam" ? "交卷" : "提交答卷";
    submitBtn.title = generating
      ? "正在组卷，请稍候"
      : state.mode === "exam"
        ? "交卷并计算模拟考试成绩"
        : "统一验证本次练习中的所有题目";
    submitBtn.disabled = !!state.submitting || generating;
  }
  /* 「清空本题单」在考场里清的不是本题单，是整场考试（resetPractice 的 exam 分支会
     清掉草稿、整卷作答并回到组卷页）。文案/title 必须说实话，交卷后再禁用 ——
     那时已经没有什么可放弃的了。 */
  const resetBtn = $("resetBtn");
  if (resetBtn) {
    const examRunning = state.mode === "exam";
    resetBtn.textContent = examRunning ? "放弃本场考试" : "清空本题单";
    resetBtn.title = examRunning
      ? "放弃本场考试：本场已答内容会全部清除，不计入成绩，且不可恢复"
      : "仅清空当前题单作答，不影响其他章节";
    resetBtn.disabled = examRunning && state.submitted;
  }
  /* ── 按「本页有没有题单」收敛工具栏（UI-08）──────────────────────────
     训练/看板是任务页：state.questions 恒为空，没有「本题单」这个对象。
     旧实现只按「提交中 / 组卷中」禁用，于是 training 上「显示答案 / 清空本题单 /
     统一验证 / 提交答卷 / 打印」全都可点（实测 questions=0 时 5 个按钮全亮）——
     用户很容易把「提交答卷」当成「提交今日训练计划」。
     现在：
       · 显示答案 / 清空本题单 / 提交答卷 / 统一验证 / 打印 —— 只对题单有意义，
         有题单才展示（题单读取失败但留着旧内容时也算「没有可信题单」，见 UI-05）；
       · 试题筛选 —— 只在列表模式（题库/错题/收藏/考场）展示：收藏页要靠它筛题型；
         训练/看板点开的是一个和本页无关的面板；
       · 放大 / 缩小 / 全屏 —— 显示设置，任何页面都保留。
     用 .hidden 而不是 disabled：一排点不动的按钮同样是噪声，而且读取失败时
     该点的是顶部横幅的「重试读取」，不是交卷。
     取舍：没有给训练/看板**新加**工具栏动作 —— 这两页自己的动作已经在 hero、
     任务卡和 .training-tools 里，工具栏再放一套只会出现两个入口。 */
  const listMode = ["practice", "wrong", "favorite", "exam"].includes(state.mode) && !isAdmin();
  const hasList = listMode
    && (state.questions.length > 0 || (state.mode === "exam" && !!state.exam))
    && !state.questionListStale;
  ["answerToggleBtn", "resetBtn", "submitBtn", "verifyModeBtn", "printBtn"].forEach((id) => {
    $(id)?.classList.toggle("hidden", !hasList);
  });
  $("filterBtn")?.classList.toggle("hidden", !listMode);
  /* 筛选面板只由这个按钮开合：按钮在训练/看板被收起来时，面板也必须一起关，
     否则会留一个「打不开也关不掉」的面板在任务页上。 */
  if (!listMode) $("searchPanel")?.classList.add("hidden");
}

function toggleVerifyMode() {
  state.storage.settings ||= {};
  const modes = ["paper", "instant", "review"];
  const current = currentVerifyMode();
  const next = modes[(Math.max(0, modes.indexOf(current)) + 1) % modes.length];
  state.storage.settings.verifyMode = next;
  state.verifyMode = next;
  if (next !== "review" && !state.submitted) state.answerVisible = false;
  state.answerHiddenOverride = false;   // 换验证方式 = 重新按新规则的默认值显示答案
  scheduleSave();
  if (state.questions[state.currentIndex]?.detail) renderQuestion();
  else renderVerifyModeControls();
  toast(`已切换为${{ paper: "统一验证", instant: "立即反馈", review: "背题模式" }[next]}`);
}

function changeZoom(delta) {
  state.zoom = normalizeZoom(Math.round((state.zoom + delta) * 10) / 10);
  state.storage.settings ||= {};
  state.storage.settings.zoom = state.zoom;
  scheduleSave();
  if (state.questions[state.currentIndex]?.detail) renderQuestion();
  else $("questionView")?.style.setProperty("--zoom", state.zoom);
}

function markQuestionVerified(q) {
  const courseStore = userCourseStore();
  courseStore.verified ||= {};
  const record = {
    answer: currentAnswerSnapshot(q.id),
    at: nowText(),
    /* 主观题标记：累计正确率只算客观题，主观题单列（见 confirmedSets）。
       老记录没有这个字段，读的时候按「判过客观题必然落 correct/wrong」兜底。 */
    subjective: isSubjective(q),
  };
  const verifiedStore = currentPracticeVerifiedStore();
  verifiedStore[q.id] = record;
  // Keep long-term verification for ordinary practice while the active smart
  // batch uses its own record to decide what it may reveal.
  if (verifiedStore !== courseStore.verified) courseStore.verified[q.id] = record;
}

function resetQuestionVerification(questionId) {
  if (state.mode === "exam" || !questionId) return;
  const courseStore = userCourseStore();
  const session = activeScopedAnswerSession();
  if (session) {
    delete session.verifiedAnswers[questionId];
    delete session.results[questionId];
  }
  if (courseStore.verified) delete courseStore.verified[questionId];
  if (!state.submitted) {
    delete courseStore.correct?.[questionId];
    state.answerVisible = false;
  }
}

function clearVerifiedForCourse() {
  if (!state.currentCourse) return;
  userCourseStore().verified = {};
}

function verifyQuestionResult(q) {
  markQuestionVerified(q);
  if (!isSubjective(q) && hasAnswer(q.id)) markResult(q);
  recordPracticeActivity(q, { verified: true });
}

function confirmCurrentAnswer() {
  const q = state.questions[state.currentIndex]?.detail;
  if (!q) return;
  if (!hasAnswer(q.id)) {
    toast("请先选择或填写答案");
    return;
  }
  verifyQuestionResult(q);
  state.answerVisible = true;
  scheduleSave();
  renderQuestion();
  if (isSubjective(q)) {
    toast("已显示参考答案，主观题请人工核对");
  } else {
    toast(isAnswerCorrect(q) ? "回答正确" : "回答错误，已加入错题");
  }
}

function renderProgress() {
  setPanelPage(true);
  const courseStore = userCourseStore();
  const stats = getCourseStats();
  const analysisItems = state.analysisQuestions.length ? state.analysisQuestions : state.questions;
  const wrongTotal = Object.keys(courseStore.wrong || {}).length;
  /* 收藏题只数**当前科目**——与同页的分组 chip、错题、题量同一范围。
     旧 KPI 数的是全部科目（实测 11），同屏 chip 只数本科目（3），
     两个数字都叫「收藏题」。 */
  const favTotal = favoriteIdsForCurrentCourse().length;
  const favAllCourses = Object.keys(state.storage.favorite || {}).length;
  const noteTotal = Object.keys(state.storage.notes || {}).filter((id) => state.storage.notes[id]).length;
  /* 正确率走 confirmedRate（唯一口径），标签也点明分母是「已确认」。 */
  const rate = confirmedRate(stats.correct, stats.verified);

  $("questionBody").classList.add("hidden");
  $("emptyState").classList.remove("hidden");
  $("emptyState").innerHTML = `
    <div class="analysis-panel">
      <div>
        <strong>学习看板</strong>
        <p>${escapeHtml(state.currentCourse?.name || "当前科目")} · 当前进度、学习情况与自我提升记录</p>
      </div>
      ${renderStudyDashboard(courseStore, stats, analysisItems)}
      <div class="analysis-grid">
        <div><b>${stats.done}</b><span>已做试题</span></div>
        <div title="${escapeHtml(rateKpiTitle(stats))}"><b>${rate}%</b><span>客观题正确率（已确认）</span></div>
        <div><b>${wrongTotal}</b><span>待解决错题</span></div>
        <div title="${escapeHtml(`当前科目 ${state.currentCourse?.name || ""} 的收藏；全部科目共 ${favAllCourses} 题`)}"><b>${favTotal}</b><span>收藏题（本科目）</span></div>
        ${/* 笔记记录里**没有** courseId（加字段要迁移，见产品决策），所以这一格
             统计的是**全部科目**的笔记。范围写进标题，和上一格「收藏题（本科目）」
             形成对照 —— 不说清会让人以为同一页的两个数字同范围。 */ ""}
        <div title="${escapeHtml(`笔记记录里没有科目字段，这一格是全部科目的笔记（共 ${noteTotal} 条）；同页其它数字都是当前科目 ${state.currentCourse?.name || ""}`)}"><b>${noteTotal}</b><span>笔记（全部科目）</span></div>
        <div><b>${analysisItems.length}</b><span>当前科目题量</span></div>
      </div>
      <div class="trend-panel">
        ${/* 范围写明：曲线吃的是 storage.history（每条 = 一次交卷）+ 考场卷按
             ref 回 examHistory 取数（考试记录的唯一事实来源），分母是「本次已作答」，
             和 KPI「正确率（已确认）」的累计口径不是一回事 —— 标题里点明。 */ ""}
        <div class="trend-title">近期正确率趋势（每次交卷，按本次已作答）</div>
        <canvas id="progressTrend" width="680" height="180"></canvas>
      </div>
      ${renderTrainingHistoryPanel(courseStore)}
      ${renderLearningSignalPanel(courseStore)}
      ${renderDeepAnalysis(courseStore, analysisItems)}
      ${renderReviewPlanPanel(stats, wrongTotal, analysisItems)}
    </div>
  `;
  renderProgressTrend();
  bindStudyDashboardActions(courseStore);
  bindTrainingHistoryActions();
  bindDeepAnalysisActions();
  bindReviewPlanActions(stats, wrongTotal, analysisItems.length);
  bindLearningSignalActions();
}

function renderTraining() {
  setPanelPage(true);
  const courseStore = userCourseStore();
  const stats = getCourseStats();
  const items = state.analysisQuestions.length ? state.analysisQuestions : state.questions;
  const today = getTodayActivity();
  const servedToday = todayServedIds();
  const dueRecords = getWrongRecordsForCurrentCourse(courseStore).filter(([, item]) => isReviewDue(item));
  const dueTotal = dueRecords.length;
  /* 今天已经在别的训练单里出过的到期错题不会再发一次（见 startDueWrongReview），
     所以「到期错题」摘要格显示总量，而错题复习卡片显示的是「还可复习」。 */
  const dueLeft = dueRecords.filter(([id]) => !servedToday.has(Number(id))).length;
  const weak = getWeakChapterRows(courseStore, items).slice(0, 3);
  refreshDailyTrainingState();
  const analysisReady = state.analysisCourseId === Number(state.currentCourse?.id || 0) && state.analysisQuestions.length;
  /* 卡片题量和题单构成都用「真实生成逻辑」算出来，避免文案与实际题单不符（旧实现是估算公式，
     实测卡片写「20 题」而点进去生成 60 题）。这里算一次给三张卡片共用，避免重复遍历题库。 */
  const smartPlan = buildSmartPracticePlan(courseStore, {});
  const sprintPlan = buildSprintPracticePlan(courseStore);
  const previewCounts = {
    smart: smartPlan.ids.length,
    similar: Math.min(80, buildSimilarWrongIds(courseStore).length),
    sprint: sprintPlan.ids.length,
  };
  const previewMix = { smart: smartPlan.counts, sprint: sprintPlan.counts };
  const previewContext = { items, analysisReady, counts: previewCounts, mix: previewMix };
  const smartPreview = previewPracticePlan("smart", courseStore, previewContext);
  const similarPreview = previewPracticePlan("similar", courseStore, previewContext);
  const sprintPreview = previewPracticePlan("sprint", courseStore, previewContext);
  /* 「专项练习」卡只有一个入口，两种练法的描述写进弹窗，卡上的 meta 就要说明
     「点进去还能选」，否则用户会以为这张卡就是练同章同型。
     两种都没题时，把两边的「没有」都说清（这里显示的是不可点的原因）。 */
  const specialAny = previewCounts.similar > 0 || previewCounts.sprint > 0;
  const specialPreview = specialAny
    ? (previewCounts.similar > 0 && previewCounts.sprint > 0
      ? `${similarPreview} / ${sprintPreview} · 二选一`
      : `${previewCounts.similar > 0 ? similarPreview : sprintPreview} · 仅一种可选`)
    : "当前科目暂无可用题目";
  /* 「错题本」卡的 meta：错题总量 + 今天到期几道。
     原来「今日错题复习」是独立一张卡（显示到期数），现在并进错题本 ——
     到期数仍要出现在卡片上，否则用户看不出「今天有没有该复习的」，
     还得点进去才知道。 */
  const wrongTotalForCard = getWrongRecordsForCurrentCourse(courseStore).length;
  const wrongPreview = wrongTotalForCard
    ? `共 ${wrongTotalForCard} 道 · ${dueTotal ? `${dueTotal} 道今天到期` : "今天没有到期"}`
    : "还没有错题";
  /* 「继续上次」和 hero 共用同一个判据（resumableTrainingSession），
     所以同屏不会再出现两个都能点、却各指一条题单的「继续」。
     hero 的文案据实而写：有可续的题单就说「继续」（且点下去续的就是这条），
     否则说「开始」—— 旧实现按 type 反查另一条会话，于是 hero 说「继续今日强化」
     而它续的是 A，「继续上次」续的是 B。 */
  const resumable = resumableTrainingSession();
  /* 可续的那条不是今天的就带上日期（见产品决策：范围放宽到所有未完成会话）。 */
  const resumeDay = sessionResumeDayText(resumable);
  const heroLabel = !resumable
    ? "开始今日强化"
    : resumeDay
      ? `继续上次（${resumeDay}）`
      : resumable.type === "smart"
        ? "继续今日强化"
        : "继续上次训练";
  const hasReviewPlan = !!activeReviewPlan().examDate;
  const planOverview = hasReviewPlan
    ? buildReviewPlanOverview(stats, Object.keys(courseStore.wrong || {}).length, items)
    : { hasPlan: false, task: { ids: [], counts: {} }, days: 0, debt: 0, todayCompleted: false, todaySession: null };
  /* 今日建议 / KPI 格里的章也要按门槛说话：只有真的达标（verified≥5 且 rate<60）
     才叫「薄弱章节」；只是相对最弱就说「相对最弱章节」，别把 92% 的章也叫薄弱。 */
  const weakTop = weak[0] || null;
  const weakTopIsWeak = !!weakTop && isWeakRow(weakTop);
  const weakTopLabel = weakTopIsWeak ? "薄弱章节" : "相对最弱章节";
  const mainAdvice = dueLeft
    ? `优先复习 ${dueLeft} 道到期错题`
    : weakTop
      ? `优先强化 ${weakTop.name}`
      : today.total
        ? "继续做一组今日强化"
        : "建议从今日强化开始";
  const adviceDetail = dueLeft
    ? "这些题已经到复习周期，先处理能减少反复遗忘。"
    : weakTop
      ? (weakTopIsWeak
        ? `当前薄弱章节正确率 ${weakTop.rate}%，系统会优先混入相关错题和未做题。`
        : `暂无低于 60% 的章节，相对最弱的是它（${weakTop.rate}%），系统会优先混入相关错题和未做题。`)
      : "系统会按错题、相对最弱的章节和未做题自动生成练习。";
  $("questionBody").classList.add("hidden");
  $("emptyState").classList.remove("hidden");
  $("emptyState").innerHTML = `
    <div class="training-panel">
      <div class="training-hero">
        <div>
          <strong>智能训练</strong>
          <p>${escapeHtml(state.currentCourse?.name || "当前科目")} · 今日已练 ${today.total} 题 · 确认正确率 ${today.rate}%</p>
        </div>
        <button class="primary-action" id="trainingSmartBtn" type="button">${heroLabel}</button>
      </div>
      <div class="training-advice">
        <span>今日建议</span>
        <b>${escapeHtml(mainAdvice)}</b>
        <small>${escapeHtml(adviceDetail)}</small>
      </div>
      ${planOverview.hasPlan ? `
        <div class="training-plan-card">
          <div>
            <span>今日计划</span>
            <b>${planOverview.task.ids.length} 题 · 距离考试 ${planOverview.days} 天</b>
            <small>复习 ${planOverview.task.counts.review || 0} · 强化 ${planOverview.task.counts.weak || 0} · 新题 ${planOverview.task.counts.new || 0} · 欠账 ${planOverview.debt}</small>
          </div>
          <button id="trainingPlanBtn" type="button" ${planButtonDisabled(planOverview) ? "disabled" : ""}>${planButtonLabel(planOverview)}</button>
        </div>
      ` : ""}
      <div class="training-summary-grid">
        <div><b>${dueTotal}</b><span>到期错题</span></div>
        <div><b>${today.total}</b><span>今日已练</span></div>
        <div><b>${weakTop ? weakTop.rate : 0}%</b><span>${weakTop ? `${escapeHtml(weakTop.name)}（${weakTopLabel}）` : "暂无薄弱章节"}</span></div>
        <div><b>${Math.max(0, items.length - stats.done)}</b><span>未做题</span></div>
      </div>
      <div class="training-card-grid">
        ${renderTrainingCard("smart", "今日训练", "到期错题、反复错、薄弱章节、未做题自动成一张卷子 —— 不知道练什么就点这个。", smartPreview, "开始训练")}
        ${renderTrainingCard("special", "专项练习", "只练一类：同章同型（照着你错得最多的题扩展），或考前冲刺组合。", specialPreview, "选择专项", {
          disabled: previewCounts.similar === 0 && previewCounts.sprint === 0,
          reason: "当前科目暂无可专项练习的题",
        })}
        ${renderTrainingCard("wrong", "错题本", "所有答错过的题都在这里，按复习周期 / 错误次数筛选后再练。", wrongPreview, "打开错题本")}
      </div>
      <div class="training-tools">
        <button type="button" id="trainingDashboardBtn">查看学习看板</button>
        <button type="button" id="trainingWrongBtn">查看错题本</button>
        <button type="button" id="trainingPracticeBtn">返回考试题库</button>
      </div>
    </div>
  `;
  bindTrainingActions();
}

/* 训练卡。`options.disabled` 用于「这个入口现在没有内容可发」的情况：
   只弹 toast 的话按钮看着还是可点的，和同页「今日计划」的置灰风格也不一致。
   「今日强化」永远有内容（新题保底 SMART_PRACTICE_FLOOR_NEW = 24 道），所以不传 disabled ——
   唯一的例外是题库本身为空（连分析数据都没加载出来），那种情况下计划为 0 道，
   但这里仍然不禁用，因为禁用会把「先点一下、数据加载完再生成」的正常路径也堵掉。
   原因文案写在 strong（meta）里，title 兜底给鼠标用户 / 读屏。 */
function renderTrainingCard(type, title, desc, meta, action, options = {}) {
  const disabled = !!options.disabled;
  /* 冷启动时 meta 是「正在计算」（分析数据还没回来），而 reason 是硬编码的
     「当前科目暂无可冲刺的题」—— 两者在同一张卡上互相矛盾（title 说没题、正文说在算）。
     还在算就只说在算，别下结论。 */
  const loading = /正在计算/.test(meta || "");
  const reason = loading ? "正在计算…" : (options.reason || meta || "");
  return `
    <section class="training-card">
      <div>
        <span>${escapeHtml(title)}</span>
        <p>${escapeHtml(desc)}</p>
      </div>
      <strong>${escapeHtml(meta || "自动生成")}</strong>
      <button type="button" data-training-action="${type}"${disabled
        ? ` disabled aria-disabled="true" title="${escapeHtml(reason)}"`
        : ""}>${escapeHtml(action)}</button>
    </section>
  `;
}

function previewPracticePlan(type, courseStore, context = {}) {
  const items = context.items || (state.analysisQuestions.length ? state.analysisQuestions : state.questions);
  const loading = !context.analysisReady && state.analysisLoadingCourseId === Number(state.currentCourse?.id || 0);
  if (loading && !items.length) return "正在计算";
  /* 题量直接取「真实生成逻辑」的结果（context.counts 由 renderTraining 预算一次），
     不再用估算公式 —— 旧公式与 buildXxxIds 的装填逻辑不一致，文案必然对不上。
     构成也一并显示：这样「今天有多少新题」在点进去之前就是可见的。 */
  const count = Number(context.counts?.[type] || 0);
  const mix = context.mix?.[type] || null;
  /* 「新题」统一用 neverDone（累计从没答过的题数）。旧文案用的是 mix.fresh，
     那是「未做题保底」那一段的装填数量：段③薄弱章节里的大量新题没被算进去，
     实测同一轮 60 题里 58 题从没答过，卡片却写「新题46」。
     前两段（复习 / 弱项）是分段的题量，与「新题」不是一个维度（会互相重叠），
     所以「新题」前面加「其中」，表明它是整张卷子的属性，不是第四段。 */
  const realNew = Number(mix?.neverDone ?? mix?.fresh ?? 0);
  /* ⚠️ 这三个数**不可相加**：复习段 / 弱项段是分段装填的，而「新题」是整张卷子的属性，
     同一个题可以既在「弱项」里又是「新题」（实测 12 + 22 + 38 = 72 > 60 题）。
     所以分段数之间用「+」连、并在后面点明会重叠，别写成三个并列的统计量。
     （旧文案「复习12 弱项22 · 其中新题38」用的全是空格分隔，读起来像可以相加。） */
  if (type === "smart") {
    if (!count) return "自动生成 · 错题优先";
    return mix
      ? `${count} 题 · 复习${mix.due} + 弱项${mix.repeated + mix.weak}（两段会重叠）· 其中新题${realNew}`
      : `${count} 题 · 错题优先`;
  }
  if (type === "similar") return count ? `${count} 题 · 同章同型` : "自动生成 · 同章同型";
  if (type === "sprint") {
    if (!count) return "自动生成 · 冲刺组合";
    return mix
      ? `${count} 题 · 错题${mix.wrong} + 弱项${mix.weak}（两段会重叠）· 其中新题${realNew}`
      : `${count} 题 · 冲刺组合`;
  }
  return "";
}

function bindTrainingActions() {
  $("trainingSmartBtn").onclick = () => startSmartPractice().catch((err) => toast(err.message));
  /* 工具行原来还有一个「继续上次：<题单名>」按钮，和上面 hero 的「继续上次训练」
     是同一个动作（都走 startSmartPractice → 续同一条会话），只是文案多带了题单名。
     同屏两个「继续上次」按 A/B 审计的结论删掉工具行那个：题单名在下面的
     「个人训练看板」每一行里都有（行内也有自己的「继续」）。 */
  if ($("trainingPlanBtn")) $("trainingPlanBtn").onclick = () => startTodayReviewPlan().catch((err) => toast(err.message));
  /* 训练页底部的三个出口原来各手抄一份「切 mode」逻辑（都不递增
     modeTransitionId → 在途请求回来照样写屏）。现在统一走 switchMode。 */
  $("trainingDashboardBtn").onclick = () => switchMode("progress");
  $("trainingWrongBtn").onclick = () => switchMode("wrong");
  $("trainingPracticeBtn").onclick = backToPractice;
  document.querySelectorAll("[data-training-action]").forEach((btn) => {
    btn.onclick = () => {
      const action = btn.dataset.trainingAction;
      if (action === "smart") startSmartPractice().catch((err) => toast(err.message));
      /* "wrong" 是跳转而不是开练：错题本有自己的筛选（复习周期 / 错误次数 / 状态），
         先筛再练比在这里替用户决定题池更合理。 */
      if (action === "wrong") switchMode("wrong");
      if (action === "special") openSpecialPracticePicker("similar").catch((err) => toast(err.message));
      /* 兼容旧缓存里的卡片（?v= 没抬时可能还是 similar/sprint 两个按钮）。
         新卡片一律走 "special"，这里保留旧分支只为不出现「点了没反应」。 */
      if (action === "similar" || action === "sprint") openSpecialPracticePicker(action).catch((err) => toast(err.message));
    };
  });
}

/* 「专项练习」二级选择。
   原来同类题型 / 考前冲刺是两张并列的卡，加上今日强化、今日错题复习一共四张 ——
   四张卡的题池互相重叠（今日强化本身就含到期复习段），用户要在四个说法里猜
   「今天该点哪个」。现在收成一张卡，点进来再二选一，两个选项的差别写清。
   弹窗结构与 askExamDraftQuestionGap 同一套（modal-backdrop + modal-panel +
   data-* 分发），保持全站对话框一致，不另造组件。 */
function pickSpecialPractice() {
  return new Promise((resolve) => {
    const courseStore = userCourseStore();
    const items = state.analysisQuestions.length ? state.analysisQuestions : state.questions;
    /* 不能用 renderTrainingDashboard 里的 previewCounts / previewMix —— 那两个是
       该函数的局部变量，弹窗在它之外打开，直接引用会 ReferenceError（按钮点了没反应，
       错误被 .catch(toast) 吞掉）。这里按同一口径自己算一次，代价是再遍历一遍题库
       （这个弹窗一次点击才走一次，可以接受）。 */
    const sprintPlan = buildSprintPracticePlan(courseStore);
    const counts = {
      similar: Math.min(80, buildSimilarWrongIds(courseStore).length),
      sprint: sprintPlan.ids.length,
    };
    const ctx = { items, analysisReady: true, counts, mix: { sprint: sprintPlan.counts } };
    const similarMeta = previewPracticePlan("similar", courseStore, ctx);
    const sprintMeta = previewPracticePlan("sprint", courseStore, ctx);
    const similarOff = counts.similar === 0;
    const sprintOff = counts.sprint === 0;
    const row = (value, title, meta, desc, off) => `
      <button type="button" class="special-pick${off ? " is-off" : ""}"
        data-special-pick="${value}"${off ? " disabled aria-disabled=\"true\"" : ""}>
        <b>${escapeHtml(title)}</b>
        <em>${escapeHtml(off ? "当前科目暂无可用题目" : meta)}</em>
        <small>${escapeHtml(desc)}</small>
      </button>`;
    const host = document.createElement("div");
    host.id = "specialPickModal";
    host.className = "modal-backdrop";
    host.innerHTML = `
      <div class="modal-panel conflict-modal special-pick-panel" role="dialog" aria-modal="true" aria-labelledby="specialPickTitle">
        <div class="modal-head">
          <strong id="specialPickTitle">专项练习</strong>
          <button type="button" data-special-pick="cancel">关闭</button>
        </div>
        <p class="conflict-message">两种都从「你错得最多的题」出发，区别是范围：</p>
        <div class="special-pick-list">
          ${row("similar", "同章同型", similarMeta, "只出和错题同章节、同题型的题（大多不是错题本身），用来把题型练熟。", similarOff)}
          ${row("sprint", "考前冲刺", sprintMeta, "近 7 天错题 + 反复错 + 薄弱章节 + 未做，范围更大、更杂。", sprintOff)}
        </div>
      </div>`;
    const finish = (choice) => {
      host.remove();
      resolve(choice === "cancel" ? "" : choice);
    };
    host.querySelectorAll("[data-special-pick]").forEach((btn) => {
      btn.onclick = () => finish(btn.dataset.specialPick);
    });
    host.addEventListener("click", (event) => {
      if (event.target === host) finish("cancel");
    });
    document.addEventListener("keydown", function onSpecialPickKey(event) {
      if (event.key !== "Escape") return;
      document.removeEventListener("keydown", onSpecialPickKey, true);
      finish("cancel");
    }, true);
    document.body.appendChild(host);
  });
}

async function openSpecialPracticePicker(initial) {
  const pick = await pickSpecialPractice();
  if (!pick) return;
  if (pick === "similar") await startSimilarWrongPractice();
  if (pick === "sprint") await startSprintPractice();
}

/* 从课本章名里拆出「可区分的尾部」，给窄屏用（见 renderWeakChapterCard）。
   题库里前缀相同、只差尾部的章名真实存在（「…真题精选 (浙江)」/「(陕西)」、
   「…《建设工程计价》真题」/「…《造价管理》真题」），单靠首部省略分不清。
   规则：取**最后一个**中/全角括号段（含括号本身）作为尾部；没有括号就返回空串
   （这类章名的区分位在前部，例如「第四章 建设工程发承包阶段…」）。
   只做展示，不改 row.key（取题仍用完整章名）。 */
function weakChapterNameTail(name) {
  const text = String(name || "");
  const m = text.match(/[（(][^（()）]*[)）]\s*$/);
  if (!m) return "";
  /* 尾部独占整个名字（章名本身就是括号内容）时不必拆，拆了反而主体为空。 */
  if (m[0].trim() === text.trim()) return "";
  return m[0];
}

/* 学习看板「薄弱章节」卡。三件必须在卡上说清的事：
     ① **能不能叫「薄弱」**：只有真的达标（verified≥WEAK_MIN_VERIFIED 且 rate<60）
        才配这两个字，门槛调 isWeakRow()（与深分析树徽章同一个函数、同一个值）。
        这一批还统一了样本量：以前 getWeakChapterRows() 用 3、chapterMastery() 用 5，
        3~4 道确认的章会「上榜却被别处判成薄弱」—— 现在两处都是 WEAK_MIN_VERIFIED。
     ② **会出多少题**：每行按钮上写题量（点之前看得见，别让用户点进去才知道）。
        取题走 weakChapterPracticeIds —— 与「今日强化」段③同一条件，写几道就发几道。
     ③ **不能练的时候**：按钮 disabled + 原因写在卡上（title + 行内文案），
        不留一个点了才弹 toast 的假按钮（同 renderTrainingCard 的 disabled 风格）。 */
function renderWeakChapterCard(courseStore, items, allWeak, weak, weakBelowCount) {
  /* 分析数据还没到（看板会先于 /api/questions 渲染一轮）：既不说「暂无明显短板」
     也不说 0 题 —— 那两句都是结论，此时还没资格下（同 renderTrainingCard 的
     「正在计算」处理）。analysisCourseId 才是「这个科目已经算出来过」的凭据。 */
  const courseId = Number(state.currentCourse?.id || 0);
  const analysisLoaded = state.analysisCourseId === courseId;
  const loading = !items.length && !analysisLoaded;
  /* 没有数据（加载中 / 还没有章达到 5 道已确认）时说「相对最弱」和说「薄弱」一样是没有
     依据的结论，所以这两种情况下都不贴章节标签，只写「章节掌握」。 */
  const title = loading || !weak.length ? "章节掌握" : weakBelowCount ? "薄弱章节" : "相对最弱的几章";
  const headline = loading ? "正在计算…" : weak[0] ? escapeHtml(weak[0].name) : "暂无明显短板";
  const note = loading
    ? "题库分析数据还在加载，稍后自动刷新"
    : weakBelowCount
      ? `共 ${weakBelowCount} 章判定为薄弱（正确率 < 60%）`
      : weak.length
        ? `暂无低于 60% 的章节，以下只是相对最弱（上榜需已确认 ≥ ${WEAK_MIN_VERIFIED} 题）`
        : `继续积累做题数据后会更准（每个章已确认 ≥ ${WEAK_MIN_VERIFIED} 题才会统计）`;
  const rows = weak.map((row) => {
    const ids = weakChapterPracticeIds(row.key, courseStore, items);
    const disabled = !ids.length;
    /* 一道可练的都没有 = 这一章的题**全都答对了**（ids 只在「答对过」时被剔掉），
       所以这里说的是事实，不是「0 题」这种没信息量的数字。 */
    const reason = disabled
      ? `这一章 ${row.done} 道题都已答对，没有「没做对过」的题了`
      : `只练「${row.name}」：${ids.length} 题（已排除你答对过的题）`;
    /* 窄屏会把章名挤成一行（见 .weak-chapter-row 的 760px 规则）。题目库里的章名很长
       （实测 291 个 grade=1 章名里 258 个超过 14 字），而且**有前缀相同的**：
       「2019年二级造价安装真题精选 (浙江)」/「(陕西)」只差**尾部括号**——
       只做首部省略的话这两行会长得一模一样，用户分不清。
       所以除了首部（保留「第X章」这种区分位），再单独渲染一段「尾部」：
       把中文/全角括号里的内容（卷别、地区、年份版本）拆出来贴在章名后面，
       窄屏下它不参与省略 —— 同前缀的两行靠尾部一眼分开。
       全名同时写进 title（鼠标用户可读），但**不依赖** title（手机没有 hover）。 */
    const tail = weakChapterNameTail(row.name);
    const head = tail ? row.name.slice(0, row.name.length - tail.length).trim() : row.name;
    /* 按钮文案两套：宽屏带题量（「只练这一章 · N 题」，点之前看得见），
       窄屏只留「只练这一章」—— 题量那 60 多像素要还给章名（详见 style.css 的
       760px 规则：390 下章名不够宽就会出现「第二章 安装工程施工技术」与
       「第二章 安装工程计量」省略后**撞成同一串**的问题）。
       题量不会丢：同一行的 meta 里就写着「可练 N 题」，窄屏两处不重复。 */
    const actionLong = `只练这一章 · ${ids.length} 题`;
    const actionShort = "只练这一章";
    return `
      <div class="weak-chapter-row" data-chapter-name="${escapeHtml(row.name)}">
        <span class="weak-chapter-name" title="${escapeHtml(row.name)}">${escapeHtml(head)}${tail
          ? `<span class="weak-chapter-tail">${escapeHtml(tail)}</span>`
          : ""}</span>
        <em class="weak-chapter-meta">正确率 ${row.rate}% · ${disabled ? "已无可练题" : `可练 ${ids.length} 题`}</em>
        <button type="button" data-weak-chapter="${escapeHtml(row.key)}"${disabled
          ? ` disabled aria-disabled="true" title="${escapeHtml(reason)}"`
          : ` title="${escapeHtml(reason)}"`}>${disabled
            ? `<span class="weak-action-long">${actionShort}</span><span class="weak-action-short">${actionShort}</span>`
            : `<span class="weak-action-long">${actionLong}</span><span class="weak-action-short">${actionShort}</span>`}</button>
      </div>
    `;
  }).join("");
  return `
    <div class="study-card">
      <span>${title}</span>
      <b>${headline}</b>
      <small>${escapeHtml(note)}</small>
      ${rows ? `<div class="weak-chapter-list">${rows}</div>` : ""}
    </div>
  `;
}

function renderStudyDashboard(courseStore, stats, items) {
  const records = getWrongRecordsForCurrentCourse(courseStore);
  const due = records.filter(([, item]) => isReviewDue(item)).length;
  /* 取全量再切 3 行：判断「有没有真的低于门槛的章」必须看全量（一个 75% 的章
     可能排在第 4 行之外，只看前 3 行会漏判）。门槛统一走 isWeakRow()。 */
  const allWeak = getWeakChapterRows(courseStore, items);
  const weak = allWeak.slice(0, 3);
  const weakBelowCount = allWeak.filter((row) => isWeakRow(row)).length;
  const savedReport = getTodayReport();
  const report = savedReport || buildDailyReport(courseStore, stats, items);
  const today = getTodayActivity();
  const reportText = report
    ? `已记录快照：累计已做 ${report.done} 题 · 确认正确率 ${report.rate}% · ${escapeHtml(report.focus || "继续保持")}`
    : buildStudyReportSummary(courseStore, stats, items).summary;
  /* 「智能练习」卡与下面的「个人训练看板」行共用 resumableTrainingSession ——
     旧实现这张卡读 smartPractice 指针、行读会话列表，同屏互相否定：
     卡片写「暂无练习批次」（禁用），下面两行却是可点的「继续」。 */
  const resumable = resumableTrainingSession();
  /* 「智能练习」卡的可续判据同上；不是今天的会话要写出日期，别让用户以为
     是刚才那条（见 resumableTrainingSession 的注释）。 */
  const resumeDay = sessionResumeDayText(resumable);
  return `
    <div class="study-dashboard">
      <div class="study-card">
        <span>今日建议</span>
        <b>${due ? `复习 ${due} 道错题` : "智能训练一组"}</b>
        <small>${today.total ? `今日已练 ${today.total} 题，确认正确率 ${today.rate}%` : escapeHtml(reportText)}</small>
      </div>
      <div class="study-card">
        <span>今日练习</span>
        <b>${today.total} 题</b>
        <small>已确认 ${today.verified} 题 · 答对 ${today.correct} 题 · ${today.rate}%${today.durationMs ? ` · 用时 ${formatDuration(today.durationMs)}` : ""}</small>
      </div>
      ${renderWeakChapterCard(courseStore, items, allWeak, weak, weakBelowCount)}
      <div class="study-card">
        <span>学习日报</span>
        <b>${savedReport ? "今日已记录" : "今日未记录"}</b>
        <small>${escapeHtml(report.tomorrow || "明天继续保持节奏")}</small>
        <button id="dailyReportBtn" type="button">更新日报</button>
      </div>
      <div class="study-card ${resumable ? "" : "muted-card"}">
        <span>智能练习</span>
        <b>${resumable ? `${normalizeSessionIds(resumable.ids).length} 题可继续 · ${escapeHtml(resumable.sourceTitle || "智能训练")}${resumeDay ? ` · 上次 ${resumeDay}` : ""}` : "暂无可继续的训练单"}</b>
        <button id="continueSmartBtn" type="button" ${resumable ? "" : "disabled"}>继续上次${resumeDay ? `（${resumeDay}）` : ""}</button>
      </div>
    </div>
  `;
}

function bindStudyDashboardActions(courseStore) {
  const btn = $("dailyReportBtn");
  if (!btn) return;
  btn.onclick = () => {
    const report = buildDailyReport(courseStore, getCourseStats(), state.analysisQuestions.length ? state.analysisQuestions : state.questions);
    state.storage.dailyReports ||= [];
    const key = todayKey();
    state.storage.dailyReports = state.storage.dailyReports.filter((item) => item.key !== key);
    state.storage.dailyReports.unshift({ key, at: nowText(), ...report });
    state.storage.dailyReports = state.storage.dailyReports.slice(0, 60);
    scheduleSave();
    if (state.mode === "training") renderTraining();
    else renderProgress();
    toast("学习日报已更新");
  };
  const smartBtn = $("continueSmartBtn");
  if (smartBtn) smartBtn.onclick = () => continueSmartPractice().catch((err) => toast(err.message));
  /* 「薄弱章节」卡每行的「只练这一章」（见 renderWeakChapterCard）。
     disabled 的按钮不会派发 click，所以这里不必过滤 ——
     不能练的原因已经写在卡上（title + 行内文案），点不到也不会静默。 */
  document.querySelectorAll("[data-weak-chapter]").forEach((row) => {
    row.onclick = () => startWeakChapterPractice(row.dataset.weakChapter).catch((err) => toast(err.message));
  });
}

function renderTrainingHistoryPanel(courseStore) {
  const sessions = getTrainingSessionsForCourse().map((session) => syncTrainingSessionStats(session)).filter(Boolean);
  const recent = sessions.slice(0, 8);
  const finished = sessions.filter((item) => item.status === "completed").length;
  const ended = sessions.filter((item) => item.status === "abandoned" || item.status === "expired").length;
  const totalDone = sessions.reduce((sum, item) => sum + Number(item.done || 0), 0);
  const totalCorrect = sessions.reduce((sum, item) => sum + Number(item.correct || 0), 0);
  const totalVerified = sessions.reduce((sum, item) => sum + Number(item.verified || 0), 0);
  const overallRate = confirmedRate(totalCorrect, totalVerified);
  return `
    <div class="training-history-panel">
      <div class="training-history-head">
        <div>
          <span>个人训练看板</span>
          ${/* 标题不再声明「N 套进行中」——旧实现把「非 completed」一律算进行中，
               而会话过去不会自动收尾（见 sweepTrainingSessionStates），于是
               「3 套进行中」挂着一堆早就没人做的题单，只会误导。
               现在只报两个可核实的事实：几套、平均正确率多少。 */ ""}
          <strong>共 ${sessions.length} 套训练 · 已完成 ${finished} 套${ended ? ` · 已结束 ${ended} 套` : ""} · 平均正确率 ${overallRate}%</strong>
        </div>
        <b>累计作答 ${totalDone} 题</b>
      </div>
      ${recent.length ? `
        <div class="training-history-list">
          ${recent.map((session) => renderTrainingHistoryRow(session)).join("")}
        </div>
      ` : `<div class="muted">暂无训练记录，开始今日强化后会在这里沉淀自己的训练数据。</div>`}
    </div>
  `;
}

function renderTrainingHistoryRow(session) {
  const total = Number(session.total || session.ids?.length || 0);
  const done = Number(session.done || 0);
  const progress = total ? Math.round(done / total * 100) : 0;
  const isActive = state.storage.activeTrainingSessionId === session.id;
  /* 按钮的可点性与 isTrainingSessionOpen 同判据 ——
     旧实现只要「没做完」就写「继续」，点下去才提示「该训练已结束」，自相矛盾。 */
  const btn = trainingSessionButton(session);
  const closedNote = session.status === "abandoned"
    ? ` · 已结束（${SESSION_ABANDON_DAYS} 天未继续）`
    : session.status === "expired" ? " · 已结束" : "";
  /* 时间是**相对**的，和课程列表（renderCourses 里的「更新 3 天前」）同一套说法。
     裸绝对时间「2026-10-05 10:00:00」在列表里既长又难比；精确时刻留在 title 里。 */
  const createdText = formatRelativeTime(session.createdAt);
  const completedText = session.completedAt ? ` · 完成 ${formatRelativeTime(session.completedAt)}` : "";
  const absolute = [session.createdAt || "", session.completedAt ? `完成 ${session.completedAt}` : ""]
    .filter(Boolean).join(" · ");
  return `
    <section class="training-history-row ${isActive ? "active" : ""}">
      <div class="training-history-main">
        <span>${escapeHtml(session.sourceTitle || "智能训练")}</span>
        <strong>${done}/${total} 题 · 正确率 ${Number(session.rate || 0)}% · 错题 ${Number(session.wrong || 0)}</strong>
        <small title="${escapeHtml(absolute)}">${escapeHtml(session.courseName || "")} · ${escapeHtml(createdText)}${completedText ? escapeHtml(completedText) : ""}${closedNote}</small>
      </div>
      <div class="training-history-progress" aria-hidden="true"><i style="width:${Math.max(2, progress)}%"></i></div>
      <button type="button" data-training-session="${escapeHtml(session.id)}" ${btn.disabled ? "disabled" : ""}>${btn.label}</button>
    </section>
  `;
}

function bindTrainingHistoryActions() {
  document.querySelectorAll("[data-training-session]").forEach((btn) => {
    btn.onclick = () => continueTrainingSession(btn.dataset.trainingSession).catch((err) => toast(err.message));
  });
}

/* 「继续」的唯一入口。只做一件事：把指针指到用户点名的那条会话上，
   然后让 loadQuestions 从**这条会话自己的 ids** 取题（getSmartPracticeIds 只读）。
   于是「点的是哪条，续的就是哪条」——旧实现点下去会走 loadQuestions →
   getModeQuestionIds → getSmartPracticeIds，而那里续不上（跨天）就现场新建一条
   60 题会话，用户的 77 题题单原样留着、toast 还说「已继续」。 */
async function continueTrainingSession(sessionId) {
  const session = (state.storage.trainingSessions || []).find((item) => item.id === sessionId);
  if (!session || Number(session.courseId || 0) !== Number(state.currentCourse?.id || 0)) {
    toast("训练记录不属于当前科目");
    return;
  }
  if (!Array.isArray(session.ids) || !session.ids.length) {
    toast("训练记录没有题目");
    return;
  }
  syncTrainingSessionStats(session);
  /* toast 不能撒谎：续不上就说续不上，并说清为什么。 */
  if (!isTrainingSessionOpen(session)) {
    toast(session.status === "completed"
      ? "该训练本轮题已全部完成，请开始新的智能训练"
      : "该训练已结束，请开始新的智能训练");
    return;
  }
  state.storage.activeTrainingSessionId = session.id;
  resetGeneratedPracticeFilters();
  if (session.mode === "wrong") {
    state.mode = "wrong";
    /* 同 startWrongBookPractice：续的一轮从「未交卷」开始，
       否则练习交卷残留的 state.submitted 会让整份题单的答案在进训练时全部展开。
       本轮**已经答过**的题靠会话自己的 verifiedAnswers 显示判定结果（见 activeWrongPracticeSession）。 */
    state.submitted = false;
    state.answerVisible = false;
    /* 同 startDueWrongReview：不把会话的题源口径写进「页面筛选」。
       due 会话的题池靠 session.ids 冻结，页面筛选强写 due 会留在错题本上，
       让用户回到错题本时看不到今天刚错的题。 */
    state.wrongFilters = { ...(state.wrongFilters || {}), status: "active" };
    setCoursePicker(false);
    await loadQuestions();
    toast(`已继续${session.sourceTitle || "错题复习"}`);
    return;
  }
  ensureSmartTrainingSessionState(session);
  state.smartPracticeFreshStart = false;
  state.mode = "smart";
  state.submitted = false;
  state.answerVisible = false;
  state.smartLeftCourseName = "";
  state.smartLeftCourseId = 0;
  setCoursePicker(false);
  scheduleSave();
  await loadQuestions();
  /* 题单拿不到（题库/接口问题）时不能还说「已继续」——把实际题数报出来。 */
  const loaded = state.questions.length;
  /* toast 带上日期：续的可能不是今天那条（见 resumableTrainingSession）。 */
  const dayText = sessionResumeDayText(session);
  toast(loaded
    ? `已继续${session.sourceTitle || "智能训练"}（${loaded} 题）${dayText ? ` · 上次 ${dayText}` : ""}`
    : `${session.sourceTitle || "智能训练"}的题目暂时取不到，请重试`);
}

function getTodayReport() {
  const key = todayKey();
  return (state.storage.dailyReports || []).find((item) => item.key === key);
}

function buildStudyReportSummary(courseStore, stats, items) {
  /* 与 KPI 同一口径：答对 ÷ 已确认。 */
  const rate = confirmedRate(stats.correct, stats.verified);
  const weak = getWeakChapterRows(courseStore, items)[0];
  const due = getWrongRecordsForCurrentCourse(courseStore).filter(([, item]) => isReviewDue(item)).length;
  /* 措辞跟门槛走（同训练页/看板）：真的达标才叫「薄弱」，
     只是相对最弱就写「相对最弱章节」—— 别把 92% 的章也叫薄弱。 */
  const weakIsWeak = !!weak && isWeakRow(weak);
  const focus = weak
    ? `${weakIsWeak ? "优先练薄弱章" : "优先练相对最弱章节"} ${weak.name}`
    : due ? "优先复习到期错题" : "保持当前节奏";
  return {
    done: stats.done,
    correct: stats.correct,
    verified: stats.verified,
    /* 主观题单列：日报的 rate 与 KPI 现在是同一个客观题集合，主观题只报数不参与除法。 */
    subjective: Number(stats.subjective || 0),
    wrong: stats.wrong,
    rate,
    due,
    focus,
    summary: `当前进度快照：累计已做 ${stats.done} 题 · 客观题确认正确率 ${rate}%（答对 ${stats.correct} / 已确认 ${stats.verified}）`
      + `${Number(stats.subjective || 0) ? ` · 主观题 ${stats.subjective} 道单列` : ""} · ${focus}`,
  };
}

function buildDailyReport(courseStore, stats, items) {
  const base = buildStudyReportSummary(courseStore, stats, items);
  const today = getTodayActivity();
  const weak = getWeakChapterRows(courseStore, items)[0];
  const due = getWrongRecordsForCurrentCourse(courseStore).filter(([, item]) => isReviewDue(item)).length;
  /* 同上：只有真达标才说「薄弱章」，否则照实说「相对最弱章节」。 */
  const tomorrow = weak
    ? `明天优先练${isWeakRow(weak) ? "薄弱章" : "相对最弱章节"}「${weak.name}」+ 到期错题 ${due} 道`
    : due
      ? `明天优先复习到期错题 ${due} 道`
      : "明天可继续智能训练";
  return {
    ...base,
    todayTotal: today.total,
    todayVerified: today.verified,
    todayCorrect: today.correct,
    todayRate: today.rate,
    tomorrow,
    summary: `今日 ${today.total} 题 · 确认正确率 ${today.rate}% · ${tomorrow}`,
  };
}

/* 「薄弱章节」按**课本章名**聚合，不按 chapter id。
   题库里同一个课本的章在每个卷册（grade 0）里都有一份独立的 chapter 记录 ——
   实测课程 20 的「第二章 建设工程计价原理、方法及计价依据」被拆成 5 个 id、合计 1085 题。
   按 id 聚合的话，同一章的薄弱会被拆成 5 行互不相认的统计，也没法「针对某个章节做训练」。

   一道题可能同时属于多个章（同一道题考察多个章节，或者整卷题靠外挂映射归了多章），
   所以用 textbookChapterKeys 拿到集合，**每个所属章各记一次** ——
   这道题做错了，那几个章都该算弱。

   行的 key 就是课本章名，筛题用 textbookChapterKeys(item).includes(row.key)。 */
function getWeakChapterRows(courseStore, items) {
  /* 「做过」的判据是 done ∪ correct ∪ wrong（见 isQuestionDone）。
     旧实现只看 done：老数据（尤其**旧版考场交卷**只写 correct/wrong）里
     做错过的题一道都进不来，用户看着「薄弱章节」是空的，实际错题一堆。 */
  const correctIds = new Set(Object.keys(courseStore.correct || {}).map(Number));
  /* 分母只算「已确认」（与 confirmedRate 同一口径）。
     旧实现拿「已做」当分母 —— 这一屏同时出现三处分母不同的百分比
     （本题 1/2=50%、今日卡片 1/1=100%、会话 1/1=100%）。
     现在取**客观题确认集合**（confirmedSets，与 KPI / 按章节表同一集合）：
     主观题确认不进客观正确率，也不会把薄弱章节的分母稀释掉。 */
  const verifiedIds = new Set(Array.from(confirmedSets(courseStore).objective).map(Number));
  const byChapter = new Map();
  items.forEach((item) => {
    if (!isQuestionDone(courseStore, item.id)) return;
    const id = Number(item.id);
    const isVerified = verifiedIds.has(id);
    const isCorrect = correctIds.has(id);
    textbookChapterKeys(item, items).forEach((key) => {
      const row = byChapter.get(key) || { key, name: key.startsWith("#") ? "未分章节" : key, done: 0, verified: 0, correct: 0 };
      row.done++;
      if (isVerified) row.verified++;
      if (isVerified && isCorrect) row.correct++;
      byChapter.set(key, row);
    });
  });
  return [...byChapter.values()]
    .filter((row) => row.verified >= WEAK_MIN_VERIFIED)
    .map((row) => ({ ...row, rate: confirmedRate(row.correct, row.verified) }))
    .sort((a, b) => a.rate - b.rate || b.verified - a.verified);
}

/* 一行章是否「够格被叫薄弱」——**全站唯一的判定入口**。
   深分析树的徽章、看板薄弱卡的门槛、生成会话的标题前缀都走这里，
   阈值与最小样本量统一取上面的常量（旧实现是 3 / 5 两个值）。
   传入的 row 至少要带 { verified, correct, done }（rate 会被重算）。 */
function isWeakRow(row) {
  const verified = Number(row.verified || 0);
  if (verified < WEAK_MIN_VERIFIED) return true;
  return confirmedRate(row.correct, verified) < 60;
}

/* 「只练这一章」的取题条件 —— 与「今日强化」段③（buildSmartPracticePlan 里那段）
   **逐字相同**，一个字都没加：按课本章名判章（textbookChapterKeys，跨卷册、
   一题多章）+ 排除已答对（!courseStore.correct?.[id]）。
   不新写算法：两处必须永远一致，所以只把它抽成一个函数给看板按钮复用。

   ⚠️ 这里是「课本章名」维度，不是 chapterId —— 同一个课本章在题库里被拆成
   5 个 chapterId（见上面 getWeakChapterRows 的注释），按 id 取会漏掉大部分题。 */
function weakChapterPracticeIds(chapterKey, courseStore = userCourseStore(), items = null) {
  const pool = items || (state.analysisQuestions.length ? state.analysisQuestions : state.questions);
  return pool
    .filter((item) => textbookChapterKeys(item).includes(chapterKey))
    .filter((item) => !courseStore.correct?.[item.id])
    .map((item) => item.id);
}

function renderLearningSignalPanel(courseStore) {
  const wrongReasonRows = buildSignalRows(state.storage.wrongReasons || {}, courseStore, "reason");
  const confidenceRows = buildSignalRows(state.storage.confidence || {}, courseStore, "confidence");
  const favoriteRows = buildFavoriteGroupRows(courseStore);
  if (!wrongReasonRows.length && !confidenceRows.length && !favoriteRows.length) {
    return `
      <div class="learning-signal-panel">
        <div class="trend-title">学习信号</div>
        <div class="muted">在题目右侧标记信心或错因后，这里会汇总成专项练习入口。</div>
      </div>
    `;
  }
  const renderRows = (rows, type) => rows.length ? rows.map((row) => `
    <button type="button" class="signal-chip" data-signal-type="${type}" data-signal-label="${escapeHtml(row.label)}">
      <span>${escapeHtml(row.label)}</span>
      <b>${row.count}</b>
    </button>
  `).join("") : `<span class="muted">暂无数据</span>`;
  return `
    <div class="learning-signal-panel">
      <div class="trend-title">学习信号</div>
      <div class="signal-grid">
        <div>
          <strong>错因分布</strong>
          <div class="signal-list">${renderRows(wrongReasonRows, "reason")}</div>
        </div>
        <div>
          <strong>信心分布</strong>
          <div class="signal-list">${renderRows(confidenceRows, "confidence")}</div>
        </div>
        <div>
          <strong>收藏分组</strong>
          <div class="signal-list">${renderRows(favoriteRows, "favorite")}</div>
        </div>
      </div>
    </div>
  `;
}

/* 收藏范围的**唯一**口径：当前科目。
   state.storage.favorite 是跨科目的（每条记录带 courseId），
   而同一屏上「收藏题」KPI 数全部科目、「收藏分组」chip 只数本科目 ——
   实测 11 vs 3，两个数字都叫「收藏题」。
   这里统一成「当前科目」，和同页的错题 / 题量 / 已做 一致；
   全部科目的总数仍可在 KPI 的 title 里看到（不丢信息）。 */
function favoriteIdsForCurrentCourse(courseId = state.currentCourse?.id) {
  const cid = Number(courseId || 0);
  const favorite = state.storage.favorite || {};
  return Object.keys(favorite)
    .filter((id) => !cid || Number(favorite[id]?.courseId || 0) === cid)
    .map(Number)
    .filter(Boolean);
}

function buildFavoriteGroupRows(courseStore) {
  const counts = new Map();
  const allowed = new Set(favoriteIdsForCurrentCourse());
  Object.entries(state.storage.favorite || {}).forEach(([id, item]) => {
    if (!allowed.has(Number(id))) return;
    const label = item?.group || "未分组";
    const row = counts.get(label) || { label, count: 0, ids: [] };
    row.count++;
    row.ids.push(Number(id));
    counts.set(label, row);
  });
  return [...counts.values()].sort((a, b) => b.count - a.count || a.label.localeCompare(b.label, "zh-CN"));
}

function buildSignalRows(source, courseStore, type) {
  const counts = new Map();
  const validIds = new Set([
    ...Object.keys(courseStore.done || {}),
    ...Object.keys(courseStore.wrong || {}),
    ...Object.keys(courseStore.correct || {}),
    ...Object.keys(courseStore.answers || {}),
    ...(state.analysisQuestions.length ? state.analysisQuestions : state.questions).map((item) => item.id),
  ].map(Number));
  Object.entries(source || {}).forEach(([id, label]) => {
    const clean = String(label || "").trim();
    if (!clean) return;
    if (validIds.size && !validIds.has(Number(id))) return;
    const row = counts.get(clean) || { label: clean, count: 0, ids: [] };
    row.count++;
    row.ids.push(Number(id));
    counts.set(clean, row);
  });
  return [...counts.values()].sort((a, b) => b.count - a.count || a.label.localeCompare(b.label, "zh-CN"));
}

function bindLearningSignalActions() {
  document.querySelectorAll("[data-signal-type][data-signal-label]").forEach((btn) => {
    btn.onclick = () => startSignalPractice(btn.dataset.signalType, btn.dataset.signalLabel).catch((err) => toast(err.message));
  });
}

function resetGeneratedPracticeFilters() {
  state.currentChapter = null;
  state.storage.profile.lastChapterId = 0;
  state.tagFilter = "";
  if ($("questionSearch")) $("questionSearch").value = "";
  if ($("typeSelect")) $("typeSelect").value = "";
  if ($("tagSelect")) $("tagSelect").value = "";
  if (state.chapters.length) renderChapters();
}

async function startSignalPractice(type, label) {
  if (!state.currentCourse || !label) return;
  const courseStore = userCourseStore();
  if (type === "favorite") {
    const match = buildFavoriteGroupRows(courseStore).find((row) => row.label === label);
    if (!match?.ids?.length) {
      toast("当前分组没有收藏题");
      return;
    }
    await enterSmartPractice({
      ids: match.ids,
      sourceTitle: `收藏：${label}`,
      type: "favorite",
      limit: 80,
      toastText: `已生成「${label}」收藏练习`,
    });
    return;
  }
  const source = type === "confidence" ? state.storage.confidence : state.storage.wrongReasons;
  const rows = buildSignalRows(source || {}, courseStore, type);
  const match = rows.find((row) => row.label === label);
  if (!match?.ids?.length) {
    toast("当前科目没有匹配题目");
    return;
  }
  await enterSmartPractice({
    ids: match.ids,
    sourceTitle: `${type === "confidence" ? "信心" : "错因"}：${label}`,
    type,
    limit: 80,
    toastText: `已生成「${label}」专项练习`,
  });
}

/* hero「开始/继续今日强化」。续哪条、开新的，都走 resumableTrainingSession ——
   和「继续上次」完全同一个判据，所以按钮上写「继续」时点下去必然续的就是
   「继续上次」点名的那条（旧实现按 type 反查，会续另一条题单）。 */
async function startSmartPractice(options = {}) {
  if (!state.currentCourse) return;
  refreshDailyTrainingState();
  if (!options.force) {
    const resumable = resumableTrainingSession();
    if (resumable) {
      await continueTrainingSession(resumable.id);
      return;
    }
  }
  if (!state.analysisQuestions.length) await loadAnalysisQuestions();
  const ids = buildSmartPracticeIds(userCourseStore(), { randomize: !!options.force });
  if (!ids.length) {
    toast("暂无可生成的智能练习");
    return;
  }
  await enterSmartPractice({
    ids,
    sourceTitle: "今日强化",
    type: "smart",
    toastText: `已生成今日强化（${state.questions.length || ids.length} 题）`,
  });
}

/* 学习看板「薄弱章节」卡 →「只练这一章」。
   **不新写取题算法**：走 weakChapterPracticeIds，即「今日强化」段③那一句
   （textbookChapterKeys 判课本章 + 排除 courseStore.correct），一题不多、一题不少。

   ⚠️ 会话 type 用 "weak"，**不复用 "smart"**：两条必须是不同的会话，
   否则点一次「只练这一章」就把今日强化的题单顶掉了，
   「继续上次」也分不清自己续的是哪一条（session id 不同 → 互不覆盖）。 */
async function startWeakChapterPractice(chapterKey) {
  if (!state.currentCourse || !chapterKey) return;
  if (!state.analysisQuestions.length) await loadAnalysisQuestions();
  const courseStore = userCourseStore();
  const ids = weakChapterPracticeIds(chapterKey, courseStore);
  if (!ids.length) {
    /* 正常走不到：该章没有可练的题时按钮已经是 disabled（见 renderWeakChapterCard）。
       只有「渲染之后数据又变了」才会命中 —— 照实说，不静默，也不生成空题单。 */
    toast("这一章已经没有「没做对过」的题了");
    renderProgress();
    return;
  }
  const items = state.analysisQuestions.length ? state.analysisQuestions : state.questions;
  const row = getWeakChapterRows(courseStore, items).find((item) => item.key === chapterKey);
  const name = row?.name || chapterKey;
  /* sourceTitle 要能认出是哪一章（「继续上次」/智能练习卡直接显示它）。
     前缀跟着卡上的展示判断走：真低于门槛才写「薄弱章节」，只是相对最弱就照实说。 */
  const prefix = row && chapterMastery(row).level !== "weak" ? "相对最弱章节" : "薄弱章节";
  await enterSmartPractice({
    ids,
    sourceTitle: `${prefix}：${name}`,
    type: "weak",
    toastText: `已生成「${name}」专项练习（${state.questions.length || ids.length} 题）`,
  });
}

async function exitSmartPractice() {
  if (state.mode !== "smart") return;
  syncCurrentTrainingSession();
  state.mode = "practice";
  /* 只清「我正在哪一轮」这个指针。题单本身还在会话列表里 ——
     用户随时能从学习看板的「继续」回到它（旧实现顺带把题单指针也清了，
     于是退出一次后「继续上次」变成「暂无题单」，与同屏还在的「继续继续」自相矛盾）。 */
  clearActiveTrainingSession();
  scheduleSave();
  await loadQuestions();
  toast("已退出智能练习");
}

async function exitTrainingSession() {
  syncCurrentTrainingSession();
  if (state.mode === "smart") {
    await exitSmartPractice();
    return;
  }
  clearActiveTrainingSession();
  await loadQuestions();
  toast("已退出本次训练");
}

/* 「继续上次」。目标由 resumableTrainingSession 唯一决定（和 hero、看板卡片同一判据）。
   没有就明确说没有 —— 不再偷偷生成一条新题单冒充「继续」。 */
async function continueSmartPractice() {
  if (!state.currentCourse) return;
  refreshDailyTrainingState();
  const resumable = resumableTrainingSession();
  if (!resumable) {
    toast("没有可继续的训练单，点「开始今日强化」新建一轮");
    if (state.mode === "training") renderTraining();
    else if (state.mode === "progress") renderProgress();
    return;
  }
  await continueTrainingSession(resumable.id);
}

/* ── 错题本「开始训练」 ────────────────────────────────────────────────
   为什么需要它：错题本原来只有「导出全部错题」一个按钮，唯一的纯错题训练入口是
   训练页的「今日错题复习」，而它只出**到期**的题 —— stage 0 的到期日是答错次日
   （REVIEW_INTERVAL_DAYS[0] = 1），所以今天刚错的题今天一道都练不了。
   这里按「当前筛选结果」组一轮训练，未到期的也进池。 */

function wrongBookSessionId(courseId = state.currentCourse?.id) {
  /* 稳定 id。进错题本会 clearActiveTrainingSession()，如果用 trainingSessionId()
     的时间戳+随机串，退出再进就是全新的一轮、进度看着丢了。
     稳定 id 让同一科目同一天的**第一批**错题训练是同一条记录 → 再点「继续上轮」
     续的就是它（题单不缩水、跳到上次那一题）。 */
  return `wrong-${Number(courseId || 0)}-${todayKey()}`;
}

/* 「按筛选新建」用的 id：**不复用**已有 id。
   旧实现无论如何都用那个稳定 id，于是「新建」会把上一批的题单/作答整批覆盖 ——
   用户点「按筛选新建 4 题」时，上一轮 32 题的记录就没了。新批次另起一个 id，
   旧记录原样留在训练记录里（可回看、可继续）。 */
function nextWrongBookSessionId(courseId = state.currentCourse?.id) {
  const base = wrongBookSessionId(courseId);
  const used = new Set((state.storage.trainingSessions || []).map((item) => item.id));
  if (!used.has(base)) return base;
  for (let n = 2; n < 100; n += 1) {
    const id = `${base}-${n}`;
    if (!used.has(id)) return id;
  }
  return `${base}-${Date.now()}`;
}

/* 本科目下**未完成**的错题本训练（最近更新的一条）。
   判据与 resumableTrainingSession 完全一致（不限于今天、终态排除、completed 收敛），
   只把范围收窄到「错题本训练」这一族：mode="wrong" 且 type="wrong"。

   ⚠️ 为什么必须带 type：`startDueWrongReview` 造的「今日错题复习」也是 mode="wrong"，
   只筛 mode 的话错题本会把它当成「上一轮错题本训练」—— 实测做完今日错题复习后，
   错题本面板显示「继续上轮（已答 0/3）」并续到 due 会话上，而用户以为续的是
   自己按筛选建的那一轮。两个入口题池不同（筛选结果 vs 只到期），不能互相冒充。

   用途：错题本面板分「继续上轮」/「按筛选新建」两个动作、以及 startWrongBookPractice
   在没显式要求新建时续上它。**不用于取题**：取题只看指针（见 getModeQuestionIds）。 */
function wrongBookSessionForCourse(courseId = state.currentCourse?.id) {
  return resumableTrainingSession(courseId, { mode: "wrong", type: "wrong" });
}

/* 题池 = 当前筛选结果（复用 wrongRecordMatchesFilter，与错题本列表同一套口径）。
   ignoreReview = true 时忽略「复习」这一项：空态里「立即训练全部 N 道」要在
   「一道都没到期」时也能开练 —— 这正是那个入口要救的场景。 */
function buildWrongBookPracticeIds(courseStore = userCourseStore(), options = {}) {
  const filters = options.ignoreReview
    ? { ...(state.wrongFilters || {}), review: "all" }
    : (state.wrongFilters || {});
  const records = getWrongRecordsForCurrentCourse(courseStore)
    .filter(([id, item]) => wrongRecordMatchesFilter(id, item, courseStore, filters));
  const recordById = new Map(records.map(([id, item]) => [Number(id), item]));
  /* 排序：到期优先 → 错得多的优先 → 先到期的优先 → 同章节不相邻。
     同章节去相邻**只在「到期 / 未到期」两组内部**做：整体打散会把未到期的题插到
     到期题中间，「到期优先」这个主键就名存实亡了（实测到期题被插到第 1/3/5 位）。 */
  const ordered = records
    .map(([id]) => Number(id))
    .filter(Boolean)
    .sort((a, b) => {
      const ia = recordById.get(a) || {};
      const ib = recordById.get(b) || {};
      const da = isReviewDue(ia) ? 0 : 1;
      const db = isReviewDue(ib) ? 0 : 1;
      if (da !== db) return da - db;
      const ca = Number(ia.count || 0);
      const cb = Number(ib.count || 0);
      if (ca !== cb) return cb - ca;
      const ta = reviewDueDate(ia).getTime();
      const tb = reviewDueDate(ib).getTime();
      if (ta !== tb) return ta - tb;
      return a - b;
    });
  const isDueId = (id) => isReviewDue(recordById.get(id) || {});
  return [
    ...spreadByChapter(ordered.filter(isDueId), recordById),
    ...spreadByChapter(ordered.filter((id) => !isDueId(id)), recordById),
  ];
}

/* 同章节不相邻：贪心重排，稳定 —— 只在「非这么排就相邻」时才打乱相对顺序。
   只剩同章节的尾巴时认了（不可能无解）。 */
function spreadByChapter(ids, recordById) {
  const keyOf = (id) => {
    const item = recordById.get(Number(id)) || {};
    return Number(item.chapterId || 0) || String(item.chapterName || "");
  };
  const rest = ids.slice();
  const out = [];
  let lastKey = null;
  while (rest.length) {
    let pick = rest.findIndex((id) => keyOf(id) !== lastKey);
    if (pick < 0) pick = 0;
    const [id] = rest.splice(pick, 1);
    lastKey = keyOf(id);
    out.push(id);
  }
  return out;
}

/* 错题训练单的唯一构造处。错题本「按筛选新建」和训练页「今日错题复习」原来
   各写一份字面量（字段一一对应），差别只有 id 生成方式、type 和 sourceTitle。
   合并后新增字段只改一处，也保证两条入口的作答作用域字段不会漏写
   （漏了就会拿上一轮的作答当本轮，答案直接摊开）。
   options.id / type / sourceTitle / ids 必填；currentIndex / lastSubjectId 由这里统一置 0。 */
function startWrongPracticeSession({ id, type, sourceTitle, ids }) {
  resetGeneratedPracticeFilters();
  const session = upsertTrainingSession({
    id,
    type,
    mode: "wrong",
    sourceTitle,
    courseId: state.currentCourse.id,
    courseName: state.currentCourse.name || "",
    ids,
    /* 干净的一轮：本轮作答另起一份（见 activeWrongPracticeSession）。
       answers 必须显式给空对象 —— upsertTrainingSession 是 { ...old, ...session }，
       不写的话会继承上一次同 id 会话里已经答过的题。 */
    answers: {},
    verifiedAnswers: {},
    results: {},
    currentIndex: 0,
    lastSubjectId: 0,
    status: "active",
  });
  state.mode = "wrong";
  /* 练习刚交卷就跑来练错题时 state.submitted 还是 true（它是**练习那一轮**的运行时状态，
     既不落盘也不按模式复位）—— 带进训练轮的话 shouldRevealCurrentAnswer 对每道题都返回
     true，等于把答案全摊开。新的训练轮一律从「未交卷」开始。 */
  state.submitted = false;
  state.answerVisible = false;
  setCoursePicker(false);
  return session;
}

async function startWrongBookPractice(options = {}) {
  if (!state.currentCourse) return;
  /* 空态里的「立即训练全部 N 道」= 忽略「复习」这一项开练。页面筛选也必须一起回到
     「全部」：只把题池放宽而列表仍被「到期」滤着的话，题确实发下去了，界面还是空的
     —— 文案承诺了多少道就该看到多少道。 */
  if (options.ignoreReview) {
    state.wrongFilters = { ...(state.wrongFilters || {}), review: "all" };
  }
  /* 两个动作必须分开（见 renderWrongFilters）：
     · 默认 → 本科目还有没做完的一轮就**续**它（保护进度，不重复冻结题单）；
     · options.newBatch（按钮上的「按筛选新建 N 道」）→ 明确要求按当前筛选开新的一批：
       先确认「离开旧批次」，旧记录**保留**（见 nextWrongBookSessionId）。
     旧实现只有一个入口：按钮按当前筛选写「开始训练 N 道」，点下去却续了旧会话 ——
     实测切到「错误次数≥3」（列表 4 题）后点它，进去仍是那 32 题的旧题单。 */
  if (!options.newBatch) {
    const openSession = wrongBookSessionForCourse();
    if (openSession) {
      await continueTrainingSession(openSession.id);
      return;
    }
  } else {
    /* 「按筛选新建」是**不可逆的上下文切换**（虽然旧记录保留），所以显式确认一次。
       按钮上写着两件事（继续上轮 N/M、按筛选新建 K 道），别让用户点错了才发现。 */
    const openSession = wrongBookSessionForCourse();
    if (openSession && !confirm(`按当前筛选新建一轮错题训练？\n\n上一轮还没做完（已答 ${openSession.done}/${openSession.total} 题）。它的记录会保留在训练记录里，随时可以继续。`)) {
      return;
    }
  }
  /* 题池 = 当前筛选结果的**全部**，不截断（旧实现的 .slice(0, 20) 已去掉）。
     按钮上的 N 就是这里的 ids.length：筛出 8 道就练 8 道，筛出 200 道就练 200 道。
     ⚠️ 筛选只作用于**新建批次**：正在训练时取题只看会话冻结的 ids（getModeQuestionIds）。 */
  const ids = buildWrongBookPracticeIds(userCourseStore(), { ignoreReview: !!options.ignoreReview });
  if (!ids.length) {
    toast(options.ignoreReview ? "错题本里还没有错题" : "当前筛选没有错题，换个筛选条件试试");
    return;
  }
  const sessionId = nextWrongBookSessionId();
  startWrongPracticeSession({ id: sessionId, type: "wrong", sourceTitle: "错题本训练", ids });
  await loadQuestions();
  toast(`已开始错题训练（${ids.length} 道）`);
}

async function startDueWrongReview() {
  if (!state.currentCourse) return;
  const servedToday = todayServedIds();
  const dueIds = getWrongRecordsForCurrentCourse(userCourseStore())
    .filter(([, item]) => isReviewDue(item) && !item.resolved)
    .map(([id]) => Number(id))
    .filter(Boolean);
  /* 同一天内不重复出题：今天已经在别的训练单里出现过的到期错题不再发一次。
     不补这一步的话，先做「今日强化」再做「今日错题复习」，
     强化 A/B 段那最多 22 道错题会原封不动再来一遍（到期题 ≤12 道时是 100% 重合）。
     只做错题复习、不碰强化的用户完全不受影响。 */
  const fresh = dueIds.filter((id) => !servedToday.has(Number(id)));
  if (!fresh.length) {
    toast(dueIds.length ? `今天到期的 ${dueIds.length} 道错题都已经练过了` : "今天没有到期错题");
    return;
  }
  startWrongPracticeSession({ id: trainingSessionId("due"), type: "due", sourceTitle: "今日错题复习", ids: fresh });
  /* 这里**不写** state.wrongFilters.review = "due"。
     旧实现写了，于是「点过今日错题复习 → 切错题本」时错题本的「复习」筛选被留在
     「到期」，今天刚错的题（stage 0 的到期日是次日）在错题本里全都不见 ——
     用户点「错题本」却看不到自己刚做错的题。
     而且这一写本来就是多余的：本次复习的题池由上面的 session.ids 冻结，
     getModeQuestionIds 在「指针指着一条 wrong 会话」时直接发 session.ids
     （不再和页面筛选求交集），筛选只作用于「按筛选新建」。
     只保留 status="active"：中途去错题本浏览时「状态」还是默认的「未解决」，
     不会停在上一次选的「已解决」上。 */
  state.wrongFilters = { ...(state.wrongFilters || {}), status: "active" };
  setCoursePicker(false);
  await loadQuestions();
  toast(fresh.length < dueIds.length
    ? `已进入今日错题复习（${fresh.length} 道，另有 ${dueIds.length - fresh.length} 道今天练过了）`
    : "已进入今日错题复习");
}

async function startSimilarWrongPractice() {
  if (!state.currentCourse) return;
  if (!state.analysisQuestions.length) await loadAnalysisQuestions();
  const ids = buildSimilarWrongIds(userCourseStore());
  if (!ids.length) {
    toast("暂无可扩展的同类题");
    return;
  }
  await enterSmartPractice({
    ids,
    sourceTitle: "同类题型练习",
    type: "similar",
    limit: 80,
    toastText: "已生成同类题型练习",
  });
}

function buildSimilarWrongIds(courseStore = userCourseStore()) {
  const allItems = state.analysisQuestions.length ? state.analysisQuestions : state.questions;
  const itemById = new Map(allItems.map((item) => [Number(item.id), item]));
  const seen = buildSeenIndex();
  const servedToday = todayServedIds();
  const records = getWrongRecordsForCurrentCourse(courseStore).filter(([, item]) => !item.resolved);
  const recordById = new Map(records.map(([id, item]) => [Number(id), item]));
  const countById = new Map(records.map(([id, item]) => [Number(id), Number(item.count || 0)]));
  /* 种子按「错得最多」排，但冷却期内的降到队尾。
     否则 count 最高的那 12 道永远是同一批种子，重练永远围着它们转。 */
  const seeds = orderCandidates(
    records.map(([id]) => id),
    seen,
    (a, b) => (countById.get(Number(b)) || 0) - (countById.get(Number(a)) || 0),
  ).slice(0, 12);
  const ids = [];
  const picked = new Set();
  const push = (id) => {
    const n = Number(id);
    if (!n || picked.has(n) || servedToday.has(n)) return false;
    picked.add(n);
    ids.push(n);
    return true;
  };
  /* 题干纯文本缓存：每个种子都要扫一遍题库，不缓存的话同一道题会被反复 strip。
     一次调用内复用（12 个种子 → 每道题只算一次），调用结束即释放。 */
  const textCache = new Map();
  const textOf = (item) => {
    const key = Number(item.id);
    const hit = textCache.get(key);
    if (hit !== undefined) return hit;
    const text = fastPlainQuestionText(item);
    textCache.set(key, text);
    return text;
  };

  seeds.forEach((seedId) => {
    const id = Number(seedId);
    const record = recordById.get(id) || {};
    push(id);
    /* 关键词从完整题干切，不再只用 record.title —— 那是题干前 120 字，
       长题干的区分词经常落在 120 字之后，同型题根本匹配不上。 */
    const seedItem = itemById.get(id);
    const keywords = extractKeywords(seedItem ? fastPlainQuestionText(seedItem) : record.title || "");
    const seedChapterId = Number(record.chapterId || seedItem?.chapterId || 0);
    /* 同章节 = 同一个「课本章」，按章名判，不按 chapter id —— 理由见 textbookChapterName。
       回落到裸 chapterName 只在章节树还没加载（chapterPathForId 返回空）时用，
       且必须两边都非空，否则空值相等会把全库都判成同章节。 */
    const seedChapterName = textbookChapterName(seedChapterId);
    const sameChapterOf = (item) => {
      if (seedChapterName) return textbookChapterName(item.chapterId) === seedChapterName;
      return !!record.chapterName && item.chapterName === record.chapterName;
    };
    /* 打分取前 N，不再「撞上条件就收」。
       旧实现按题库顺序取前 6 道，且只要命中一个关键词就算同类，
       实际抽到的常常是「同题型里题干碰巧含『万元』的前 6 道」——
       和薄弱章节/冲刺里已经修掉的是同一个毛病。 */
    const scored = [];
    for (const item of allItems) {
      const qid = Number(item.id);
      if (!qid || qid === id || picked.has(qid) || servedToday.has(qid)) continue;
      if (record.type && item.type !== record.type) continue;
      const sameChapter = sameChapterOf(item);
      let hits = 0;
      if (!sameChapter && keywords.length) {
        const text = textOf(item);
        for (const word of keywords) {
          if (text.includes(word)) hits++;
        }
      }
      if (!sameChapter && hits < SIMILAR_MIN_KEYWORD_HITS) continue;
      scored.push({ id: qid, score: (sameChapter ? 100 : 0) + hits });
    }
    scored.sort((a, b) => b.score - a.score);
    scored.slice(0, SIMILAR_PER_SEED).forEach((row) => push(row.id));
  });
  return ids;
}

function fastPlainQuestionText(item) {
  const raw = String(item.title || item.stem || item.detail?.stem || "");
  return raw.includes("<") ? stripText(raw) : raw;
}

/* 切关键词。中文没有空格，只按标点 split 会让整句变成一个超长 token 被长度过滤掉
   （实测「某公司2018年的营业收入为1000万元」整段 18 字，直接出局，关键词为空），
   所以还要在「中文 ↔ 数字/拉丁」的边界上再切一刀。 */
function extractKeywords(text) {
  return [...new Set(String(text || "")
    .replace(/([\u4e00-\u9fff])([0-9A-Za-z])/g, "$1 $2")
    .replace(/([0-9A-Za-z])([\u4e00-\u9fff])/g, "$1 $2")
    .replace(/[，。、“”‘’；：！？（）()《》【】\[\],.;:!?]/g, " ")
    .split(/\s+/)
    .map((item) => item.trim())
    .filter((item) => item.length >= 2 && item.length <= 12)
  )].slice(0, 8);
}

async function startSprintPractice() {
  if (!state.currentCourse) return;
  if (!state.analysisQuestions.length) await loadAnalysisQuestions();
  const ids = buildSprintPracticeIds(userCourseStore());
  if (!ids.length) {
    toast("暂无可生成的冲刺题");
    return;
  }
  await enterSmartPractice({
    ids,
    sourceTitle: "考前冲刺",
    type: "sprint",
    limit: 100,
    toastText: "已生成考前冲刺题",
  });
}

/* 考前冲刺的选题预算表。和今日强化同一套机制：每段有上限、新题有保底、跨天轮换
   （去重器 / 薄弱章段 / 新题段都共用 buildSmartPracticePlan 上面那三个零件）。 */
function buildSprintPracticePlan(courseStore = userCourseStore(), options = {}) {
  const randomize = !!options.randomize;
  const allItems = state.analysisQuestions.length ? state.analysisQuestions : state.questions;
  const seen = buildSeenIndex();
  const servedToday = todayServedIds();
  const { ids, picked, take } = createPracticePicker(servedToday);

  const counts = { wrong: 0, weak: 0, fresh: 0, neverDone: 0 };

  /* ① 近 7 天错题 / 反复错。「近 7 天」必须真有时间戳：旧实现的 `!at || ...`
     让没有时间戳的老错题永远算作「近 7 天错题」，冲刺题单里塞满陈年错题。 */
  const cutoff = Date.now() - 7 * 86400000;
  const wrongList = getWrongRecordsForCurrentCourse(courseStore)
    .filter(([, item]) => {
      if (item.resolved) return false;
      const at = parseDate(item.at || item.wrongAt);
      return (!!at && at.getTime() >= cutoff) || Number(item.count || 0) >= 2;
    })
    .map(([id]) => id);
  counts.wrong = take(orderCandidates(wrongList, seen), SPRINT_QUOTA.wrong);

  /* ② 薄弱章节：top6 章均分配额，章内按轮换排序（旧实现同样是 slice(0,10)）。 */
  counts.weak = takeWeakChapterIds({
    allItems,
    courseStore,
    weakChapters: getWeakChapterRows(courseStore, allItems).slice(0, 6),
    quota: SPRINT_QUOTA.weak,
    take,
    seen,
  });

  /* ③ 未做题保底：冲刺只给错题 25 + 弱章 35，剩下 40 个位置留给没做过的题。 */
  const freshQuota = Math.max(SPRINT_PRACTICE_FLOOR_NEW, SPRINT_PRACTICE_SIZE - ids.length);
  counts.fresh = takeFreshIds({ allItems, courseStore, quota: freshQuota, picked, servedToday, seen, randomize, take });

  if (ids.length < SPRINT_PRACTICE_SIZE) {
    counts.wrong += take(orderCandidates(wrongList, seen), SPRINT_PRACTICE_SIZE - ids.length);
  }

  const planIds = ids.slice(0, SPRINT_PRACTICE_SIZE);
  counts.neverDone = countNeverDone(planIds, courseStore);
  return { ids: planIds, counts };
}

function buildSprintPracticeIds(courseStore = userCourseStore()) {
  return buildSprintPracticePlan(courseStore).ids;
}

function renderDeepAnalysis(courseStore, items = state.questions) {
  /* 「做过」的判据与全站统一走 isQuestionDone（done ∪ correct ∪ wrong）：
     旧实现只看 done，旧版考场交卷（只写 correct/wrong）的题在这张表里
     全被算成「没做过」，和看板/薄弱章节的「已做」对不上。 */
  const isDone = (id) => isQuestionDone(courseStore, id);
  const correctIds = new Set(Object.keys(courseStore.correct || {}).map(Number));
  /* 分母取「客观题确认集合」，与 KPI 同一口径（confirmedSets）：
     确认记录必须还对得上当前答案，主观题不进客观正确率表 —— 否则同一屏里
     按章节表的分母会和 KPI 的分母对不上。 */
  const verifiedIds = new Set(Array.from(confirmedSets(courseStore).objective).map(Number));
  const byChapter = new Map();
  const byType = new Map();
  /* 「薄弱」判定必须按**课本章名**聚合，不能按 chapterId 逐行判。
     同一个课本章在题库里被拆成多份 chapter 记录（卷册各一份，实测 5 份），
     每份的样本量不同 —— 按 id 逐行判的话，同一个课本章会出现「一行薄弱、
     旁边同名一行稳定」这种自相矛盾的画面（用户没法互相对照）。
     所以这里额外按章名攒一份聚合统计，渲染时**每一行都取同名的聚合判定**。
     ⚠️ 只换判定依据，章节树的层级/展开折叠/点行跳章一律不动。 */
  const byTextbook = new Map();
  const textbookKeyOfChapterId = new Map();
  const ensureChapterRow = (node, depth) => {
    const id = Number(node?.id || 0);
    if (!id) return null;
    if (!byChapter.has(id)) {
      byChapter.set(id, {
        id,
        name: node.name || "未分章节",
        total: 0,
        done: 0,
        verified: 0,
        correct: 0,
        depth,
      });
    }
    return byChapter.get(id);
  };
  items.forEach((item) => {
    const id = Number(item.id);
    const isVerified = verifiedIds.has(id);
    const isCorrect = correctIds.has(id);
    const path = chapterPathForId(item.chapterId).slice(0, 2);
    const targets = path.length ? path : [{ id: item.chapterId || 0, name: item.chapterName || "未分章节" }];
    targets.forEach((node, index) => {
      const chapter = ensureChapterRow(node, index + 1);
      if (!chapter) return;
      chapter.total++;
      if (isDone(id)) chapter.done++;
      if (isVerified) chapter.verified++;
      if (isVerified && isCorrect) chapter.correct++;
    });
    /* 按课本章名聚合一份：同一个课本章的多个 chapterId 行共用这一份样本，
       判定（薄弱/稳定…）就从它算 —— 与「看板薄弱卡」「生成的题单」同一维度。
       一题多章时每个所属章各记一次（同 getWeakChapterRows 的约定）。 */
    textbookChapterKeys(item, items).forEach((key) => {
      const agg = byTextbook.get(key) || { key, name: key, done: 0, verified: 0, correct: 0 };
      agg.done++;
      if (isVerified) agg.verified++;
      if (isVerified && isCorrect) agg.correct++;
      byTextbook.set(key, agg);
      /* 记录「这个 chapterId 属于哪个课本章名」，好让树里每一行认领聚合判定。
         同一个 id 只认第一个章名即可（外挂映射下可能多个，取稳定排序的第一个）。 */
      targets.forEach((node) => {
        const cid = Number(node?.id || 0);
        if (!cid) return;
        if (!textbookKeyOfChapterId.has(cid)) textbookKeyOfChapterId.set(cid, key);
      });
    });

    const typeKey = item.subjectType ?? item.type ?? "题目";
    const type = byType.get(typeKey) || { id: typeKey, name: item.type || "题目", total: 0, done: 0, verified: 0, correct: 0 };
    type.total++;
    if (isDone(id)) type.done++;
    if (isVerified) type.verified++;
    if (isVerified && isCorrect) type.correct++;
    byType.set(typeKey, type);
  });
  const chapterRows = [...byChapter.values()]
    .sort((a, b) => {
      const aPath = chapterPathForId(a.id);
      const bPath = chapterPathForId(b.id);
      const aCode = aPath.map((node) => String(node.code || "").padEnd(8, "0")).join(".");
      const bCode = bPath.map((node) => String(node.code || "").padEnd(8, "0")).join(".");
      return aCode.localeCompare(bCode, "zh-CN") || (a.depth || 9) - (b.depth || 9);
    })
    /* 认领同课本章名的聚合统计：**只对 depth=2（课本章那一层）**生效 ——
       depth=1 是卷册行（名字是「章节练习」这种卷册名），既不是课本章，
       也不该跟着某一章被判「薄弱」。认领不到就沿用本行样本。 */
    .map((row) => {
      if (Number(row.depth) !== 2) return { ...row, textbookKey: null, weakStats: row };
      const key = textbookKeyOfChapterId.get(Number(row.id));
      const agg = key ? byTextbook.get(key) : null;
      return agg ? { ...row, textbookKey: key, weakStats: agg } : { ...row, textbookKey: null, weakStats: row };
    });
  const typeRows = [...byType.values()].sort((a, b) => String(a.name).localeCompare(String(b.name), "zh-Hans-CN"));
  return `
    <div class="analysis-detail-grid">
      <div class="analysis-table-card">
        <div class="trend-title">按章节正确率</div>
        ${renderAnalysisRows(chapterRows, true)}
      </div>
      <div class="analysis-table-card">
        <div class="trend-title">按题型正确率</div>
        ${renderAnalysisRows(typeRows, false)}
      </div>
    </div>
  `;
}

function renderAnalysisRows(rows, clickable) {
  if (!rows.length) return `<div class="muted">暂无统计数据</div>`;
  if (clickable) return renderAnalysisChapterTree(rows);
  return rows.map((row) => {
    /* 分母 = 已确认（唯一口径）；「已做」另列，两个数字不再混用。 */
    const rate = confirmedRate(row.correct, row.verified);
    const level = rate >= 80 ? "good" : rate >= 60 ? "warn" : "bad";
    const mastery = chapterMastery(row);
    return `
      <button type="button" class="analysis-row ${level}" disabled>
        <span>${escapeHtml(row.name)}</span>
        <em>${row.total}题 · 已做${row.done} · 对${row.correct}/${row.verified}确认</em>
        <i class="mastery-badge ${mastery.level}">${mastery.label}</i>
        <b>${rate}%</b>
      </button>
    `;
  }).join("");
}

/* 掌握度分级。判据与 confirmedRate 同口径（答对 ÷ 已确认）——
   旧实现用「已做」当分母，同一行里的 <b>rate%</b> 与 KPI 的正确率算法不同。
   「薄弱」这一档直接调 isWeakRow()：门槛（含最小样本量 WEAK_MIN_VERIFIED）
   全站只有那一处定义，这里不再自己写一份。 */
function chapterMastery(row) {
  const verified = Number(row.verified || 0);
  const rate = confirmedRate(row.correct, verified);
  if (!verified) return { label: Number(row.done || 0) ? "待确认" : "未开始", level: "none" };
  if (isWeakRow(row)) return { label: "薄弱", level: "weak" };
  if (rate < 80) return { label: "一般", level: "normal" };
  return { label: "稳定", level: "stable" };
}

function renderAnalysisChapterTree(rows) {
  const map = new Map(rows.map((row) => [Number(row.id), row]));
  const roots = (state.chapterTreeRoots?.length ? state.chapterTreeRoots : buildChapterTree(state.chapters))
    .filter((node) => map.has(Number(node.id)));
  const renderNode = (node, depth) => {
    if (depth > 2) return "";
    const row = map.get(Number(node.id));
    if (!row) return "";
    const children = (node.children || []).filter((child) => map.has(Number(child.id)));
    const expanded = state.analysisExpandedChapters.has(Number(node.id));
    const rate = confirmedRate(row.correct, row.verified);
    const level = rate >= 80 ? "good" : rate >= 60 ? "warn" : "bad";
    /* 徽章按**课本章名**判，不按本行 chapterId —— 否则同一个课本章被拆成的
       几行会一行「薄弱」一行「稳定」（各行样本量不同）。depth=2 的行都带
       weakStats（同名聚合），depth=1 是卷册行、没有课本章名，沿用本行样本。
       数字（em / rate%）仍是本行的，只有「薄弱」这个标签是整章的 —— 两处不再打架。 */
    const mastery = chapterMastery(row.weakStats || row);
    /* 整章聚合同一判定：给同名行挂 data-mastery-key，方便用户/探针核对「同名同判」。
       本行样本与整章不一致时补一句 title，说清这个标签是整章口径。 */
    const aggregateMismatch = row.weakStats && row.weakStats !== row
      && (Number(row.weakStats.verified || 0) !== Number(row.verified || 0));
    const badgeTitle = aggregateMismatch
      ? `按课本章名「${row.weakStats.name}」整体判定：${row.weakStats.correct}/${row.weakStats.verified} 确认`
      : "";
    return `
      <div class="analysis-tree-node level-${depth}">
        <div class="analysis-tree-row">
          <button type="button" class="analysis-toggle ${expanded ? "expanded" : "collapsed"}" data-analysis-toggle="${node.id}" ${children.length ? "" : "disabled"}>${children.length ? (expanded ? "−" : "+") : ""}</button>
          <button type="button" class="analysis-row ${level} ${depth === 1 ? "heading" : ""}" ${depth === 1 ? "disabled" : `data-analysis-chapter="${row.id}"`}${row.textbookKey ? ` data-mastery-key="${escapeHtml(row.textbookKey)}"` : ""}>
            <span>${escapeHtml(row.name)}</span>
            <em>${row.total}题 · 已做${row.done} · 对${row.correct}/${row.verified}确认</em>
            <i class="mastery-badge ${mastery.level}"${badgeTitle ? ` title="${escapeHtml(badgeTitle)}"` : ""}>${mastery.label}</i>
            <b>${rate}%</b>
          </button>
        </div>
        ${children.length && expanded ? `<div class="analysis-tree-children">${children.map((child) => renderNode(child, depth + 1)).join("")}</div>` : ""}
      </div>
    `;
  };
  return roots.map((node) => renderNode(node, 1)).join("") || `<div class="muted">暂无统计数据</div>`;
}

function bindDeepAnalysisActions() {
  document.querySelectorAll("[data-analysis-toggle]").forEach((btn) => {
    btn.onclick = () => {
      const id = Number(btn.dataset.analysisToggle || 0);
      if (!id) return;
      if (state.analysisExpandedChapters.has(id)) state.analysisExpandedChapters.delete(id);
      else state.analysisExpandedChapters.add(id);
      renderProgress();
    };
  });
  document.querySelectorAll("[data-analysis-chapter]").forEach((btn) => {
    btn.onclick = async () => {
      const chapterId = Number(btn.dataset.analysisChapter || 0);
      const chapter = state.chapters.find((item) => Number(item.id) === chapterId);
      if (chapter) {
        state.mode = "practice";
        renderMode();
        await selectChapter(chapter);
      }
    };
  });
}

function activeReviewPlan() {
  const courseId = Number(state.currentCourse?.id || 0);
  if (!courseId) return {};
  state.storage.plans ||= {};
  const key = String(courseId);
  if (!state.storage.plans[key] && state.storage.plan && (!state.storage.plan.courseId || Number(state.storage.plan.courseId) === courseId)) {
    state.storage.plans[key] = { ...state.storage.plan, courseId };
    state.storage.plan = null;
  }
  return state.storage.plans[key] || {};
}

function ensureReviewPlan(courseId = state.currentCourse?.id) {
  const id = Number(courseId || 0);
  if (!id) return {};
  state.storage.plans ||= {};
  state.storage.plans[String(id)] ||= { courseId: id, dailyLog: {} };
  return state.storage.plans[String(id)];
}

function saveActiveReviewPlan(plan) {
  const courseId = Number(state.currentCourse?.id || plan?.courseId || 0);
  if (!courseId) return;
  state.storage.plans ||= {};
  state.storage.plans[String(courseId)] = { ...(plan || {}), courseId };
  state.storage.plan = null;
}

function daysUntilExam(examDate) {
  const date = parseDate(examDate);
  if (!date) return 0;
  date.setHours(0, 0, 0, 0);
  return Math.max(1, Math.ceil((date.getTime() - startOfToday().getTime()) / 86400000));
}

function recentDailyAverage(courseId = state.currentCourse?.id, days = 7) {
  const id = String(courseId || "global");
  let total = 0;
  for (let i = 1; i <= days; i++) {
    const key = dateKey(addDays(startOfToday(), -i));
    total += Number(state.storage.dailyActivity?.[key]?.courses?.[id]?.total || 0);
  }
  return Math.round(total / days);
}

function reviewPlanSessionForToday(plan = activeReviewPlan()) {
  const key = todayKey();
  const id = plan.todaySessionId || "";
  const sessions = state.storage.trainingSessions || [];
  const matched = sessions.find((session) => session.id === id);
  if (matched && (matched.planKey === key || dateKey(parseDate(matched.createdAt) || new Date()) === key)) return matched;
  return sessions.find((session) =>
    session.type === "plan"
    && Number(session.courseId || 0) === Number(state.currentCourse?.id || 0)
    && (session.planKey === key || dateKey(parseDate(session.createdAt) || new Date()) === key)
  ) || null;
}

function buildReviewPlanPools(courseStore, items) {
  const allItems = items.length ? items : state.questions;
  const allIds = allItems.map((item) => Number(item.id)).filter(Boolean);
  const itemById = new Map(allItems.map((item) => [Number(item.id), item]));
  const wrongRecords = getWrongRecordsForCurrentCourse(courseStore).filter(([, item]) => !item.resolved);
  const dueIds = wrongRecords.filter(([, item]) => isReviewDue(item)).map(([id]) => Number(id)).filter(Boolean);
  const repeatedIds = wrongRecords.filter(([, item]) => Number(item.count || 0) >= 2).map(([id]) => Number(id)).filter(Boolean);
  const weakChapterKeys = new Set(getWeakChapterRows(courseStore, allItems).slice(0, 6).map((row) => row.key));
  const isWeakItem = (item) => textbookChapterKeys(item).some((key) => weakChapterKeys.has(key));
  const weakProblemIds = [];
  const weakUndoneIds = [];
  const undoneIds = [];
  allItems.forEach((item) => {
    const id = Number(item.id);
    if (!id) return;
    /* 「做过」的判据与全站其余地方统一走 isQuestionDone（done ∪ correct ∪ wrong）。
       旧实现只看 done：老数据（尤其**旧版考场交卷**只写 correct/wrong、不写 done）
       里做过的题会被算成「没做过」，今日计划把它们当新题再发一遍。 */
    const done = isQuestionDone(courseStore, id);
    const weak = isWeakItem(item);
    if (!done) {
      undoneIds.push(id);
      if (weak) weakUndoneIds.push(id);
    } else if (weak && (courseStore.wrong?.[id] || !courseStore.correct?.[id])) {
      weakProblemIds.push(id);
    }
  });
  const carryoverIds = getReviewPlanCarryoverIds(courseStore);
  return {
    allIds,
    itemById,
    dueIds,
    repeatedIds,
    weakProblemIds,
    weakUndoneIds,
    undoneIds,
    carryoverIds,
  };
}

function getReviewPlanCarryoverIds(courseStore) {
  /* 同 buildReviewPlanPools：做过 = done ∪ correct ∪ wrong，别只看 done。 */
  const isDone = (id) => isQuestionDone(courseStore, id);
  const ids = new Set((activeReviewPlan().carryoverIds || []).map(Number).filter(Boolean));
  const today = todayKey();
  (state.storage.trainingSessions || []).forEach((session) => {
    if (session.type !== "plan" || Number(session.courseId || 0) !== Number(state.currentCourse?.id || 0)) return;
    syncTrainingSessionStats(session);
    const key = session.planKey || dateKey(parseDate(session.createdAt) || new Date());
    if (key >= today || session.status === "completed") return;
    normalizeSessionIds(session.ids).forEach((id) => {
      if (!isDone(id)) ids.add(Number(id));
    });
  });
  return [...ids].filter((id) => !isDone(id));
}

function suggestDailyTarget(stats, items, pools, days) {
  const remainingNew = Math.max(0, (items.length || 0) - Number(stats.done || 0));
  const baseNew = days ? Math.ceil(remainingNew / days) : remainingNew;
  const wrongPressure = Math.ceil(pools.dueIds.length * 0.8) + Math.ceil(pools.repeatedIds.length * 0.25);
  const historyAverage = recentDailyAverage();
  const raw = historyAverage
    ? Math.ceil((baseNew + wrongPressure) * 0.68 + Math.max(historyAverage, baseNew) * 0.32)
    : baseNew + wrongPressure;
  if (remainingNew + pools.dueIds.length + pools.repeatedIds.length <= 0) return 0;
  if (days && days <= 7 && raw > 120) return raw;
  return Math.max(15, Math.min(120, raw));
}

function buildTodayReviewPlanTask(pools, target) {
  const effectiveTarget = Math.max(0, Number(target || 0));
  const cap = Math.max(effectiveTarget, Math.ceil(effectiveTarget * 1.3));
  const ids = [];
  const counts = { review: 0, weak: 0, new: 0, carryover: 0 };
  const seen = new Set();
  const isWeakId = (id) => {
    const item = pools.itemById.get(Number(id));
    if (!item) return false;
    return pools.weakProblemIds.includes(Number(id)) || pools.weakUndoneIds.includes(Number(id));
  };
  const push = (list, type) => {
    for (const raw of list || []) {
      const id = Number(raw);
      if (!id || seen.has(id)) continue;
      if (cap && ids.length >= cap) break;
      seen.add(id);
      ids.push(id);
      counts[type] = Number(counts[type] || 0) + 1;
    }
  };
  const importantCarryover = pools.carryoverIds.filter((id) => pools.dueIds.includes(id) || pools.repeatedIds.includes(id) || isWeakId(id));
  const ordinaryCarryover = pools.carryoverIds.filter((id) => !importantCarryover.includes(id));
  push(importantCarryover, "carryover");
  /* 到期题也受 cap 约束：旧实现的 allowOverCap 在到期题多于 cap 时整批灌入，
     把「每日目标」变成摆设，第二天继续积压。 */
  push(pools.dueIds, "review");
  push(pools.repeatedIds, "review");
  push(pools.weakProblemIds, "weak");
  push(pools.weakUndoneIds, "weak");
  push(ordinaryCarryover, "new");
  push(pools.undoneIds, "new");
  return { ids, counts, cap };
}

function buildReviewPlanOverview(stats, wrongTotal, items = state.questions) {
  const courseStore = userCourseStore();
  const plan = activeReviewPlan();
  const hasPlan = !!plan.examDate;
  const days = hasPlan ? daysUntilExam(plan.examDate) : 0;
  const pools = buildReviewPlanPools(courseStore, items);
  const suggestedTarget = suggestDailyTarget(stats, items, pools, days || 30);
  const manualTarget = Number(plan.dailyTarget || 0);
  const effectiveTarget = plan.dailyTargetManual && manualTarget ? manualTarget : suggestedTarget;
  const todaySession = reviewPlanSessionForToday(plan);
  if (todaySession) syncTrainingSessionStats(todaySession);
  const generatedTask = buildTodayReviewPlanTask(pools, effectiveTarget);
  const task = todaySession?.ids?.length
    ? { ids: normalizeSessionIds(todaySession.ids), counts: todaySession.taskCounts || generatedTask.counts, cap: generatedTask.cap }
    : generatedTask;
  const completionRate = items.length ? Math.round(Number(stats.done || 0) / items.length * 100) : 0;
  return {
    plan,
    hasPlan,
    days,
    pools,
    suggestedTarget,
    effectiveTarget,
    manualTarget,
    historyAverage: recentDailyAverage(),
    debt: pools.carryoverIds.length,
    task,
    todaySession,
    todayCompleted: todaySession?.status === "completed",
    completionRate,
    pressureHigh: suggestedTarget > 120 || task.ids.length > Math.max(effectiveTarget, Math.ceil(effectiveTarget * 1.3)),
    wrongTotal,
  };
}

/* 「今日计划」按钮的三态文案与禁用判据 —— 训练页卡片和看板复习计划面板
   各写过一份（表达式逐字相同），合并到这里，改口径时只改一处。 */
function planButtonLabel(overview) {
  if (overview.todaySession && !overview.todayCompleted) return "继续今日计划";
  if (overview.todayCompleted) return "今日计划已完成";
  return "开始今日计划";
}

function planButtonDisabled(overview) {
  /* hasPlan 必须算进来：看板那块面板在没设考试日期时也会渲染，此时 task 是按
     系统建议临时算出来的，不能点（训练页那张卡整块都不渲染，等价）。 */
  return !(overview.hasPlan && overview.task.ids.length && !overview.todayCompleted);
}

function renderReviewPlanPanel(stats, wrongTotal, items = state.questions) {
  const overview = buildReviewPlanOverview(stats, wrongTotal, items);
  const plan = overview.plan;
  const calendarDays = buildPlanCalendar(overview);
  const targetValue = plan.dailyTargetManual && plan.dailyTarget ? plan.dailyTarget : overview.suggestedTarget || "";
  const taskCount = overview.task.ids.length;
  return `
    <div class="review-plan-panel">
      <div class="trend-title">复习计划</div>
      <div class="plan-form">
        <label>
          <span>考试日期</span>
          <input id="examDateInput" type="date" value="${escapeHtml(plan.examDate || "")}">
        </label>
        <label>
          <span>每日目标</span>
          <input id="dailyTargetInput" type="number" min="1" step="1" value="${escapeHtml(targetValue)}" data-suggested-target="${overview.suggestedTarget}">
        </label>
        <button id="savePlanBtn" type="button">保存计划</button>
        <button id="startTodayPlanBtn" type="button" ${planButtonDisabled(overview) ? "disabled" : ""}>${planButtonLabel(overview)}</button>
      </div>
      <div class="plan-summary">
        ${overview.hasPlan
          ? `距离考试 ${overview.days} 天 · 系统建议 ${overview.suggestedTarget} 题/天 · 当前目标 ${overview.effectiveTarget} 题/天 · 欠账 ${overview.debt} 题 · 完成率 ${overview.completionRate}%${overview.pressureHigh ? " · 压力较高" : ""}`
          : `设置考试日期后生成动态计划 · 当前系统建议 ${overview.suggestedTarget || 0} 题/天`}
      </div>
      ${overview.hasPlan ? `
        <div class="plan-today-card">
          <div>
            <span>今日任务</span>
            <b>${taskCount} 题</b>
            <small>复习 ${overview.task.counts.review || 0} · 强化 ${overview.task.counts.weak || 0} · 新题 ${overview.task.counts.new || 0} · 结转 ${overview.task.counts.carryover || 0}</small>
          </div>
          <em>${overview.todayCompleted ? "已完成" : overview.todaySession ? "进行中" : "待开始"}</em>
        </div>
      ` : ""}
      <div class="plan-calendar">
        ${calendarDays.map((day) => `
          <label class="plan-day ${day.done ? "done" : ""}">
            <input type="checkbox" data-plan-day="${day.key}" data-new-task="${day.newTask}" data-review-task="${day.reviewTask}" data-weak-task="${day.weakTask}" ${day.done ? "checked" : ""}>
            <span>${day.label}</span>
            <em>总 ${day.totalTask} · 新题 ${day.newTask} · 复习 ${day.reviewTask} · 强化 ${day.weakTask}</em>
          </label>
        `).join("")}
      </div>
    </div>
  `;
}

function buildPlanCalendar(overview) {
  const result = [];
  const plan = overview.plan || {};
  const log = plan.dailyLog || {};
  const today = startOfToday();
  const futureDays = Math.max(1, (overview.days || 1) - 1);
  const todayNewLike = Number(overview.task.counts.new || 0) + Number(overview.task.counts.weak || 0);
  const remainingAfterToday = Math.max(0, overview.pools.undoneIds.length - todayNewLike);
  const futureNew = overview.hasPlan ? Math.min(overview.effectiveTarget || 0, Math.ceil(remainingAfterToday / futureDays)) : 0;
  for (let i = 0; i < 14; i++) {
    const date = addDays(today, i);
    const key = dateKey(date);
    const inPlanWindow = !overview.hasPlan || i < Math.max(overview.days, 1);
    const reviewTask = i === 0 ? Number(overview.task.counts.review || 0) + Number(overview.task.counts.carryover || 0) : 0;
    const weakTask = i === 0 ? Number(overview.task.counts.weak || 0) : Math.ceil(futureNew * 0.25);
    const newTask = i === 0 ? Number(overview.task.counts.new || 0) : Math.max(0, futureNew - weakTask);
    const totalTask = inPlanWindow ? reviewTask + weakTask + newTask : 0;
    result.push({
      key,
      label: i === 0 ? "今天" : `${date.getMonth() + 1}/${date.getDate()}`,
      newTask: totalTask ? newTask : 0,
      reviewTask: totalTask ? reviewTask : 0,
      weakTask: totalTask ? weakTask : 0,
      totalTask,
      done: !!log[key] || (i === 0 && overview.todayCompleted),
    });
  }
  return result;
}

function bindReviewPlanActions(stats, wrongTotal, totalItems = state.questions.length) {
  const saveBtn = $("savePlanBtn");
  if (saveBtn) {
    saveBtn.onclick = () => {
      const examDate = $("examDateInput").value;
      if (!examDate) {
        toast("请选择考试日期");
        return;
      }
      const items = state.analysisQuestions.length ? state.analysisQuestions : state.questions;
      const pools = buildReviewPlanPools(userCourseStore(), items);
      const suggestedTarget = suggestDailyTarget(stats, items, pools, daysUntilExam(examDate) || 30);
      const targetInput = $("dailyTargetInput");
      const previousSuggestedTarget = Number(targetInput?.dataset.suggestedTarget || 0);
      const typedTarget = Math.round(Number(targetInput?.value || 0));
      const userChangedTarget = typedTarget > 0 && typedTarget !== previousSuggestedTarget;
      const dailyTarget = Math.max(1, userChangedTarget ? typedTarget : suggestedTarget);
      const overview = buildReviewPlanOverview(stats, wrongTotal, items);
      const currentPlan = activeReviewPlan();
      saveActiveReviewPlan({
        ...currentPlan,
        examDate,
        courseId: state.currentCourse?.id,
        dailyTarget,
        dailyTargetManual: userChangedTarget && dailyTarget !== suggestedTarget,
        lastSuggestedTarget: suggestedTarget,
        lastRebalancedAt: nowText(),
        carryoverIds: overview.pools.carryoverIds,
        createdAt: currentPlan.createdAt || nowText(),
        dailyLog: currentPlan.dailyLog || {},
        snapshot: {
          total: totalItems,
          done: stats.done,
          correct: stats.correct,
          wrong: wrongTotal,
        },
      });
      scheduleSave();
      renderProgress();
      toast("复习计划已保存");
    };
  }
  const startBtn = $("startTodayPlanBtn");
  if (startBtn) startBtn.onclick = () => startTodayReviewPlan().catch((err) => toast(err.message));
  document.querySelectorAll("[data-plan-day]").forEach((checkbox) => {
    checkbox.onchange = () => {
      const plan = ensureReviewPlan();
      plan.dailyLog ||= {};
      const key = checkbox.dataset.planDay;
      if (checkbox.checked) {
        plan.dailyLog[key] = {
          newDone: Number(checkbox.dataset.newTask || 0),
          reviewDone: Number(checkbox.dataset.reviewTask || 0),
          weakDone: Number(checkbox.dataset.weakTask || 0),
          at: nowText(),
        };
      } else {
        delete plan.dailyLog[key];
      }
      scheduleSave();
      checkbox.closest(".plan-day")?.classList.toggle("done", checkbox.checked);
    };
  });
}

async function startTodayReviewPlan() {
  if (!state.currentCourse) return;
  if (!state.analysisQuestions.length) await loadAnalysisQuestions();
  const stats = getCourseStats();
  const items = state.analysisQuestions.length ? state.analysisQuestions : state.questions;
  const overview = buildReviewPlanOverview(stats, Object.keys(userCourseStore().wrong || {}).length, items);
  if (!overview.hasPlan) {
    toast("请先在学习看板设置考试日期");
    return;
  }
  if (overview.todaySession && overview.todaySession.status !== "completed") {
    await continueTrainingSession(overview.todaySession.id);
    return;
  }
  const ids = normalizeSessionIds(overview.task.ids);
  if (!ids.length) {
    toast("今日计划暂无题目");
    return;
  }
  await enterSmartPractice({
    ids,
    sourceTitle: "今日计划",
    type: "plan",
    toastText: "已生成今日计划",
    extra: (session) => {
      session.planKey = todayKey();
      session.taskCounts = overview.task.counts;
      saveActiveReviewPlan({
        ...activeReviewPlan(),
        courseId: state.currentCourse.id,
        todaySessionId: session.id,
        lastSuggestedTarget: overview.suggestedTarget,
        lastRebalancedAt: nowText(),
        carryoverIds: overview.pools.carryoverIds,
      });
      scheduleSave();
    },
  });
}

function todayKey() {
  return dateKey(new Date());
}

function getTodayActivity(courseId = state.currentCourse?.id) {
  const day = state.storage.dailyActivity?.[todayKey()];
  if (!day) return { total: 0, verified: 0, correct: 0, rate: 0, durationMs: 0 };
  /* 正确率口径统一走 confirmedRate（分母 = 已确认题数），别在这里再手写一份
     Math.round(correct/verified*100) —— 两处已经各写一份，改口径时会漏。 */
  if (!courseId) {
    const correct = Number(day.correct || 0);
    const verified = Number(day.verified || 0);
    return { total: Number(day.total || 0), verified, correct, rate: confirmedRate(correct, verified), durationMs: Number(day.durationMs || 0) };
  }
  const course = day.courses?.[String(courseId)] || {};
  const verified = Number(course.verified || 0);
  const correct = Number(course.correct || 0);
  return {
    total: Number(course.total || 0),
    verified,
    correct,
    rate: confirmedRate(correct, verified),
    durationMs: Number(course.durationMs || 0),
  };
}

function dateKey(d) {
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/* 学习时长的人类可读文案（S6）。
   ⚠️ 不足 1 分钟时**不能**说「不到 1 分钟」：这个函数吃的是 ms，
   实测打开一题又立刻切走只花了 412ms，卡片上照样写「用时 不到 1 分钟」——
   读起来像「练了快一分钟」，其实连一秒都不到。按秒说实话。 */
function formatDuration(ms) {
  const totalSeconds = Math.max(0, Math.floor(Number(ms || 0) / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  if (minutes < 1) return `${totalSeconds} 秒`;
  if (minutes < 60) return `${minutes} 分钟`;
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return rest ? `${hours} 小时 ${rest} 分` : `${hours} 小时`;
}

/* 错题本导航项上的「待复习」角标（S11）。
   到期数在 isReviewDue 里算好但只在用户主动打开训练页时才可见，这里提到导航上。 */
function updateWrongBadge() {
  const buttons = document.querySelectorAll('.nav-item[data-mode="wrong"]');
  const count = (!state.user || isAdmin() || !state.currentCourse)
    ? 0
    : getWrongRecordsForCurrentCourse(peekCourseStore()).filter(([, item]) => isReviewDue(item)).length;
  buttons.forEach((btn) => {
    if (count > 0) btn.dataset.badge = count > 99 ? "99+" : String(count);
    else delete btn.dataset.badge;
  });
}

/* 用户端数据导出/备份（S4）。
   此前全项目只有管理端能下载数据，学员无法自己备份进度。 */
function exportUserData() {
  if (!state.user) return;
  const payload = {
    exportedAt: new Date().toISOString(),
    user: state.user,
    revision: state.storageRevision,
    data: state.storage,
  };
  const blob = new Blob([JSON.stringify(payload, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = `yunxi-${state.user}-${dateKey(new Date())}.json`;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
  toast("已导出备份文件");
}

/* 从备份恢复（导出按钮的另一半）。
   以前只能导出、没有回灌路径 —— 全站文件输入只收 .zip（管理员专用），
   用户以为存了备份，真要恢复时没有任何入口。

   流程：选文件 → 本地先粗校验形状 → 让用户确认「会覆盖」→ POST /api/user/import?force=1
   → 成功后整页重新读一遍数据（不就地改 state.storage，避免和内存里那套缓存不同步）。 */
function importUserDataFromFile() {
  const input = $("importDataInput");
  if (!input) return;
  input.value = "";
  input.click();
}

async function handleImportFile(file) {
  if (!file) return;
  if (file.size > 32 * 1024 * 1024) {
    toast("备份文件超过 32MB");
    return;
  }
  let payload;
  try {
    payload = JSON.parse(await file.text());
  } catch (err) {
    toast("不是有效的 JSON 文件");
    return;
  }
  const data = payload && typeof payload === "object" && payload.data && typeof payload.data === "object"
    ? payload.data
    : payload;
  /* 形状不对就别发请求了：落盘会把进度换成垃圾，这一层比服务端那道更早拦住手滑。 */
  if (!data || typeof data !== "object" || Array.isArray(data) || !("courses" in data)) {
    toast("这不是云习题库的备份文件");
    return;
  }
  const fromUser = typeof payload?.user === "string" ? payload.user.trim() : "";
  const exportedAt = typeof payload?.exportedAt === "string" ? payload.exportedAt : "";
  const courseCount = Object.keys(data.courses || {}).length;
  const wrongCount = Object.keys(data.wrong || {}).length;
  const lines = [
    "恢复备份会**整体替换**当前账号的进度，之后新做的题会丢失。",
    "",
    `备份来自：${fromUser || "未知账号"}${exportedAt ? `（${exportedAt.slice(0, 10)} 导出）` : ""}`,
    `内容：${courseCount} 门科目 · ${wrongCount} 条错题记录`,
    "",
    "建议先点「导出我的数据」把当前进度存一份，再恢复。",
    "",
    "确定继续吗？",
  ];
  if (!confirm(lines.join("\n"))) return;

  const btn = $("importDataBtn");
  if (btn) { btn.disabled = true; btn.textContent = "正在恢复…"; }
  try {
    const res = await api(`/api/user/import?force=1&user=${encodeURIComponent(state.user)}`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "If-Match": `"${state.storageRevision || ""}"` },
      body: JSON.stringify(data),
    });
    if (!res?.ok) throw new Error(res?.error || "恢复失败");
    /* 整页重读：内存里的 courses/answers/questionChapter 缓存都要按新数据重建，
       就地改 state.storage 会留下对不上号的旧缓存。 */
    toast("备份已恢复，正在重新加载…");
    setTimeout(() => window.location.reload(), 600);
  } catch (err) {
    toast(err?.message || "恢复失败");
  } finally {
    if (btn) { btn.disabled = false; btn.textContent = "从备份恢复"; }
  }
}

function bindAccountMenuActions() {
  const btn = $("exportDataBtn");
  if (btn) btn.onclick = () => exportUserData();
  const importBtn = $("importDataBtn");
  const importInput = $("importDataInput");
  if (importBtn) importBtn.onclick = importUserDataFromFile;
  if (importInput) {
    importInput.onchange = () => {
      const file = importInput.files && importInput.files[0];
      handleImportFile(file);
    };
  }
}

/* 「近期正确率趋势」的数据源解析。
   storage.history 每条 = 一次交卷：练习卷自带 done/correct；考场卷只存
   {ref: <examHistory id>, at}，分数一律回 examHistory 取 —— 那里是考试记录的
   **唯一事实来源**（旧实现两边各存一份，趋势图只读 history，改一处另一边不知道）。
   兼容三种老/新数据：
     · ref 能解析           → 用 examHistory 的 done/correct（新数据）
     · 没有 ref 但自带数字   → 直接用（本批之前写下的 mode:"exam" 条目，不丢）
     · ref 解析不到（那一场已被 examHistory 的 50 条上限裁掉）→ 若条目里还留着
                              数字就用它；彻底没数就跳过这一点，不编造。 */
function trendSeriesForCourse(courseId = state.currentCourse?.id, limit = 20) {
  const examById = new Map((state.storage.examHistory || []).map((item) => [String(item.id), item]));
  const rows = (state.storage.history || [])
    .filter((item) => !courseId || Number(item.courseId || 0) === Number(courseId))
    .slice(0, limit)
    .reverse();
  const points = [];
  rows.forEach((item) => {
    let source = item;
    if (item.ref) {
      source = examById.get(String(item.ref));
      if (!source) source = (item.done != null || item.correct != null) ? item : null;
    }
    if (!source) return;
    const done = Number(source.done || 0);
    const correct = Number(source.correct || 0);
    points.push({
      mode: item.mode || "practice",
      at: item.at || source.at || "",
      done,
      correct,
      rate: done ? Math.round((correct / done) * 100) : 0,
    });
  });
  return points;
}

function renderProgressTrend() {
  const canvas = $("progressTrend");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  const history = trendSeriesForCourse();
  const w = canvas.width;
  const h = canvas.height;
  // 颜色全部从主题令牌取，切换配色后重画即可跟上
  const chartBg = themeToken("--bg") || "#f8fafc";
  const chartGrid = themeToken("--border") || "#e2e8f0";
  const chartMuted = themeToken("--muted") || "#64748b";
  const chartLine = themeToken("--primary") || "#6265ef";
  const chartPoint = themeToken("--card") || "#ffffff";
  const chartLabel = themeToken("--text") || "#0f172a";
  ctx.clearRect(0, 0, w, h);
  ctx.fillStyle = chartBg;
  ctx.fillRect(0, 0, w, h);
  ctx.strokeStyle = chartGrid;
  ctx.lineWidth = 1;
  for (let i = 0; i <= 4; i++) {
    const y = 18 + i * ((h - 44) / 4);
    ctx.beginPath();
    ctx.moveTo(36, y);
    ctx.lineTo(w - 18, y);
    ctx.stroke();
  }
  ctx.fillStyle = chartMuted;
  ctx.font = "13px Inter, Noto Sans SC, Microsoft YaHei, sans-serif";
  ctx.fillText("100%", 4, 22);
  ctx.fillText("0%", 14, h - 24);
  if (!history.length) {
    ctx.fillStyle = chartMuted;
    ctx.fillText("暂无提交记录", Math.max(36, w / 2 - 42), h / 2 + 4);
    return;
  }
  const points = history.map((item, index) => {
    /* rate 由 trendSeriesForCourse 按「答对 ÷ 本次已作答」算好（同一口径只算一处）。 */
    const rate = Number(item.rate || 0);
    const x = history.length === 1 ? w / 2 : 42 + index * ((w - 72) / (history.length - 1));
    const y = 18 + (100 - rate) * ((h - 44) / 100);
    return { x, y, rate };
  });
  ctx.strokeStyle = chartLine;
  ctx.lineWidth = 3;
  ctx.beginPath();
  points.forEach((point, index) => {
    if (index) ctx.lineTo(point.x, point.y);
    else ctx.moveTo(point.x, point.y);
  });
  ctx.stroke();
  points.forEach((point) => {
    ctx.beginPath();
    ctx.fillStyle = chartPoint;
    ctx.arc(point.x, point.y, 5, 0, Math.PI * 2);
    ctx.fill();
    ctx.strokeStyle = chartLine;
    ctx.lineWidth = 2;
    ctx.stroke();
  });
  const last = points[points.length - 1];
  ctx.fillStyle = chartLabel;
  ctx.fillText(`${last.rate}%`, Math.min(w - 54, last.x + 8), Math.max(16, last.y - 8));
}

async function renderAdminDashboard() {
  document.body.classList.add("admin-view");
  setCourseTitle("管理员数据看板");
  setText("courseMeta", "查看账号进度、管理题库更新");
  $("adminDashboard").classList.remove("hidden");
  $("questionView").classList.add("hidden");
  $("answerCard").innerHTML = "";
  updateStats();
  if (!state.adminCourses.length) {
    await loadAdminCourses().catch((err) => {
      console.warn("admin course load failed", err);
      state.adminCourses = [];
    });
  }

  const users = state.users.filter((item) => item.name.toLowerCase() !== "admin");
  const results = await Promise.all(users.map(async (item) => {
    try {
      const res = await api(`/api/user/load?user=${encodeURIComponent(item.name)}`);
      const data = normalizeStorage(res.data || {});
      const meta = state.users.find((user) => user.name.toLowerCase() === res.user.toLowerCase()) || {};
      const summary = buildUserSummary(res.user, data, meta);
      // 该用户被分配的题库（null = 全部可见）
      try {
        const scope = await api(`/api/admin/user-courses?user=${encodeURIComponent(state.user)}&target=${encodeURIComponent(item.name)}`);
        summary.assignedCourses = scope.assigned === null || scope.assigned === undefined ? null : scope.assigned;
      } catch (err) {
        summary.assignedCourses = undefined;
      }
      return summary;
    } catch (err) {
      console.warn("admin dashboard user load failed", item.name, err);
      return null;
    }
  }));
  const rows = results.filter(Boolean);
  state.adminRows = rows;
  state.adminFailedCount = users.length - rows.length;
  renderAdminRows(rows, users.length - rows.length);
}

/* 管理端的正确率：与学员端同一口径 —— 分母优先用「已确认」（verified），
   老数据没有 verified 时才退回 done。旧实现一律 correct/done，于是同一个人
   在学员端看到 80%、管理员看到 53%（答过但没确认的题把分母撑大了）。 */
function adminRate(correct, verified, done) {
  const denom = Number(verified || 0) || Number(done || 0);
  return denom ? Math.round((Number(correct || 0) / denom) * 100) : 0;
}

function buildUserSummary(user, data, meta = {}) {
  const courseStores = Object.values(data.courses || {});
  const done = uniqueCount(courseStores.flatMap((course) => Object.keys(course.done || {})));
  const verified = uniqueCount(courseStores.flatMap((course) => Object.keys(course.verified || {})));
  const correct = uniqueCount(courseStores.flatMap((course) => Object.keys(course.correct || {})));
  const wrong = uniqueCount(courseStores.flatMap((course) => Object.keys(course.wrong || {})));
  const rate = adminRate(correct, verified, done);
  const history = Array.isArray(data.history) ? data.history : [];
  const courseStats = buildUserCourseStats(data);
  return {
    user,
    disabled: !!meta.disabled,
    lastLoginAt: data.profile.lastLoginAt || "未记录",
    lastCourseName: data.profile.lastCourseName || history[0]?.courseName || "未开始",
    done,
    correct,
    wrong,
    rate,
    favorite: Object.keys(data.favorite || {}).length,
    notes: Object.keys(data.notes || {}).filter((id) => data.notes[id]).length,
    corrections: (Array.isArray(data.corrections) ? data.corrections : []).filter((item) => item.status !== "deleted").length,
    historyCount: history.length,
    lastPracticeAt: history[0]?.at || "未记录",
    courseStats,
    correctionsList: Array.isArray(data.corrections) ? data.corrections : [],
  };
}

function buildUserCourseStats(data) {
  const result = {};
  Object.entries(data.courses || {}).forEach(([courseId, course]) => {
    const id = Number(courseId);
    if (!id) return;
    const done = Object.keys(course.done || {}).length;
    const verified = Object.keys(course.verified || {}).length;
    const correct = Object.keys(course.correct || {}).length;
    const wrong = Object.keys(course.wrong || {}).length;
    const courseName = (data.history || []).find((item) => Number(item.courseId || 0) === id)?.courseName
      || (Number(data.profile?.lastCourseId || 0) === id ? data.profile.lastCourseName : "")
      || (state.courses.find((item) => Number(item.id) === id)?.name)
      || `课程${id}`;
    result[id] = {
      id,
      name: courseName,
      done,
      correct,
      wrong,
      verified,
      rate: adminRate(correct, verified, done),
      lastSubjectId: course.lastSubjectId || 0,
    };
  });
  return result;
}

function renderAdminRows(rows, failedCount = 0) {
  state.adminView = state.adminView || "users";
  const viewMeta = {
    users: { title: "账号管理", desc: "查看进度、停用账号、重置密码" },
    courses: { title: "课程进度", desc: "按课程横向查看学习情况" },
    corrections: { title: "纠错反馈", desc: "处理用户提交的题目问题" },
    banks: { title: "题库更新", desc: "按课程拉取或上传更新" },
    bankEditor: { title: "题库编辑", desc: "在线修改题干、选项与解析" },
    data: { title: "数据管理", desc: "整包上传下载题库和用户数据" },
  };
  const currentMeta = viewMeta[state.adminView] || viewMeta.users;
  $("adminDashboard").innerHTML = `
    <div class="admin-shell">
      <div class="admin-toolbar">
        <div class="admin-toolbar-main">
          <button id="adminAddUserBtn" class="primary-action" type="button">添加用户</button>
          <button id="adminRefreshBtn" type="button">刷新看板</button>
        </div>
        <div class="admin-view-tabs" role="tablist" aria-label="管理视图">
          <button class="admin-view-btn ${state.adminView === "users" ? "active" : ""}" data-admin-view="users" type="button">账号</button>
          <button class="admin-view-btn ${state.adminView === "courses" ? "active" : ""}" data-admin-view="courses" type="button">课程</button>
          <button class="admin-view-btn ${state.adminView === "corrections" ? "active" : ""}" data-admin-view="corrections" type="button">纠错</button>
          <button class="admin-view-btn ${state.adminView === "banks" ? "active" : ""}" data-admin-view="banks" type="button">更新</button>
          <button class="admin-view-btn ${state.adminView === "bankEditor" ? "active" : ""}" data-admin-view="bankEditor" type="button">编辑</button>
          <button class="admin-view-btn ${state.adminView === "data" ? "active" : ""}" data-admin-view="data" type="button">数据</button>
        </div>
        ${failedCount ? `<span class="admin-warning">${failedCount} 个用户数据加载失败，已跳过</span>` : ""}
      </div>
      <div class="admin-view-head">
        <div>
          <strong>${escapeHtml(currentMeta.title)}</strong>
          <span>${escapeHtml(currentMeta.desc)}</span>
        </div>
        <div class="admin-view-head-meta">
          <span>用户 ${rows.length}</span>
          <span>课程 ${state.adminCourses.length || state.courses.length || 0}</span>
        </div>
      </div>
      <div class="admin-view-body">
        ${state.adminView === "courses" ? renderAdminCourseTable(rows) : state.adminView === "banks" ? renderAdminBankTable() : state.adminView === "bankEditor" ? renderAdminBankEditorPanel() : state.adminView === "corrections" ? renderAdminCorrectionTable(rows) : state.adminView === "data" ? renderAdminDataPanel() : renderAdminUserTable(rows)}
      </div>
    </div>
  `;
  $("adminAddUserBtn").onclick = openAdminUserDialog;
  if ($("adminRefreshBtn")) {
    $("adminRefreshBtn").onclick = async () => {
      /* 刷新看板会整块重绘，编辑区的草稿只活在 DOM 里（UI-06）。 */
      if (!await confirmBankEditorLeave("刷新管理看板")) return;
      renderAdminDashboard().catch((err) => toast(err.message));
    };
  }
  document.querySelectorAll("[data-admin-view]").forEach((btn) => {
    btn.onclick = async () => {
      const nextView = btn.dataset.adminView;
      /* 切管理页签也是重绘编辑区 —— 有草稿先给保存/放弃/取消。
         同页签再点一次同样会重建输入框，所以也走这一道（没草稿就直接返回，不重绘）。 */
      if (state.adminView === "bankEditor") {
        const reason = nextView === "bankEditor" ? "刷新编辑页" : "切换到其它管理页签";
        if (!await confirmBankEditorLeave(reason)) return;
        if (nextView === state.adminView && !isBankEditorDirty()) return;
      }
      state.adminView = nextView;
      renderAdminRows(state.adminRows, state.adminFailedCount);
    };
  });
  if ($("adminBankSearch")) {
    $("adminBankSearch").oninput = () => {
      state.adminBankQuery = $("adminBankSearch").value;
      refreshAdminBankTableDebounced();
    };
  }
  if ($("adminBankCategory")) {
    $("adminBankCategory").onchange = () => {
      state.adminBankCategory = $("adminBankCategory").value;
      const options = courseFilterOptions(state.adminCourses || []);
      // 换了一级分类，二级分类若不在新范围里就清掉
      if (state.adminBankSubcategory && state.adminBankCategory
          && !options.subsOf(state.adminBankCategory).includes(state.adminBankSubcategory)) {
        state.adminBankSubcategory = "";
      }
      // 就地重建二级下拉（不整块重渲染，免得把搜索框里的输入法组字打断）
      const subSelect = $("adminBankSubcategory");
      if (subSelect) {
        subSelect.innerHTML = `<option value="">全部二级分类</option>`
          + categorySubOptionsHtml(options, state.adminBankCategory, state.adminBankSubcategory);
        subSelect.value = state.adminBankSubcategory;
      }
      refreshAdminBankTable();
    };
  }
  if ($("adminBankSubcategory")) {
    $("adminBankSubcategory").onchange = () => {
      state.adminBankSubcategory = $("adminBankSubcategory").value;
      refreshAdminBankTable();
    };
  }
  if ($("adminBankStatus")) {
    $("adminBankStatus").onchange = () => {
      state.adminBankStatus = $("adminBankStatus").value;
      refreshAdminBankTable();
    };
  }
  if ($("adminBankToggleAll")) {
    $("adminBankToggleAll").onclick = () => {
      const tops = courseFilterOptions(state.adminCourses || []).tops;
      const anyCollapsed = tops.some((name) => state.adminBankCollapsed[name]);
      state.adminBankCollapsed = {};
      if (!anyCollapsed) tops.forEach((name) => { state.adminBankCollapsed[name] = true; });
      refreshAdminBankTable();
    };
  }
  if ($("adminBankCheckUpdate")) {
    $("adminBankCheckUpdate").onclick = (event) => checkVisibleCourseUpdates(event.target).catch((err) => toast(err.message));
  }
  bindAdminBankUpdateButtons();
  bindAdminBankEditorActions();
  // 刷新页面 / 切回「更新」页签时，把服务端正在跑的拉取重新接上（进度记在服务端进程里）
  resumeBankPullProgress();
  document.querySelectorAll("[data-admin-action]").forEach((btn) => {
    btn.onclick = () => adminUserAction(btn.dataset.user, btn.dataset.adminAction).catch((err) => toast(err.message));
  });
  document.querySelectorAll("[data-admin-assign]").forEach((btn) => {
    btn.onclick = () => openCourseAssign(btn.dataset.adminAssign);
  });
  document.querySelectorAll("[data-correction-action]").forEach((btn) => {
    btn.onclick = () => adminCorrectionAction(btn.dataset.user, btn.dataset.correctionId, btn.dataset.correctionAction).catch((err) => toast(err.message));
  });
  bindAdminDataActions();
}

function bindAdminBankUpdateButtons() {
  ensureAdminBankUpdateMode().catch(() => {});
  // 一级分类表头可折叠：点一下收起/展开该分类下的课
  document.querySelectorAll("[data-bank-group]").forEach((row) => {
    row.onclick = () => {
      const name = row.dataset.bankGroup;
      if (state.adminBankCollapsed[name]) delete state.adminBankCollapsed[name];
      else state.adminBankCollapsed[name] = true;
      refreshAdminBankTable();
    };
  });
  const closeReport = document.querySelector("[data-bank-report-close]");
  if (closeReport) {
    closeReport.onclick = () => {
      state.adminBankLastReport = null;
      state.adminBankLastReportAt = "";
      renderAdminRows(state.adminRows, state.adminFailedCount);
    };
  }
  document.querySelectorAll("[data-update-course]").forEach((btn) => {
    btn.onclick = () => {
      updateQuestionBank(Number(btn.dataset.updateCourse || 0), btn).catch((err) => {
        toast(err.message);
        renderAdminRows(state.adminRows, state.adminFailedCount);
      });
    };
  });
}

async function ensureAdminBankUpdateMode() {
  if (state.adminBankUpdateMode || state.user.toLowerCase() !== "admin") return state.adminBankUpdateMode;
  if (state.adminBankUpdateModePromise) return state.adminBankUpdateModePromise;
  const sample = (state.adminCourses || []).find((course) => !!course.owned && Number(course.questionCount || 0) > 0);
  if (!sample) return "";
  state.adminBankUpdateModePromise = (async () => {
    const query = new URLSearchParams({ user: state.user, courseId: String(sample.id), dryRun: "true" });
    try {
      const result = await api(`/api/admin/update-bank?${query}`, { method: "POST" });
      state.adminBankUpdateMode = result.reserved || result.mode === "upload" ? "upload" : "pull";
    } finally {
      // 这条只是探测更新方式（dryRun），但往返期间可能有别的课正在拉取：顺手把列表接回来
      await syncBankPullsFromServer(0).catch(() => {});
    }
    refreshAdminBankTable();
    return state.adminBankUpdateMode;
  })();
  try {
    return await state.adminBankUpdateModePromise;
  } finally {
    state.adminBankUpdateModePromise = null;
  }
}

// ---- 按用户分配题库 --------------------------------------------------------

function openCourseAssign(user) {
  state.adminCourseAssign = { user, selected: new Set(), query: "", category: "", subcategory: "", all: false };
  api(`/api/admin/user-courses?user=${encodeURIComponent(state.user)}&target=${encodeURIComponent(user)}`)
    .then((scope) => {
      const assign = state.adminCourseAssign;
      if (!assign || assign.user !== user) return;
      assign.all = scope.assigned === null || scope.assigned === undefined;
      (scope.assigned || []).forEach((id) => assign.selected.add(Number(id)));
      renderCourseAssignOverlay();
    })
    .catch((err) => toast(err.message));
}

function closeCourseAssign() {
  state.adminCourseAssign = null;
  const overlay = $("courseAssignOverlay");
  if (overlay) overlay.remove();
}

const ASSIGN_LIST_CAP = 400;

function assignFilteredCourses(assign) {
  const courses = state.adminCourses || [];
  const query = (assign.query || "").trim().toLowerCase();
  const top = assign.category || "";
  return courses.filter((course) => {
    if (top && course.category !== top) return false;
    if (assign.subcategory && course.subcategory !== assign.subcategory) return false;
    if (!query) return true;
    return [course.name, course.category, course.subcategory, course.id]
      .some((value) => String(value || "").toLowerCase().includes(query));
  });
}

// 两级分组渲染；最多渲染 ASSIGN_LIST_CAP 门，避免一次插上万条 DOM
function assignGroupedHtml(list) {
  const assign = state.adminCourseAssign;
  let budget = ASSIGN_LIST_CAP;
  const renderItem = (course) => {
    /* 未下载的课 questionCount 恒为 0，原来一律写「0 题」——
       分配浮层里 13 门课全是「0 题」，像数据坏了，其实只是本地没这个题库。
       说清原因，别让管理员以为这些课真的没题。 */
    const count = Number(course.questionCount || 0);
    return `
    <label class="assign-item">
      <input type="checkbox" data-assign-course="${course.id}" ${assign.selected.has(Number(course.id)) ? "checked" : ""}>
      <span>${escapeHtml(course.name)}</span>
      <small class="${count ? "" : "is-missing"}">${count ? `${count} 题` : "未下载题库"}</small>
    </label>`;
  };
  return groupCoursesByCategory(list).map((group) => {
    if (budget <= 0) return "";
    const subsHtml = group.subs.map((sub) => {
      if (budget <= 0) return "";
      const items = sub.list.slice(0, budget);
      budget -= items.length;
      return `<div class="assign-subgroup"><span>${escapeHtml(sub.name)}</span><small>${sub.list.length} 门</small></div>`
        + items.map(renderItem).join("");
    }).join("");
    return `<div class="assign-group"><span>${escapeHtml(group.name)}</span><small>${group.count} 门</small></div>${subsHtml}`;
  }).join("");
}

function assignFootText(assign, listCount) {
  return assign.all
    ? "保存后该用户可见全部有内容的题库"
    : `共选中 ${assign.selected.size} 门（筛选结果 ${listCount} 门，列表最多显示 ${ASSIGN_LIST_CAP} 门）`;
}

// 只刷新列表/统计，**不重建浮层**——整块重建会把输入框换掉，中文输入法的组字就被打断了
function refreshAssignList() {
  const assign = state.adminCourseAssign;
  if (!assign) return;
  const list = assignFilteredCourses(assign);
  const box = $("assignList");
  if (box) box.innerHTML = assignGroupedHtml(list) || `<div class="admin-empty">没有匹配的题库</div>`;
  const foot = $("assignFootText");
  if (foot) foot.textContent = assignFootText(assign, list.length);
  const scope = $("assignHeadScope");
  if (scope) scope.textContent = assign.all ? "当前：全部可见" : `当前：${assign.selected.size} 门`;
  bindAssignCheckboxes();
}

const refreshAssignListDebounced = debounce(() => refreshAssignList(), 160);

function bindAssignCheckboxes() {
  const overlay = $("courseAssignOverlay");
  if (!overlay) return;
  overlay.querySelectorAll("[data-assign-course]").forEach((box) => {
    box.onchange = () => {
      const assign = state.adminCourseAssign;
      if (!assign) return;
      const id = Number(box.dataset.assignCourse);
      if (box.checked) assign.selected.add(id); else assign.selected.delete(id);
      assign.all = false;
      const foot = $("assignFootText");
      if (foot) foot.textContent = assignFootText(assign, assignFilteredCourses(assign).length);
      const scope = $("assignHeadScope");
      if (scope) scope.textContent = `当前：${assign.selected.size} 门`;
    };
  });
}

function renderCourseAssignOverlay() {
  const assign = state.adminCourseAssign;
  if (!assign) return;
  let overlay = $("courseAssignOverlay");
  if (!overlay) {
    overlay = document.createElement("div");
    overlay.id = "courseAssignOverlay";
    overlay.className = "assign-overlay";
    document.body.appendChild(overlay);
  }
  const options = courseFilterOptions(state.adminCourses || []);
  const top = assign.category || "";
  const list = assignFilteredCourses(assign);
  overlay.innerHTML = `
    <div class="assign-card">
      <div class="assign-head">
        <div>
          <b>给「${escapeHtml(assign.user)}」分配题库</b>
          <span id="assignHeadScope">${assign.all ? "当前：全部可见" : `当前：${assign.selected.size} 门`}</span>
        </div>
        <button id="assignClose" type="button">×</button>
      </div>
      <div class="assign-tools">
        <input id="assignSearch" value="${escapeHtml(assign.query || "")}" placeholder="搜索题库名称、分类或ID">
        <select id="assignCategory" title="一级分类">
          <option value="">全部一级分类</option>
          ${options.tops.map((name) => `<option value="${escapeHtml(name)}" ${top === name ? "selected" : ""}>${escapeHtml(name)}</option>`).join("")}
        </select>
        <select id="assignSubcategory" title="二级分类">
          <option value="">全部二级分类</option>
          ${categorySubOptionsHtml(options, top, assign.subcategory)}
        </select>
        <button id="assignPickFiltered" type="button">选中当前筛选</button>
        <button id="assignAll" type="button">全部可见</button>
        <button id="assignNone" type="button">全不选</button>
      </div>
      <div class="assign-list" id="assignList">
        ${assignGroupedHtml(list) || `<div class="admin-empty">没有匹配的题库</div>`}
      </div>
      <div class="assign-foot">
        <span id="assignFootText">${assignFootText(assign, list.length)}</span>
        <button id="assignSave" class="primary-action" type="button">保存</button>
        <button id="assignCancel" type="button">取消</button>
      </div>
    </div>
  `;

  // 搜索框：中文输入法组字期间不碰 DOM，组字结束再刷新列表
  const search = $("assignSearch");
  let composing = false;
  search.addEventListener("compositionstart", () => { composing = true; });
  search.addEventListener("compositionend", () => {
    composing = false;
    assign.query = search.value;
    refreshAssignList();
  });
  search.oninput = () => {
    assign.query = search.value;
    if (composing) return;   // 组字中：动了 DOM 中文就打不出来
    refreshAssignListDebounced();
  };

  // 级联下拉：只重建二级选项 + 刷新列表，不整块重渲染
  $("assignCategory").onchange = () => {
    assign.category = $("assignCategory").value;
    if (assign.subcategory && assign.category && !options.subsOf(assign.category).includes(assign.subcategory)) {
      assign.subcategory = "";
    }
    const sub = $("assignSubcategory");
    if (sub) {
      sub.innerHTML = `<option value="">全部二级分类</option>`
        + categorySubOptionsHtml(options, assign.category, assign.subcategory);
      sub.value = assign.subcategory;
    }
    refreshAssignList();
  };
  $("assignSubcategory").onchange = () => {
    assign.subcategory = $("assignSubcategory").value;
    refreshAssignList();
  };
  $("assignPickFiltered").onclick = () => {
    assign.all = false;
    assignFilteredCourses(assign).forEach((course) => assign.selected.add(Number(course.id)));
    refreshAssignList();
  };
  $("assignAll").onclick = () => { assign.all = true; assign.selected.clear(); refreshAssignList(); };
  $("assignNone").onclick = () => { assign.all = false; assign.selected.clear(); refreshAssignList(); };
  $("assignClose").onclick = closeCourseAssign;
  $("assignCancel").onclick = closeCourseAssign;
  bindAssignCheckboxes();
  $("assignSave").onclick = () => saveCourseAssign().catch((err) => toast(err.message));
}

async function saveCourseAssign() {
  const assign = state.adminCourseAssign;
  if (!assign) return;
  /* 必须带上 ?user=：后端 user-courses 的管理员判定读的是这个查询参数，
     漏掉就会直接 403「admin only」（GET 那两处一直带着，只有这里漏了）。 */
  const result = await api(`/api/admin/user-courses?user=${encodeURIComponent(state.user)}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ user: assign.user, courses: assign.all ? null : [...assign.selected] }),
  });
  toast(result.assigned === null
    ? `「${assign.user}」现在可见全部题库`
    : `已给「${assign.user}」分配 ${result.assigned.length} 门题库`);
  closeCourseAssign();
  await renderAdminDashboard();
}

function renderAdminUserTable(rows) {
  if (!rows.length) return `<div class="admin-empty">暂无普通用户数据</div>`;
  return `
    <div class="admin-user-list">
      ${rows.map((row) => `
        <section class="admin-user-card ${row.disabled ? "is-disabled" : ""}">
          <div class="admin-user-card-head">
            <div>
              <div class="admin-user-name-row">
                <b>${escapeHtml(row.user)}</b>
                <span class="status-pill ${row.disabled ? "disabled" : "active"}">${row.disabled ? "停用" : "正常"}</span>
              </div>
              <div class="admin-user-meta">
                <span>最近登录：${escapeHtml(row.lastLoginAt)}</span>
                <span>最近题库：${escapeHtml(row.lastCourseName)}</span>
                <span>最近提交：${escapeHtml(row.lastPracticeAt)}</span>
                <span>题库：${row.assignedCourses === null || row.assignedCourses === undefined ? "全部" : `${row.assignedCourses.length} 门`}</span>
              </div>
            </div>
            <div class="admin-actions admin-user-actions">
              <button data-admin-assign="${escapeHtml(row.user)}">分配题库</button>
              <button data-admin-action="${row.disabled ? "enable" : "disable"}" data-user="${escapeHtml(row.user)}">${row.disabled ? "启用" : "停用"}</button>
              <button data-admin-action="reset-password" data-user="${escapeHtml(row.user)}">重置密码</button>
              <button data-admin-action="clear-data" data-user="${escapeHtml(row.user)}">清空数据</button>
              <button class="danger" data-admin-action="delete" data-user="${escapeHtml(row.user)}">删除</button>
            </div>
          </div>
          <div class="admin-user-metrics">
            <div><b>${row.done}</b><span>做题</span></div>
            <div><b>${row.rate}%</b><span>正确率</span></div>
            <div><b>${row.wrong}</b><span>错题</span></div>
            <div><b>${row.favorite}</b><span>收藏</span></div>
            <div><b>${row.notes}</b><span>笔记</span></div>
            <div><b>${row.corrections}</b><span>纠错</span></div>
          </div>
        </section>
      `).join("")}
    </div>
  `;
}

function renderAdminCourseTable(rows) {
  const courses = collectAdminCourses(rows);
  if (!courses.length) return `<div class="admin-empty">暂无课程做题数据</div>`;
  return `
    <div class="admin-table-wrap admin-course-wrap">
      <table class="admin-table admin-course-table">
        <thead>
          <tr>
            <th>账号</th>
            ${courses.map((course) => `<th title="${escapeHtml(course.name)}">${escapeHtml(course.name)}</th>`).join("")}
          </tr>
        </thead>
        <tbody>
          ${rows.map((row) => `
            <tr>
              <td class="admin-user-cell">${escapeHtml(row.user)}</td>
              ${courses.map((course) => {
                const stat = row.courseStats?.[course.id];
                return `<td>${stat && stat.done ? `<b>${stat.done}</b><span>${stat.rate}% · 错${stat.wrong}</span>` : "<span>未开始</span>"}</td>`;
              }).join("")}
            </tr>
          `).join("")}
          <tr class="admin-course-total">
            <td>课程合计</td>
            ${courses.map((course) => {
              const stats = rows.map((row) => row.courseStats?.[course.id]).filter(Boolean);
              const done = stats.reduce((sum, item) => sum + item.done, 0);
              const verified = stats.reduce((sum, item) => sum + Number(item.verified || 0), 0);
              const correct = stats.reduce((sum, item) => sum + item.correct, 0);
              const rate = adminRate(correct, verified, done);
              return `<td><b>${done}</b><span>平均 ${rate}%</span></td>`;
            }).join("")}
          </tr>
        </tbody>
      </table>
    </div>
  `;
}

function collectCorrections(rows) {
  return rows.flatMap((row) => (row.correctionsList || [])
    .filter((item) => item.status !== "deleted")
    .map((item) => ({ ...item, user: row.user })))
    .sort((a, b) => String(b.at || "").localeCompare(String(a.at || "")));
}

function renderAdminCorrectionTable(rows) {
  const corrections = collectCorrections(rows);
  if (!corrections.length) return `<div class="admin-empty">暂无纠错反馈</div>`;
  return `
    <div class="admin-table-wrap">
      <table class="admin-table admin-correction-table">
        <thead>
          <tr>
            <th>状态</th>
            <th>账号</th>
            <th>题库/章节</th>
            <th>题目</th>
            <th>问题类型</th>
            <th>说明</th>
            <th>提交时间</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody>
          ${corrections.map((item) => `
            <tr>
              <td data-label="状态"><span class="status-pill ${item.status === "resolved" ? "active" : "disabled"}">${item.status === "resolved" ? "已处理" : "待处理"}</span></td>
              <td data-label="账号" class="admin-user-cell">${escapeHtml(item.user)}</td>
              <td data-label="题库/章节"><div>${escapeHtml(item.courseName || "")}<span>${escapeHtml(item.chapterName || "")}</span></div></td>
              <td data-label="题目"><div><b>${escapeHtml(String(item.questionNo || item.questionId || ""))}</b><span>${escapeHtml(item.title || "")}</span></div></td>
              <td data-label="问题类型">${escapeHtml(item.type || "其他")}</td>
              <td data-label="说明" class="correction-note">${escapeHtml(item.note || "")}</td>
              <td data-label="提交时间">${escapeHtml(item.at || "未记录")}</td>
              <td data-label="操作" class="admin-actions">
                <button data-bank-editor-open="${escapeHtml(item.questionId || "")}" data-editor-course="${escapeHtml(item.courseId || "")}">编辑题库</button>
                <button data-correction-action="${item.status === "resolved" ? "reopen" : "resolve"}" data-user="${escapeHtml(item.user)}" data-correction-id="${escapeHtml(item.id)}">${item.status === "resolved" ? "转待处理" : "标记处理"}</button>
                <button class="danger" data-correction-action="delete" data-user="${escapeHtml(item.user)}" data-correction-id="${escapeHtml(item.id)}">删除</button>
              </td>
            </tr>
          `).join("")}
        </tbody>
      </table>
    </div>
  `;
}

function collectAdminCourses(rows) {
  const map = new Map();
  rows.forEach((row) => {
    Object.values(row.courseStats || {}).forEach((course) => {
      if (!map.has(course.id)) map.set(course.id, { id: course.id, name: course.name });
    });
  });
  return [...map.values()].sort((a, b) => a.id - b.id);
}

// 拉取后立刻给出对比报告（原「题库管理器」的预检并入这里）：新增/消失题数 + 本地改动受影响情况
function renderAdminBankReport(report) {
  if (!report || !report.after) return "";
  const before = report.before || {};
  const after = report.after || {};
  const edits = report.localEdits || {};
  const courseName = report.courseName || "";
  const targetText = report.target === "pulled" ? "拉取库（未购课程）" : "主库";
  const chips = [
    `<span class="bank-report-chip added">新增 ${Number(report.addedSubjects || 0)} 题</span>`,
    `<span class="bank-report-chip removed">消失 ${Number(report.removedSubjects || 0)} 题</span>`,
    Number(report.addedChapters || 0) || Number(report.removedChapters || 0)
      ? `<span class="bank-report-chip">章节 +${Number(report.addedChapters || 0)} / -${Number(report.removedChapters || 0)}</span>`
      : "",
    `<span class="bank-report-chip">题量 ${Number(before.subjects || 0)} → ${Number(after.subjects || 0)}</span>`,
  ].filter(Boolean).join("");
  const editLine = Number(edits.subjects || 0) || Number(edits.chapters || 0) || Number(edits.orphans || 0)
    ? `<p class="bank-report-edit">本地改动：重贴 ${Number(edits.subjects || 0)} 题 / ${Number(edits.chapters || 0)} 个章节${
        Number(edits.orphans || 0) ? `；${Number(edits.orphans || 0)} 处对应的题目已不在新题库里（记录保留）` : ""}</p>`
    : `<p class="bank-report-edit muted">本地改动：无</p>`;
  return `
    <div class="bank-report">
      <div class="bank-report-head">
        <b>更新报告${courseName ? `：${escapeHtml(courseName)}` : ""}</b>
        <span>写入 ${escapeHtml(targetText)} · ${escapeHtml(state.adminBankLastReportAt || "")}</span>
        <button type="button" class="bank-report-close" data-bank-report-close="1">关闭</button>
      </div>
      <div class="bank-report-chips">${chips}</div>
      ${editLine}
    </div>
  `;
}

// 二级分类下拉的选项：选定一级时只列它的二级，否则按一级用 optgroup 分组列全
function categorySubOptionsHtml(options, top, selected) {
  if (top) {
    return options.subsOf(top)
      .map((name) => `<option value="${escapeHtml(name)}" ${selected === name ? "selected" : ""}>${escapeHtml(name)}</option>`)
      .join("");
  }
  return options.allSubs().map(({ top: groupName, subs }) =>
    `<optgroup label="${escapeHtml(groupName)}">${subs
      .map((name) => `<option value="${escapeHtml(name)}" ${selected === name ? "selected" : ""}>${escapeHtml(name)}</option>`)
      .join("")}</optgroup>`).join("");
}

/* ── 拉取进度（可以同时显示多门课）─────────────────────────────────────
   拉取的 POST 本身是同步的（要几分钟），所以进度另走一个只读端点：
   POST 飞行期间每 700ms 轮询 /api/admin/update-bank/progress（**不带 courseId = 读全部**）。
   服务端对每门课各记一条，排在写闸门口等的那些也有记录（stage=queued），
   所以同时更新多门课时，每一门都有自己的位置和阶段，互不覆盖。
   阶段名与计数是服务端上报的精确值；百分比是按阶段权重折算的**估算**
   （题多的课与图多的课，两个大头占比差很多，没法预先算准），界面上如实标注。 */
const BANK_PULL_POLL_MS = 700;
// 结束的行还留多久（毫秒）再撤掉：让「完成 · 用时 N 秒」/失败原因看得见，不至于一闪而过
const BANK_PULL_DONE_DWELL_MS = 8000;
// 点过按钮但服务端还没登记的宽限时间（毫秒）：POST 会先向上游问一次"有没有更新"，等几秒很正常
const BANK_PULL_UNKNOWN_GRACE_MS = 8000;
let bankPullPollTimer = null;
let bankPullDwellTimer = null;
let bankPullPollBusy = false;
let bankPullResumeAt = 0;

function formatElapsed(seconds) {
  const total = Math.max(0, Math.round(Number(seconds) || 0));
  if (total < 60) return `${total} 秒`;
  const m = Math.floor(total / 60);
  const s = total % 60;
  return s ? `${m} 分 ${s} 秒` : `${m} 分`;
}

// 一行 = 一门课。state.adminBankPulls 是 courseId -> 行，不是单槽，多门课同时更新各占一行
function bankPullRows() {
  const map = state.adminBankPulls || {};
  return Object.keys(map)
    .map((key) => map[key])
    .filter((pull) => !!pull)
    // 按发起时间升序（先点的在上面）：位置稳定，别的课结束时不会跳来跳去
    .sort((a, b) => (a.startedAtLocal || 0) - (b.startedAtLocal || 0)
      || Number(a.courseId) - Number(b.courseId));
}

function bankPullCourseName(courseId) {
  const course = (state.adminCourses || []).find((item) => Number(item.id) === Number(courseId));
  return course?.name || `课程 ${courseId}`;
}

/* 这一行的终止态只能由服务端说了算：active=false。
   ok=true →「完成 · 用时 N 秒」；ok=false → 失败 + 服务端给的中文原因。
   （旧实现在这里不看 active/ok/error，服务端早就 done 了界面还在写"正在更新"。） */
function renderBankPullRow(pull) {
  const name = pull.courseName || bankPullCourseName(pull.courseId);
  const pct = Math.max(0, Math.min(100, Number(pull.percent) || 0));
  /* 进行中按本地时钟算已用（服务端的 elapsedSeconds 是"最后一次上报到现在"，两个阶段之间会停住不动）；
     结束时用服务端那条记录的总时长（精确的拉取耗时），别把轮询延迟算进去。 */
  const localElapsed = (Date.now() - (pull.startedAtLocal || Date.now())) / 1000;
  const serverElapsed = Number.isFinite(Number(pull.elapsedSeconds)) ? Number(pull.elapsedSeconds) : null;
  const elapsed = pull.done && serverElapsed !== null ? serverElapsed : localElapsed;

  if (pull.done) {
    const okTitle = pull.ok ? `更新完成：${name}` : `更新失败：${name}`;
    const okRight = pull.ok ? `完成 · 用时 ${formatElapsed(elapsed)}` : "失败";
    const bits = pull.ok
      ? [`完成`, `用时 ${formatElapsed(elapsed)}`]
      : [`失败`, pull.error || "更新失败（服务端没给出原因）"];
    bits.push(`已用 ${formatElapsed(elapsed)}`);
    return `
      <div class="bank-progress ${pull.ok ? "done" : "failed"}" data-bank-pull="${pull.courseId}">
        <div class="bank-progress-head">
          <b>${escapeHtml(okTitle)}</b>
          <span>${escapeHtml(okRight)}</span>
        </div>
        <div class="bank-progress-track"><i style="width:${pull.ok ? 100 : 0}%"></i></div>
        <div class="bank-progress-meta">
          <span>${escapeHtml(bits.join(" · "))}</span>
          <span class="bank-progress-hint">${pull.ok ? "稍后自动从列表移除" : "详情见服务端日志"}</span>
        </div>
      </div>
    `;
  }

  /* 进行中（含排队）。"排队中"是服务端上报的 stage，跟"服务端没数据"是两回事：
     前者照实写阶段名，后者才提示进度接口没数据。 */
  const bits = [pull.stageLabel || "准备中"];
  if (!pull.queued && pull.total > 0) bits.push(`${pull.current} / ${pull.total}`);
  if (pull.detail) bits.push(pull.detail);
  bits.push(`已用 ${formatElapsed(elapsed)}`);
  const unknown = !!pull.unknown;
  const hint = unknown
    ? (elapsed * 1000 < BANK_PULL_UNKNOWN_GRACE_MS
      ? "正在连接服务器…"
      : "进度接口暂时没数据，正在重试")
    : "百分比为估算，阶段与计数是实际值";
  const indeterminate = unknown || !!pull.queued;
  const right = pull.queued ? "排队中" : (unknown ? "等待服务器上报进度…" : `约 ${pct}%`);
  const head = pull.queued ? "排队中" : "正在更新";
  return `
    <div class="bank-progress${indeterminate ? " indeterminate" : ""}${pull.queued ? " queued" : ""}" data-bank-pull="${pull.courseId}">
      <div class="bank-progress-head">
        <b>${escapeHtml(`${head}：${name}`)}</b>
        <span>${escapeHtml(right)}</span>
      </div>
      <div class="bank-progress-track"><i style="width:${indeterminate ? 100 : pct}%"></i></div>
      <div class="bank-progress-meta">
        <span>${escapeHtml(bits.join(" · "))}</span>
        <span class="bank-progress-hint">${escapeHtml(hint)}</span>
      </div>
    </div>
  `;
}

function renderBankPullProgress() {
  const rows = bankPullRows();
  if (!rows.length) return "";
  return `<div class="bank-progress-list" id="bankProgress">${rows.map(renderBankPullRow).join("")}</div>`;
}

// 只替换进度块这一个节点，不整块重渲染表格（否则会打断用户的滚动/筛选）
function refreshBankPullProgressUi() {
  const existing = $("bankProgress");
  const rows = bankPullRows();
  if (!rows.length) {
    if (existing) existing.remove();
    return;
  }
  const html = renderBankPullProgress();
  if (existing) {
    existing.outerHTML = html;
    return;
  }
  const tools = document.querySelector(".admin-bank-tools");
  if (tools) tools.insertAdjacentHTML("afterend", html);
}

/* 「这条快照是不是我这次发起的」：服务端按课程只留一条记录、结束还要留 10 分钟，
   紧接着再更新同一门课时会先读到上一轮的收尾状态。用服务端量出来的这条记录时长
   跟本行年龄比：记录时长本身已经超过本行年龄，说明它开始于我这一行之前，是我上一次的。 */
function bankPullSnapshotIsOurs(pull, snapshot) {
  if (snapshot.active === true) return true;
  const durationSec = Number(snapshot.elapsedSeconds || 0);
  const ageSec = (Date.now() - (pull.startedAtLocal || Date.now())) / 1000;
  return durationSec <= ageSec + 2;
}

function applyBankPullSnapshots(snapshots) {
  const map = state.adminBankPulls || (state.adminBankPulls = {});
  const seen = new Set();
  snapshots.forEach((snapshot) => {
    const courseId = Number(snapshot && snapshot.courseId ? snapshot.courseId : 0);
    if (!courseId) return;
    let pull = map[courseId];
    if (!pull) {
      // 不是本页发起的（刷新页面后重接、另一个标签页/账号发起的）—— 只接在跑的，
      // 保留期内的历史记录不该一刷新就冒出来
      if (snapshot.active !== true) return;
      const elapsedMs = Math.max(0, Number(snapshot.elapsedSeconds || 0) * 1000);
      pull = map[courseId] = { courseId, courseName: "", startedAtLocal: Date.now() - elapsedMs, adopted: true };
    }
    seen.add(courseId);
    if (!pull.adopted && !pull.done && !bankPullSnapshotIsOurs(pull, snapshot)) return;
    pull.courseName = snapshot.label || pull.courseName || bankPullCourseName(courseId);
    pull.stageLabel = snapshot.stageLabel || pull.stageLabel || "准备中";
    pull.current = Number(snapshot.current || 0);
    pull.total = Number(snapshot.total || 0);
    pull.detail = snapshot.detail || "";
    pull.percent = Number(snapshot.percent || 0);
    pull.queued = snapshot.active === true && snapshot.stage === "queued";
    pull.unknown = false;
    pull.elapsedSeconds = Number(snapshot.elapsedSeconds || 0);
    if (snapshot.active !== true) {
      pull.done = true;
      pull.ok = snapshot.ok === true;
      pull.error = snapshot.error || "";
      if (!pull.finishedAtLocal) pull.finishedAtLocal = Date.now();
    }
  });
  // 本页有、这一轮服务端没有的：刚发起时服务端还没登记（它得先向上游问一次有没有更新）
  Object.keys(map).forEach((key) => {
    const pull = map[key];
    if (!pull || pull.done || seen.has(Number(key))) return;
    pull.unknown = true;
    pull.queued = false;
    pull.elapsedSeconds = null;
  });
  scheduleBankPullDwell();
}

// 一次请求读全部：一个定时器轮所有未结束的条目，不为每条起一个定时器
async function pollBankPulls() {
  if (bankPullPollBusy) return;             // 上一轮还没回来就跳过，别把请求叠起来
  const rows = bankPullRows();
  if (!rows.length) {
    stopBankPullPolling();
    return;
  }
  bankPullPollBusy = true;
  let snapshots = null;
  try {
    const res = await api(`/api/admin/update-bank/progress?user=${encodeURIComponent(state.user)}`);
    snapshots = Array.isArray(res && res.snapshots) ? res.snapshots : [];
  } catch (err) {
    // 轮询失败不该打断拉取：标成不确定态，下个周期再试
    rows.forEach((pull) => { if (!pull.done) pull.unknown = true; });
  } finally {
    bankPullPollBusy = false;
  }
  if (snapshots) applyBankPullSnapshots(snapshots);
  pruneBankPullRows();
  refreshBankPullProgressUi();
  // 全部结束就停轮询：还留着的那些只是"展示期"，不需要再问了
  if (!bankPullRows().some((pull) => !pull.done)) stopBankPullPolling();
}

function scheduleBankPullDwell() {
  if (bankPullDwellTimer) {
    clearTimeout(bankPullDwellTimer);
    bankPullDwellTimer = null;
  }
  const done = bankPullRows().filter((pull) => pull.done && pull.finishedAtLocal);
  if (!done.length) return;
  const due = Math.min(...done.map((pull) => pull.finishedAtLocal + BANK_PULL_DONE_DWELL_MS));
  bankPullDwellTimer = setTimeout(() => {
    bankPullDwellTimer = null;
    pruneBankPullRows();
    refreshBankPullProgressUi();
    scheduleBankPullDwell();          // 还有别的行在展示期内就接着排
  }, Math.max(120, due - Date.now()));
}

// 展示期到了才撤行：终止态先让人看见，再消失
function pruneBankPullRows() {
  const map = state.adminBankPulls || {};
  const now = Date.now();
  Object.keys(map).forEach((key) => {
    const pull = map[key];
    if (!pull) { delete map[key]; return; }
    if (pull.done && pull.finishedAtLocal && now - pull.finishedAtLocal >= BANK_PULL_DONE_DWELL_MS) delete map[key];
  });
}

function stopBankPullPolling() {
  if (bankPullPollTimer) {
    clearInterval(bankPullPollTimer);
    bankPullPollTimer = null;
  }
}

function ensureBankPullPolling() {
  if (bankPullPollTimer) return;
  bankPullPollTimer = setInterval(() => pollBankPulls(), BANK_PULL_POLL_MS);
}

// 本页点「更新」时先占一行；服务端一登记就切到它的真实阶段（含"排队中"）
function startBankPullProgress(courseId) {
  const id = Number(courseId || 0);
  if (!id) return;
  const map = state.adminBankPulls || (state.adminBankPulls = {});
  map[id] = {
    courseId: id,
    courseName: bankPullCourseName(id),
    startedAtLocal: Date.now(),
    stageLabel: "准备中",
    current: 0,
    total: 0,
    detail: "",
    percent: 0,
    queued: false,
    unknown: true,
    elapsedSeconds: null,
    done: false,
    ok: false,
    error: "",
    finishedAtLocal: 0,
  };
  refreshBankPullProgressUi();
  ensureBankPullPolling();
  pollBankPulls();   // 立刻先探一次，别干等一个周期
}

/* POST 回来了（成功 / 报错 / 抛异常都要走这里）：重新读一遍全部快照收敛。
   不能直接清空 —— 同一页可能还有别的课在跑，它们得留在列表里；
   也不能不管 —— 服务端连记录都没有的（409 被拒 / 已是最新没拉 / 进程重启过）就得把行收掉。 */
async function syncBankPullsFromServer(finishedCourseId = 0) {
  try {
    const res = await api(`/api/admin/update-bank/progress?user=${encodeURIComponent(state.user)}`);
    applyBankPullSnapshots(Array.isArray(res && res.snapshots) ? res.snapshots : []);
  } catch (err) {
    console.warn("读取拉取进度失败", err);
  }
  const map = state.adminBankPulls || {};
  const id = Number(finishedCourseId || 0);
  if (id && map[id] && !map[id].done) delete map[id];
  pruneBankPullRows();
  refreshBankPullProgressUi();
  scheduleBankPullDwell();
  // 还有没结束的就得继续轮（刷新页面后接回来的那些也要，否则行会停在接回来那一刻不动）
  if (bankPullRows().some((pull) => !pull.done)) ensureBankPullPolling();
  else stopBankPullPolling();
}

// 刷新页面/重进「更新」页签：进度记录在服务端进程里，本页是空的，重新接上
async function resumeBankPullProgress() {
  if (bankPullResumeAt && Date.now() - bankPullResumeAt < 3000) return;
  bankPullResumeAt = Date.now();
  await syncBankPullsFromServer(0);
}

/* 「更新」页签的一级分类**默认折叠**（S-28）。
   实测本地题库有 664 门课，全展开 = 674 个按钮、页面高 26 万像素 ——
   打开这个页签先要滚过一片空白按钮墙。默认收起后一屏就能看到分类概览，
   点分类表头即展开（bindAdminBankUpdateButtons 里的 data-bank-group 已实现）。
   只在第一次渲染时铺初值，之后用户的展开/收起状态不被覆盖。 */
function ensureAdminBankCollapsedInit() {
  if (state.adminBankCollapsedInit) return;
  state.adminBankCollapsedInit = true;
  const tops = courseFilterOptions(state.adminCourses || []).tops;
  state.adminBankCollapsed = {};
  tops.forEach((name) => { state.adminBankCollapsed[name] = true; });
}

function renderAdminBankTable() {
  const courses = state.adminCourses || [];
  if (!courses.length) return `<div class="admin-empty">暂无题库数据</div>`;
  ensureAdminBankCollapsedInit();
  const visibleCourses = getFilteredAdminCourses();
  const options = courseFilterOptions(courses);
  const top = state.adminBankCategory || "";
  const collapsedCount = options.tops.filter((name) => state.adminBankCollapsed[name]).length;
  return `
    <div class="admin-bank-tools">
      <input id="adminBankSearch" value="${escapeHtml(state.adminBankQuery || "")}" placeholder="搜索题库名称、分类或ID">
      <select id="adminBankCategory" title="一级分类">
        <option value="">全部一级分类</option>
        ${options.tops.map((name) => `<option value="${escapeHtml(name)}" ${top === name ? "selected" : ""}>${escapeHtml(name)}</option>`).join("")}
      </select>
      <select id="adminBankSubcategory" title="二级分类">
        <option value="">全部二级分类</option>
        ${categorySubOptionsHtml(options, top, state.adminBankSubcategory)}
      </select>
      <select id="adminBankStatus">
        ${[["", "全部状态"], ["downloaded", "已下载"], ["missing", "未下载"]]
          .map(([value, label]) => `<option value="${value}" ${state.adminBankStatus === value ? "selected" : ""}>${label}</option>`)
          .join("")}
      </select>
      <button id="adminBankCheckUpdate" type="button">检查可更新</button>
      <button id="adminBankToggleAll" type="button">${collapsedCount ? "全部展开" : "全部收起"}</button>
      <span id="adminBankSummary">${renderAdminBankSummary(visibleCourses.length)}</span>
    </div>
    ${renderBankPullProgress()}
    ${renderAdminBankReport(state.adminBankLastReport)}
    <div class="admin-table-wrap">
      <table class="admin-table admin-bank-table">
        <thead>
          <tr>
            <th>题库</th>
            <th>分类</th>
            <th>本地题量</th>
            <th>更新时间</th>
            <th>状态</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody id="adminBankRows">
          ${renderAdminBankRows(visibleCourses)}
        </tbody>
      </table>
    </div>
  `;
}

// 排序：已下载优先 → 课程 ID（组内排序；组间按分类 iindex，见 groupCoursesByCategory）
function byDownloadedThenId(a, b) {
  const byDownloaded = (Number(b.questionCount || 0) > 0 ? 1 : 0) - (Number(a.questionCount || 0) > 0 ? 1 : 0);
  if (byDownloaded !== 0) return byDownloaded;
  return Number(a.id || 0) - Number(b.id || 0);
}

// 课程按「一级分类 → 二级分类」两级归组；组间用后端的分类 iindex（categoryOrder / subcategoryOrder）排序，
// 这样顺序跟客户端里的分类顺序一致，而不是拼音序。
function groupCoursesByCategory(courses) {
  const groups = new Map();
  (courses || []).forEach((course) => {
    const top = course.category || "未分类";
    const sub = course.subcategory || "未分类";
    if (!groups.has(top)) {
      groups.set(top, { name: top, order: Number(course.categoryOrder || 0), count: 0, subs: new Map() });
    }
    const group = groups.get(top);
    group.count += 1;
    if (!group.subs.has(sub)) {
      group.subs.set(sub, { name: sub, order: Number(course.subcategoryOrder || 0), list: [] });
    }
    group.subs.get(sub).list.push(course);
  });
  const byOrderThenName = (a, b) => a.order - b.order || String(a.name).localeCompare(String(b.name), "zh-Hans-CN");
  return [...groups.values()].sort(byOrderThenName).map((group) => ({
    ...group,
    subs: [...group.subs.values()].sort(byOrderThenName)
      .map((sub) => ({ ...sub, list: sub.list.slice().sort(byDownloadedThenId) })),
  }));
}

// 级联筛选用的选项：一级分类列表 + 「某一级下的二级分类」查询
function courseFilterOptions(courses) {
  const tops = new Map();
  const subs = new Map();
  (courses || []).forEach((course) => {
    const top = course.category || "未分类";
    const sub = course.subcategory || "未分类";
    if (!tops.has(top)) tops.set(top, Number(course.categoryOrder || 0));
    if (!subs.has(top)) subs.set(top, new Map());
    if (!subs.get(top).has(sub)) subs.get(top).set(sub, Number(course.subcategoryOrder || 0));
  });
  const names = (map) => [...map.entries()]
    .sort((a, b) => a[1] - b[1] || String(a[0]).localeCompare(String(b[0]), "zh-Hans-CN"))
    .map(([name]) => name);
  return {
    tops: names(tops),
    subsOf: (top) => (subs.has(top) ? names(subs.get(top)) : []),
    allSubs: () => [...subs.entries()].map(([top, map]) => ({ top, subs: names(map) })),
  };
}

// 排序：已下载优先 → 一级分类 → 二级分类 → 课程 ID（列表很长时，常用的课排前面）
function getFilteredAdminCourses() {
  const courses = state.adminCourses || [];
  const query = (state.adminBankQuery || "").trim().toLowerCase();
  const category = state.adminBankCategory || "";
  const subcategory = state.adminBankSubcategory || "";
  const status = state.adminBankStatus || "";
  const filtered = courses.filter((course) => {
    if (category && course.category !== category) return false;
    if (subcategory && course.subcategory !== subcategory) return false;
    const hasLocal = Number(course.questionCount || 0) > 0;
    if (status === "downloaded" && !hasLocal) return false;
    if (status === "missing" && hasLocal) return false;
    if (!query) return true;
    return [course.name, course.category, course.subcategory, course.id]
      .some((value) => String(value || "").toLowerCase().includes(query));
  });
  return filtered.sort((a, b) => {
    const byCategory = Number(a.categoryOrder || 0) - Number(b.categoryOrder || 0);
    if (byCategory !== 0) return byCategory;
    const bySubcategory = Number(a.subcategoryOrder || 0) - Number(b.subcategoryOrder || 0);
    if (bySubcategory !== 0) return bySubcategory;
    return byDownloadedThenId(a, b);
  });
}

// 拉取模式不校验授权：能出现在这个列表里的课程，库里都有它的 course 行，直接拉即可
// （上游服务端本身也不校验购买权限，未购课程同样能拉到）。上传模式仍要求课程已有本地
// 数据，避免上传包按 ID 匹配到一门空课程。
function canUpdateAdminCourse(course) {
  if (state.adminBankUpdateMode === "pull") return true;
  return !!course.owned && Number(course.questionCount || 0) > 0;
}

function renderAdminBankSummary(visibleCount = getFilteredAdminCourses().length) {
  const courses = state.adminCourses || [];
  const downloaded = courses.filter((course) => Number(course.questionCount || 0) > 0).length;
  const updatable = courses.filter((course) => state.adminUpdateCheck[course.id] === true).length;
  const groups = groupCoursesByCategory(getFilteredAdminCourses()).length;
  return `共 ${courses.length} 门 · 已下载 ${downloaded} 门 · 当前显示 ${visibleCount} 门 / ${groups} 个一级分类`
    + (updatable ? ` · 可更新 ${updatable} 门` : "");
}

// 单门课的一行（分组渲染时复用）
function renderAdminBankCourseRow(course) {
  const hasLocal = Number(course.questionCount || 0) > 0;
  const canUpdate = canUpdateAdminCourse(course);
  // 状态只说"本地有没有这门课的题库"，不再区分已购/未购（对管理没有意义）
  const status = hasLocal ? "已下载" : "未下载";
  const updateFlag = state.adminUpdateCheck[course.id];
  const updateBadge = updateFlag === true
    ? ` <span class="status-pill active">可更新</span>`
    : updateFlag === false
      ? ` <span class="status-pill disabled">已最新</span>`
      : "";
  const updateText = state.adminBankUpdateMode === "upload" ? "上传更新" : state.adminBankUpdateMode === "pull" ? "拉取更新" : "更新题库";
  return `
    <tr class="${canUpdate ? "" : "muted-row"}">
      <td><b>${escapeHtml(course.name)}</b><span class="admin-course-id">ID ${course.id}</span></td>
      <td>${escapeHtml(course.category || "")}<span>${escapeHtml(course.subcategory || "")}</span></td>
      <td>${Number(course.questionCount || 0)} 题</td>
      <td>${escapeHtml(formatRelativeTime(course.changedAt))}<span>${escapeHtml(course.changedAt || "未记录")}</span></td>
      <td><span class="status-pill ${hasLocal ? "active" : "disabled"}">${escapeHtml(status)}</span>${updateBadge}</td>
      <td>
        <button class="primary-action secondary compact" data-update-course="${course.id}" ${canUpdate ? "" : "disabled"}>${canUpdate ? updateText : "不可更新"}</button>
      </td>
    </tr>
  `;
}

// 按「一级分类 → 二级分类」两级渲染：一级可折叠，二级带小标题，避免一千多门课平铺在一个维度上
function renderAdminBankRows(visibleCourses) {
  const groups = groupCoursesByCategory(visibleCourses);
  if (!groups.length) return `<tr><td colspan="6">没有匹配的题库</td></tr>`;
  const rows = [];
  groups.forEach((group) => {
    const collapsed = !!state.adminBankCollapsed[group.name];
    rows.push(`
      <tr class="bank-group-row" data-bank-group="${escapeHtml(group.name)}">
        <td colspan="6">
          <span class="bank-group-toggle">${collapsed ? "▸" : "▾"}</span>
          <b>${escapeHtml(group.name)}</b>
          <small>${group.count} 门 · ${group.subs.length} 个二级分类</small>
        </td>
      </tr>
    `);
    if (collapsed) return;
    group.subs.forEach((sub) => {
      rows.push(`
        <tr class="bank-subgroup-row">
          <td colspan="6"><span>${escapeHtml(sub.name)}</span><small>${sub.list.length} 门</small></td>
        </tr>
      `);
      sub.list.forEach((course) => rows.push(renderAdminBankCourseRow(course)));
    });
  });
  return rows.join("");
}

// 「检查可更新」：只探当前可见的前 60 门（避免一次探上千门），结果缓存到 state.adminUpdateCheck
async function checkVisibleCourseUpdates(btn = null) {
  const courses = getFilteredAdminCourses().slice(0, 60);
  if (!courses.length) {
    toast("当前列表没有可检查的题库");
    return;
  }
  if (btn) { btn.disabled = true; btn.textContent = "检查中..."; }
  try {
    const result = await api("/api/admin/course-update-check", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ courseIds: courses.map((course) => Number(course.id)) }),
    });
    const items = result.results || [];
    items.forEach((item) => {
      state.adminUpdateCheck[item.courseId] = !!item.hasUpdate;
    });
    refreshAdminBankTable();
    const updatable = items.filter((item) => item.hasUpdate).length;
    toast(`已检查 ${items.length} 门题库，其中 ${updatable} 门可更新`);
  } finally {
    if (btn) { btn.disabled = false; btn.textContent = "检查可更新"; }
  }
}

function refreshAdminBankTable() {
  const visibleCourses = getFilteredAdminCourses();
  const summary = $("adminBankSummary");
  const rows = $("adminBankRows");
  if (summary) summary.textContent = renderAdminBankSummary(visibleCourses.length);
  if (rows) rows.innerHTML = renderAdminBankRows(visibleCourses);
  bindAdminBankUpdateButtons();
}

function renderAdminBankEditorPanel() {
  const courses = getBankEditorCourses();
  if (!courses.length) return `<div class="admin-empty">暂无可编辑题库，请先加载题库数据。</div>`;
  const selected = getBankEditorCourse(courses);
  ensureAdminEditorChapters(selected.id).catch((err) => toast(err.message));
  const chapters = state.adminEditorChaptersCourseId === Number(selected.id) ? state.adminEditorChapters : [];
  const correctionIds = getEditorCorrectionIds(Number(selected.id));
  return `
    <div class="bank-editor-panel">
      <section class="admin-data-card bank-editor-card">
        <div class="admin-data-card-head">
          <div>
            <b>题库编辑</b>
            <span>用于修正当前题库的章节标题、题目、答案和解析；保存前会自动备份数据库。</span>
          </div>
          <span class="status-pill active">管理员</span>
        </div>
        <div class="bank-editor-filters">
          <label>
            <span>题库</span>
            <select id="bankEditorCourseSelect">
              ${courses.map((course) => `<option value="${course.id}" ${Number(course.id) === Number(selected.id) ? "selected" : ""}>${escapeHtml(course.name)} · ${Number(course.questionCount || 0)}题</option>`).join("")}
            </select>
          </label>
          <label>
            <span>章节</span>
            <select id="bankEditorChapterSelect">
              <option value="0">全部章节</option>
              ${chapters.map((chapter) => `<option value="${chapter.id}" ${Number(chapter.id) === Number(state.adminEditorChapterId) ? "selected" : ""}>${"&nbsp;".repeat(Math.max(0, Number(chapter.grade || 0)) * 2)}${escapeHtml(chapter.name)} · ${Number(chapter.questionCount || 0)}题</option>`).join("")}
            </select>
          </label>
          <label>
            <span>关键词 / 题号</span>
            <input id="bankEditorSearch" value="${escapeHtml(state.adminEditorQuery || "")}" placeholder="搜索题干、答案、解析或题号">
          </label>
          <button class="primary-action" id="bankEditorSearchBtn" type="button">${state.adminEditorBusy === "search" ? "搜索中..." : "搜索"}</button>
          <label class="bank-editor-check">
            <input id="bankEditorCorrectionOnly" type="checkbox" ${state.adminEditorCorrectionOnly ? "checked" : ""}>
            <span>只看纠错反馈 ${correctionIds.length ? `(${correctionIds.length})` : ""}</span>
          </label>
        </div>
      </section>
      <div class="bank-editor-layout">
        <section class="admin-data-card bank-editor-list-card">
          <div class="admin-data-card-head">
            <div>
              <b>题目列表</b>
              <span>${state.adminEditorResults.length ? `当前显示 ${state.adminEditorResults.length} 道` : "请选择条件后搜索"}</span>
            </div>
          </div>
          <div class="bank-editor-list">
            ${renderBankEditorResults()}
          </div>
        </section>
        <section class="admin-data-card bank-editor-detail-card">
          ${renderBankEditorDetail()}
        </section>
      </div>
    </div>
  `;
}

function getBankEditorCourses() {
  return (state.adminCourses || [])
    .filter((course) => Number(course.questionCount || 0) > 0)
    .slice()
    .sort((a, b) => Number(b.owned || 0) - Number(a.owned || 0) || Number(a.id || 0) - Number(b.id || 0));
}

function getBankEditorCourse(courses = getBankEditorCourses()) {
  const current = courses.find((course) => Number(course.id) === Number(state.adminEditorCourseId));
  const fallback = current || courses.find((course) => Number(course.id) === Number(state.currentCourse?.id || 0)) || courses[0] || {};
  state.adminEditorCourseId = Number(fallback.id || 0);
  return fallback;
}

async function ensureAdminEditorChapters(courseId) {
  courseId = Number(courseId || 0);
  if (!courseId || state.adminEditorChaptersCourseId === courseId) return;
  state.adminEditorChaptersCourseId = courseId;
  state.adminEditorChapters = [];
  const chapters = await api(`/api/chapters?courseId=${courseId}`);
  state.adminEditorChapters = Array.isArray(chapters) ? chapters : [];
  /* 章节表回来时**不要**在有未保存草稿的情况下重绘编辑页：重绘会用 detail 重建输入框，
     等于绕过「保存/放弃/取消」把草稿丢掉（UI-06）。下拉里的章节列表会在下一次
     显式动作时刷新，不差这一下。 */
  if (state.adminView === "bankEditor" && !isBankEditorDirty()) {
    renderAdminRows(state.adminRows, state.adminFailedCount);
  }
}

function renderBankEditorResults() {
  if (state.adminEditorBusy === "search") return `<div class="admin-empty">正在搜索题目...</div>`;
  if (!state.adminEditorResults.length) return `<div class="admin-empty">暂无题目。可选择章节、输入关键词，或勾选“只看纠错反馈”。</div>`;
  const correctionIds = new Set(getEditorCorrectionIds(Number(state.adminEditorCourseId)));
  return state.adminEditorResults.map((item) => `
    <button class="bank-editor-result ${Number(item.id) === Number(state.adminEditorSelectedId) ? "active" : ""}" data-bank-editor-open="${item.id}" data-editor-course="${item.courseId}" type="button">
      <b>#${item.id} ${escapeHtml(item.type || "题目")}</b>
      <span>${escapeHtml(item.chapterName || "")}${correctionIds.has(Number(item.id)) ? " · 有纠错反馈" : ""}${item.localEdited ? " · 本地已改" : ""}</span>
      <small>${escapeHtml((item.title || "").slice(0, 180))}</small>
    </button>
  `).join("");
}

function renderBankEditorDetail() {
  const detail = state.adminEditorDetail;
  if (state.adminEditorBusy === "detail") return `<div class="admin-empty">正在打开题目...</div>`;
  if (!detail) {
    return `
      <div class="admin-data-card-head">
        <div>
          <b>编辑区</b>
          <span>从左侧选择一道题，或在纠错反馈中点击“编辑题库”。</span>
        </div>
      </div>
      <div class="admin-empty">尚未选择题目</div>
    `;
  }
  const path = Array.isArray(detail.chapterPath) ? detail.chapterPath : [];
  const localEdit = detail.localEdit;
  return `
    <div class="admin-data-card-head">
      <div>
        <b>#${detail.id} ${escapeHtml(detail.type || "题目")}</b>
        <span>${escapeHtml(detail.courseName || "")} · ${escapeHtml(detail.updatedAt || "未记录")}${localEdit ? ` · <span class="local-edit-pill">本地已改${localEdit.editedAt ? ` ${escapeHtml(localEdit.editedAt)}` : ""}${localEdit.editor ? ` by ${escapeHtml(localEdit.editor)}` : ""}</span>` : ""}</span>
      </div>
      <div class="bank-editor-detail-actions">
        ${localEdit ? `<button class="danger" id="bankEditorRestoreBtn" type="button">${state.adminEditorBusy === "restore" ? "还原中..." : "还原为服务端版本"}</button>` : ""}
        <button class="primary-action" id="bankEditorSaveBtn" type="button">${state.adminEditorBusy === "save" ? "保存中..." : "保存修改"}</button>
      </div>
    </div>
    <div class="bank-editor-form">
      <div class="bank-editor-chapter-fields">
        ${path.map((chapter, index) => {
          /* UI-07：这一格原来写「该章 N 题」，N 是**直挂**题量 —— 父章上会显示 0，
             而它下面明明有题（实测直挂 0、子树 1305）。这里直接说清直挂多少、含子章共多少。 */
          const scopeText = editorChapterScopeText(chapter.id);
          return `
          <label>
            <span>${["一级标题", "二级标题", "三级标题", "四级标题"][index] || `第 ${index + 1} 级标题`} · ID ${chapter.id} · ${scopeText}</span>
            <input data-editor-chapter-id="${chapter.id}" value="${escapeHtml(chapter.name || "")}"
                   title="章节名是整章共用的：改这里会同时改掉该章${scopeText}的章节名（新名称会出现在全部后代题的章节路径里），保存时会再确认一次">
          </label>
        `;
        }).join("") || `<div class="admin-empty">未找到章节路径</div>`}
      </div>
      <label>
        <span>题目 / 选项</span>
        <textarea id="bankEditorTitleInput" rows="8">${escapeHtml(detail.title || "")}</textarea>
      </label>
      <div class="bank-editor-preview" id="bankEditorTitlePreview"></div>
      <label>
        <span>附加题干 / 案例材料</span>
        <textarea id="bankEditorQuestionInput" rows="5">${escapeHtml(detail.question || "")}</textarea>
      </label>
      <div class="bank-editor-preview" id="bankEditorQuestionPreview"></div>
      <label>
        <span>答案</span>
        <textarea id="bankEditorAnswerInput" rows="3">${escapeHtml(detail.answer || "")}</textarea>
      </label>
      <label>
        <span>解析</span>
        <textarea id="bankEditorDescriptionInput" rows="6">${escapeHtml(detail.description || "")}</textarea>
      </label>
      <div class="bank-editor-preview" id="bankEditorDescriptionPreview"></div>
    </div>
  `;
}

/* 富文本字段下面那块「学生看到的样子」预览。
   原来只有 textarea —— 题干里的 <img> 只是一段字面文本，管理员根本看不到图，
   纠错时无法判断图片对不对。预览里图片可以点开看大图（和做题页共用查看器）。

   注意：预览走 sanitizeRichHtml（含本地路径改写），但**只用于显示**，
   保存时提交的仍是 textarea 里的原文，不会把改写后的路径写回题库。 */
function bindBankEditorPreviews() {
  const pairs = [
    ["bankEditorTitleInput", "bankEditorTitlePreview"],
    ["bankEditorQuestionInput", "bankEditorQuestionPreview"],
    ["bankEditorDescriptionInput", "bankEditorDescriptionPreview"],
  ];
  for (const [inputId, previewId] of pairs) {
    const input = $(inputId);
    const preview = $(previewId);
    if (!input || !preview) continue;
    const paint = () => {
      const html = sanitizeRichHtml(input.value || "");
      preview.innerHTML = html.trim()
        ? html
        : '<span class="bank-editor-preview-empty">（空）</span>';
    };
    // 每敲一个字都跑净化器对长题干太费，防抖一下
    const schedule = typeof debounce === "function" ? debounce(paint, 220) : paint;
    input.oninput = schedule;
    paint();
  }
}

function bindAdminBankEditorActions() {
  document.querySelectorAll("[data-bank-editor-open]").forEach((btn) => {
    btn.onclick = () => openBankEditorQuestion(Number(btn.dataset.bankEditorOpen || 0), Number(btn.dataset.editorCourse || 0)).catch((err) => toast(err.message));
  });
  const courseSelect = $("bankEditorCourseSelect");
  if (courseSelect) {
    courseSelect.onchange = async () => {
      /* 切科目会把编辑区清空（detail=null）—— 有草稿先问三选一，取消则把下拉拨回去，
         不能出现「界面显示科目 B、状态还停在科目 A」的错位。 */
      const nextName = (courseSelect.selectedOptions?.[0]?.textContent || "").trim() || "另一个题库";
      if (!await confirmBankEditorLeave(`切换到「${nextName}」`)) {
        courseSelect.value = String(state.adminEditorCourseId || "");
        return;
      }
      state.adminEditorCourseId = Number(courseSelect.value || 0);
      state.adminEditorChapterId = 0;
      state.adminEditorResults = [];
      state.adminEditorSelectedId = 0;
      state.adminEditorDetail = null;
      state.adminEditorChaptersCourseId = 0;
      state.adminEditorAutoLoadKey = "";
      renderAdminRows(state.adminRows, state.adminFailedCount);
    };
  }
  const chapterSelect = $("bankEditorChapterSelect");
  if (chapterSelect) chapterSelect.onchange = () => { state.adminEditorChapterId = Number(chapterSelect.value || 0); };
  const search = $("bankEditorSearch");
  if (search) search.oninput = () => { state.adminEditorQuery = search.value; };
  const correctionOnly = $("bankEditorCorrectionOnly");
  if (correctionOnly) correctionOnly.onchange = () => { state.adminEditorCorrectionOnly = correctionOnly.checked; };
  const searchBtn = $("bankEditorSearchBtn");
  if (searchBtn) {
    /* 「搜索」本身不改 detail，但它会把不在结果集里的题从列表里摘掉、并顺手把 detail 置空
       —— 未保存的草稿就是这么没的。有草稿时同样先问。 */
    searchBtn.onclick = async () => {
      if (!await confirmBankEditorLeave("重新搜索题目")) return;
      loadBankEditorQuestions().catch((err) => toast(err.message));
    };
  }
  const saveBtn = $("bankEditorSaveBtn");
  if (saveBtn) saveBtn.onclick = () => saveBankEditorQuestion().catch((err) => toast(err.message));
  const restoreBtn = $("bankEditorRestoreBtn");
  if (restoreBtn) restoreBtn.onclick = () => restoreBankEditorQuestion().catch((err) => toast(err.message));
  bindBankEditorPreviews();
  maybeAutoLoadBankEditor();
}

function bankEditorAutoLoadKey() {
  return [
    Number(state.adminEditorCourseId || 0),
    Number(state.adminEditorChapterId || 0),
    state.adminEditorCorrectionOnly ? "corrections" : "all",
    (state.adminEditorQuery || "").trim(),
  ].join("|");
}

function maybeAutoLoadBankEditor() {
  if (state.adminView !== "bankEditor" || state.adminEditorBusy || state.adminEditorResults.length || state.adminEditorDetail) return;
  const key = bankEditorAutoLoadKey();
  if (state.adminEditorAutoLoadKey === key) return;
  state.adminEditorAutoLoadKey = key;
  setTimeout(() => {
    if (state.adminView !== "bankEditor" || state.adminEditorBusy || state.adminEditorResults.length || state.adminEditorDetail) return;
    loadBankEditorQuestions().catch((err) => toast(err.message));
  }, 0);
}

function selectedEditorChapterIds() {
  const selectedId = Number(state.adminEditorChapterId || 0);
  if (!selectedId) return [];
  const chapters = state.adminEditorChapters || [];
  const selected = chapters.find((chapter) => Number(chapter.id) === selectedId);
  if (!selected) return [selectedId];
  const code = String(selected.code || "");
  const type = Number(selected.type || 0);
  return chapters
    .filter((chapter) => Number(chapter.type || 0) === type && String(chapter.code || "").startsWith(code))
    .map((chapter) => Number(chapter.id))
    .filter(Boolean);
}

function getEditorCorrectionIds(courseId = 0) {
  const ids = collectCorrections(state.adminRows)
    .filter((item) => !courseId || Number(item.courseId || 0) === Number(courseId))
    .map((item) => Number(item.questionId || 0))
    .filter(Boolean);
  return [...new Set(ids)];
}

async function loadBankEditorQuestions() {
  const courseId = Number(state.adminEditorCourseId || getBankEditorCourse().id || 0);
  if (!courseId) {
    toast("请选择题库");
    return;
  }
  const query = new URLSearchParams({ user: state.user, courseId: String(courseId), limit: "160" });
  const chapterIds = selectedEditorChapterIds();
  if (chapterIds.length) query.set("chapterIds", chapterIds.join(","));
  if ((state.adminEditorQuery || "").trim()) query.set("q", state.adminEditorQuery.trim());
  if (state.adminEditorCorrectionOnly) {
    const ids = getEditorCorrectionIds(courseId);
    if (!ids.length) {
      state.adminEditorResults = [];
      state.adminEditorDetail = null;
      renderAdminRows(state.adminRows, state.adminFailedCount);
      toast("当前题库暂无纠错反馈");
      return;
    }
    query.set("ids", ids.join(","));
  }
  state.adminEditorBusy = "search";
  renderAdminRows(state.adminRows, state.adminFailedCount);
  try {
    const result = await api(`/api/admin/bank-editor/questions?${query}`);
    state.adminEditorResults = Array.isArray(result.items) ? result.items : [];
    if (!state.adminEditorResults.some((item) => Number(item.id) === Number(state.adminEditorSelectedId))) {
      state.adminEditorSelectedId = 0;
      state.adminEditorDetail = null;
    }
  } finally {
    state.adminEditorBusy = "";
    renderAdminRows(state.adminRows, state.adminFailedCount);
  }
}

/* ── 编辑区未保存草稿（UI-06）──────────────────────────────────────────
   实测：给题干追加一个标记，然后打开另一道题 —— 标记消失，一句提示都没有
   （编辑区是整块重绘的，输入框里的东西只活在 DOM 里）。丢弃草稿的路径有四条：
   切题、切科目、切管理页签、离开页面；另外「搜索」会换掉结果集并把 detail 置空。
   这里给三选一（保存 / 放弃 / 取消），并且**只在字段真的变了时**才拦 ——
   单纯点开、浏览各题不弹任何东西（dirty 是拿 DOM 当前值和已加载的 detail 比出来的，
   没有监听器、没有「碰过就算脏」）。 */

/* 章节名的波及范围（UI-07）：服务端给的 questionCount 是**直挂**题量
   （SQL 里 `s.ichapterid=ch.ichapterid`，不含子章）。旧提示直接拿它当影响面，
   于是父章「2025年章节练习~基础」弹出「影响该章全部 0 道题」，而它下面实际有题
   （实测该父章直挂 0、子树 1305）。改名影响的是**整棵子树**：新名字会出现在
   全部后代题的章节路径里，所有按章节分组/筛选的地方都会跟着变。
   子树判定与「章节」下拉的过滤口径一致：同 itype 且 code 前缀匹配（见
   selectedEditorChapterIds）。 */
function editorChapterScope(chapterId) {
  const chapters = state.adminEditorChapters || [];
  const target = chapters.find((chapter) => Number(chapter.id) === Number(chapterId));
  const direct = Number(target?.questionCount || 0);
  if (!target) return { direct, children: 0, childQuestions: 0, total: direct, known: false };
  const code = String(target.code || "");
  const type = Number(target.type || 0);
  const kids = code
    ? chapters.filter((chapter) => Number(chapter.id) !== Number(chapterId)
        && Number(chapter.type || 0) === type
        && String(chapter.code || "").startsWith(code))
    : [];
  const childQuestions = kids.reduce((sum, chapter) => sum + Number(chapter.questionCount || 0), 0);
  return { direct, children: kids.length, childQuestions, total: direct + childQuestions, known: true };
}

/* 一句人能读懂的影响面：直挂多少、含子章共多少。 */
function editorChapterScopeText(chapterId) {
  const scope = editorChapterScope(chapterId);
  if (!scope.children) return `直挂 ${scope.direct} 道题（无子章）`;
  return `直挂 ${scope.direct} 道题，含 ${scope.children} 个子章共 ${scope.total} 道题`;
}

/* 编辑区当前的内容和「已加载的那份 detail」比：只有真的不一样才算 dirty。 */
function isBankEditorDirty() {
  const detail = state.adminEditorDetail;
  if (!detail || !$("bankEditorTitleInput")) return false;
  const norm = (value) => String(value ?? "").replace(/\r\n/g, "\n");
  if (norm($("bankEditorTitleInput").value) !== norm(detail.title)) return true;
  if (norm($("bankEditorQuestionInput")?.value) !== norm(detail.question)) return true;
  if (norm($("bankEditorAnswerInput")?.value) !== norm(detail.answer)) return true;
  if (norm($("bankEditorDescriptionInput")?.value) !== norm(detail.description)) return true;
  const pathById = new Map((detail.chapterPath || []).map((chapter) => [Number(chapter.id), chapter]));
  for (const input of document.querySelectorAll("[data-editor-chapter-id]")) {
    const before = pathById.get(Number(input.dataset.editorChapterId || 0));
    if (before && String(input.value).trim() !== String(before.name || "").trim()) return true;
  }
  return false;
}

let bankEditorLeaveResolver = null;

/* 三个出口都走这里：resolve(true) = 可以继续（已保存或用户放弃），false = 留在原地。 */
function resolveBankEditorLeave(ok) {
  $("bankEditorDirtyDialog")?.classList.add("hidden");
  const resolve = bankEditorLeaveResolver;
  bankEditorLeaveResolver = null;
  if (resolve) resolve(!!ok);
}

/* 有草稿就先问一句；没草稿直接放行（浏览不该被打扰）。 */
function confirmBankEditorLeave(reason = "离开这道题") {
  if (!isBankEditorDirty()) return Promise.resolve(true);
  const dialog = $("bankEditorDirtyDialog");
  const detailId = Number(state.adminEditorDetail?.id || 0);
  if (!dialog) return Promise.resolve(true);
  setText("bankEditorDirtyMessage",
    `${reason}前，题目 #${detailId} 还有未保存的修改。可以「保存后继续」，或放弃这次修改。`);
  dialog.classList.remove("hidden");
  $("bkeCancelBtn").onclick = () => resolveBankEditorLeave(false);
  $("bkeDiscardBtn").onclick = () => resolveBankEditorLeave(true);
  $("bkeSaveBtn").onclick = async () => {
    /* 保存走既有入口（含章节改名确认 + 保存确认）：用户在那一层点「取消」等于没保存，
       这张对话框就不该放行 —— 否则又是一次静默丢稿。 */
    const saved = await saveBankEditorQuestion().catch((err) => { toast(err.message); return false; });
    if (saved) resolveBankEditorLeave(true);
  };
  return new Promise((resolve) => { bankEditorLeaveResolver = resolve; });
}

async function openBankEditorQuestion(questionId, courseId = 0, options = {}) {
  if (!questionId) return;
  if (!options.skipLeaveCheck && Number(state.adminEditorDetail?.id || 0) !== Number(questionId)) {
    if (!await confirmBankEditorLeave("打开另一道题")) return;
  }
  if (courseId) state.adminEditorCourseId = Number(courseId);
  state.adminView = "bankEditor";
  state.adminEditorSelectedId = Number(questionId);
  state.adminEditorBusy = "detail";
  renderAdminRows(state.adminRows, state.adminFailedCount);
  try {
    const detail = await api(`/api/admin/bank-editor/question?user=${encodeURIComponent(state.user)}&id=${questionId}`);
    state.adminEditorDetail = detail;
    state.adminEditorCourseId = Number(detail.courseId || state.adminEditorCourseId || 0);
    await ensureAdminEditorChapters(state.adminEditorCourseId).catch(() => {});
    if (!state.adminEditorResults.some((item) => Number(item.id) === Number(questionId))) {
      state.adminEditorResults = [{
        id: detail.id,
        courseId: detail.courseId,
        chapterName: detail.chapterName,
        type: detail.type,
        title: stripText(detail.title || "").slice(0, 180),
      }, ...state.adminEditorResults].slice(0, 160);
    }
  } finally {
    state.adminEditorBusy = "";
    renderAdminRows(state.adminRows, state.adminFailedCount);
  }
}

async function saveBankEditorQuestion() {
  const detail = state.adminEditorDetail;
  if (!detail?.id) return false;
  const chapters = [...document.querySelectorAll("[data-editor-chapter-id]")].map((input) => ({
    id: Number(input.dataset.editorChapterId || 0),
    name: input.value.trim(),
  }));
  const payload = {
    id: detail.id,
    chapters,
    title: $("bankEditorTitleInput")?.value || "",
    question: $("bankEditorQuestionInput")?.value || "",
    answer: $("bankEditorAnswerInput")?.value || "",
    description: $("bankEditorDescriptionInput")?.value || "",
  };
  if (!payload.title.trim()) {
    toast("题目内容不能为空");
    return false;
  }
  /* ⚠️ 章节名是「一章一份」的：改一个字，这一章下**全部**题的 chapterName 都跟着变
     （实测把章节 20173 改名后，同章 96 道题的 chapterName 全变）。
     以前只有和「保存题干」共用的那一句通用 confirm，管理员根本看不出这次改的是整章；
     这里对**真的改动了名字**的每一级标题单独再确认一次，并报出影响面。
     ⚠️ 影响面必须算**整棵子树**（UI-07）：服务端的 questionCount 只数直挂，
     旧提示对父章写「影响该章全部 0 道题」，而该父章下实际有题（实测直挂 0、子树 1305）
     —— 这个 0 会让管理员以为改名无风险。 */
  const pathById = new Map((detail.chapterPath || []).map((chapter) => [Number(chapter.id), chapter]));
  const chapterRenames = chapters
    .map((chapter) => {
      const before = pathById.get(Number(chapter.id));
      if (!before) return null;
      if (String(before.name || "") === String(chapter.name || "")) return null;
      return { id: Number(chapter.id), from: before.name || "（空）", to: chapter.name || "（空）" };
    })
    .filter(Boolean);
  for (const rename of chapterRenames) {
    const scopeText = editorChapterScopeText(rename.id);
    if (!confirm(`章节「${rename.from}」将改名为「${rename.to}」。\n\n影响面：${scopeText}。`
      + `\n章节名是整章共用的：新名称会出现在这些题（含全部子章）的章节路径里，`
      + `所有按章节分组、筛选、打印的地方都会跟着变。\n\n确定要改这一章的标题吗？`)) return false;
  }
  if (!confirm("确定保存题库修改吗？系统会先自动备份当前数据库。")) return false;
  state.adminEditorBusy = "save";
  renderAdminRows(state.adminRows, state.adminFailedCount);
  try {
    const result = await api(`/api/admin/bank-editor/question?user=${encodeURIComponent(state.user)}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });
    state.adminEditorDetail = result.question || state.adminEditorDetail;
    state.adminCourses = [];
    state.chapters = [];
    await loadAdminCourses().catch(() => {});
    await ensureAdminEditorChapters(state.adminEditorCourseId).catch(() => {});
    await loadBankEditorQuestions().catch(() => {});
    toast("题库修改已保存");
    return true;
  } finally {
    state.adminEditorBusy = "";
    renderAdminRows(state.adminRows, state.adminFailedCount);
  }
}

async function restoreBankEditorQuestion() {
  const detail = state.adminEditorDetail;
  if (!detail?.id) return;
  if (!confirm("确定把这道题还原成服务端版本吗？本地改动记录会一并删除。")) return;
  state.adminEditorBusy = "restore";
  renderAdminRows(state.adminRows, state.adminFailedCount);
  try {
    const result = await api(`/api/admin/bank-editor/restore?user=${encodeURIComponent(state.user)}&id=${detail.id}`, { method: "POST" });
    state.adminEditorDetail = result.question || state.adminEditorDetail;
    await loadBankEditorQuestions().catch(() => {});
    toast(result.message || (result.ok ? "已还原为服务端版本" : "还原失败"));
  } finally {
    state.adminEditorBusy = "";
    renderAdminRows(state.adminRows, state.adminFailedCount);
  }
}

function renderAdminDataPanel() {
  const status = state.adminDataStatus;
  if (!status) {
    loadAdminDataStatus().catch((err) => toast(err.message));
  }
  const bank = status?.bank || {};
  const userdata = status?.userdata || {};
  const backups = status?.backups || {};
  return `
    <div class="admin-data-panel">
      <section class="admin-data-card">
        <div class="admin-data-card-head">
          <div>
            <b>题库数据</b>
            <span>上传或下载 SQLite 题库和图片资源</span>
          </div>
          <span class="status-pill ${bank.exists ? "active" : "disabled"}">${bank.exists ? "已加载" : "未加载"}</span>
        </div>
        <div class="admin-data-metrics">
          <div><b>${formatFileSize(bank.size || 0)}</b><span>数据库大小</span></div>
          <div><b>${Number(bank.assetsFiles || 0)}</b><span>图片文件</span></div>
          <div><b>${formatFileSize(bank.assetsSize || 0)}</b><span>图片大小</span></div>
          <div><b>${Number(backups.bank || 0)}</b><span>备份</span></div>
        </div>
        <div class="admin-data-path">数据库：${escapeHtml(bank.path || "未配置")}</div>
        <div class="admin-data-path">图片：${escapeHtml(bank.assetsPath || "未配置")}</div>
        <div class="admin-data-actions">
          <input id="adminBankUploadInput" type="file" accept=".zip">
          <button class="primary-action" data-admin-upload="bank" type="button">上传题库</button>
          <button data-admin-download="bank" type="button" ${bank.exists ? "" : "disabled"}>下载题库</button>
        </div>
        <small>上传题库包必须是 zip，包内包含 question-bank.db 和 assets 目录；下载时也会打成同样结构。</small>
      </section>

      <section class="admin-data-card">
        <div class="admin-data-card-head">
          <div>
            <b>用户数据</b>
            <span>上传或下载账号、密码、做题记录、收藏、笔记和纠错</span>
          </div>
          <span class="status-pill ${userdata.files ? "active" : "disabled"}">${userdata.files ? "有数据" : "空数据"}</span>
        </div>
        <div class="admin-data-metrics">
          <div><b>${Number(userdata.users || 0)}</b><span>账号文件</span></div>
          <div><b>${Number(userdata.files || 0)}</b><span>数据文件</span></div>
          <div><b>${formatFileSize(userdata.size || 0)}</b><span>总大小</span></div>
          <div><b>${Number(backups.userdata || 0)}</b><span>备份</span></div>
        </div>
        <div class="admin-data-path">目录：${escapeHtml(userdata.path || "未配置")}</div>
        <div class="admin-data-actions">
          <input id="adminUserDataUploadInput" type="file" accept=".zip">
          <button class="primary-action" data-admin-upload="userdata" type="button">上传用户数据</button>
          <button data-admin-download="userdata" type="button">下载用户数据</button>
        </div>
        <small>用户数据只上传/下载 zip 包，包含 accounts.dat 和各账号 json；替换前会自动备份。</small>
      </section>

      <section class="admin-data-card">
        <div class="admin-data-card-head">
          <div>
            <b>题库内容清洗</b>
            <span>清除上游塞进解析里的推广文字（金考典、押题、下载链接等）</span>
          </div>
          <span class="status-pill ${state.adminCleanAdsResult ? "active" : "disabled"}">${state.adminCleanAdsResult ? "已执行" : "未执行"}</span>
        </div>
        <div class="admin-data-actions">
          <button data-admin-clean-ads="all" type="button">清洗全部题库</button>
        </div>
        <small>${state.adminCleanAdsResult
          ? escapeHtml(state.adminCleanAdsResult)
          : "拉取题库时会自动清洗；此处用于清理库中已经存在的历史广告。执行前会自动备份数据库，只改动命中广告的题目。"}</small>
      </section>

      <section class="admin-data-card admin-backup-card">
        <div class="admin-data-card-head">
          <div>
            <b>备份</b>
            <span>换题库、导入数据、删题之前系统自动留的快照；可以下载，也可以一键回滚</span>
          </div>
          <span class="status-pill ${backupListOf().length ? "active" : "disabled"}">${backupListOf().length ? backupListOf().length + " 份" : "无备份"}</span>
        </div>
        ${renderBackupRows()}
        <small>回滚前系统会先把「当前状态」再备份一份，所以回滚本身也能撤回；题库回滚会做健康检查，读不出来自动退回原库。只保留最近 20 份、超过 30 天的会被清理。</small>
      </section>
    </div>
  `;
}

/* 备份列表。state.adminBackups 由 loadAdminBackups() 填；没加载过就先显示「读取中」，
   加载失败会写进 state.adminBackupsError（不弹 toast，因为进后台时同时会拉 status）。 */
function backupListOf() {
  return Array.isArray(state.adminBackups) ? state.adminBackups : [];
}

function renderBackupRows() {
  if (state.adminBackupsError) {
    return `<div class="admin-backup-empty">读取备份列表失败：${escapeHtml(state.adminBackupsError)}</div>`;
  }
  if (!Array.isArray(state.adminBackups)) {
    return `<div class="admin-backup-empty">正在读取备份列表...</div>`;
  }
  if (!state.adminBackups.length) {
    return `<div class="admin-backup-empty">还没有备份。换题库、导入用户数据、删题时会自动留一份。</div>`;
  }
  return `<ul class="admin-backup-list">${state.adminBackups.map((item) => {
    const kindText = item.kind === "userdata" ? "用户数据" : "题库";
    const restorable = item.restorable !== false;
    return `<li class="admin-backup-row">
      <div class="admin-backup-main">
        <b>${escapeHtml(item.name)}</b>
        <span>${kindText} · ${escapeHtml(item.updatedAt || "")} · ${formatFileSize(item.size || 0)}</span>
      </div>
      <div class="admin-backup-actions">
        <button type="button" data-backup-download="${escapeHtml(item.name)}">下载</button>
        <button type="button" class="admin-backup-restore" data-backup-restore="${escapeHtml(item.name)}" ${restorable ? "" : "disabled"}>回滚</button>
      </div>
    </li>`;
  }).join("")}</ul>`;
}

async function loadAdminBackups(refresh = false) {
  if (!isAdmin()) return [];
  if (!refresh && Array.isArray(state.adminBackups)) return state.adminBackups;
  state.adminBackupsError = "";
  try {
    const result = await api(`/api/admin/backups?user=${encodeURIComponent(state.user)}`);
    state.adminBackups = Array.isArray(result.items) ? result.items : [];
  } catch (err) {
    state.adminBackups = [];
    state.adminBackupsError = err.message || "未知错误";
  }
  if (state.adminView === "data") renderAdminRows(state.adminRows, state.adminFailedCount);
  return state.adminBackups;
}

async function restoreAdminBackup(name) {
  if (!isAdmin() || !name) return;
  const kindText = String(name).endsWith(".zip") ? "用户数据" : "题库";
  // 回滚是破坏性动作：说清代价再让用户点。confirm 文案里带上备份名，避免点错行。
  const ok = confirm(`确定把${kindText}回滚到这一份备份吗？\n\n${name}\n\n当前状态会先自动再备份一份，回滚后可以再撤回来。`);
  if (!ok) return;
  state.adminBackupBusy = name;
  if (state.adminView === "data") renderAdminRows(state.adminRows, state.adminFailedCount);
  try {
    const result = await api(`/api/admin/backups/restore?user=${encodeURIComponent(state.user)}&name=${encodeURIComponent(name)}`, { method: "POST" });
    toast(result.message || "已回滚");
    state.adminDataStatus = null;
    state.adminBackups = null;   // 回滚本身又产生一份备份，列表要重读
    state.adminCourses = [];
    state.courses = [];
    await loadAdminDataStatus(true).catch(() => {});
    await loadAdminBackups(true).catch(() => {});
    // 题库变了，章节映射和科目列表都得重来
    if (kindText === "题库") await loadCourses().catch(() => {});
  } catch (err) {
    toast(err.message || "回滚失败");
  } finally {
    state.adminBackupBusy = "";
    if (state.adminView === "data") renderAdminRows(state.adminRows, state.adminFailedCount);
  }
}

async function loadAdminDataStatus(refresh = false) {
  if (!refresh && state.adminDataStatus) return state.adminDataStatus;
  const result = await api(`/api/admin/data/status?user=${encodeURIComponent(state.user)}`);
  state.adminDataStatus = result;
  if (state.adminView === "data") renderAdminRows(state.adminRows, state.adminFailedCount);
  return result;
}

function bindAdminDataActions() {
  document.querySelectorAll("[data-admin-download]").forEach((btn) => {
    btn.onclick = () => downloadAdminData(btn.dataset.adminDownload);
  });
  document.querySelectorAll("[data-admin-upload]").forEach((btn) => {
    btn.onclick = () => uploadAdminData(btn.dataset.adminUpload, btn).catch((err) => toast(err.message));
  });
  document.querySelectorAll("[data-admin-clean-ads]").forEach((btn) => {
    btn.onclick = () => cleanAdsInBank(btn).catch((err) => toast(err.message));
  });
  document.querySelectorAll("[data-backup-download]").forEach((btn) => {
    btn.onclick = () => downloadAdminBackup(btn.dataset.backupDownload);
  });
  document.querySelectorAll("[data-backup-restore]").forEach((btn) => {
    btn.onclick = () => restoreAdminBackup(btn.dataset.backupRestore);
  });
  // 进后台「数据」页时才拉列表：它要跑一遍目录枚举，没必要每次开后台都读
  if (state.adminView === "data" && !Array.isArray(state.adminBackups) && !state.adminBackupsError) {
    loadAdminBackups().catch(() => {});
  }
}

/* 直接用链接下载，不走 api()：备份可能几百 MB，走 fetch 再 blob 会在内存里多存一份。
   token 在 cookie 里，链接点击天然带得上。 */
function downloadAdminBackup(name) {
  if (!name) return;
  const url = `/api/admin/backups/download?name=${encodeURIComponent(name)}`;
  const link = document.createElement("a");
  link.href = url;
  link.download = name;
  document.body.appendChild(link);
  link.click();
  link.remove();
}

async function cleanAdsInBank(btn) {
  if (!isAdmin()) return;
  if (!confirm("确定清洗题库中的历史广告内容吗？系统会先自动备份数据库。")) return;
  const oldText = btn.textContent;
  btn.disabled = true;
  btn.textContent = "清洗中...";
  try {
    const result = await api(`/api/admin/clean-ads?user=${encodeURIComponent(state.user)}`, { method: "POST" });
    state.adminCleanAdsResult = result.message || "清洗完成";
    toast(state.adminCleanAdsResult);
    state.adminDataStatus = null;
    state.adminCourses = [];
    state.courses = [];
    state.chapters = [];
    state.types = [];
    state.questions = [];
    state.currentCourse = null;
    if (state.adminView === "data") renderAdminRows(state.adminRows, state.adminFailedCount);
  } finally {
    btn.disabled = false;
    btn.textContent = oldText || "清洗全部题库";
  }
}

function downloadAdminData(type) {
  if (!type) return;
  const url = `/api/admin/data/download?user=${encodeURIComponent(state.user)}&type=${encodeURIComponent(type)}`;
  window.location.href = url;
}

async function uploadAdminData(type, btn) {
  const input = type === "bank" ? $("adminBankUploadInput") : $("adminUserDataUploadInput");
  const file = input?.files?.[0];
  if (!file) {
    toast("请先选择要上传的文件");
    return;
  }
  const label = type === "bank" ? "题库数据" : "用户数据";
  if (!confirm(`确定上传并替换${label}吗？系统会先自动备份当前数据。`)) return;
  const form = new FormData();
  form.append("file", file);
  const oldText = btn.textContent;
  btn.disabled = true;
  btn.textContent = "上传中...";
  try {
    const result = await api(`/api/admin/data/upload?user=${encodeURIComponent(state.user)}&type=${encodeURIComponent(type)}`, {
      method: "POST",
      body: form,
    });
    toast(result.message || "上传完成");
    state.adminDataStatus = result.status || null;
    input.value = "";
    if (type === "bank") {
      state.adminDataStatus = null;
      state.adminCourses = [];
      state.courses = [];
      state.types = [];
      state.questions = [];
      state.currentCourse = null;
      resetChapterState();
      state.currentIndex = -1;
      await loadCourses();
      await loadAdminCourses().catch(() => {});
      state.adminView = "banks";
      await reloadChaptersAfterBankChange();
    } else {
      await loadUsers();
    }
    await renderAdminDashboard();
  } finally {
    btn.disabled = false;
    btn.textContent = oldText;
  }
}

function formatFileSize(bytes) {
  const size = Number(bytes || 0);
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
  if (size < 1024 * 1024 * 1024) return `${(size / 1024 / 1024).toFixed(1)} MB`;
  return `${(size / 1024 / 1024 / 1024).toFixed(1)} GB`;
}

function openAdminUserDialog() {
  $("adminUserNameInput").value = "";
  $("adminUserModal").classList.remove("hidden");
  $("adminUserNameInput").focus();
}

function closeAdminUserDialog() {
  $("adminUserModal").classList.add("hidden");
  $("adminUserNameInput").value = "";
}

async function adminAddUser() {
  const user = ($("adminUserNameInput").value || "").trim();
  if (!user) return;
  if (user.toLowerCase() === "admin") {
    toast("不能创建管理员同名账号");
    return;
  }
  if (state.users.some((item) => item.name.toLowerCase() === user.toLowerCase())) {
    toast("账号已存在");
    return;
  }
  const created = await api(`/api/admin/user-action?user=${encodeURIComponent(state.user)}`, {
    method: "POST",
    headers: { "Content-Type": "application/json; charset=utf-8" },
    body: JSON.stringify({ target: user, action: "create" }),
  });
  await loadUsers();
  await renderAdminDashboard();
  closeAdminUserDialog();
  toast(`已添加用户：${user}，初始密码 ${created.defaultPassword || "见部署配置"}`);
}

function uniqueCount(values) {
  return new Set(values.filter(Boolean)).size;
}

async function adminUserAction(target, action) {
  const names = {
    disable: "停用",
    enable: "启用",
    "reset-password": "重置密码",
    "clear-data": "清空做题数据",
    delete: "删除账号",
  };
  if (["delete", "clear-data", "reset-password", "disable"].includes(action)) {
    /* 确认文案必须写清**后果**（旧文案只说「确定要删除账号「x」吗？」）：
       删账号会连做题数据一起删且不可恢复；重置密码会强制该用户下次登录改密。
       用户按下去之前应该知道自己在放弃什么。 */
    const consequences = {
      delete: `删除后该账号的做题数据、错题本、笔记、收藏会一并删除，且无法恢复。`,
      "clear-data": "该账号的做题记录、错题本、笔记、收藏会被清空，且无法恢复。",
      "reset-password": `密码会被重置回部署配置的初始密码，该用户下次登录必须重新设置新密码。`,
      disable: "该账号将无法登录（数据保留）。",
    };
    const ok = confirm(`确定要${names[action]}「${target}」吗？\n\n${consequences[action]}`);
    if (!ok) return;
  }
  const result = await api(`/api/admin/user-action?user=${encodeURIComponent(state.user)}`, {
    method: "POST",
    headers: { "Content-Type": "application/json; charset=utf-8" },
    body: JSON.stringify({ target, action }),
  });
  const hints = {
    "reset-password": `初始密码已重置为 ${result.defaultPassword || "部署配置值"}，用户下次登录需修改`,
    "clear-data": "该账号做题数据已清空",
    delete: "账号已删除",
    disable: "账号已停用",
    enable: "账号已启用",
  };
  toast(result.message || hints[action] || `${names[action] || "操作"}成功`);
  await loadUsers();
  await renderAdminDashboard();
}

async function adminCorrectionAction(user, correctionId, action) {
  if (!user || !correctionId) return;
  if (action === "delete" && !confirm(`确定删除「${user}」的这条纠错反馈吗？`)) return;
  const res = await api(`/api/user/load?user=${encodeURIComponent(user)}`);
  const data = normalizeStorage(res.data || {});
  const item = data.corrections.find((correction) => String(correction.id) === String(correctionId));
  if (!item) {
    toast("纠错反馈不存在");
    return;
  }
  if (action === "delete") item.status = "deleted";
  if (action === "resolve") {
    item.status = "resolved";
    item.resolvedAt = nowText();
    item.resolvedBy = state.user;
  }
  if (action === "reopen") {
    item.status = "open";
    item.reopenedAt = nowText();
  }
  await api(`/api/user/save?user=${encodeURIComponent(user)}`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      ...(res.revision ? { "If-Match": `"${res.revision}"` } : {}),
    },
    body: JSON.stringify(data),
  });
  toast("纠错反馈已更新");
  await renderAdminDashboard();
}

/* 正确率的**唯一**口径：答对 ÷ 已确认（`correct / verified`）。
   为什么分母不是「已做」（done）：done 里包含「已作答但还没点『确认答案』」的题，
   拿它当分母等于把「还没判分」默认算成「错」。
   实测（done=2 / verified=1 / correct=1，同一批数据、同一屏）：
     学习看板 KPI「正确率」  = 1/2 = 50%
     同屏今日卡片「确认正确率」= 1/1 = 100%
   两个数字都自称「正确率」，范围也一样（当前科目的作答记录），
   所以这不是「本次 vs 累计」的口径差，是算法本身取了两个分母 —— 属于 bug。
   只有确认过的题才谈得上对错，所以分子分母都只取已确认的。
   调用点：getCourseStats 的 KPI、学习日报、会话统计、看板汇总。
   ⚠️ 交卷成绩面板 / 近期趋势用的是**另一次作答**的 scope（本次交卷，交卷时全部判过），
   那边分母是「本次已作答」，不是这里的分母，文案里已标明范围。 */
function confirmedRate(correct, verified) {
  const v = Number(verified || 0);
  return v ? Math.round((Number(correct || 0) / v) * 100) : 0;
}

/* 一条确认记录对「这题当前的作答」还成不成立（快照校验）。
   · true（无快照时代的老记录）→ 成立
   · 带 answer 的对象 → 必须等于这题当前的答案快照
     当前答案为空按「没有反证」处理：考场交卷旧版只写判定不写科目作答，
     而「答案真的被清掉」的路径都会一并删掉确认记录（resetQuestionVerification /
     resetPractice），所以这里不会把改过的答案误判成有效。
   · 其它形状 → 不成立 */
function verificationRecordApplies(record, currentAnswer) {
  if (record === true) return true;
  if (!record || typeof record !== "object") return false;
  if (!("answer" in record)) return true;
  const current = normalizeAnswer(currentAnswer);
  if (!current) return true;
  return String(record.answer || "") === current;
}

/* 把一条确认记录分类：'objective' | 'subjective' | ''（没确认 / 快照已失效）。
   老记录没有 subjective 标记：客观题判过必然落 correct 或 wrong 之一，
   两边都不在的按主观题确认处理（旧数据里主观题确认就是这么进来的）。 */
function classifyVerification(record, currentAnswer, hasVerdict) {
  if (!verificationRecordApplies(record, currentAnswer)) return "";
  if (!record || typeof record !== "object") return "objective";
  if (record.subjective === true) return "subjective";
  if (record.subjective === undefined && !hasVerdict) return "subjective";
  return "objective";
}

/* 「累计确认正确率」的**唯一分子分母来源**：客观题确认集合 + 主观题确认集合。
   为什么必须重做：旧口径分子取 Object.keys(correct).length、
   分母取 Object.keys(verified).length —— 两个集合可以毫不相干。实测（改前）：
     · 新账号考场两题全对（只写 correct、不写 verified）→ 2 / 0 = 0%
     · 再确认一道主观题（只写 verified）→ 2 / 1 = **200%**
   规则（分子 ⊆ 分母，主观题不进分母）：
     ① 有确认记录、记录里的答案快照和当前作答一致、且不是主观题 → 计入客观题集合；
     ② 老记录没有 subjective 标记时按「有没有对错判定」兜底分类（见上）；
     ③ 旧数据里 `correct` 有、确认记录**从来没有过**的题 → 按**历史判定**计入客观集合：
        考场交卷在旧版只写 correct/wrong、从不写 verified，只认 verified 的话
        老账号会整片显示 0%（把「历史口径不同」当成「一道都没答对」）。
     ④ resultLog 里记过账（新口径判定过）的题**不享受 ③** —— 那说明确认记录是被
        resetQuestionVerification 清掉的（答案已经改了），不能算「已确认」。
   不做 clamp：0% / 200% 是集合不一致的症状，压到 100% 只是把症状盖住。 */
function confirmedSets(data) {
  const objective = new Set();
  const subjective = new Set();
  if (data) {
    const answers = data.answers || {};
    const verified = data.verified || {};
    const resultLog = data.resultLog || {};
    Object.entries(verified).forEach(([id, record]) => {
      const kind = classifyVerification(record, answers[id], !!(data.correct?.[id] || data.wrong?.[id]));
      if (!kind) return;
      (kind === "subjective" ? subjective : objective).add(String(id));
    });
    Object.keys(data.correct || {}).forEach((id) => {
      if (verified[id] || resultLog[id]) return;
      objective.add(String(id));
    });
  }
  return { objective, subjective };
}

function getCourseStats(courseId = state.currentCourse?.id) {
  const data = peekCourseStore(courseId);
  /* 分子分母取**同一个集合**：客观题确认集合（见 confirmedSets）。
     分子只数这个集合里 correct 的题 —— correct 只会被非主观题的 markResult 写，
     所以天然不含主观题，也不可能超过分母。主观题单独给一个数（subjective），
     不混进正确率。 */
  const sets = confirmedSets(data);
  const confirmedCorrect = Array.from(sets.objective).filter((id) => !!data.correct?.[id]).length;
  return {
    /* 「已做」= done ∪ correct ∪ wrong（见 isQuestionDone）。
       只数 done 的话，旧版考场交卷 / 导入的老数据会显示「已做 0」，
       和练习页顶部、章节树的口径又对不上 —— 这次一并统一。 */
    done: doneQuestionIds(data).size,
    correct: confirmedCorrect,
    /* verified = 真正的分母（客观题确认集合）。给 KPI 用，顺带让调用方拿到
       分子分母两个数，不用各自再写一遍除法。 */
    verified: sets.objective.size,
    /* 主观题单列：已确认的主观题数量，不计入上面的正确率。 */
    subjective: sets.subjective.size,
    wrong: Object.keys(data.wrong || {}).length,
  };
}

/* 当前科目 KPI 那格的完整口径文案（值 + 分子/分母），
   title 里给鼠标用户 / 读屏一个「这 50% 是怎么算出来的」。 */
function rateKpiTitle(stats) {
  const unverified = Math.max(0,
    Number(stats.done || 0) - Number(stats.verified || 0) - Number(stats.subjective || 0));
  const subjectiveNote = Number(stats.subjective || 0)
    ? `；主观题 ${stats.subjective} 道已确认，单独人工核对，不计入这个正确率`
    : "";
  return `客观题确认正确率：答对 ${stats.correct} ÷ 已确认 ${stats.verified} = ${confirmedRate(stats.correct, stats.verified)}%`
    + `（已做 ${stats.done} 题，其中 ${unverified} 题还没点「确认答案」，不计入正确率${subjectiveNote}）`;
}

/* 上一题 / 下一题的可用性。
   旧实现到头了也照样可点：index 被 Math.min/max 夹住 → 画面一动不动、也没有任何提示，
   屏上还可能残留上一次操作留下的 toast（「已收藏到…」），看着像按钮坏了。
   现在按钮在边界直接禁用，moveQuestion 里再兜一层 toast ——
   滑动手势（不走按钮，走 moveQuestion）也能得到同一句提示。 */
function syncQuestionNavButtons() {
  const total = state.questions.length;
  const index = state.currentIndex;
  const prev = $("prevBtn");
  const next = $("nextBtn");
  if (prev) {
    const atFirst = index <= 0;
    prev.disabled = atFirst || index < 0;
    prev.title = atFirst ? "已经是第一题" : "上一题";
  }
  if (next) {
    const atLast = total > 0 && index >= total - 1;
    next.disabled = atLast || index < 0;
    next.title = index < 0 ? "请先选择题库" : atLast ? "已经是最后一题" : "下一题";
  }
}

async function moveQuestion(delta) {
  if (!state.questions.length) return;
  const next = state.currentIndex + delta;
  /* 边界不再静默：说清为什么不动（旧实现 index 被夹住，一声不响）。 */
  if (next < 0) {
    toast("已经是第一题");
    return;
  }
  if (next > state.questions.length - 1) {
    toast("已经是最后一题");
    return;
  }
  state.currentIndex = next;
  await loadCurrentQuestion().catch((err) => toast(err.message));
}

function isSwipeIgnoredTarget(target) {
  if (!target?.closest) return false;
  return !!target.closest("input, textarea, select, label, a, button.option, .option, .options, .answer-card-wrap, .question-tag-panel, .practice-context-panel, .question-actions, .toolbar, .main-nav, .modal, [data-no-swipe]");
}

function canSwipeQuestions() {
  return !isAdmin()
    && !state.pickerOpen
    && state.questions.length > 1
    && state.currentIndex >= 0
    && !["training", "progress"].includes(state.mode)
    && !$("questionBody")?.classList.contains("hidden");
}

function hasTextSelection() {
  return !!String(window.getSelection?.()?.toString?.() || "").trim();
}

function isMousePointerEvent(event) {
  return event?.pointerType === "mouse" || (!event?.pointerType && event?.type?.startsWith("mouse"));
}

function bindQuestionSwipe() {
  const view = $("questionView");
  if (!view || view.dataset.swipeBound) return;
  view.dataset.swipeBound = "1";
  const startSwipe = (point, target, event) => {
    // On desktop, a mouse drag is text selection/copying, never navigation.
    if (!point || isMousePointerEvent(event) || isSwipeIgnoredTarget(target) || !canSwipeQuestions()) {
      state.touchStart = null;
      return;
    }
    state.touchStart = {
      x: point.clientX,
      y: point.clientY,
      at: Date.now(),
      active: false,
      pointerType: event?.pointerType || "touch",
      selectionAtStart: hasTextSelection(),
    };
  };
  const finishSwipe = (point, event) => {
    const start = state.touchStart;
    state.touchStart = null;
    if (!start || !point || isMousePointerEvent(event) || !start.active || !canSwipeQuestions()) return;
    if (!start.selectionAtStart && hasTextSelection()) return;
    const dx = point.clientX - start.x;
    const dy = point.clientY - start.y;
    const elapsed = Date.now() - start.at;
    if (elapsed > 650 || Math.abs(dx) < 58 || Math.abs(dx) < Math.abs(dy) * 1.25 || Math.abs(dy) > 90) return;
    moveQuestion(dx < 0 ? 1 : -1).catch((err) => toast(err.message));
  };
  const updateSwipe = (point, event, sourceEvent) => {
    const start = state.touchStart;
    if (!start || !point || isMousePointerEvent(sourceEvent)) return;
    const dx = point.clientX - start.x;
    const dy = point.clientY - start.y;
    if (Math.abs(dx) > 18 && Math.abs(dx) > Math.abs(dy) * 1.15) {
      start.active = true;
      event?.preventDefault?.();
    }
  };
  const supportsPointerEvents = "PointerEvent" in window;
  if (supportsPointerEvents) {
    view.addEventListener("pointerdown", (event) => {
      if (event.button && event.button !== 0) return;
      startSwipe(event, event.target, event);
      if (state.touchStart && event.pointerId != null) view.setPointerCapture?.(event.pointerId);
    });
    view.addEventListener("pointermove", (event) => {
      updateSwipe(event, event, event);
    });
    view.addEventListener("pointerup", (event) => {
      finishSwipe(event, event);
      if (event.pointerId != null && view.hasPointerCapture?.(event.pointerId)) view.releasePointerCapture?.(event.pointerId);
    });
    view.addEventListener("pointercancel", (event) => {
      state.touchStart = null;
      if (event.pointerId != null && view.hasPointerCapture?.(event.pointerId)) view.releasePointerCapture?.(event.pointerId);
    });
    view.addEventListener("lostpointercapture", () => {
      state.touchStart = null;
    });
    return;
  }
  // Older browsers without Pointer Events keep the touch-only fallback.
  view.addEventListener("touchstart", (event) => {
    startSwipe(event.changedTouches?.[0], event.target, event);
  }, { passive: true });
  view.addEventListener("touchmove", (event) => {
    updateSwipe(event.changedTouches?.[0], event, event);
  }, { passive: false });
  view.addEventListener("touchend", (event) => {
    finishSwipe(event.changedTouches?.[0], event);
  }, { passive: true });
  view.addEventListener("touchcancel", () => {
    state.touchStart = null;
  }, { passive: true });
}
/* 手动交卷前的二次确认（只在考场里问，且不问自动交卷）。
   旧实现点一下立刻结束考试：3 道未作答不提示，全空白卷也能一键交掉。
   未作答数按答题卡/统计行同一口径（hasAnswer）算，别让确认框里的数字
   和页面上写的对不上。 */
function confirmExamSubmit() {
  if (state.mode !== "exam") return true;
  /* 没有进行中的考试（组卷页静止 / 正在组卷 / 已经交过卷）时先给结论：
     否则会先弹一句「确定交卷吗？已作答 0 题，还有 0 题未作答」再去拒绝它 ——
     这句话本身就是在胡说（根本不存在一份卷子）。
     submitPaper 里那条守卫照样保留：它挡的是**所有**入口，不只是这个按钮。 */
  if (state.submitted) {
    toast("已经交卷");
    return false;
  }
  if (!state.exam || !state.questions.length) {
    toast("当前没有进行中的模拟考试");
    return false;
  }
  const total = state.questions.length;
  const answered = state.questions.filter((item) => hasAnswer(item.id)).length;
  const todo = Math.max(0, total - answered);
  return confirm(`确定交卷吗？\n\n已作答 ${answered} 题，还有 ${todo} 题未作答。交卷后不能再修改答案。`);
}

/* 所有**人工**交卷只有这一条入口：确认（confirmExamSubmit）→ 交卷（submitPaper）。
   旧实现里按钮走确认，Ctrl+Enter 却直接调 submitPaper（实测 2 题、一个字没答，
   按一下快捷键就 submitted=true、揭示答案、写出 0 分记录，一句都没问）。
   超时自动交卷（计时器 tick / 恢复草稿时已过期）仍然直调 submitPaper(true)：
   那一刻没有「用户正在点击」这件事，没法也不能问。 */
function requestExamSubmit() {
  if (!confirmExamSubmit()) return;
  submitPaper().catch((err) => toast(err.message));
}

/* 本轮答案快照指纹：交卷时记下来，用来分出「重复交卷」和「改了答案再交」。
   只比内存里的这份快照（刷新之后 state.submitted 本来就没了，不会再走这条路）。 */
function currentRoundAnswerKey(mode = state.mode, courseId = state.currentCourse?.id) {
  const store = currentPracticeAnswerStore();
  return JSON.stringify([
    Number(courseId || 0),
    mode,
    state.questions.map((item) => [Number(item.id || 0), normalizeAnswer(store[item.id])]),
  ]);
}

async function submitPaper(autoSubmit = false) {
  if (state.submitting) {
    toast("正在提交，请稍候");
    return;
  }
  /* 屏上是「上次缓存的题单」时不交卷：这一批的构成可能已经不是用户看到的筛选结果
     （超时自动交卷除外 —— 那一刻没有用户在场，题单也确实是他那份）。
     按钮侧已经收起来了（见 renderVerifyModeControls），这里是给快捷键等入口兜底。 */
  if (!autoSubmit && state.mode !== "exam" && blockWriteOnStaleList()) return;
  if (state.mode === "exam" && state.submitted && state.exam?.result) {
    toast("已经交卷");
    return;
  }
  if (state.mode === "exam" && state.submitted) {
    toast("正在提交，请稍候");
    return;
  }
  /* 组卷页静止时（state.exam === null）和「开始组卷」后的生成期间都没有
     「进行中的模拟考试」，这里必须拦住。旧实现一路走到底：写一条
     total:0 的假记录进 state.storage.history（学习看板的正确率曲线就吃这个），
     再弹一个「交卷完成 0/0 分」的成绩面板。 */
  if (state.mode === "exam" && (!state.exam || !state.questions.length)) {
    toast("当前没有进行中的模拟考试");
    return;
  }
  const submitMode = state.mode;
  const submitCourseId = Number(state.currentCourse?.id || 0);
  const submitQuestions = state.questions;
  const submitExam = state.exam;
  /* 练习模式原来**没有任何**「已交卷」守卫：再点一次「提交答卷」就再走一遍判定、
     再记一条 history（实测 stage 3→4、history 1→2）。
     但练习交卷后**可以**改答案再交 —— 那是新的一轮判定，必须放行。
     所以判据不是 state.submitted，而是「本轮已交卷 **且** 答案快照没变过」。 */
  if (submitMode !== "exam" && state.submitted
    && state.submittedAnswerKey === currentRoundAnswerKey(submitMode, submitCourseId)) {
    toast("本轮已提交，答案没有变化");
    if (state.lastSubmitResult) showSubmitResult(state.lastSubmitResult);
    return;
  }
  if (submitMode !== "exam" && !submitQuestions.some((item) => hasAnswer(item.id))) {
    toast("请先选择或填写答案");
    return;
  }
  const submitToken = (state.submitToken || 0) + 1;
  state.submitToken = submitToken;
  state.submitting = true;
  renderVerifyModeControls();
  try {
    // Resolve all details before mutating verification/result/history state. A
    // failed request therefore cannot leave a half-submitted training round.
    for (const item of submitQuestions) {
      item.detail = item.detail || await api(`/api/question?id=${item.id}`);
    }
    const contextStillCurrent = () => state.submitToken === submitToken
      && state.questions === submitQuestions
      && state.mode === submitMode
      && Number(state.currentCourse?.id || 0) === submitCourseId;
    if (!contextStillCurrent()) return;
    if (submitMode !== "exam" && !submitQuestions.some((item) => hasAnswer(item.id))) {
      toast("请先选择或填写答案");
      return;
    }
    state.submitted = true;
    state.submittedAnswerKey = currentRoundAnswerKey(submitMode, submitCourseId);
    let correct = 0;
    let checked = 0;
    let score = 0;
    let totalScore = 0;
    let subjective = 0;
    const examDetails = [];
    for (const item of submitQuestions) {
      const itemScore = getQuestionScore(item.detail);
      const itemSubjective = isSubjective(item.detail);
      const answered = hasAnswer(item.id);
      const itemCorrect = answered && !itemSubjective && isAnswerCorrect(item.detail);
      const earnedScore = answered ? calculateQuestionScore(item.detail) : 0;
      totalScore += itemScore;
      if (itemSubjective) subjective++;
      if (submitMode === "exam") {
        examDetails.push({
          id: item.detail.id,
          no: examDetails.length + 1,
          chapterId: item.detail.chapterId,
          chapterName: item.detail.chapterName || "",
          type: item.detail.type || "题目",
          title: stripText(item.detail.stem || item.detail.title || "").slice(0, 120),
          answer: normalizeAnswer(state.answers[item.detail.id]),
          correctAnswer: normalizeAnswer(item.detail.answer),
          answered,
          correct: itemCorrect,
          subjective: itemSubjective,
          score: earnedScore,
          maxScore: itemScore,
        });
      }
      if (!answered) continue;
      checked++;
      if (itemCorrect) correct++;
      score += earnedScore;
      /* 考场交卷也要落「已做」。旧实现这里只走 markResult（写 correct/wrong），
         于是同一道题：答题卡上是黄的（有作答）、统计行写「已完成 0 题」、
         「新题」口径（countNeverDone）还把它当从没做过再推一遍、
         「薄弱章节」里也永远看不见 —— 四个口径各说各话。 */
      /* 考场交卷也要落「长期作答 + 长期确认」。
         旧实现只写 done + markResult（correct/wrong），`verified` 一条都不写 ——
         于是新账号考场两题全对，累计 KPI 显示「correct 2 / verified 0 = 0%」；
         再确认一道主观题（只写 verified）就成了 2/1 = 200%（见 getCourseStats）。
         答案快照必须一起写：确认记录的 answer 要能和「这题当前的作答」对上，
         否则下次读统计时会被当成失效快照。 */
      if (submitMode === "exam") {
        const examStore = userCourseStore();
        examStore.done[item.detail.id] = true;
        examStore.answers ||= {};
        examStore.answers[item.detail.id] = currentAnswerSnapshot(item.detail.id);
      }
      markQuestionVerified(item.detail);
      if (!itemSubjective) markResult(item.detail);
      if (submitMode !== "exam") recordPracticeActivity(item.detail, { verified: true });
    }
    const rate = checked ? Math.round((correct / checked) * 100) : 0;
    const submittedAt = nowText();
    let examRecordId = "";
    if (submitMode === "exam" && submitExam) {
      submitExam.submittedAt = Date.now();
      submitExam.result = { score, totalScore, correct, checked, rate, subjective };
      examRecordId = recordExamHistory({ score, totalScore, correct, checked, rate, subjective, autoSubmit, submittedAt }, examDetails)?.id || "";
      clearExamDraft();
      stopExamTimer();
    }
    if (examRecordId) {
      /* 考场记录：history 里**只放一条指针**（{ref, at} + 科目信息）。
         分数与明细一律回 examHistory 取（唯一事实来源）—— 旧实现两边各存一份，
         口径一旦不同就是两个都说自己对。练习卷（非考场）没有 examHistory 记录，
         照旧自带分数（趋势图的数据源解析见 trendSeriesForCourse）。 */
      state.storage.history.unshift({
        user: state.user,
        courseId: state.currentCourse?.id,
        courseName: state.currentCourse?.name,
        mode: "exam",
        ref: examRecordId,
        at: submittedAt,
      });
    } else {
      state.storage.history.unshift({
        user: state.user,
        courseId: state.currentCourse?.id,
        courseName: state.currentCourse?.name,
        mode: submitMode,
        total: submitQuestions.length,
        done: checked,
        correct,
        score,
        totalScore,
        at: submittedAt,
      });
    }
    state.storage.history = state.storage.history.slice(0, 200);
    scheduleSave();
    state.answerVisible = true;
    state.answerHiddenOverride = false;   // 交卷后答案必须看得到（本次作答的对错全在这里）
    renderQuestion();
    renderPracticeContextPanel();
    if (submitMode === "exam") {
      const subjectiveHint = subjective ? `，主观题 ${subjective} 道未自动计分` : "";
      toast(`${autoSubmit ? "时间到，已自动交卷" : "已交卷"}：${formatScore(score)}/${formatScore(totalScore)} 分${subjectiveHint}`);
    } else {
      const subjectiveHint = subjective ? `，主观题 ${subjective} 道需人工核对` : "";
      toast(`已提交：${correct}/${checked} 题正确${subjectiveHint}`);
    }
    /* 弹出成绩面板。练习模式（真题/章节练习这类纸质卷）原来只弹一个几秒就消失的
       toast，考试模式只有顶栏一行小字 —— 用户要的是「交卷后算出具体得分」，
       所以两种模式都给一个能看清构成的持久面板。 */
    /* 记一份：非考试模式「答案没变又点交卷」时直接把同一份成绩面板再打开，
       不再重复判分、不再重复记 history。 */
    state.lastSubmitResult = {
      score, totalScore, correct, checked,
      total: submitQuestions.length,
      rate, subjective, autoSubmit,
    };
    showSubmitResult(state.lastSubmitResult);
  } finally {
    if (state.submitToken === submitToken) {
      state.submitting = false;
      renderVerifyModeControls();
    }
  }
}
function resetPractice() {
  const resettingExam = state.mode === "exam";
  if (resettingExam) {
    /* ⚠️ 确认之前**不许碰**考试的生命周期：旧实现在这里先 stopExamTimer() 再问，
        用户点「取消」之后 timer 已经没了 —— 考试还在、endsAt 还在真实倒计时，
        但状态条冻住、到点提醒和自动交卷都不会再触发（浏览器实测 timer true→false）。
        取消路径必须零副作用。 */
    if (!confirm("确定放弃本次模拟考试吗？已答内容将清除。")) return;
    stopExamTimer();
    clearExamDraft();
    state.exam = null;
    state.answers = {};
    state.submitted = false;
    state.answerVisible = false;
    state.questions = [];
    state.currentIndex = -1;
    scheduleSave();
    renderExamHome();
    toast("已放弃本次模拟考");
    return;
  }

  const ids = state.questions.map((item) => Number(item.id)).filter(Boolean);
  if (!ids.length) {
    toast("当前没有可重置的题目");
    return;
  }
  /* 屏上是陈旧题单时不碰写入：清的是「上次缓存的那一份」，和用户以为的题单可能不是一回事。 */
  if (blockWriteOnStaleList()) return;
  const scopeLabel = state.currentChapter
    ? `当前章节「${state.currentChapter.name}」共 ${ids.length} 题`
    : `当前题单共 ${ids.length} 题`;
  if (!confirm(`仅清空${scopeLabel}的作答记录，不会影响其他章节。\n\n确定继续？`)) return;
  /* 练习模式本来不该有考试计时器；真挂着也不能留在后台。
     放在确认之后 —— 取消这条路径同样什么都不能改（见上面的注释）。 */
  stopExamTimer();

  const courseStore = userCourseStore();
  const batchSession = activeSmartTrainingSession();
  if (batchSession) {
    batchSession.answers = {};
    batchSession.verifiedAnswers = {};
    batchSession.results = {};
    batchSession.status = "active";
    batchSession.completedAt = "";
    batchSession.currentIndex = 0;
    batchSession.lastSubjectId = 0;
  }
  state.answers = {};
  state.currentIndex = 0;
  state.answerCardPage = 0;
  state.submitted = false;
  state.answerVisible = false;
  for (const id of ids) {
    const key = String(id);
    delete courseStore.answers?.[id];
    delete courseStore.answers?.[key];
    delete courseStore.done?.[id];
    delete courseStore.done?.[key];
    delete courseStore.correct?.[id];
    delete courseStore.correct?.[key];
    delete courseStore.wrong?.[id];
    delete courseStore.wrong?.[key];
    delete courseStore.verified?.[id];
    delete courseStore.verified?.[key];
    /* 本轮判定也一笔勾销：答案都清空了，记账表再留着会让「重新做一遍」推不动 SRS。
       只清**本轮记账**，SRS 自己的 state.storage.wrong（stage/count）不动。 */
    delete courseStore.resultLog?.[id];
    delete courseStore.resultLog?.[key];
  }
  // Keep course-level progress for other chapters intact.
  scheduleSave();
  if (!state.questions.length) {
    renderAll();
    toast("已清空当前题单作答");
    return;
  }
  renderQuestion();
  toast("已清空当前题单作答");
}

function isFavorite(id) {
  return !!state.storage.favorite[id];
}

function toggleFavorite() {
  const q = state.questions[state.currentIndex]?.detail;
  if (!q) return;
  if (state.storage.favorite[q.id]) {
    delete state.storage.favorite[q.id];
    toast("已取消收藏");
  } else {
    /* 默认塞进「考前速看」分组 —— 分组名必须说出来，否则用户既不知道收进去了，
       也不知道它在哪个分组里（改分组在题目下方的标签面板里）。 */
    ensureFavoriteRecord(q, "考前速看");
    toast("已收藏到「考前速看」，可在标签面板改分组");
  }
  scheduleSave();
  renderQuestion();
}

function getNote(id) {
  return state.storage.notes[id] || "";
}

/* ── 笔记保存 ────────────────────────────────────────────────────────────
   两条硬约束（旧实现的坑都在这）：
   1. 题号必须在**输入发生的那一刻**捕获。旧实现在 300ms 防抖回调里现读
      「当前题」，快速切题时会把这行字写到**新题**上（实测：给 qA 打字后立刻点
      下一题 → notes 里多出 {新题id: ""}，qA 一个字都没存下来）。
   2. 空串不写进 notes。留一个 "" 在表里，学习看板按 truthy 数（算 0 篇），
      导出/同步里它又是真实存在的一条，两个口径打架。
   ──────────────────────────────────────────────────────────────────────── */
let pendingNote = null;   // { id, value }，正在输入、还没落库的这份笔记

function currentNoteQuestionId() {
  return state.questions[state.currentIndex]?.detail?.id ?? null;
}

function saveNote(questionId = null, value = null) {
  const qid = questionId ?? currentNoteQuestionId();
  if (!qid) return;
  const editor = $("noteEditor");
  const text = value != null ? String(value) : (editor ? editor.value : "");
  const previous = state.storage.notes[qid] || "";
  if (text === previous) {
    refreshNoteButton();
    return;   // 没变就别写、也别把存档标脏（省一次整包 POST）
  }
  if (text) state.storage.notes[qid] = text;
  else delete state.storage.notes[qid];
  scheduleSave();
  refreshNoteButton();
}

/* 把挂起的那份输入立刻落库（切题、点标签 chip 之前调）。
   pendingNote 自带题号，所以哪怕此刻已经翻到别的题，写的还是原来那道题。 */
function flushPendingNote() {
  if (!pendingNote) return;
  const { id, value } = pendingNote;
  pendingNote = null;
  saveNote(id, value);
}

/* 笔记防抖：题号和文本都在**这次 input** 里抓死（见上面第 1 条）。 */
const scheduleNoteSave = debounce(flushPendingNote, 300);

/* 「这题有笔记」提示：给 #noteBtn 加 .has-note（变色 + 角标）。
   旧实现里 #noteBtn 的 onclick 只 classList.toggle("hidden") 切编辑框，
   class 恒为空 —— 翻完一整章也看不出哪道题写过笔记。 */
function refreshNoteButton() {
  const btn = $("noteBtn");
  if (!btn) return;
  const q = state.questions[state.currentIndex]?.detail;
  btn.classList.toggle("has-note", !!q && !!getNote(q.id));
  btn.title = q && getNote(q.id) ? "本题有笔记（点开查看/编辑）" : "写本题笔记";
}

/* 点「笔记」：切编辑框显隐。
   手机上编辑框长期落在屏幕外（实测 350×116 的框在 y=892，而视口只有 844），
   旧实现只 classList.toggle("hidden")，不聚焦也不滚动 ——
   用户点完「操作 → 笔记」，屏上什么都没发生，只能以为按钮坏了。
   block:"nearest" 只在需要时才滚，不会把整页跳走。 */
function toggleNoteEditor() {
  const editor = $("noteEditor");
  if (!editor) return;
  const willShow = editor.classList.contains("hidden");
  editor.classList.toggle("hidden", !willShow);
  if (!willShow) return;   // 收起时不抢焦点
  editor.focus({ preventScroll: true });
  editor.scrollIntoView({ block: "nearest", behavior: "smooth" });
}

function toggleAnswer() {
  if (state.currentIndex < 0) return;
  const q = state.questions[state.currentIndex]?.detail;
  /* 目标状态是「点完之后答案该不该在屏上」，而不是「翻转 state.answerVisible」。
     背题模式下答案本来就在屏上（shouldRevealCurrentAnswer 恒真），
     翻了 answerVisible 也收不回来 —— 旧实现点两次文案回到「显示答案」、
     答案却还在。这里按可见性算，并落一个显式覆盖位。 */
  const reveal = !shouldRevealCurrentAnswer(q);
  state.answerHiddenOverride = !reveal;
  state.answerVisible = reveal;
  scheduleSave();
  renderQuestion();
}

function examDraftForCurrentCourse() {
  if (!state.currentCourse) return null;
  const courseKey = String(state.currentCourse.id || "");
  state.storage.examDrafts = state.storage.examDrafts && typeof state.storage.examDrafts === "object"
    ? state.storage.examDrafts
    : {};
  if (state.storage.examDraft?.courseId && !state.storage.examDrafts[String(state.storage.examDraft.courseId)]) {
    state.storage.examDrafts[String(state.storage.examDraft.courseId)] = state.storage.examDraft;
    state.storage.examDraft = null;
    scheduleSave();
  }
  const draft = state.storage.examDrafts[courseKey];
  if (!draft || typeof draft !== "object") return null;
  if (!Array.isArray(draft.questionIds) || !draft.questionIds.length) return null;
  return draft;
}

function examDraftAnswerCount(draft = examDraftForCurrentCourse()) {
  if (!draft?.answers) return 0;
  return Object.values(draft.answers).filter((answer) => normalizeAnswer(answer).length > 0).length;
}

/* 草稿里最多存多少道题。
   真实卷子：内置规则最大 80 题（标准卷 单选 60 + 多选 20；安装计量 40+20+20；
   案例卷 25），手填题量也只有几百。1000 是一眼能看出「这不是正常卷子」的量级：
   约 8KB 的 questionIds、恢复时 10 个 100/批的请求。
   超过就不写草稿 —— 宁可不存，也不静默截断出一张**缺题**的卷子。 */
const EXAM_DRAFT_MAX_IDS = 1000;

function saveExamDraft() {
  if (state.mode !== "exam" || !state.exam || state.submitted || !state.currentCourse) return;
  /* 题单只能从 state.exam 取，**不能**读活的 state.questions：
     切模块时 state.questions 已经换成别的模块的题，而 state.mode 先一步被改成 "exam"，
     于是「5 题卷答了 2 题 → 切错题本 → 回考场」会把草稿覆盖成错题本那一题
     （实测：10 题卷/2 个答案 → 被覆盖成 4 题/1 个答案，原卷和答案全丢）。 */
  const ids = Array.isArray(state.exam.questionIds) ? state.exam.questionIds.map(Number).filter(Boolean) : [];
  if (!ids.length) return;
  if (ids.length > EXAM_DRAFT_MAX_IDS) {
    if (state.exam.draftSkipNotified !== ids.length) {
      state.exam.draftSkipNotified = ids.length;
      toast(`本卷共 ${ids.length} 题，超过草稿上限 ${EXAM_DRAFT_MAX_IDS} 题：本次考试不保存草稿（关页后无法续考，交卷不受影响）`);
    }
    return;
  }
  const courseKey = String(state.currentCourse.id);
  state.storage.examDrafts = state.storage.examDrafts && typeof state.storage.examDrafts === "object"
    ? state.storage.examDrafts
    : {};
  const draft = {
    id: state.exam.draftId || `${state.exam.startedAt}-${state.currentCourse.id}`,
    courseId: state.currentCourse.id,
    courseName: state.currentCourse.name || "",
    rule: state.exam.rule,
    scoreMap: state.exam.scoreMap || {},
    questionIds: ids,
    answers: { ...state.answers },
    currentIndex: Math.max(0, state.currentIndex),
    answerCardPage: Math.max(0, state.answerCardPage),
    startedAt: Number(state.exam.startedAt || Date.now()),
    endsAt: Number(state.exam.endsAt || Date.now()),
    remainingMs: getExamRemainingMs(state.exam),
    pausedAt: state.exam.pausedAt || null,
    pauseReason: state.exam.pauseReason || "",
    savedAt: Date.now(),
    savedAtText: nowText(),
  };
  state.storage.examDrafts[courseKey] = draft;
  state.storage.examDraft = null;
  state.exam.draftId = draft.id;
  scheduleSave();
}

function clearExamDraft(courseId = state.currentCourse?.id) {
  const courseKey = String(courseId || "");
  if (!courseKey || !state.storage.examDrafts?.[courseKey]) return;
  delete state.storage.examDrafts[courseKey];
  state.storage.examDraft = null;
  scheduleSave();
}

function renderExamDraftCard(draft = examDraftForCurrentCourse()) {
  if (!draft) return "";
  const leftMs = draft.pausedAt != null
    ? Math.max(0, Number(draft.remainingMs || 0))
    : Math.max(0, Number(draft.endsAt || 0) - Date.now());
  const expired = leftMs <= 0;
  const paused = !!draft.pausedAt && !expired;
  const answered = examDraftAnswerCount(draft);
  const total = draft.questionIds?.length || 0;
  return `
    <div class="exam-draft-card">
      <div>
        <strong>未完成考试</strong>
        <span>${escapeHtml(draft.rule?.name || "模拟卷")} · 已答 ${answered}/${total} 题 · ${expired ? "已到交卷时间" : paused ? `已暂停 · 剩余 ${formatSeconds(leftMs / 1000)}` : `剩余 ${formatSeconds(leftMs / 1000)}`}</span>
        <small>${escapeHtml(draft.courseName || state.currentCourse?.name || "")} · 保存于 ${escapeHtml(draft.savedAtText || "")}</small>
      </div>
      <div class="exam-draft-actions">
        <button id="resumeExamDraftBtn" class="primary-action" type="button">继续考试</button>
        <button id="abandonExamDraftBtn" type="button">放弃草稿</button>
      </div>
    </div>
  `;
}

function bindExamDraftActions() {
  const resumeBtn = $("resumeExamDraftBtn");
  if (resumeBtn) resumeBtn.onclick = () => resumeExamDraft().catch((err) => toast(err.message));
  const abandonBtn = $("abandonExamDraftBtn");
  if (abandonBtn) abandonBtn.onclick = () => abandonExamDraft();
}

/* ── 组卷 / 续考的「代际」守卫 ──────────────────────────────────────────
   组卷和续考都是「先发请求、稍后写 state」的异步流程。等待期间用户可以切科目、
   换模块、登出，甚至再点一次组卷/续考 —— **迟到的回包绝不能写进活的 state**：
   实测旧实现里科目 1 挂起的组卷回包会把科目 2 的题单变成 [202, 101]（两道不同
   科目的题混在一起），还会清空当前答案、凭空建出一份混合科目的 exam；
   科目 1 挂起的续考回包则会把科目 1 的题写进科目 2 的草稿（examDrafts[2]）。
   所以：流程开始时领一个自增的 generationId，并在这一刻钉住
   用户 / 科目 / 模式 / 导航代际（modeTransitionId）；结果先在**局部变量**里
   累积，写 state 之前必须证明「还是我这一代，且上下文一个都没变」。
   不能在活 state 上先累加、最后再检查 —— 检查失败时脏数据已经进去了。 */
function beginExamFlow(kind) {
  const generationId = (state.examGenerationId || 0) + 1;
  state.examGenerationId = generationId;
  return {
    kind,
    generationId,
    user: state.user,
    course: state.currentCourse,
    courseId: Number(state.currentCourse?.id || 0),
    mode: state.mode,
    transitionId: state.modeTransitionId || 0,
  };
}

function examFlowStillCurrent(flow) {
  if (!flow) return false;
  if (state.examGenerationId !== flow.generationId) return false;   // 已经有新的一代顶上
  if (state.user !== flow.user) return false;                      // 换了账号/登出
  if (state.mode !== flow.mode) return false;                      // 切了模块
  if (state.currentCourse !== flow.course) return false;           // 换了科目对象
  if (Number(state.currentCourse?.id || 0) !== flow.courseId) return false;
  if ((state.modeTransitionId || 0) !== flow.transitionId) return false;   // 导航过一次再回来
  return true;
}

/* 流程作废时的收尾：一个字都不写 state，也不要去动 DOM（用户很可能已经在别的
   模块里了）。只有「仍停在同一科目的考场首页」时才把组卷页恢复出来 —— 那时
   界面上还挂着「正在组卷...」的占位。 */
function abortExamFlow(flow) {
  if (flow?.kind === "generate" && state.mode === "exam"
    && Number(state.currentCourse?.id || 0) === flow.courseId) {
    renderExamHome();
  }
  toast(flow?.kind === "resume"
    ? "已取消恢复考试：期间切换了科目或模块"
    : "已取消这次组卷：期间切换了科目或模块");
}

/* 草稿缺题：**明确列出缺哪几道**，由用户选「放弃 / 重新组卷」，绝不静默缩卷。
   旧实现只校验「拉到的题非空」：草稿 [101,102] 只拉到 101 也照样开考，而
   state.exam.questionIds 和新写回去的草稿仍是 [101,102] —— 顶栏满分按 102 题算、
   判分只算 101 题，用户既不知道少了一道，也永远拿不到满分。
   返回值："abandon"（放弃草稿） / "regenerate"（按原配置重新组卷） /
   "keep"（关闭 = 什么都不改，草稿留着等题库恢复后再续考）。 */
function askExamDraftQuestionGap(missingIds, draft) {
  return new Promise((resolve) => {
    const shown = missingIds.slice(0, 12).map((id) => `#${id}`).join("、");
    const more = missingIds.length > 12 ? ` 等共 ${missingIds.length} 道` : "";
    const host = document.createElement("div");
    host.id = "examDraftMissingModal";
    host.className = "modal-backdrop";
    host.innerHTML = `
      <div class="modal-panel conflict-modal" role="dialog" aria-modal="true" aria-labelledby="examDraftMissingTitle">
        <div class="modal-head">
          <strong id="examDraftMissingTitle">这份考试草稿缺题</strong>
          <button type="button" data-exam-draft-choice="keep">关闭</button>
        </div>
        <p class="conflict-message">草稿共 ${Number(draft.questionIds?.length || 0)} 题，其中
          <b>${missingIds.length}</b> 道题在题库里已经不存在：${escapeHtml(shown)}${escapeHtml(more)}<br>
          继续考试会让题数、顺序和满分跟你当初看到的那份卷子对不上，所以不能直接开考。</p>
        <div class="modal-actions conflict-actions">
          <button type="button" class="primary-action" data-exam-draft-choice="regenerate">按原配置重新组卷</button>
          <button type="button" data-exam-draft-choice="abandon">放弃这份草稿</button>
        </div>
        <small class="conflict-hint">关闭 = 保留草稿（本场作答不丢），等题库恢复后再续考。</small>
      </div>`;
    const finish = (choice) => {
      host.remove();
      resolve(choice);
    };
    host.querySelectorAll("[data-exam-draft-choice]").forEach((btn) => {
      btn.onclick = () => finish(btn.dataset.examDraftChoice);
    });
    host.addEventListener("click", (event) => {
      if (event.target === host) finish("keep");
    });
    document.addEventListener("keydown", function onDraftGapKey(event) {
      if (event.key !== "Escape") return;
      document.removeEventListener("keydown", onDraftGapKey, true);
      finish("keep");
    }, true);
    document.body.appendChild(host);
  });
}

/* 考场运行时（state.exam）的唯一构造处。新卷和续考草稿原来各写一份字面量，
   字段一一对应但已经漂移（草稿多 draftId）；满分重算也是两段一模一样的 reduce。
   合并到这里：满分一律按**实际题单**重算，不按用户填的数字 —— 题量填得比题库还大时
   （实测单选池 6727、用户填 7227）旧实现顶栏写「满分 7227 分」而卷子只有 6727 题。
   分值口径与判分完全一致：每题都过 getQuestionScore（scoreMap 优先）。 */
function buildExamRuntime({ rule, scoreMap, questions, questionIds, remainingMs, startedAt, draftId }) {
  const started = Number(startedAt || Date.now());
  const remaining = Math.max(0, Number(remainingMs || 0));
  const runtime = {
    rule: rule || getExamRule(),
    scoreMap: scoreMap || {},
    /* 题单随考试对象一起存：saveExamDraft 只认它（见那边的注释）。 */
    questionIds: questionIds || (questions || []).map((item) => Number(item.id)).filter(Boolean),
    startedAt: started,
    endsAt: Date.now() + remaining,
    remainingMs: remaining,
    pausedAt: null,
    pauseReason: "",
    submittedAt: null,
    result: null,
  };
  if (draftId) runtime.draftId = draftId;
  runtime.rule.totalScore = (questions || []).reduce((sum, item) => sum + getQuestionScore(item.detail || item), 0);
  return runtime;
}

async function resumeExamDraft() {
  const draft = examDraftForCurrentCourse();
  if (!draft) {
    toast("没有可继续的模拟考试");
    renderExamHome();
    return;
  }
  /* 题号先按**集合**去重：重复 id 会让「题数必须等于草稿题数」的校验永远失败，
     而这种 id 本来也只该考一次。 */
  const ids = Array.from(new Set(draft.questionIds.map(Number).filter(Boolean)));
  if (!ids.length) {
    clearExamDraft();
    toast("考试草稿已失效");
    renderExamHome();
    return;
  }
  setPanelPage(false);
  stopExamTimer();
  state.typeRequestId = (state.typeRequestId || 0) + 1;
  state.questionRequestId = (state.questionRequestId || 0) + 1;
  state.submitToken = (state.submitToken || 0) + 1;
  state.submitting = false;
  state.mode = "exam";
  /* ⚠️ 恢复期间**一个字都不写** state.questions / state.answers / state.exam：
     旧实现先把 answers 灌成草稿那份、再 await 拉题，回包只查 `state.mode === "exam"`
     —— 科目 1 的草稿回包就这样写进了科目 2（实测 examDrafts[2] 里是科目 1 的题和
     原 draftId）。现在全部攒在局部变量里，提交前按 flow 校验上下文。 */
  const flow = beginExamFlow("resume");
  /* 分批拉题（100/批，见 fetchQuestionsByIds）。
     旧实现把全部 ids 拼进查询串：4200 题的 URL 37KB+，直接顶穿 Kestrel 的
     MaxRequestLineSize(32KB) → HTTP 414，草稿**永远恢复不了**，
     而 414 的响应体是空/HTML，前端拿到的只有一句英文 "URI Too Long"。 */
  const params = new URLSearchParams({
    courseId: String(flow.courseId),
    limit: String(ids.length),
  });
  let items;
  try {
    items = await fetchQuestionsByIds(params, ids, () => !examFlowStillCurrent(flow));
  } catch (err) {
    if (err?.status === 414) throw new Error("这份草稿的题号太长，服务器拒绝了这个请求（414）。请放弃这份草稿后重新组卷。");
    throw err;
  }
  if (!examFlowStillCurrent(flow)) return abortExamFlow(flow);
  /* 拉回来的必须**逐题**对上草稿：少一道就不是同一份卷子。多出来的（服务端不按
     ids 过滤时的兜底）直接丢掉，别让它混进题单把题数撑大。 */
  const wanted = new Set(ids);
  const usable = (items || []).filter((item) => wanted.has(Number(item?.id || 0)));
  const loadedIds = new Set(usable.map((item) => Number(item.id)));
  const missingIds = ids.filter((id) => !loadedIds.has(id));
  if (missingIds.length) {
    const choice = await askExamDraftQuestionGap(missingIds, draft);
    /* 缺题对话框也可能选着选着就切走了（native/dispatch 都可能）—— 再查一次。 */
    if (!examFlowStillCurrent(flow)) return;
    if (choice === "abandon") {
      clearExamDraft(flow.courseId);
      toast(`已放弃这份草稿：其中 ${missingIds.length} 道题在题库中已不存在`);
      renderExamHome();
      return;
    }
    if (choice === "regenerate") {
      clearExamDraft(flow.courseId);
      const rule = draft.rule || getExamRule();
      if (!normalizeExamParts(rule).length) {
        toast("当前题库没有可用于组卷的题型");
        renderExamHome();
        return;
      }
      state.examConfig = rule;
      toast("已作废缺题的草稿，按原配置重新组卷");
      await startExamPaper();
      return;
    }
    toast("已保留这份草稿，没有开始考试");
    renderExamHome();
    return;
  }
  const loaded = orderItemsByIds(usable, ids);
  /* 提交前最后一次校验（缺题对话框期间同样可能切科目/切模块）。 */
  if (!examFlowStillCurrent(flow) || loaded.length !== ids.length) return abortExamFlow(flow);
  const remaining = draft.pausedAt != null
    ? Math.max(0, Number(draft.remainingMs || 0))
    : Math.max(0, Number(draft.endsAt || 0) - Date.now());
  state.questions = loaded;
  /* 卷子也是「题单」的一种：记下它的模块身份（UI-05）。否则切回「题库」时
     屏上的考卷会被当成同一个模块的旧题单继续留着。 */
  markQuestionListLoadedIdentity();
  state.answers = { ...(draft.answers || {}) };
  state.submitted = false;
  state.answerVisible = false;
  state.answerCardPage = Number(draft.answerCardPage || 0);
  state.currentIndex = Math.min(Math.max(0, Number(draft.currentIndex || 0)), loaded.length - 1);
  state.exam = buildExamRuntime({
    rule: draft.rule || getExamRule(),
    scoreMap: draft.scoreMap || {},
    questions: loaded,
    questionIds: ids,
    startedAt: draft.startedAt,
    remainingMs: remaining,
    draftId: draft.id || `${Date.now()}-${flow.courseId}`,
  });
  renderAll();
  await loadCurrentQuestion().catch((err) => toast(err.message));
  if (!examFlowStillCurrent(flow)) return;
  if (remaining <= 0) {
    await submitPaper(true);
  } else {
    startExamTimer();
    toast("已恢复未完成考试");
  }
}

function abandonExamDraft() {
  const draft = examDraftForCurrentCourse();
  if (!draft) return;
  if (!confirm("确定放弃这份未完成考试吗？草稿答案会被删除。")) return;
  clearExamDraft();
  if (state.mode === "exam") {
    stopExamTimer();
    state.exam = null;
    state.questions = [];
    state.answers = {};
    state.currentIndex = -1;
    state.submitted = false;
    state.answerVisible = false;
    renderExamHome();
  }
  toast("已放弃未完成考试");
}

/* 考试历史 = 考试记录的**唯一事实来源**（分数、明细、趋势点都从这里取；
   storage.history 里的考场条目只留一条 {ref, at} 指针，见 submitPaper）。
   逐题数据（questionIds + answers + details）只保留**最近 1 条**：
   单卷实测 6,167 B，上限 50 条 = 300KB 的同步包，而逐题明细只有最近几场会看。
   更早的场次只留分数摘要，并**显式标记** perQuestionPruned —— 渲染时说
   「该场次已仅保留成绩」，不假装数据还在（见 renderExamHistoryDetails）。 */
function recordExamHistory(result, details = []) {
  if (!state.exam) return null;
  state.storage.examHistory ||= [];
  const submittedAtMs = state.exam.submittedAt || Date.now();
  const usedSeconds = Math.max(0, Math.round((submittedAtMs - state.exam.startedAt) / 1000));
  const record = {
    id: `${submittedAtMs}-${state.currentCourse?.id || 0}`,
    name: state.exam.rule?.name || "模拟考场",
    courseId: state.currentCourse?.id || 0,
    courseName: state.currentCourse?.name || "",
    total: state.questions.length,
    done: result.checked,
    correct: result.correct,
    score: result.score,
    totalScore: result.totalScore,
    rate: result.rate,
    subjective: result.subjective,
    durationMinutes: state.exam.rule?.durationMinutes || 0,
    usedSeconds,
    autoSubmit: !!result.autoSubmit,
    at: result.submittedAt,
    questionIds: state.questions.map((item) => Number(item.id)).filter(Boolean),
    answers: { ...state.answers },
    details: details.slice(0, 200),
  };
  state.storage.examHistory.unshift(record);
  state.storage.examHistory = state.storage.examHistory.slice(0, 50);
  state.storage.examHistory.forEach((item, index) => {
    if (index === 0) return;
    if (!item.questionIds && !item.answers && !item.details) return;
    delete item.questionIds;
    delete item.answers;
    delete item.details;
    item.perQuestionPruned = true;
  });
  return record;
}

/* 当前科目的模拟记录（新的在前）。列表和抽屉共用，口径只有这一处。 */
function examHistoryForCourse(courseId = state.currentCourse?.id) {
  const id = Number(courseId || 0);
  return (state.storage.examHistory || [])
    .filter((item) => !id || Number(item.courseId || 0) === id);
}

/* 一条模拟记录（首页列表 = 只读；抽屉 = 带「删除」）。 */
function renderExamHistoryRow(item, options = {}) {
  const used = Number(item.usedSeconds || 0);
  return `
    <div class="exam-history-row">
      <b>${formatScore(item.score)} / ${formatScore(item.totalScore)} 分</b>
      <span>${Number(item.correct || 0)}/${Number(item.done || 0)} 题 · ${Number(item.rate || 0)}%</span>
      <small>${escapeHtml(item.at || "")} · 用时 ${formatSeconds(used)}${item.autoSubmit ? " · 自动交卷" : ""}</small>
      ${options.deletable ? `<div class="exam-history-row-actions">
        ${renderExamHistoryDetails(item)}
        <button class="exam-history-delete" type="button" data-exam-history-delete="${escapeHtml(item.id)}">删除</button>
      </div>` : renderExamHistoryDetails(item)}
    </div>
  `;
}

const EXAM_HISTORY_PREVIEW_COUNT = 5;

function renderExamHistory() {
  const all = examHistoryForCourse();
  const records = all.slice(0, EXAM_HISTORY_PREVIEW_COUNT);
  if (!records.length) {
    return `
      <div class="exam-history-card">
        <div class="exam-section-title">模拟记录</div>
        <span class="muted">暂无模拟考记录</span>
      </div>
    `;
  }
  /* 标题写清「最近几场 + 一共几场」：旧实现只写「模拟记录」，第 6 场之后
     在任何入口都看不到（实测 7 场只显示 5 行、既没有「更多」也没有删除入口）。 */
  const title = all.length > EXAM_HISTORY_PREVIEW_COUNT
    ? `最近 ${EXAM_HISTORY_PREVIEW_COUNT} 场模拟记录 · 共 ${all.length} 场`
    : `模拟记录 · 共 ${all.length} 场`;
  return `
    <div class="exam-history-card">
      <div class="exam-section-title">${title}</div>
      <div class="exam-history-list">
        ${records.map((item) => renderExamHistoryRow(item)).join("")}
      </div>
      ${all.length > EXAM_HISTORY_PREVIEW_COUNT ? `
        <div class="exam-history-more">
          <button id="examHistoryAllBtn" type="button">查看全部 ${all.length} 场</button>
        </div>
      ` : ""}
    </div>
  `;
}

/* 首页历史卡片的局部重绘（删记录后同步，不用整页重渲染）。 */
function refreshExamHistoryCard() {
  const host = $("examHistoryCard");
  if (!host) return;
  host.innerHTML = renderExamHistory();
  bindExamHistoryActions();
}

/* ── 每日提醒设置 ────────────────────────────────────────────────────────────
   配置存在**服务端**（userdata/push/<user>.json），到点由 PushScheduler 自己发，
   页面关着也照发。这里只做三件事：把配置读出来填表、把表单存回去、发一条测试。

   「按现在的设置，下次会这样发」走 /api/push/preview：服务端把**它自己算的**
   到期数 / 连续天数 / 距考试天数摊开显示。这是 REVIEW_INTERVAL_DAYS 双份实现
   （app.js:180 与 PushDigestReader）的探针 —— 一旦漂移，这里和错题本的数字当场对不上。 */
/* 每个渠道都写清楚「去哪儿拿、拿什么、填在哪」，因为这一步是绑定时唯一会卡住的地方。
   结构：lead 一句话说明落地位置和额度；steps 具体操作；link 一键打开获取页；
   hint 是地址输入框下面那行短提示（保持简短，长内容放 steps）。 */
const PUSH_TYPE_INFO = {
  /* 浏览器通知：唯一不需要装 App、不需要第三方账号的渠道。
     它没有「地址」也没有「令牌」—— 凭据是每台设备各一份订阅，靠下面的「绑定本机」按钮产生。
     hideUrl 让地址输入框整行消失，devices 让设备列表那一行出现。 */
  webpush: {
    lead: "直接推到这台设备的通知栏，不用装 App，也不用注册任何第三方账号。",
    steps: [
      "点下面的「绑定本机」，浏览器会弹一个「允许通知」的询问，选「允许」。",
      "想收在哪台设备上，就在那台设备上打开本站再点一次「绑定本机」—— 手机和电脑可以同时绑。",
      "iPhone 要先把这个站「添加到主屏幕」，从主屏幕图标打开后再绑定（iOS 16.4 起才支持）。",
    ],
    hint: "",
    token: false,
    template: false,
    hideUrl: true,
    devices: true,
  },
  /* 两个「直接进个人微信」的渠道放最前面 —— 这是绝大多数人想要的「推送到微信」。
     它们只需要一串 key，接口地址固定，所以地址留空即可（后端会补默认值）。 */
  serverchan: {
    label: "接口地址（可留空）",
    placeholder: "https://sctapi.ftqq.com",
    hint: "地址留空就走官方接口，不用改。",
    lead: "消息直接发到你的微信。免费版每天 5 条 —— 一天只推一条，够用。",
    steps: [
      "打开 sct.ftqq.com，用 GitHub 账号登录（没有 GitHub 就现注册一个，免费）。",
      "进入「SendKey」页面，复制那一串（以 SCT 开头）。",
      "粘到下面「SendKey」，再点「发送测试」，微信收到就说明绑好了。",
    ],
    link: { href: "https://sct.ftqq.com", text: "打开 Server酱拿 SendKey" },
    token: true,
    tokenLabel: "SendKey",
    template: false,
  },
  pushplus: {
    label: "接口地址（可留空）",
    placeholder: "https://www.pushplus.plus/send",
    hint: "地址留空就走官方接口，不用改。",
    lead: "消息直接发到你的微信。免费版每天 200 条，最不容易被额度卡住。",
    steps: [
      "打开 pushplus.plus，用微信扫码登录。",
      "进「一对一推送」，复制那串 32 位的 token。",
      "粘到下面「token」，点「发送测试」。第一次要在公众号里点一下「启用」才会收到。",
    ],
    link: { href: "https://www.pushplus.plus", text: "打开 PushPlus 拿 token" },
    token: true,
    tokenLabel: "token",
    template: false,
  },
  wechat: {
    label: "群机器人地址",
    placeholder: "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=…",
    hint: "整条 Webhook 地址都粘进来，别只粘 key。",
    lead: "发到企业微信的群里，不是个人微信 —— 群成员都能看到。",
    steps: [
      "在企业微信群里点右上角「…」→「添加群机器人」→ 新建一个。",
      "复制它给的 Webhook 地址，形如 https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=xxxx。",
      "整条粘到下面，点「发送测试」，群里出现消息就成。",
    ],
    token: false,
    template: false,
  },
  bark: {
    label: "推送地址",
    placeholder: "https://api.day.app/你的设备Key",
    hint: "Bark App 首页复制的那一整条地址，含 Key 部分。",
    lead: "iPhone 上装 Bark，通知走苹果推送通道，最快。",
    steps: [
      "在 App Store 搜 Bark 装上，打开。",
      "首页会显示一条 https://api.day.app/xxxxx 的地址，点右边的复制。",
      "粘到下面即可，不需要令牌。用自建服务器就把 api.day.app 换成自己的域名。",
    ],
    token: false,
    template: false,
  },
  ntfy: {
    label: "推送地址",
    placeholder: "https://ntfy.sh/你的主题名",
    hint: "地址要写到主题名这一级，例如 https://ntfy.sh/yunxi-8f3k2。",
    lead: "安卓装 ntfy 订阅一个主题名，消息推到那个主题；也能自建服务器。",
    steps: [
      "手机装 ntfy App（应用商店或 F-Droid 都有）。",
      "想一个别人猜不到的主题名，比如 yunxi-8f3k2 —— 公共服务器上同名主题谁都能看到。",
      "在 App 里订阅这个主题名；这里的地址填 https://ntfy.sh/主题名。",
      "自建服务器就把 https://ntfy.sh 换成自己的域名。服务器要求登录时，把它的访问令牌填到下面的令牌里（按 Bearer 发）。",
    ],
    token: true,
    tokenLabel: "访问令牌",
    template: false,
  },
  webhook: {
    label: "接口地址",
    placeholder: "https://你的服务/push",
    hint: "一个能收 POST 的地址就行。",
    lead: "往你自己的地址 POST 一段 JSON。飞书、钉钉、自建服务都能接。",
    steps: [
      "填一个能收 POST 的地址（http 或 https）。",
      "请求体模板里可以写 {{title}} {{body}} {{url}}，发送时会被替换成实际内容（已做 JSON 转义）。",
      "模板留空 = 发 {\"title\":…,\"body\":…,\"url\":…}。",
      "超时 10 秒；返回非 2xx 会记成失败并在设置页显示原因。",
    ],
    token: false,
    template: true,
  },
};

/* 「内容风格」的说明跟着选项走 —— 三个选项的差别不看解释很难选对。 */
const PUSH_STYLE_HINT = {
  milestone: "没练的日子到点催一条；练过的日子只在连续 7/30/60 天、或当天全对时才推。",
  remind: "只在没练的日子推。练了完全不打扰。",
  digest: "每天都推一条今日成果（做几题、正确率、连续天数）。",
};

function renderPushGuide(info) {
  const box = $("pushGuide");
  if (!box) return;
  box.replaceChildren();
  if (info.lead) {
    const p = document.createElement("p");
    p.className = "push-guide-lead";
    p.textContent = info.lead;
    box.appendChild(p);
  }
  if (info.steps && info.steps.length) {
    const ol = document.createElement("ol");
    ol.className = "push-guide-steps";
    for (const step of info.steps) {
      const li = document.createElement("li");
      li.textContent = step;
      ol.appendChild(li);
    }
    box.appendChild(ol);
  }
  if (info.link) {
    const a = document.createElement("a");
    a.className = "push-guide-link";
    a.href = info.link.href;
    a.target = "_blank";
    a.rel = "noopener noreferrer";
    a.textContent = info.link.text;
    box.appendChild(a);
  }
  box.classList.toggle("hidden", box.childElementCount === 0);
}
let pushLoadedConfig = null;
/* 令牌是「打码回传」的：输入框留空 = 不改，所以必须记住用户有没有真的动过它，
   否则每次保存都会把已经设好的令牌抹掉。 */
let pushTokenDirty = false;
let pushPreviewTimer = null;

/* 渠道收敛：选项从 7 个收成 4 个（浏览器通知 / Bark / 微信 / 自定义 webhook）。
   被合并掉的三个**后端仍然认得**，所以老配置不会突然发不出去；只是在界面上
   不再作为独立选项出现，选到它们时给一句「这属于哪个」，并把输入框按新渠道摆：
     ntfy    → 和 Bark 一样是「一整条地址」，共用地址框即可
     serverchan → 和 PushPlus 一样是「一串 key」，共用令牌框
     wechat  → 就是往一个地址 POST JSON，按 webhook 摆 */
const PUSH_LEGACY = {
  ntfy: {
    group: "bark",
    note: "ntfy 已并入「Bark」：两者都是填一整条推送地址，用法完全一样（原来是单独一项，功能重复）。填好地址直接保存即可，不用改选。",
  },
  serverchan: {
    group: "pushplus",
    note: "Server酱已并入「微信」：和 PushPlus 一样是填一串 key，用法完全一样（原来是单独一项，功能重复）。把 SendKey 填在下面令牌里直接保存即可。",
  },
  wechat: {
    group: "webhook",
    note: "企业微信群机器人已并入「自定义 webhook」：它本来就是往一个地址 POST 一段 JSON。把那条 Webhook 地址填在下面直接保存即可。",
  },
};

function refreshPushTypeFields() {
  const type = $("pushType")?.value || "bark";
  const info = PUSH_TYPE_INFO[type] || PUSH_TYPE_INFO.bark;
  setText("pushUrlLabel", info.label || "");
  const url = $("pushUrl");
  if (url) url.placeholder = info.placeholder || "";
  setText("pushUrlHint", info.hint || "");
  /* 浏览器通知既没有地址也没有令牌，地址行整行收起 —— 留一个空输入框只会让人
     以为自己少填了什么。 */
  $("pushUrl")?.closest(".push-row")?.classList.toggle("hidden", !!info.hideUrl);
  $("pushUrlHint")?.classList.toggle("hidden", !!info.hideUrl || !info.hint);
  setText("pushTokenLabel", info.tokenLabel || "访问令牌");
  $("pushTokenRow")?.classList.toggle("hidden", !info.token);
  $("pushTemplateRow")?.classList.toggle("hidden", !info.template);
  $("pushDeviceRow")?.classList.toggle("hidden", !info.devices);
  if (info.devices) renderPushDevices(pushLoadedConfig);
  renderPushGuide(info);
}

/* 老配置里存着已合并的渠道值：下拉框里没有那个 option，直接赋 value 会变成空选择。
   这里在下拉里临时补一个「当前正在用」的选项，让用户看到真实值 + 提示，
   但**不写回**（保存时仍按原 type 发，后端照旧认得，绑定关系不丢）。 */
function reconcilePushType(cfg) {
  const select = $("pushType");
  const legacyBox = $("pushTypeLegacy");
  if (!select) return;
  select.querySelectorAll("option[data-legacy]").forEach((opt) => opt.remove());
  const type = String(cfg?.channel?.type || "");
  const legacy = PUSH_LEGACY[type];
  if (!legacy) {
    legacyBox?.classList.add("hidden");
    if (legacyBox) setText("pushTypeLegacy", "");
    return;
  }
  const option = document.createElement("option");
  option.value = type;
  // 文案只标「已合并」，具体该换哪个由下面那句提示说清 —— 选项里塞长句子会撑爆下拉宽度
  option.textContent = `${type}（已合并）`;
  option.dataset.legacy = "1";
  select.appendChild(option);
  select.value = type;
  if (legacyBox) {
    legacyBox.classList.remove("hidden");
    setText("pushTypeLegacy", legacy.note);
  }
}

function refreshPushStyleHint() {
  const style = $("pushStyle")?.value || "milestone";
  setText("pushStyleHint", PUSH_STYLE_HINT[style] || "");
}

/* ── 浏览器通知的绑定 ────────────────────────────────────────────────────────
   订阅是**每台设备一份**凭据，所以它不跟设置表单一起整包保存，而是单独
   POST /api/push/subscribe。服务端把订阅存进同一个配置文件、并且顺手把渠道切成 webpush。 */

/* 能不能绑。iOS 上标签页里 PushManager 也存在，但 subscribe 会失败 ——
   必须先「添加到主屏幕」再打开（display-mode: standalone）。 */
function webPushSupport() {
  if (!window.isSecureContext) {
    return { ok: false, reason: "浏览器通知要求 HTTPS（或本机 localhost）。当前不是安全连接，绑不了。" };
  }
  if (!("serviceWorker" in navigator) || !("PushManager" in window) || !("Notification" in window)) {
    return { ok: false, reason: "这个浏览器不支持通知推送。换 Chrome / Edge / Safari 16.4+ 试试。" };
  }
  const isIOS = /iPad|iPhone|iPod/.test(navigator.userAgent);
  const standalone = window.matchMedia("(display-mode: standalone)").matches || navigator.standalone === true;
  if (isIOS && !standalone) {
    return { ok: false, reason: "iPhone / iPad 上要先把本站「添加到主屏幕」，从主屏幕图标打开后再绑定（iOS 16.4 起支持）。" };
  }
  return { ok: true };
}

/* VAPID 公钥是 base64url，PushManager 要的是 Uint8Array。 */
function urlBase64ToUint8Array(base64) {
  const padded = (base64 + "=".repeat((4 - (base64.length % 4)) % 4)).replace(/-/g, "+").replace(/_/g, "/");
  const raw = atob(padded);
  const bytes = new Uint8Array(raw.length);
  for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
  return bytes;
}

/* 设备名用 UA 摘要（服务端已经缩成「Chrome · Android」这种），没有才退回推送域名。 */
function renderPushDevices(cfg) {
  const list = $("pushDeviceList");
  if (!list) return;
  const subs = cfg?.channel?.subscriptions || [];
  list.replaceChildren();
  for (const sub of subs) {
    const li = document.createElement("li");
    li.className = "push-device-item";
    const main = document.createElement("div");
    main.className = "push-device-main";
    const name = document.createElement("span");
    name.className = "push-device-name";
    name.textContent = sub.userAgent || sub.host || "已绑定设备";
    const meta = document.createElement("span");
    meta.className = "push-device-meta";
    meta.textContent = [sub.host, sub.createdAt ? `绑定于 ${sub.createdAt}` : ""].filter(Boolean).join(" · ");
    main.append(name, meta);
    const btn = document.createElement("button");
    btn.type = "button";
    btn.textContent = "解除";
    btn.setAttribute("aria-label", `解除「${name.textContent}」的绑定`);
    btn.onclick = () => { unbindWebPush(sub.id, btn); };
    li.append(main, btn);
    list.appendChild(li);
  }
  const support = webPushSupport();
  const bindBtn = $("pushBindBtn");
  if (bindBtn) bindBtn.disabled = !support.ok;
  const hint = $("pushDeviceHint");
  if (hint) {
    hint.textContent = support.ok
      ? (subs.length
        ? `已绑定 ${subs.length} 台。换设备就在那台设备上再点一次「绑定本机」。`
        : "还没绑定设备。点「绑定本机」把通知开到这台设备上。")
      : support.reason;
  }
}

async function bindWebPush() {
  const support = webPushSupport();
  if (!support.ok) { toast(support.reason); return; }
  const button = $("pushBindBtn");
  const hint = $("pushDeviceHint");
  if (button) button.disabled = true;
  try {
    /* 顺序：先订阅再保存。反过来不行 —— 启用状态的 webpush 配置在「一台设备都没绑」
       时会被服务端拒绝保存（Validate），而订阅本身不要求启用。 */
    if (Notification.permission === "denied") {
      throw new Error("这台设备已经把本站的通知权限拒掉了。要去浏览器的站点设置里重新允许，之后才能绑定。");
    }
    if (Notification.permission !== "granted") {
      const granted = await Notification.requestPermission();
      if (granted !== "granted") throw new Error("没有拿到通知权限，绑定没完成。");
    }
    if (hint) hint.textContent = "正在绑定…";
    const reg = await navigator.serviceWorker.ready;
    const { publicKey } = await api("/api/push/vapid");
    if (!publicKey) throw new Error("服务端没有可用的推送公钥，暂时绑不了浏览器通知。");
    /* 同一台设备重复点「绑定本机」会拿到同一个 endpoint，服务端按 endpoint 去重，
       所以这里直接复用已有订阅，避免每次都新建。 */
    const existing = await reg.pushManager.getSubscription();
    const sub = existing || await reg.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: urlBase64ToUint8Array(publicKey),
    });
    const cfg = await api("/api/push/subscribe", {
      method: "POST",
      headers: { "Content-Type": "application/json; charset=utf-8" },
      body: JSON.stringify(sub.toJSON()),
    });
    pushLoadedConfig = cfg;
    if ($("pushType")) $("pushType").value = "webpush";
    renderPushForm(cfg);
    toast("已绑定这台设备");
  } catch (err) {
    if (hint) hint.textContent = `绑定失败：${err.message || "未知原因"}`;
    toast(err.message || "绑定失败");
  } finally {
    if (button) button.disabled = false;
  }
}

async function unbindWebPush(id, button) {
  if (!confirm("解除后这台设备就收不到每日提醒了，确定解除吗？")) return;
  if (button) button.disabled = true;
  try {
    const res = await api("/api/push/unsubscribe", {
      method: "POST",
      headers: { "Content-Type": "application/json; charset=utf-8" },
      body: JSON.stringify({ id }),
    });
    pushLoadedConfig = res.config || pushLoadedConfig;
    renderPushForm(pushLoadedConfig);
    toast(res.removed ? "已解除绑定" : "这台设备已经不在了");
  } catch (err) {
    toast(err.message || "解除失败");
    if (button) button.disabled = false;
  }
}

function renderPushStatus(cfg) {
  const box = $("pushStatus");
  if (!box) return;
  const parts = [];
  if (cfg?.lastSentDate) {
    parts.push(`上次推送 ${cfg.lastSentDate}${cfg.lastResult === "ok" ? "（成功）" : ""}`);
  }
  if (cfg?.lastResult === "fail" && cfg.lastError) parts.push(`最近一次失败：${cfg.lastError}`);
  box.textContent = parts.join(" · ");
  box.classList.toggle("hidden", parts.length === 0);
  box.classList.toggle("is-error", cfg?.lastResult === "fail");
}

function renderPushForm(cfg) {
  const c = cfg || {};
  const channel = c.channel || {};
  if ($("pushEnabled")) $("pushEnabled").checked = !!c.enabled;
  if ($("pushTime")) $("pushTime").value = c.time || "20:00";
  /* 顺序不能反：reconcile 会为「已合并」的老渠道补一条临时 option，
     必须在赋 value 之前补好，否则 value 落空、下拉框显示第一项，
     而用户看到的和实际保存的 type 不一致。 */
  reconcilePushType(c);
  if ($("pushType")) $("pushType").value = channel.type || "bark";
  if ($("pushUrl")) $("pushUrl").value = channel.url || "";
  if ($("pushToken")) {
    $("pushToken").value = "";
    $("pushToken").placeholder = channel.tokenSet ? `已设置（${channel.token || "****"}），留空不改` : "没有就留空";
  }
  if ($("pushTemplate")) $("pushTemplate").value = channel.template || "";
  if ($("pushBaseUrl")) $("pushBaseUrl").value = c.baseUrl || "";
  if ($("pushStyle")) $("pushStyle").value = c.style || "milestone";
  pushTokenDirty = false;
  refreshPushTypeFields();
  refreshPushStyleHint();
  renderPushStatus(c);
}

function collectPushForm() {
  const tokenInput = ($("pushToken")?.value || "").trim();
  const hadToken = !!pushLoadedConfig?.channel?.tokenSet;
  /* 没动过输入框 → 回传 `****`（后端约定：以 **** 开头 = 保留原值）；
     动过且清空 → 空串 = 明确清除。 */
  const token = pushTokenDirty ? tokenInput : (hadToken ? "****" : "");
  return {
    enabled: !!$("pushEnabled")?.checked,
    time: $("pushTime")?.value || "20:00",
    style: $("pushStyle")?.value || "milestone",
    baseUrl: ($("pushBaseUrl")?.value || "").trim(),
    channel: {
      type: $("pushType")?.value || "bark",
      url: ($("pushUrl")?.value || "").trim(),
      token,
      template: ($("pushTemplate")?.value || "").trim(),
    },
  };
}

async function refreshPushPreview() {
  const style = $("pushStyle")?.value || "milestone";
  const baseUrl = ($("pushBaseUrl")?.value || "").trim();
  const query = new URLSearchParams({ style, baseUrl });
  try {
    const res = await api(`/api/push/preview?${query.toString()}`);
    const message = res.message;
    if (!res.willSend || !message) {
      setText("pushPreviewTitle", "这条不发");
      setText("pushPreviewBody", style === "remind"
        ? "只催没练：今天已经练过了，所以不发。"
        : "今天已经练过，而且没到里程碑，所以不发。");
    } else {
      setText("pushPreviewTitle", message.title || "（无标题）");
      setText("pushPreviewBody", message.body || "（无正文）");
    }
    const d = res.digest || {};
    const bits = [`到期 ${d.due} 道`, `错题 ${d.wrongTotal} 道`, `今天已练 ${d.doneToday} 题`, `连续 ${d.streak} 天`];
    if (d.daysLeft >= 0) bits.push(`距考试 ${d.daysLeft} 天`);
    setText("pushPreviewMeta", `服务端算的：${bits.join(" · ")}`);
  } catch (err) {
    setText("pushPreviewTitle", "预览失败");
    setText("pushPreviewBody", err.message || "");
    setText("pushPreviewMeta", "");
  }
}

function schedulePushPreview() {
  clearTimeout(pushPreviewTimer);
  pushPreviewTimer = setTimeout(() => { refreshPushPreview(); }, 250);
}

function openPushModal() {
  const modal = $("pushModal");
  if (!modal) return;
  // 账号下拉还开着的话先收起，否则它会压在遮罩后面
  document.querySelector(".account-box")?.classList.remove("open");
  $("accountMenu")?.classList.add("hidden");
  $("accountMenuBtn")?.setAttribute("aria-expanded", "false");
  modal.classList.remove("hidden");
  pushLoadedConfig = null;
  setText("pushPreviewTitle", "读取中…");
  setText("pushPreviewBody", "");
  setText("pushPreviewMeta", "");
  return api("/api/push/config")
    .then((cfg) => {
      pushLoadedConfig = cfg;
      renderPushForm(cfg);
      return refreshPushPreview();
    })
    .catch((err) => toast(err.message || "读取提醒设置失败"));
}

function closePushModal() {
  $("pushModal")?.classList.add("hidden");
  clearTimeout(pushPreviewTimer);
}

async function savePushSettings() {
  const button = $("pushSaveBtn");
  if (button) button.disabled = true;
  try {
    const cfg = await api("/api/push/config", {
      method: "PUT",
      headers: { "Content-Type": "application/json; charset=utf-8" },
      body: JSON.stringify(collectPushForm()),
    });
    pushLoadedConfig = cfg;
    renderPushForm(cfg);
    await refreshPushPreview();
    toast(cfg.enabled ? `已保存，每天 ${cfg.time} 提醒` : "已保存，提醒已关闭");
    return cfg;
  } catch (err) {
    toast(err.message || "保存失败");
    return null;
  } finally {
    if (button) button.disabled = false;
  }
}

/* 「发送测试」先保存再发：测的必须是**屏幕上这套**设置，而不是上一次存下的那套。
   失败信息直接摊在弹窗里（不只用 toast），因为里面通常带着渠道返回的原因。 */
async function testPushSettings() {
  const button = $("pushTestBtn");
  if (button) button.disabled = true;
  try {
    const saved = await savePushSettings();
    if (!saved) return;
    const res = await api("/api/push/test", { method: "POST" });
    const box = $("pushStatus");
    if (res.ok) {
      if (box) {
        box.textContent = "测试消息已发出，去手机上看一眼";
        box.classList.remove("hidden");
        box.classList.remove("is-error");
      }
      toast("测试消息已发出");
    } else if (box) {
      box.textContent = `发送失败：${res.error || "未知原因"}`;
      box.classList.remove("hidden");
      box.classList.add("is-error");
    }
  } catch (err) {
    const box = $("pushStatus");
    if (box) {
      box.textContent = `发送失败：${err.message || "未知原因"}`;
      box.classList.remove("hidden");
      box.classList.add("is-error");
    }
  } finally {
    if (button) button.disabled = false;
  }
}

function openExamHistoryDrawer() {
  const modal = $("examHistoryModal");
  if (!modal) return;
  const rows = examHistoryForCourse();
  setText("examHistoryModalTitle", `全部模拟记录 · 共 ${rows.length} 场`);
  const body = $("examHistoryModalBody");
  if (body) {
    body.innerHTML = rows.length
      ? rows.map((item) => renderExamHistoryRow(item, { deletable: true })).join("")
      : `<span class="muted">暂无模拟考记录</span>`;
  }
  bindExamHistoryDeleteActions();
  modal.classList.remove("hidden");
}

function closeExamHistoryDrawer() {
  $("examHistoryModal")?.classList.add("hidden");
}

/* 删一场模拟记录。examHistory 是唯一事实来源，所以：
   · 删记录本身；
   · 同时删掉 storage.history 里指向它的**指针**（否则趋势图会留一个取不到数的点）；
   · 老数据（本批之前 history 自带分数、没有 ref）按 courseId + at 精确匹配同一次交卷。 */
function deleteExamHistoryRecord(id) {
  const key = String(id || "");
  const record = (state.storage.examHistory || []).find((item) => String(item.id) === key);
  if (!record) return false;
  const at = record.at || "";
  if (!confirm(`确定删除这场模拟记录吗？\n\n${at} · ${formatScore(record.score)} / ${formatScore(record.totalScore)} 分\n删除后本场成绩与明细都不再保留。`)) {
    return false;
  }
  state.storage.examHistory = (state.storage.examHistory || []).filter((item) => String(item.id) !== key);
  state.storage.history = (state.storage.history || []).filter((row) => {
    if (row.mode !== "exam") return true;
    if (row.ref) return String(row.ref) !== key;
    return !(Number(row.courseId || 0) === Number(record.courseId || 0) && String(row.at || "") === String(at));
  });
  scheduleSave();
  return true;
}

function bindExamHistoryDeleteActions() {
  document.querySelectorAll("[data-exam-history-delete]").forEach((btn) => {
    btn.onclick = () => {
      const id = btn.dataset.examHistoryDelete;
      if (!deleteExamHistoryRecord(id)) return;
      /* 抽屉还开着就重绘抽屉（少一条），首页那张卡同步重绘 —— 只重绘这两块，
         整页 renderExamHome 会顺手把章节勾选/输入框状态重置掉。 */
      if (!$("examHistoryModal")?.classList.contains("hidden")) openExamHistoryDrawer();
      refreshExamHistoryCard();
      toast("已删除该场模拟记录");
    };
  });
}

/* 首页历史卡片的入口（「查看全部 N 场」+ 抽屉的关闭/删除）。 */
function bindExamHistoryActions() {
  const allBtn = $("examHistoryAllBtn");
  if (allBtn) allBtn.onclick = () => openExamHistoryDrawer();
  bindExamHistoryDeleteActions();
}

function renderExamHistoryDetails(item) {
  const details = Array.isArray(item.details) ? item.details : [];
  if (!details.length) {
    /* 诚实：说清是**哪一种**没有明细 ——
       perQuestionPruned = 这一场的题号/作答/逐题明细被清理过（只保留最近 1 场，
       见 recordExamHistory），不是「本来就没有」。 */
    return item.perQuestionPruned
      ? `<small class="exam-history-pruned">该场次已仅保留成绩（题号与逐题明细已清理）</small>`
      : `<small>此记录暂无逐题明细</small>`;
  }
  const wrongRows = details.filter((row) => row.answered && !row.subjective && !row.correct);
  const subjectiveRows = details.filter((row) => row.subjective);
  const rows = wrongRows.length ? wrongRows : details.slice(0, 12);
  /* 「本卷前 12 题」在题量不足 12 时是假的（1 题卷也这么写）。
     按**实际渲染条数**说，缩略范围才和下面真正列出来的行数一致。 */
  const scope = wrongRows.length
    ? `错题 ${wrongRows.length} 道`
    : `本卷前 ${rows.length} 题`;
  return `
    <details class="exam-history-detail">
      <summary>查看明细：${scope}${subjectiveRows.length ? ` · 主观题 ${subjectiveRows.length} 道` : ""}</summary>
      <div class="exam-history-detail-list">
        ${rows.map((row) => `
          <div class="exam-history-detail-row ${row.correct ? "correct" : row.subjective ? "subjective" : "wrong"}">
            <b>${Number(row.no || 0) || ""}</b>
            <span>${escapeHtml(row.type || "题目")} · ${escapeHtml(row.chapterName || "")}</span>
            <em>答：${escapeHtml(row.answer || "未答")} / 正：${escapeHtml(row.correctAnswer || "见解析")} · ${formatScore(row.score)}/${formatScore(row.maxScore)}分</em>
            <small>${escapeHtml(row.title || "")}</small>
          </div>
        `).join("")}
      </div>
    </details>
  `;
}

/* 把当前组卷章节勾选写进 settings.examChapters（按科目 id 归档）。 */
/* 把当前组卷章节勾选写进 settings.examChapters（按科目 id 归档）。
   存 { ids, excluded }：只存 ids 的话，「父勾、子取消」的信息在重新进入考场时就丢了，
   父章重新展开会把被取消的子章又算回卷子里。老格式（纯数组）继续可读。 */
function persistExamChapters() {
  if (!state.currentCourse || isAdmin()) return;
  state.storage.settings ||= {};
  const map = state.storage.settings.examChapters && typeof state.storage.settings.examChapters === "object"
    ? state.storage.settings.examChapters
    : {};
  map[String(state.currentCourse.id)] = {
    ids: Array.from(state.examSelectedChapters || []).map(Number).filter(Boolean),
    excluded: Array.from(state.examExcludedChapters || []).map(Number).filter(Boolean),
  };
  state.storage.settings.examChapters = map;
  scheduleSave();
}

/* 读回组卷章节勾选，兼容旧格式（纯数组 = 没有排除信息）。 */
function restoreExamChapters(courseId) {
  const saved = state.storage.settings?.examChapters?.[String(courseId || 0)];
  const ids = Array.isArray(saved) ? saved : Array.isArray(saved?.ids) ? saved.ids : null;
  const excluded = Array.isArray(saved?.excluded) ? saved.excluded : [];
  state.examExcludedChapters = new Set(excluded.map(Number).filter(Boolean));
  state.examSelectedChapters = Array.isArray(ids) && ids.length
    ? new Set(ids.map(Number).filter(Boolean))
    : null;
}

/* 进考场首页（组卷页）。导航点击和「换科目」共用这一段 ——
   换科目时如果不重跑它，页面会停在**普通练习题**上：题单没清、导航还高亮着
   「模拟考场」、工具栏还挂着「提交答卷」，必须再点一次「模拟考场」才回得来。
   transitionId 用来判断这轮异步过程中用户有没有又切了模块。 */
async function enterExamHome(transitionId = state.modeTransitionId) {
  /* ⚠️ 这里**不能**再 saveExamDraft()。导航点击那条路已经在 state.mode 还是 "exam"、
     state.questions 还是考卷的时候存过一次草稿；到这里 state.mode 已改成 "exam"、
     但 state.questions 可能是**上一个模块**的题 —— 再存一次就会把草稿覆盖成别的
     模块的题单（实测：10 题卷/2 个答案 → 被覆盖成错题本的 4 题/1 个答案）。 */
  state.questions = [];
  state.currentIndex = -1;
  state.submitted = false;
  state.answerVisible = false;
  await loadTypes({ ignoreChapter: true });
  if (transitionId !== state.modeTransitionId) return;
  renderAnswerCard();
  updateStats();
  renderExamHome();
}

function renderExamHome() {
  setPanelPage(true);
  stopExamTimer();
  state.exam = null;
  state.answerVisible = false;
  setText("answerToggleBtn", "显示答案");
  const rule = getExamRule();
  const parts = normalizeExamParts(rule);
  /* 组卷章节选择按科目记忆：换科目时重置（章节 id 不通用），回同一科目恢复上次勾选。
     存的是空数组（用户清空过但没组卷）时按「默认全选」处理。 */
  restoreExamChapters(state.currentCourse?.id);
  $("questionBody").classList.add("hidden");
  $("emptyState").classList.remove("hidden");
  $("emptyState").innerHTML = `
    <div class="exam-home">
      <strong>${escapeHtml(rule.name)}</strong>
      <p>${escapeHtml(state.currentCourse?.name || "当前科目")} · ${rule.durationMinutes} 分钟 · 满分 ${rule.totalScore} 分</p>
      ${renderExamDraftCard()}
      <div class="exam-config-grid">
        <label>
          <span>试卷名称</span>
          <input id="examNameInput" value="${escapeHtml(rule.name)}">
        </label>
        <label>
          <span>考试时间</span>
          <input id="examDurationInput" type="number" min="1" max="${EXAM_DURATION_MAX_MINUTES}" value="${rule.durationMinutes}">
        </label>
      </div>
      <!-- 默认不勾 = 真限时：切标签页/刷新/切模块时间都照走，到点自动交卷。
           存 state.storage.settings.pauseExamOnLeave，见 pauseExamOnLeave()。 -->
      <label class="exam-pause-row">
        <input id="examPauseOnLeave" type="checkbox" ${pauseExamOnLeave() ? "checked" : ""}>
        <span>离开页面时暂停计时</span>
        <small>不勾选（默认）按真实时间倒计时：切标签页、刷新、切到别的模块时间都照走，到点自动交卷。勾选后离开即暂停，回来接着走。</small>
      </label>
      <div class="exam-rule-list">
        ${rule.parts.map((part, index) => `
          <div class="exam-part-row">
            <label>
              <input id="examPart${index}" type="checkbox" ${parts.some((p) => p.label === part.label) ? "checked" : "disabled"}>
              <b>${escapeHtml(part.label)}</b>
            </label>
            <span>
              <input id="examPartCount${index}" type="number" min="0" value="${part.count}" ${parts.some((p) => p.label === part.label) ? "" : "disabled"}>
              题 · 每题 ${part.score} 分${parts.some((p) => p.label === part.label) ? "" : " · 本题库暂无该题型"}
              <!-- 超出题库/章节题量时就地提示（填 7227 而库里只有 6727 时，
                   旧实现一路照收，结果满分 7227 而实际只出 6727 题）。 -->
              <small class="exam-part-hint hidden" data-exam-part-hint="${index}"></small>
            </span>
          </div>
        `).join("")}
      </div>
      <div class="exam-chapter-box">
        <div class="exam-section-title">选择章节</div>
        <div class="exam-chapter-actions">
          <button type="button" id="examSelectAllChapters">全选</button>
          <button type="button" id="examSelectNoChapters">清空</button>
        </div>
        <div class="exam-chapter-list">
          ${renderExamChapterTree()}
        </div>
      </div>
      <p>${escapeHtml(rule.autoScoreNote)}。组卷会按章节题量比例抽取，并尽量贴近正式考试结构。</p>
      ${parts.length ? "" : `<p class="exam-warning">当前题库没有可用于组卷的题型，请先切换题库或更新题库。</p>`}
      ${/* 历史卡片单独套一层容器：抽屉里删掉一条之后要**只重绘这一块**
           （整页重绘会顺带 reset 章节勾选/输入框状态，也会停掉计时）。 */ ""}
      <div id="examHistoryCard">${renderExamHistory()}</div>
      <!-- 「开始组卷」在手机端要滚很远才够得到（实测 390×844 下 top≈1512，
           页面高 1183，离屏约 1000px）→ ≤760px 时由 CSS 把这一条吸到底部
           （.exam-start-bar，见 style.css）。
           禁用原因原来只写在 title 上，手机点不出 tooltip；这里在按钮**上方**
           渲一条 .exam-warning，由 syncStartExamBtn 填同一句话。 -->
      <div class="exam-start-bar">
        <p class="exam-warning hidden" id="examStartWarning"></p>
        <button class="primary-action" id="startExamBtn" ${parts.length ? "" : "disabled"}>开始组卷</button>
      </div>
    </div>
  `;
  bindExamDraftActions();
  bindExamHistoryActions();
  /* 「离开页面时暂停计时」：选项立刻落 settings（随账号同步），
     下一场考试的 pauseExamOnLeave() 就读它。 */
  const pauseOnLeaveToggle = $("examPauseOnLeave");
  if (pauseOnLeaveToggle) {
    pauseOnLeaveToggle.onchange = () => {
      state.storage.settings ||= {};
      state.storage.settings.pauseExamOnLeave = pauseOnLeaveToggle.checked;
      scheduleSave();
    };
  }
  /* 题量输入框的 max + 「本章节仅 N 题」提示。判据：examPartAvailableCount()
     （题型池 ∩ 已选章节有效题量）。超出时**只提示不改正**：用户填的数字留着，
     组卷时自然只抽得到这么多题，满分按实际卷子重算。 */
  const syncExamPartLimits = () => {
    rule.parts.forEach((part, index) => {
      const input = $("examPartCount" + index);
      if (!input) return;
      const available = examPartAvailableCount(part);
      if (available > 0) input.max = String(available);
      const value = Number(input.value || 0);
      const over = available > 0 && value > available;
      if (over) input.dataset.overLimit = "1";
      else delete input.dataset.overLimit;
      const hint = document.querySelector(`[data-exam-part-hint="${index}"]`);
      if (hint) {
        hint.textContent = over
          ? `本章节仅 ${available} 题，超出的 ${value - available} 题不会出卷（满分按实际题量算）`
          : "";
        hint.classList.toggle("hidden", !over);
      }
    });
  };
  /* 章节一个都没勾选时禁用「开始组卷」，并给出 title 提示 ——
     否则用户点「清空」后会以为还能出卷（旧行为会悄悄给出全章节卷）。 */
  const syncStartExamBtn = (persist = false) => {
    const btn = $("startExamBtn");
    if (!btn) return;
    /* 题量上限与「本章节仅 N 题」提示跟着一起刷新：这个函数是所有相关事件的
       公共出口（章节勾选 / 题型勾选 / 题量输入），不用再挂一遍监听。 */
    syncExamPartLimits();
    const noneChapter = !(state.examSelectedChapters && state.examSelectedChapters.size);
    /* 题量全填 0 = 用户把每种题型都排除了，等同于要一份空卷 —— 必须拦住。
       旧行为会悄悄生成一整份卷子（实测填 0 仍出 80 题）。 */
    const noCount = !rule.parts.some((part, index) => {
      const checked = $("examPart" + index)?.checked !== false;
      const value = Number($("examPartCount" + index)?.value ?? part.count);
      return checked && Number.isFinite(value) && value > 0;
    });
    btn.disabled = !parts.length || noneChapter || noCount;
    /* 禁用原因要说在**看得见**的地方：title 在手机上永远点不出来，
       所以同一句话同时写进按钮上方的 .exam-warning。 */
    const reason = !parts.length
      ? "当前题库没有可用于组卷的题型，请先切换题库或更新题库"
      : noneChapter
        ? "请至少选择一个章节"
        : noCount
          ? "请至少为一个题型设置题量（当前全部为 0）"
          : "";
    btn.title = reason;
    const warning = $("examStartWarning");
    if (warning) {
      warning.textContent = reason;
      warning.classList.toggle("hidden", !reason);
    }
    if (persist) persistExamChapters();
  };
  $("examSelectAllChapters").onclick = () => {
    state.examSelectedChapters ||= new Set();
    state.examExcludedChapters = new Set();
    document.querySelectorAll("[data-exam-chapter]").forEach((input) => {
      input.checked = true;
      /* 全选 = 排除集合清空，之前的半选态必须一起落掉（indeterminate 是 DOM 属性，
         光改 checked 不会自动消）。 */
      input.indeterminate = false;
      state.examSelectedChapters.add(Number(input.value || 0));
    });
    syncStartExamBtn(true);
  };
  $("examSelectNoChapters").onclick = () => {
    state.examSelectedChapters.clear();
    state.examExcludedChapters = new Set();
    document.querySelectorAll("[data-exam-chapter]").forEach((input) => {
      input.checked = false;
      input.indeterminate = false;
    });
    syncStartExamBtn(true);
  };
  bindExamChapterTree();
  /* 事件委托：展开/收起会重绘 .exam-chapter-list 的 innerHTML，但容器本身不变，
     所以把监听挂在容器上（挂在 input 上会被重绘冲掉）。 */
  document.querySelector(".exam-chapter-list")?.addEventListener("change", () => syncStartExamBtn(true));
  /* 题量输入/勾选变化也要重算「开始组卷」是否可用（题量全 0 时禁用）。 */
  rule.parts.forEach((part, index) => {
    $("examPart" + index)?.addEventListener("change", () => syncStartExamBtn());
    $("examPartCount" + index)?.addEventListener("input", () => syncStartExamBtn());
  });
  syncStartExamBtn();
  $("startExamBtn").onclick = () => {
    if (!parts.length) {
      toast("当前题库没有可用于组卷的题型");
      return;
    }
    if (!(state.examSelectedChapters && state.examSelectedChapters.size)) {
      toast("请至少选择一个章节");
      return;
    }
    /* ⚠️ 配置只读一次：getExamConfigFromForm 现在会给「时长非法」弹提示并返回
       null，读两次就会既弹两遍提示、又在 null 上取 .parts 抛错。 */
    const config = getExamConfigFromForm(rule);
    if (!config) return;                     // 时长非法，提示已经弹过了
    if (!config.parts.length) {
      toast("请至少为一个题型设置题量（当前全部为 0）");
      return;
    }
    state.examConfig = config;
    startExamPaper().catch((err) => toast(err.message));
  };
  // ⚠️ 必须刷新状态条：本函数上面已经把 `state.exam` 置 null 并停了计时，
  // 但不调这一句的话 `#examStatus` 会**保留上一场的内容且不隐藏** ——
  // 实测交卷后回到考场首页，顶部还挂着「模拟考场 149:55 … 得分 0/100，正确率 0%」。
  renderExamStatus();
}

async function startExamPaper() {
  if (!state.currentCourse) return;
  setPanelPage(false);
  const rule = state.examConfig || getExamRule();
  const parts = normalizeExamParts(rule);
  if (!parts.length) {
    toast("当前题库缺少可用于组卷的题型");
    return;
  }
  const existingDraft = examDraftForCurrentCourse();
  if (existingDraft && !confirm("当前有未完成的模拟考试，开始新组卷会覆盖旧草稿，确定继续吗？")) {
    renderExamHome();
    return;
  }
  if (existingDraft) clearExamDraft();
  /* 防重入：同一个科目已经在组卷时再点一次，只会白白再抽一份卷。
     按**科目**判重而不是全局一个 bool —— 切到别的科目后旧组卷已经不会再写
     state（见 beginExamFlow），不该继续挡着新科目的按钮。 */
  const startCourseId = Number(state.currentCourse.id || 0);
  if (state.examGenerating && Number(state.examGeneratingCourseId || 0) === startCourseId) {
    toast("正在组卷，请稍候");
    return;
  }
  /* 从这一刻到卷子生成完成为止，「提交答卷」必须不可点：
     这段窗口里 state.exam 还是 null，点交卷会写出一条 0/0 分的假成绩单。
     用 try/finally 收口，而不是在每个提前 return 上各补一句 ——
     生成期间还有「章节没选」提前返回、组卷失败抛错、以及用户中途切模块三条出口。
     finally 只认**自己那一代**：切走再回来重新组卷会开新一代，旧那代的 finally
     不能把新一代的「组卷中」标记抹掉。 */
  const flow = beginExamFlow("generate");
  state.examGenerating = true;
  state.examGeneratingCourseId = flow.courseId;
  renderVerifyModeControls();
  try {
    await generateExamPaper(rule, parts, flow);
  } finally {
    if (state.examGenerationId === flow.generationId) {
      state.examGenerating = false;
      state.examGeneratingCourseId = 0;
      renderVerifyModeControls();
    }
  }
}

/* 组卷主体：按章节比例抽题 → 落 state.exam → 起计时 → 打开第 1 题。
   从 startExamPaper 拆出来只是为了让 state.examGenerating 有个 try/finally。 */
async function generateExamPaper(rule, parts, flow = null) {
  /* 这里**不再**写 state.storage.settings.verifyMode（详见 currentVerifyMode 的注释）：
     考试期间的验证模式由 state.mode 决定，考完自动恢复用户原本的偏好。 */
  state.verifyMode = "paper";
  $("emptyState").innerHTML = `<strong>正在组卷...</strong><span>按章节比例抽取试题。</span>`;
  /* ⚠️ 这一代组卷钉住的上下文：请求参数、结果累积、提交前校验只认它。
     旧实现把异步结果直接 push 进**活的** state.questions、还在 fetch 途中读活的
     state.currentCourse.id —— 挂起期间切科目，迟到回包就把科目 2 的题单变成
     [202, 101]，又清空当前答案、建出一份混合科目的 exam。 */
  const activeFlow = flow || beginExamFlow("generate");
  const courseId = activeFlow.courseId;
  const scoreMap = {};
  const seen = new Set();
  /* 结果先攒在**局部**数组里（generated），提交前校验通过才一次性写 state。 */
  const generated = [];
  const selectedIds = new Set((rule.selectedChapterIds || []).map(Number));
  /* 一个章节都没选时**不要**回退成全章节 —— 那会让「清空」按钮的语义和它的名字相反
     （用户点「清空」期望不出卷，实际拿到的是全章节卷）。改为明确提示并退回考场首页。 */
  if (!selectedIds.size) {
    toast("请至少选择一个章节");
    renderExamHome();
    return;
  }
  const examChapters = examChaptersFromIds(Array.from(selectedIds));
  for (const part of parts) {
    /* 每一批之前先确认这一代还活着：上下文已经变了就别再向服务器要题了。 */
    if (!examFlowStillCurrent(activeFlow)) return abortExamFlow(activeFlow);
    const selected = [];
    const allocations = allocateExamCounts(part, examChapters);
    for (const allocation of allocations) {
      const items = await fetchExamPartQuestions(part, allocation.count, allocation.chapterId, courseId);
      for (const item of items) {
        if (seen.has(item.id)) continue;
        seen.add(item.id);
        selected.push(item);
        scoreMap[item.id] = part.score;
        if (selected.length >= part.count) break;
      }
    }
    if (selected.length < part.count) {
      const fill = [];
      const fillChapters = selectedIds.size ? examChapters : [{ id: 0 }];
      for (const chapter of fillChapters) {
        const chunk = await fetchExamPartQuestions(part, part.count * 3, Number(chapter.id || 0), courseId);
        fill.push(...chunk);
        if (fill.length >= part.count * 3) break;
      }
      for (const item of fill) {
        if (seen.has(item.id)) continue;
        seen.add(item.id);
        selected.push(item);
        scoreMap[item.id] = part.score;
        if (selected.length >= part.count) break;
      }
    }
    generated.push(...shuffleItems(selected));
  }
  if (!generated.length) throw new Error("组卷失败，当前题库没有可抽取的题目");
  /* 提交前最后一次校验 —— 这里是整条流程里**唯一**允许写 state 的位置：
     校验失败则 state.questions / state.answers / state.exam 一个都不动。 */
  if (!examFlowStillCurrent(activeFlow)) return abortExamFlow(activeFlow);
  const questions = generated.slice(0);
  state.questions = questions;
  /* 卷子也是「题单」的一种：记下它的模块身份（UI-05），切回题库时才会被当成换模块。 */
  markQuestionListLoadedIdentity();
  state.answers = {};
  state.submitted = false;
  state.answerVisible = false;
  state.currentIndex = 0;
  state.answerCardPage = 0;
  const durationMs = rule.durationMinutes * 60000;
  state.exam = buildExamRuntime({ rule, scoreMap, questions, remainingMs: durationMs });
  saveExamDraft();
  startExamTimer();
  renderAll();
  await loadCurrentQuestion().catch((err) => toast(err.message));
  if (!examFlowStillCurrent(activeFlow)) return;
  toast(`已生成模拟卷：${questions.length} 题`);
}

/* 取题参数用**调用方捕获的** courseId：旧实现在 fetch 途中读活的
   state.currentCourse.id，挂起期间切科目就会拿科目 2 去抽科目 1 的卷子。 */
async function fetchExamPartQuestions(part, count, chapterId = 0, courseId = 0) {
  if (!count) return [];
  const targetCourseId = Number(courseId || state.currentCourse?.id || 0);
  const items = [];
  for (const typeId of part.availableTypeIds) {
    const params = new URLSearchParams({
      courseId: String(targetCourseId),
      typeId,
      limit: String(Math.max(count * 2, count)),
      order: "random",
    });
    if (chapterId) applyChapterParams(params, { id: chapterId });
    const chunk = await api(`/api/questions?${params}`);
    items.push(...chunk);
  }
  return shuffleItems(items).slice(0, count);
}

function getExamRemainingMs(exam = state.exam) {
  if (!exam) return 0;
  if (exam.pausedAt) return Math.max(0, Number(exam.remainingMs || 0));
  return Math.max(0, Number(exam.endsAt || 0) - Date.now());
}

function isExamTimedOut(exam = state.exam) {
  return !!exam && !state.submitted && getExamRemainingMs(exam) <= 0;
}

/* 超时提醒的两档阈値。**顺序 = 从最紧急到最宽松**，checkExamTimeWarnings 依赖它。
   旧实现到 00:00 直接强制交卷，全程一句提示都没有 —— 用户切走一会儿回来
   卷子已经交了。 */
const EXAM_WARN_STEPS = [
  { ms: 60000, flag: "warned1", text: "考试剩余 1 分钟，时间到会自动交卷" },
  { ms: 5 * 60000, flag: "warned5", text: "考试剩余 5 分钟，请尽快作答" },
];

/* 每档只提醒一次（state.exam.warned5 / warned1，跟随考试对象，新一场考试自然重置）。 */
function checkExamTimeWarnings() {
  const exam = state.exam;
  if (state.mode !== "exam" || !exam || state.submitted || exam.result) return;
  const left = getExamRemainingMs(exam);
  const index = EXAM_WARN_STEPS.findIndex((step) => !exam[step.flag] && left <= step.ms);
  if (index < 0) return;
  /* 命中最紧急的那档后，把更宽松的档一并标成「已提醒」：
     恢复草稿时可能一进来就只剩 30 秒，此时再补一句「剩余 5 分钟」纯属噪音。 */
  EXAM_WARN_STEPS.slice(index).forEach((step) => { exam[step.flag] = true; });
  toast(EXAM_WARN_STEPS[index].text);
}

/* 计时器每秒做的事：刷新状态条 → 到点提醒 → 到点自动交卷。 */
function tickExamClock() {
  renderExamStatus();
  checkExamTimeWarnings();
  if (isExamTimedOut()) submitPaper(true).catch((err) => toast(err.message));
}

function pauseExamTimer(reason = "background") {
  if (state.mode !== "exam" || !state.exam || state.submitted) {
    stopExamTimer();
    return;
  }
  if (!state.exam.pausedAt) {
    state.exam.remainingMs = Math.max(0, Number(state.exam.endsAt || 0) - Date.now());
    state.exam.pausedAt = Date.now();
    state.exam.pauseReason = reason;
  }
  stopExamTimer();
  renderExamStatus();
}

function resumeExamTimer() {
  if (state.mode !== "exam" || !state.exam || state.submitted) return;
  if (state.exam.pausedAt) {
    const remaining = Math.max(0, Number(state.exam.remainingMs || 0));
    state.exam.endsAt = Date.now() + remaining;
    state.exam.remainingMs = remaining;
    state.exam.pausedAt = null;
    state.exam.pauseReason = "";
  }
  if (isExamTimedOut()) {
    stopExamTimer();
    renderExamStatus();
    submitPaper(true).catch((err) => toast(err.message));
    return;
  }
  startExamTimer();
  renderExamStatus();
}

function startExamTimer() {
  stopExamTimer();
  if (state.mode !== "exam" || !state.exam || state.submitted || state.exam.pausedAt) {
    renderExamStatus();
    return;
  }
  state.examTimer = setInterval(() => {
    if (state.mode !== "exam" || !state.exam || state.submitted) return;
    if (state.exam.pausedAt) {
      stopExamTimer();
      renderExamStatus();
      return;
    }
    tickExamClock();
  }, 1000);
  tickExamClock();
}

function stopExamTimer() {
  if (state.examTimer) clearInterval(state.examTimer);
  state.examTimer = null;
}

function syncExamTimer() {
  if (state.mode !== "exam" || !state.exam) return;
  if (state.exam.pausedAt) {
    renderExamStatus();
    return;
  }
  tickExamClock();
}

function renderExamStatus() {
  const host = $("examStatus");
  if (!host) return;
  if (state.mode !== "exam" || !state.exam) {
    host.classList.add("hidden");
    host.classList.remove("exam-urgent");
    host.innerHTML = "";
    return;
  }
  const left = getExamRemainingMs();
  const result = state.exam.result;
  const paused = !!state.exam.pausedAt && !result;
  host.classList.remove("hidden");
  /* 最后 1 分钟整条状态条变红（.exam-urgent），和 ≤60s 那一档 toast 同一个阈值。 */
  host.classList.toggle("exam-urgent", !result && !state.submitted && left <= 60000);
  /* 倒计时走 formatSeconds：旧实现自己拼 `MM:SS`，超过 59 分钟就退化成
     「999998:59」这种没有意义的数（组卷时把考试时间填成 999999 分钟即可复现）。
     formatSeconds 在 ≥1 小时时给 H:MM:SS。 */
  host.innerHTML = `
    <span>模拟考场</span>
    <b>${formatSeconds(left / 1000)}</b>
    ${paused ? `<span class="exam-paused-flag">已暂停</span>` : ""}
    <span>满分 ${state.exam.rule.totalScore} 分</span>
    ${/* 「正确率」= 答对 ÷ 已作答，仅这一场（不是累计口径），标签里写明范围。
         本批之前这里只写「正确率」，与学习看板 KPI「正确率（已确认）」同屏不同义。 */ ""}
    ${result ? `<span>得分 <b>${formatScore(result.score)}</b> / ${formatScore(result.totalScore)}，本次正确率 ${result.rate}%</span>` : ""}
  `;
}

function stripText(html) {
  const div = document.createElement("div");
  div.innerHTML = sanitizeRichHtml(html);
  return div.innerText || "";
}

function nowText() {
  const d = new Date();
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

function parseDate(value) {
  if (!value) return null;
  const text = String(value).replace(/\//g, "-");
  const date = new Date(text.includes("T") ? text : text.replace(" ", "T"));
  return Number.isNaN(date.getTime()) ? null : date;
}

function formatRelativeTime(value) {
  const date = parseDate(value);
  if (!date) return "未知";
  const diffMs = Date.now() - date.getTime();
  const day = Math.floor(diffMs / 86400000);
  if (day <= 0) return "今天";
  if (day === 1) return "昨天";
  if (day < 30) return `${day} 天前`;
  if (day < 365) return `${Math.floor(day / 30)} 个月前`;
  return `${Math.floor(day / 365)} 年前`;
}

function formatScore(value) {
  const n = Number(value || 0);
  return Number.isInteger(n) ? String(n) : n.toFixed(1);
}

/* 交卷成绩面板。
   原来交卷后得分只在两个地方露面：
     · 练习模式 —— 一个几秒就消失的 toast
     · 考试模式 —— 顶栏 #examStatus 里一行小字
   看完就没了，也没法回看「对了几道、错了几道、几道没做」。
   分数本来就算好了（submitQuestions 那一轮循环里的
   score / totalScore / correct / checked / subjective），这里只是显示出来。

   exam 模式下顶栏那行仍然保留（考试中/已交卷都能看到）。 */
function showSubmitResult(payload) {
  const host = $("submitResult");
  if (!host) return;
  const {
    score = 0, totalScore = 0, correct = 0, checked = 0,
    total = 0, rate = 0, subjective = 0, autoSubmit = false,
  } = payload || {};
  const pct = totalScore > 0 ? Math.round((score / totalScore) * 100) : 0;
  const wrong = Math.max(0, checked - correct - subjective);
  const unanswered = Math.max(0, total - checked);
  setText("submitResultTitle", autoSubmit ? "时间到，已自动交卷" : "交卷完成");
  const body = host.querySelector(".submit-result-body");
  if (body) {
    body.innerHTML = `
      <div class="submit-result-score">
        <b>${formatScore(score)}</b><span>/ ${formatScore(totalScore)} 分</span>
      </div>
      <div class="submit-result-rate">得分率 ${pct}%</div>
      <dl class="submit-result-grid">
        <div><dt>题目总数</dt><dd>${total}</dd></div>
        <div><dt>已作答</dt><dd>${checked}</dd></div>
        <div><dt>答对</dt><dd class="is-right">${correct}</dd></div>
        <div><dt>答错</dt><dd class="is-wrong">${wrong}</dd></div>
        <div><dt>未作答</dt><dd>${unanswered}</dd></div>
        ${/* 「得分率」是 score/totalScore，「正确率」是 correct/checked —— 两个口径。
             用户点名「不要两个自称正确率的数」。这里把范围写进标签，
             和趋势图标题「近期正确率趋势（每次交卷，按本次已作答）」同一套说法。 */ ""}
        <div><dt>本次正确率</dt><dd>${rate}%</dd></div>
      </dl>
      ${subjective ? `<p class="submit-result-note">含 ${subjective} 道主观题，需人工核对，未计入得分。</p>` : ""}
    `;
  }
  host.classList.remove("hidden");
  const closeBtn = $("submitResultCloseBtn");
  if (closeBtn) closeBtn.onclick = () => host.classList.add("hidden");
}

function formatSeconds(value) {
  const total = Math.max(0, Math.floor(Number(value || 0)));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;
  return hours
    ? `${hours}:${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`
    : `${minutes}:${String(seconds).padStart(2, "0")}`;
}

function isCourseStale(value) {
  const date = parseDate(value);
  return !!date && Date.now() - date.getTime() > 30 * 86400000;
}

function escapeRegExp(value) {
  return String(value).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function toast(message) {
  const el = $("toast");
  if (!el) return;
  setText("toast", message);
  clearTimeout(toast.timer);
  clearTimeout(toast.hideTimer);
  const wasHidden = el.classList.contains("hidden");
  el.classList.remove("hidden");
  if (wasHidden) {
    el.classList.remove("show");
    void el.offsetWidth;
  }
  el.classList.add("show");
  toast.timer = setTimeout(() => {
    el.classList.remove("show");
    toast.hideTimer = setTimeout(() => el.classList.add("hidden"), 260);
  }, 1800);
}

function hideToast() {
  const el = $("toast");
  if (!el) return;
  clearTimeout(toast.timer);
  clearTimeout(toast.hideTimer);
  el.classList.remove("show");
  el.classList.add("hidden");
}

async function updateQuestionBank(courseId = 0, btn = null) {
  if (state.user.toLowerCase() !== "admin") return;
  if (!courseId) {
    toast("请选择要更新的题库");
    return;
  }
  if (!state.adminBankUpdateMode) {
    toast("正在识别更新方式，请稍后再点一次");
    ensureAdminBankUpdateMode().catch((err) => toast(err.message));
    return;
  }
  if (state.adminBankUpdateMode === "upload") {
    await uploadCourseQuestionBank(courseId, btn);
    return;
  }
  /* 拉取前先检查有没有更新：没有就直接跳过，不弹确认框、也不白等几分钟的拉取。
     检查失败时照常继续 —— 宁可多拉一次，也别因为检查环节出错就拉不了。 */
  const oldText = btn?.textContent || "";
  if (btn) { btn.disabled = true; btn.textContent = "检查中..."; }
  let hasUpdate = true;
  try {
    const check = await api("/api/admin/course-update-check", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ courseIds: [courseId] }),
    });
    const item = (check.results || []).find((row) => Number(row.courseId) === Number(courseId));
    if (item && item.hasUpdate === false && !item.error) hasUpdate = false;
    if (item) state.adminUpdateCheck[courseId] = !!item.hasUpdate;
  } catch (err) {
    console.warn("拉取前检查更新失败，改为直接拉取", err);
  } finally {
    if (btn) { btn.disabled = false; btn.textContent = oldText; }
  }
  if (!hasUpdate) {
    toast("该题库已是最新，无需更新");
    refreshAdminBankTable();
    return;
  }

  const query = new URLSearchParams({ user: state.user });
  query.set("courseId", String(courseId));
  if (!confirm("确定从服务器拉取并更新该题库吗？会先自动备份当前数据。")) return;
  await pullQuestionBankUpdate(query, btn);
}

async function pullQuestionBankUpdate(query, btn = null) {
  const oldText = btn?.textContent || "";
  const courseId = Number(query.get("courseId") || 0);
  if (btn) {
    btn.disabled = true;
    btn.textContent = "更新中...";
  }
  startBankPullProgress(courseId);
  let result;
  try {
    // POST 是同步的（要几分钟），进度靠轮询另一个端点，见 startBankPullProgress
    result = await api(`/api/admin/update-bank?${query}`, { method: "POST" });
  } finally {
    if (btn) {
      btn.disabled = false;
      btn.textContent = oldText || "更新题库";
    }
    // 成功、报错、抛异常都要收敛：POST 结束了这行就不能再写"正在更新"（用户反馈 2）
    await syncBankPullsFromServer(courseId).catch(() => {});
  }
  await handleQuestionBankUpdateResult(result);
}

async function uploadCourseQuestionBank(courseId, btn = null) {
  const input = document.createElement("input");
  input.type = "file";
  input.accept = ".zip";
  input.className = "hidden";
  document.body.appendChild(input);
  try {
    const file = await new Promise((resolve) => {
      input.onchange = () => resolve(input.files?.[0] || null);
      input.click();
    });
    if (!file) return;
    if (!confirm("确定上传并更新该题库吗？系统会匹配压缩包内相同 ID 的题库，并先自动备份当前数据。")) return;
    const oldText = btn?.textContent || "";
    if (btn) {
      btn.disabled = true;
      btn.textContent = "上传中...";
    }
    const form = new FormData();
    form.append("file", file);
    try {
      const query = new URLSearchParams({ user: state.user, courseId: String(courseId) });
      const result = await api(`/api/admin/update-bank?${query}`, { method: "POST", body: form });
      await handleQuestionBankUpdateResult(result);
    } finally {
      if (btn) {
        btn.disabled = false;
        btn.textContent = oldText || "更新题库";
      }
      // 上传路径本身不登记拉取进度，但这页可能同时有别的课在拉取：收敛一次列表
      await syncBankPullsFromServer(courseId).catch(() => {});
    }
  } finally {
    input.remove();
  }
}

async function handleQuestionBankUpdateResult(result) {
  if (result.skipped) {
    toast(result.message || "该题库已是最新，未执行拉取");
    return;
  }
  if (result.reserved || result.mode === "upload") {
    toast(result.message || "当前部署使用上传题库包更新");
    if (isAdmin() && state.adminView === "banks") renderAdminRows(state.adminRows, state.adminFailedCount);
    return;
  }
  if (result.ok && Array.isArray(result.results)) {
    if (result.report && result.report.after) {
      state.adminBankLastReport = { ...result.report, courseName: result.courseName || "" };
      state.adminBankLastReportAt = new Date().toLocaleString("zh-CN", { hour12: false });
    }
    const updated = result.results.filter((r) => Number(r.chapters || 0) > 0 || Number(r.subjects || 0) > 0);
    if (updated.length) {
      toast(`更新完成：${updated.map((r) => `课程${r.courseId}(${r.chapters || 0}章${r.subjects || 0}题)`).join("、")}`);
    } else {
      toast("所有题库已是最新");
    }
    state.adminCourses = [];
    state.adminDataStatus = null;
    state.courses = [];
    state.types = [];
    state.questions = [];
    /* 章节相关的状态与面板 DOM 也得清 —— 树是旧库的话，「当前章节」会指向旧 id、
       章节面板还显示旧库的章节，题单清 0 后点「下一题」零请求，界面等于死掉。 */
    resetChapterState();
    await loadCourses();
    if (isAdmin()) await loadAdminCourses().catch(() => {});
    if (isAdmin() && state.adminView === "banks") renderAdminRows(state.adminRows, state.adminFailedCount);
    await reloadChaptersAfterBankChange();
    return;
  }
  if (result.ok) {
    toast(result.message || "所有题库已是最新");
    return;
  }
  toast("更新失败：" + (result.error || "未知错误"));
}

async function handleWrongAction() {
  const q = state.questions[state.currentIndex]?.detail;
  if (!q) return;
  if (state.mode !== "wrong") {
    openCorrectionDialog(q);
    return;
  }
  delete userCourseStore().wrong[q.id];
  delete state.storage.wrong[q.id];
  scheduleSave();
  state.questions = state.questions.filter((item) => item.id !== q.id);
  if (state.currentIndex >= state.questions.length) state.currentIndex = state.questions.length - 1;
  state.answerCardPage = Math.max(0, Math.floor(Math.max(0, state.currentIndex) / state.answerCardPageSize));
  renderAll();
  if (state.currentIndex >= 0) await loadCurrentQuestion().catch((err) => toast(err.message));
  toast("已移出错题本");
}

function openCorrectionDialog(q) {
  const old = state.storage.corrections.find((item) => Number(item.questionId) === Number(q.id) && item.status !== "deleted");
  state.correctionQuestion = q;
  $("correctionType").value = old?.type || "答案错误";
  $("correctionNote").value = old?.note || "";
  $("correctionModal").classList.remove("hidden");
  $("correctionNote").focus();
}

function closeCorrectionDialog() {
  state.correctionQuestion = null;
  $("correctionModal").classList.add("hidden");
}

function saveCorrectionDialog() {
  const q = state.correctionQuestion;
  if (!q) return;
  const old = state.storage.corrections.find((item) => Number(item.questionId) === Number(q.id) && item.status !== "deleted");
  const cleanType = $("correctionType").value || "其他";
  const cleanNote = $("correctionNote").value.trim();
  if (!cleanNote) {
    toast("请填写纠错说明");
    $("correctionNote").focus();
    return;
  }
  const record = {
    id: old?.id || `${Date.now()}-${q.id}`,
    questionId: q.id,
    questionNo: state.currentIndex + 1,
    courseId: q.courseId,
    courseName: state.currentCourse?.name || "",
    chapterId: q.chapterId,
    chapterName: q.chapterName,
    subjectType: q.subjectType,
    questionType: q.type,
    title: stripText(q.stem).slice(0, 160),
    type: cleanType,
    note: cleanNote,
    status: "open",
    at: old?.at || nowText(),
    updatedAt: nowText(),
  };
  if (old) Object.assign(old, record);
  else state.storage.corrections.unshift(record);
  state.storage.corrections = state.storage.corrections.slice(0, 500);
  scheduleSave();
  closeCorrectionDialog();
  renderQuestion();   // 立刻把「已提交纠错」角标画出来，不用翻页才看到（S12 闭环）
  toast("纠错反馈已保存");
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function isTypingTarget(target) {
  const isEditable = (el) => {
    const tag = el?.tagName;
    return !!(el?.isContentEditable || tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT");
  };
  return isEditable(target) || isEditable(document.activeElement);
}

/* ── 图片查看器 ──────────────────────────────────────────────────────────
   题干 / 附加题 / 解析里的图会被 `max-width: 100%` 压到容器宽度 ——
   小屏（尤其 7 寸墨水屏）上看图纸、表格类题图基本没法看。这里给一个全屏查看器。

   交互：点图打开 · 点空白 / 关闭按钮 / Esc 关闭 · 按钮或双指缩放 ·
         普通滚轮滚动、Ctrl+滚轮缩放 · 放大后拖拽平移。

   选项是纯文本（`escapeHtml(option.text)`），不会有图，所以和「点选项作答」不冲突。
   ────────────────────────────────────────────────────────────────────── */
const IV_MIN_ZOOM = 0.2;
const IV_MAX_ZOOM = 8;
const IV_UPSCALE_CAP = 2;      // 「适应」最多把小图放大到这个倍数，再多就是糊
const imgViewerState = { zoom: 1, naturalW: 1 };

function imageViewerOpen() {
  return !!$("imgViewer")?.classList.contains("open");
}

function applyImageViewerZoom() {
  const image = $("imgViewerImage");
  if (!image) return;
  image.style.width = Math.round(imgViewerState.naturalW * imgViewerState.zoom) + "px";
  const label = $("ivZoomLabel");
  if (label) label.textContent = Math.round(imgViewerState.zoom * 100) + "%";
}

function setImageViewerZoom(zoom) {
  imgViewerState.zoom = Math.max(IV_MIN_ZOOM, Math.min(IV_MAX_ZOOM, zoom));
  applyImageViewerZoom();
}

function stepImageViewerZoom(direction) {
  setImageViewerZoom(imgViewerState.zoom * (direction > 0 ? 1.25 : 0.8));
}

function fitImageViewer() {
  const image = $("imgViewerImage");
  const scroll = $("imgViewerScroll");
  if (!image || !scroll || !image.naturalWidth) return;
  imgViewerState.naturalW = image.naturalWidth;
  const pad = 40;
  const availW = Math.max(80, scroll.clientWidth - pad);
  const availH = Math.max(80, scroll.clientHeight - pad - 70);   // 减去工具条
  const ratio = Math.min(availW / image.naturalWidth, availH / image.naturalHeight);
  /* 大图缩到刚好放下；小图最多放大 IV_UPSCALE_CAP 倍（再放就是插值糊） */
  setImageViewerZoom(ratio >= 1 ? Math.min(ratio, IV_UPSCALE_CAP) : ratio);
  scroll.scrollTop = 0;
  scroll.scrollLeft = 0;
}

function openImageViewer(src, alt) {
  const viewer = $("imgViewer");
  const image = $("imgViewerImage");
  if (!viewer || !image || !src) return;
  image.alt = alt || "题图";
  image.onload = () => fitImageViewer();
  image.src = src;
  viewer.classList.add("open");
  document.body.classList.add("img-viewer-open");
  if (image.complete && image.naturalWidth) fitImageViewer();
}

function closeImageViewer() {
  const viewer = $("imgViewer");
  if (!viewer || !viewer.classList.contains("open")) return;
  viewer.classList.remove("open");
  document.body.classList.remove("img-viewer-open");
  const image = $("imgViewerImage");
  if (image) {
    image.onload = null;
    image.removeAttribute("src");   // 释放大图内存
    image.style.width = "";
  }
}

function bindImageViewer() {
  const viewer = $("imgViewer");
  const scroll = $("imgViewerScroll");
  const image = $("imgViewerImage");
  if (!viewer || !scroll || !image) return;

  // 点空白关闭（点图本身、点工具条都不关）
  viewer.addEventListener("click", (event) => {
    if (event.target.closest("#imgViewerImage") || event.target.closest(".img-viewer-bar")) return;
    closeImageViewer();
  });

  $("ivZoomIn").onclick = () => stepImageViewerZoom(1);
  $("ivZoomOut").onclick = () => stepImageViewerZoom(-1);
  $("ivFit").onclick = () => fitImageViewer();
  $("ivActual").onclick = () => setImageViewerZoom(1);
  $("ivClose").onclick = () => closeImageViewer();

  // 普通滚轮留给滚动，Ctrl+滚轮才缩放（和浏览器习惯一致）
  scroll.addEventListener("wheel", (event) => {
    if (!event.ctrlKey) return;
    event.preventDefault();
    stepImageViewerZoom(event.deltaY < 0 ? 1 : -1);
  }, { passive: false });

  // 双指缩放
  let pinch = null;
  const touchDistance = (touches) => Math.hypot(
    touches[0].clientX - touches[1].clientX,
    touches[0].clientY - touches[1].clientY
  );
  scroll.addEventListener("touchstart", (event) => {
    pinch = event.touches.length === 2
      ? { distance: touchDistance(event.touches), zoom: imgViewerState.zoom }
      : null;
  }, { passive: true });
  scroll.addEventListener("touchmove", (event) => {
    if (!pinch || event.touches.length !== 2 || !pinch.distance) return;
    event.preventDefault();
    setImageViewerZoom(pinch.zoom * (touchDistance(event.touches) / pinch.distance));
  }, { passive: false });
  scroll.addEventListener("touchend", () => { pinch = null; }, { passive: true });

  // Esc 关闭：捕获阶段先处理，免得被全局快捷键抢走
  document.addEventListener("keydown", (event) => {
    if (event.key !== "Escape" || !imageViewerOpen()) return;
    event.preventDefault();
    event.stopPropagation();
    closeImageViewer();
  }, true);

  /* 事件委托：题干/附加题/解析每次重渲染都会换掉里面的 <img>，
     绑在容器上就不用每次重绑。题库编辑器的预览区也走同一条路 ——
     管理员纠错时要能点开题图看清楚。 */
  document.addEventListener("click", (event) => {
    const img = event.target?.closest?.("img");
    if (!img || !img.getAttribute("src")) return;
    if (!img.closest("#questionView, .bank-editor-preview")) return;
    event.preventDefault();
    event.stopPropagation();
    openImageViewer(img.currentSrc || img.src, img.alt);
  });
}

function hasOpenModal() {
  if (imageViewerOpen()) return true;
  return ["correctionModal", "adminUserModal", "printModal", "conflictModal", "examHistoryModal", "examDraftMissingModal"].some((id) => {
    const el = $(id);
    return el && !el.classList.contains("hidden");
  });
}

function handleGlobalShortcuts(event) {
  const focusedControl = event.target?.closest?.("button, a, [role=\"button\"]") || document.activeElement?.closest?.("button, a, [role=\"button\"]");
  if (!state.user || isAdmin() || state.submitting || isTypingTarget(event.target) || focusedControl || hasTextSelection() || hasOpenModal() || event.altKey || event.metaKey) return;
  if (event.key === "ArrowLeft" && state.questions.length) {
    event.preventDefault();
    moveQuestion(-1).catch((err) => toast(err.message));
    return;
  }
  if (event.key === "ArrowRight" && state.questions.length) {
    event.preventDefault();
    moveQuestion(1).catch((err) => toast(err.message));
    return;
  }
  if (event.ctrlKey && event.key === "Enter" && state.questions.length) {
    event.preventDefault();
    /* 和「交卷」按钮同一个入口：先确认再交（不可逆动作不能因为入口不同就少一道确认）。 */
    requestExamSubmit();
    return;
  }
  if (!event.ctrlKey && !event.shiftKey && event.key === "Enter" && state.currentIndex >= 0) {
    const q = state.questions[state.currentIndex]?.detail;
    if (q && currentVerifyMode() === "instant" && (isMultiChoice(q) || isSubjective(q)) && hasAnswer(q.id) && !isQuestionVerified(q.id)) {
      event.preventDefault();
      confirmCurrentAnswer();
      return;
    }
  }
  if (!event.ctrlKey && !event.shiftKey && event.key.toLowerCase() === "f" && state.currentIndex >= 0) {
    event.preventDefault();
    toggleFavorite();
    return;
  }
  if (!event.ctrlKey && !event.shiftKey && event.key.toLowerCase() === "s" && state.currentIndex >= 0) {
    event.preventDefault();
    toggleAnswer();
    return;
  }
  if (!event.ctrlKey && !event.shiftKey && /^[1-9]$/.test(event.key) && state.currentIndex >= 0) {
    const q = state.questions[state.currentIndex]?.detail;
    const index = Number(event.key) - 1;
    const option = q?.options?.[index];
    if (option && !shouldRevealCurrentAnswer(q)) {
      event.preventDefault();
      chooseOption(q, option.label);
      return;
    }
  }
  if (!event.ctrlKey && !event.shiftKey && event.key.toLowerCase() === "a" && state.currentIndex >= 0) {
    const q = state.questions[state.currentIndex]?.detail;
    if (q?.options?.some((option) => option.label === "A") && !shouldRevealCurrentAnswer(q)) {
      event.preventDefault();
      chooseOption(q, "A");
    }
    return;
  }
  if (!event.ctrlKey && !event.shiftKey && /^[b-z]$/i.test(event.key) && state.currentIndex >= 0) {
    const label = event.key.toUpperCase();
    const q = state.questions[state.currentIndex]?.detail;
    if (q?.options?.some((option) => option.label === label) && !shouldRevealCurrentAnswer(q)) {
      event.preventDefault();
      chooseOption(q, label);
    }
  }
}

ensureMobileControls();
bindQuestionSwipe();

/* 切模块的唯一实现（六个 study 模式之间）。导航项、训练页底部的三个出口、
   空态里的「返回考试题库」原来各写一份 —— 手抄的那几份都**不递增
   modeTransitionId**，于是在途的旧请求回来照样写屏（导航路径靠它作废在途请求）。
   统一到这里，顺带保证 setPanelPage / 离开考场 / 清训练指针 / 清错题筛选
   这几步在每条入口上都执行。

   这里不提供「不清训练指针」的开关：错题本内部开练走 startWrongPracticeSession
   （它自己设 mode 和指针），不经过 switchMode。 */
async function switchMode(nextMode) {
  if (!nextMode) return;
  setMobileTools(false);
  setMobileActions(false);
  if (isAdmin()) {
    renderAdminDashboard();
    return;
  }
  const transitionId = (state.modeTransitionId || 0) + 1;
  state.modeTransitionId = transitionId;
  state.questionRequestId = (state.questionRequestId || 0) + 1;
  state.submitToken = (state.submitToken || 0) + 1;
  state.submitting = false;
  setPanelPage(nextMode === "exam" || nextMode === "progress" || nextMode === "training");
  /* 切模块：离开考场时暂停（勾了选项才真暂停）、存草稿、停表。
     三处「离开考场」共用 suspendExam（见那边的注释）。 */
  suspendExam("module");
  if (state.mode === "exam" && nextMode !== "exam") stopExamTimer();
  if (["practice", "wrong", "favorite"].includes(nextMode)) {
    leaveActiveTrainingSession();
  }
  /* 进错题本一律按「全部」看：见 resetWrongBookPageFilter */
  if (nextMode === "wrong") resetWrongBookPageFilter();
  state.mode = nextMode;
  if (state.mode === "practice") setCoursePicker(shouldAutoOpenCoursePicker());
  else setCoursePicker(false);
  renderMode();
  renderWrongFilters();
  renderPracticeContextPanel();
  if (!state.currentCourse) {
    if (state.mode === "exam") renderExamHome();
    return;
  }
  if (state.mode === "exam") {
    await enterExamHome(transitionId);
    return;
  }
  if (state.mode === "progress" || state.mode === "training") {
    $("questionBody").classList.add("hidden");
    $("emptyState").classList.remove("hidden");
    $("emptyState").innerHTML = state.mode === "training"
      ? `<strong>正在生成智能训练</strong><span>正在读取错题、薄弱章节和今日练习建议。</span>`
      : `<strong>正在生成学习看板</strong><span>正在读取当前科目的进度、训练记录和学习信号。</span>`;
  }
  if (!["exam", "progress", "training"].includes(state.mode)) {
    /* UI-05：切模块的**第一时刻**就收掉上一个模块的题单并给加载占位。
       不能等 loadQuestions（它排在 loadTypes 之后）——期间屏上会一直挂着
       别的模块的题目。 */
    beginQuestionModuleEntry();
    await loadTypes();
    if (transitionId !== state.modeTransitionId) return;
  }
  await loadQuestions().catch((err) => toast(err.message));
  if (transitionId !== state.modeTransitionId) return;
}

document.querySelectorAll(".nav-item[data-mode]").forEach((btn) => {
  btn.onclick = async () => {
    const nextMode = btn.dataset.mode;
    /* 已经在题库里，再点底栏「题库」不再往返切面板（S-35）：
       手机上面板占满整屏（实测 390×844 高 703px），每点一次「题库」都被它拦一道，
       而唯一的出口是面板底部叫「进入做题」的按钮。
       现在：还没选科目 → 弹（必须选）；已有科目 → 收起 / 不动作
       （点的是当前页签，本该没动作）。换科目请点顶栏的科目名。
       桌面端维持原来的开关行为（面板是并排一栏，不挡题）。
       这一段是**页签独有**的语义（点的是当前页签），所以留在调用方，不进 switchMode。 */
    if (nextMode === "practice" && state.mode === "practice" && !isAdmin()) {
      setMobileTools(false);
      setMobileActions(false);
      setCoursePicker(isPhoneViewport() ? !state.currentCourse : !state.pickerOpen);
      renderMode();
      return;
    }
    await switchMode(nextMode);
  };
});

$("loginBtn").onclick = () => login().catch((err) => toast(err.message));
$("loginUserSelect").addEventListener("keydown", (e) => {
  if (e.key === "Enter") $("loginPassword").focus();
});
$("loginPassword").addEventListener("keydown", (e) => {
  if (e.key === "Enter") login().catch((err) => toast(err.message));
});
$("loginUserSelect").addEventListener("input", () => {
  $("changePasswordBox").classList.add("hidden");
  state.pendingPasswordUser = "";
  state.pendingOldPassword = "";
});
$("changePasswordBtn").onclick = () => changePassword().catch((err) => toast(err.message));
$("confirmPassword").addEventListener("keydown", (e) => {
  if (e.key === "Enter") changePassword().catch((err) => toast(err.message));
});
$("logoutBtn").onclick = () => logout().catch((err) => toast(err.message));
initAccountMenu();
$("mobileToolsBtn").onclick = () => setMobileTools(!state.mobileToolsOpen);
$("answerCardCollapseBtn").onclick = () => {
  state.answerCardUserTouched = true;
  setAnswerCardCollapsed(!state.answerCardCollapsed);
};
$("pickerDoneBtn").onclick = () => {
  if (!state.currentCourse) {
    toast("请先选择题库");
    return;
  }
  setCoursePicker(false);
};
$("questionView").addEventListener("click", () => {
  if (!isAdmin() && state.mode === "practice" && state.pickerOpen) setCoursePicker(false);
}, true);
$("prevBtn").onclick = () => moveQuestion(-1);
$("nextBtn").onclick = () => moveQuestion(1);
$("answerToggleBtn").onclick = toggleAnswer;
$("verifyModeBtn").onclick = toggleVerifyMode;
$("confirmAnswerBtn").onclick = confirmCurrentAnswer;
/* ⚠️ 交卷确认放在这里、不放进 submitPaper：超时自动交卷走的是
   submitPaper(true)（计时器 / 恢复草稿时直调），那条路不该被 confirm 拦住。
   ⚠️ 快捷键也必须走同一个 requestExamSubmit（见 handleGlobalShortcuts）：
   同一个不可逆动作不能因为入口不同就少一道确认。 */
$("submitBtn").onclick = requestExamSubmit;
$("resetBtn").onclick = resetPractice;
$("favoriteBtn").onclick = toggleFavorite;
$("mobileActionsBtn").onclick = () => setMobileActions(!state.mobileActionsOpen);
$("noteBtn").onclick = toggleNoteEditor;
/* ⚠️ 必须用闭包抓「这一刻」的题号 + 文本。debounce 会把参数透传给回调，
   直接写 debounce(saveNote, 300) 会把 input 事件对象当成 questionId 传进去。 */
$("noteEditor").addEventListener("input", () => {
  pendingNote = { id: currentNoteQuestionId(), value: $("noteEditor").value };
  scheduleNoteSave();
});
$("wrongBtn").onclick = () => handleWrongAction().catch((err) => toast(err.message));
$("correctionCloseBtn").onclick = closeCorrectionDialog;
$("correctionCancelBtn").onclick = closeCorrectionDialog;
$("correctionSaveBtn").onclick = saveCorrectionDialog;
$("correctionModal").addEventListener("click", (event) => {
  if (event.target === $("correctionModal")) closeCorrectionDialog();
});
$("adminUserCloseBtn").onclick = closeAdminUserDialog;
$("adminUserCancelBtn").onclick = closeAdminUserDialog;
$("adminUserSaveBtn").onclick = () => adminAddUser().catch((err) => toast(err.message));
$("adminUserNameInput").addEventListener("keydown", (event) => {
  if (event.key === "Enter") adminAddUser().catch((err) => toast(err.message));
});
$("adminUserModal").addEventListener("click", (event) => {
  if (event.target === $("adminUserModal")) closeAdminUserDialog();
});
if ($("navCourseTitle")) $("navCourseTitle").onclick = toggleCoursePicker;
if ($("courseTitle")) $("courseTitle").onclick = toggleCoursePicker;
/* 「搜索」按钮已删除（与「试题筛选」逐字重复，见 index.html 的注释）。
   题目内搜索仍在 #searchPanel 里的 #questionSearch，只是只做本地匹配。 */
$("filterBtn").onclick = () => $("searchPanel").classList.toggle("hidden");
$("printBtn").onclick = openPrintDialog;
$("printCloseBtn").onclick = closePrintDialog;
$("printCancelBtn").onclick = closePrintDialog;
$("printCurrentBtn").onclick = () => printQuestions("current").catch((err) => toast(err.message));
$("printAllBtn").onclick = () => printQuestions("all").catch((err) => toast(err.message));
$("printModal").addEventListener("click", (event) => {
  if (event.target === $("printModal")) closePrintDialog();
});
if ($("examHistoryModal")) {
  /* 全部模拟记录抽屉（考场首页「查看全部 N 场」打开）。 */
  $("examHistoryModalCloseBtn").onclick = closeExamHistoryDrawer;
  $("examHistoryModal").addEventListener("click", (event) => {
    if (event.target === $("examHistoryModal")) closeExamHistoryDrawer();
  });
}
if ($("pushModal")) {
  /* 每日提醒设置（账号菜单 →「每日提醒」）。 */
  $("pushSettingsBtn").onclick = () => openPushModal();
  $("pushModalCloseBtn").onclick = closePushModal;
  $("pushModal").addEventListener("click", (event) => {
    if (event.target === $("pushModal")) closePushModal();
  });
  $("pushType").addEventListener("change", refreshPushTypeFields);
  $("pushToken").addEventListener("input", () => { pushTokenDirty = true; });
  for (const id of ["pushStyle", "pushBaseUrl"]) {
    $(id)?.addEventListener("change", schedulePushPreview);
  }
  $("pushStyle")?.addEventListener("change", refreshPushStyleHint);
  $("pushSaveBtn").onclick = () => { savePushSettings(); };
  $("pushTestBtn").onclick = () => { testPushSettings(); };
  /* 「绑定本机」必须由用户点击触发 —— 浏览器只在用户手势里允许申请通知权限。 */
  $("pushBindBtn").onclick = () => { bindWebPush(); };
}
if ($("conflictModal")) {
  $("conflictCloseBtn").onclick = keepLocalConflictDraft;
  $("conflictKeepBtn").onclick = keepLocalConflictDraft;
  $("conflictExportBtn").onclick = exportConflictLocalDraft;
  $("conflictReloadBtn").onclick = () => reloadServerUserDataFromConflict().catch((err) => toast(err.message));
  $("conflictModal").addEventListener("click", (event) => {
    if (event.target === $("conflictModal")) keepLocalConflictDraft();
  });
}
window.addEventListener("afterprint", cleanupPrintView);
$("zoomInBtn").onclick = () => changeZoom(0.1);
$("zoomOutBtn").onclick = () => changeZoom(-0.1);
bindImageViewer();
bindTagPanelDismiss();
$("fullscreenBtn").onclick = () => toggleFullscreen().catch((err) => toast(err.message || "无法进入全屏"));
document.addEventListener("fullscreenchange", updateFullscreenState);
document.addEventListener("keydown", handleGlobalShortcuts);
// 卡片高度会因展开/收起、翻页、内容重排而变，用 ResizeObserver 让 --answer-card-h 始终跟上
(function watchAnswerCardSpace() {
  const card = document.querySelector(".answer-card-wrap");
  if (!card) return;
  if (typeof ResizeObserver === "function") {
    new ResizeObserver(syncAnswerCardSpace).observe(card);
  }
  syncAnswerCardSpace();
})();
window.addEventListener("resize", debounce(() => {
  scheduleNavIndicator();
  applyResponsiveAnswerCard();
  syncAnswerCardSpace();
}, 120));
document.addEventListener("visibilitychange", () => {
  if (document.hidden) {
    /* 默认（未勾「离开页面时暂停计时」）只存草稿、不暂停 ——
       草稿里带着 endsAt，回来续考时按真实时间算剩余，时间照走。 */
    suspendExam("background");
    if (state.storageDirty) {
      saveUserData().catch(() => {
        /* 页面已隐藏，弹不出对话框 → 退回 sendBeacon 再存一次；
           若 beacon 也发不出去，把保存状态标成失败，用户切回来能立刻看到。 */
        if (!flushPendingSaveBeacon()) handleUserSaveError(new Error("保存失败，请检查网络后重试"));
      });
    }
    return;
  }
  /* 回到前台时，如果还有未保存的改动（例如刚才那次保存失败），补一次保存。 */
  if (state.user && state.storageDirty) scheduleSave();
  if (state.user && refreshDailyTrainingState()) {
    scheduleSave();
    if (state.mode === "training") renderTraining();
  }
  if (state.mode === "exam" && state.exam && !state.submitted) {
    /* 真暂停过才「恢复」；默认状态时间一直在走，这里只补一次到点检查 ——
       后台标签页的 setInterval 被浏览器限流，回来时可能已经超时但还没交卷。 */
    if (state.exam.pausedAt) resumeExamTimer();
    else syncExamTimer();
    if (!state.submitted) saveExamDraft();
  }
});
/* 「刷新列表」：旧实现只 loadCourses()，章节树和章节面板一个字都不动
   （实测点完 /api/chapters 请求 0 个）。章节是随题库一起变的，这里一并重读。 */
document.querySelector('[data-action="refresh"]').onclick = () => {
  loadCourses()
    .then(() => reloadChaptersAfterBankChange())
    .then(() => toast("已重新读取本地题库"))
    .catch((err) => toast(err.message));
};

$("courseSearch").addEventListener("input", debounce(loadCourses));
/* 题内搜索：本地匹配（S-33）。旧实现是 debounce(loadQuestions)，每敲一个字
   重拉一次整份题单、整块重绘答题卡 —— 搜索没有网络需求，题单也不该被它改变。
   现在只重绘当前题（高亮）+ 命中面板 + 答题卡命中格；题单只在改题型/标签/题量时重取。 */
const applyLocalQuestionSearch = debounce(() => {
  if (state.currentIndex >= 0 && state.questions[state.currentIndex]?.detail) renderQuestion();
  else {
    renderSearchMatches();
    renderAnswerCardPage({ updateNav: false });
  }
}, 180);
$("questionSearch").addEventListener("input", applyLocalQuestionSearch);
$("typeSelect").addEventListener("change", loadQuestions);
$("orderSelect").addEventListener("change", () => { persistQuestionWindow(); loadQuestions(); });
$("limitSelect").addEventListener("change", () => { persistQuestionWindow(); loadQuestions(); });

window.addEventListener("beforeunload", (event) => {
  /* 管理题库编辑区有未保存草稿时，刷新/关页也要拦一次（UI-06）：
     这段草稿只在 DOM 里，页面一走就没了。浏览（没改过字段）不会触发。 */
  if (typeof isBankEditorDirty === "function" && isBankEditorDirty()) {
    event.preventDefault();
    event.returnValue = "题库编辑区还有未保存的修改";
  }
  syncCurrentTrainingSession();
  /* 刷新/关页默认不暂停（时间照走，草稿按 endsAt 续），勾了选项才暂停。 */
  suspendExam("unload");
  if (state.saveTimer) {
    clearTimeout(state.saveTimer);
    state.saveTimer = null;
  }
  flushPendingSaveBeacon();
});

init().catch((err) => {
  $("courseList").innerHTML = `<div class="muted">${escapeHtml(err.message)}</div>`;
});


