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
  currentCourse: null,
  currentChapter: null,
  currentIndex: -1,
  answers: {},
  submitted: false,
  answerVisible: false,
  zoom: 1,
  storage: {},
  saveTimer: null,
  pendingPasswordUser: "",
  pendingOldPassword: "",
  pickerOpen: false,
  mobileMenuOpen: false,
  mobileActionsOpen: false,
  mobileToolsOpen: false,
  answerCardCollapsed: false,
  mobileTagsOpen: false,
  wrongFilters: { status: "active", minCount: "1", period: "all" },
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
  adminBankUpdateMode: "",
  adminBankUpdateModePromise: null,
  adminDataStatus: null,
  adminBankManagerCourseId: 0,
  adminBankManagerReport: null,
  adminBankManagerBusy: "",
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
  correctionQuestion: null,
  expandedChapters: new Set(),
  examExpandedChapters: new Set(),
  examSelectedChapters: null,
  analysisExpandedChapters: new Set(),
  chapterAutoExpanded: false,
  smartPracticeFreshStart: false,
  practiceContextCollapsed: null,
  printAfterRender: false,
  lastAnimatedQuestionId: 0,
  lastMarkedQuestionId: 0,
};

const $ = (id) => document.getElementById(id);
const SESSION_USER_KEY = "yunxi-session-user";
const LAST_USER_KEY = "yunxi-current-user";
const REVIEW_INTERVAL_DAYS = [1, 2, 4, 7, 15, 30];
const DEFAULT_TAG_LABELS = ["计算量大", "易错题", "坑题", "重要", "待复盘"];
const FAVORITE_GROUPS = ["公式", "易混点", "考前速看", "老师提醒"];
const NOTE_TEMPLATE = "考点：\n\n易错点：\n\n正确思路：\n";

function setText(id, value) {
  const el = $(id);
  if (el) el.textContent = value;
}

async function api(path, options) {
  let res;
  try {
    res = await fetch(path, options);
  } catch (err) {
    throw new Error("网络连接失败，请检查服务是否已启动");
  }
  if (!res.ok) {
    const err = await res.json().catch(() => ({ error: res.statusText }));
    throw new Error(err.error || res.statusText);
  }
  return res.json();
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

function addLazyLoading(html) {
  if (!html) return "";
  return String(html).replace(/<img\b(?![^>]*\bloading=)/gi, '<img loading="lazy"');
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
  return String(value || "").trim().replace(/[、,，\s]/g, "");
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
    record.chapterName = q.chapterName;
    record.type = q.type;
    record.title = stripText(q.stem).slice(0, 120);
  }
  record.wrongAt ||= record.at || now;
  record.lastReviewAt ||= record.at || record.wrongAt;
  record.stage = reviewStage(record);
  record.count = Number(record.count || 1);
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
  const ids = new Set([Number(chapter.id)]);
  const visit = (node) => {
    (node.children || []).forEach((child) => {
      ids.add(Number(child.id));
      visit(child);
    });
  };
  const treeNode = state.chapterTreeIndex?.get(Number(chapter.id));
  if (treeNode) visit(treeNode);
  return Array.from(ids).filter(Boolean);
}

function chapterPathForId(chapterId) {
  const id = Number(chapterId || 0);
  if (!id || !state.chapterTreeRoots) return [];
  return findChapterPath(state.chapterTreeRoots, id);
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
      questionCount: chapterQuestionCount(chapter),
    }));
}

function getQuestionScore(q) {
  if (state.exam?.scoreMap?.[q.id] != null) return Number(state.exam.scoreMap[q.id]);
  if (Number(q.subjectType) === 1) return 2;
  if (Number(q.subjectType) === 6) return 1.5;
  if (isSubjective(q)) return 0;
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

function getExamConfigFromForm(rule) {
  const name = ($("examNameInput")?.value || rule.name).trim() || rule.name;
  const duration = Math.max(1, Number($("examDurationInput")?.value || rule.durationMinutes));
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
  if (total <= 0 || level > 2) return "";
  const hasChildren = (chapter.children || []).some((child) => chapterTotalCount(child) > 0);
  const expanded = state.examExpandedChapters.has(Number(chapter.id));
  return `
    <div class="exam-chapter-node level-${level}">
      <div class="exam-chapter-row">
        <button class="exam-chapter-toggle ${expanded ? "expanded" : "collapsed"}" type="button" data-exam-toggle="${chapter.id}" ${hasChildren ? "" : "disabled"}>${hasChildren ? (expanded ? "−" : "+") : ""}</button>
        <label title="${escapeHtml(chapter.name)}">
          <input type="checkbox" data-exam-chapter value="${chapter.id}" data-exam-parent="${hasChildren ? "1" : "0"}" ${state.examSelectedChapters.has(Number(chapter.id)) ? "checked" : ""}>
          <span>${escapeHtml(chapter.name)}</span>
          <small>${total}题</small>
        </label>
      </div>
      ${hasChildren && expanded ? `<div class="exam-chapter-children">${chapter.children.map((child) => renderExamChapterNode(child, level + 1)).join("")}</div>` : ""}
    </div>
  `;
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
    input.onchange = () => {
      const id = Number(input.value || 0);
      const node = state.chapterTreeIndex?.get(id);
      state.examSelectedChapters ||= new Set();
      if (input.checked) state.examSelectedChapters.add(id);
      else state.examSelectedChapters.delete(id);
      if (!node) return;
      childChapterIds(node).forEach((childId) => {
        if (input.checked) state.examSelectedChapters.add(childId);
        else state.examSelectedChapters.delete(childId);
        const child = document.querySelector(`[data-exam-chapter][value="${childId}"]`);
        if (child) child.checked = input.checked;
      });
    };
  });
}

function wrongRecordMatchesFilter(id, item, courseStore) {
  ensureWrongReviewFields(item);
  const filters = state.wrongFilters || {};
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
  Object.entries(state.storage.wrong || {})
    .filter(([, item]) => Number(item?.courseId || 0) === Number(state.currentCourse?.id || 0))
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
  if (!$("mobileMenuBtn")) {
    const btn = document.createElement("button");
    btn.id = "mobileMenuBtn";
    btn.className = "mobile-menu-btn";
    btn.type = "button";
    btn.textContent = "\u66f4\u591a";
    const refresh = document.querySelector('[data-action="refresh"]');
    refresh?.parentNode.insertBefore(btn, refresh);
  }

  if (!$("mobileActionsBtn") && $("noteBtn")) {
    const btn = document.createElement("button");
    btn.id = "mobileActionsBtn";
    btn.className = "mobile-actions-btn";
    btn.type = "button";
    btn.textContent = "\u66f4\u591a";
    $("noteBtn").parentNode.insertBefore(btn, $("noteBtn"));
  }

  if (!$("mobileToolsBtn")) {
    const btn = document.createElement("button");
    btn.id = "mobileToolsBtn";
    btn.className = "mobile-tools-btn";
    btn.type = "button";
    btn.textContent = "\u66f4\u591a";
    const toolbar = document.querySelector(".toolbar");
    toolbar?.appendChild(btn);
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
    const nextBtn = $("nextBtn");
    nextBtn?.parentNode.insertBefore(confirmBtn, nextBtn);
  }
}

function setMobileMenu(open) {
  state.mobileMenuOpen = open;
  document.body.classList.toggle("mobile-menu-open", open);
  if ($("mobileMenuBtn")) {
    $("mobileMenuBtn").textContent = open ? "\u6536\u8d77" : "\u66f4\u591a";
    $("mobileMenuBtn").setAttribute("aria-expanded", String(open));
  }
  scheduleNavIndicator();
  setTimeout(updateNavIndicator, 260);
}

function setMobileActions(open) {
  state.mobileActionsOpen = open;
  document.body.classList.toggle("mobile-actions-open", open);
  if ($("mobileActionsBtn")) {
    $("mobileActionsBtn").textContent = open ? "\u6536\u8d77" : "\u66f4\u591a";
    $("mobileActionsBtn").setAttribute("aria-expanded", String(open));
  }
}

function setMobileTools(open) {
  state.mobileToolsOpen = open;
  document.body.classList.toggle("mobile-tools-open", open);
  if ($("mobileToolsBtn")) {
    $("mobileToolsBtn").textContent = open ? "\u6536\u8d77" : "\u66f4\u591a";
    $("mobileToolsBtn").setAttribute("aria-expanded", String(open));
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

function setMobileTagsOpen(open) {
  state.mobileTagsOpen = !!open;
  const panel = $("questionTagPanel");
  if (panel) {
    panel.classList.toggle("open", state.mobileTagsOpen);
    const btn = $("tagPanelToggleBtn");
    if (btn) btn.setAttribute("aria-expanded", String(state.mobileTagsOpen));
  }
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
  ensureMobileControls();
  setAnswerCardCollapsed(false);
  updateFullscreenState();
  await loadUsers();
  const sessionUser = localStorage.getItem(SESSION_USER_KEY);
  if (sessionUser && state.users.some((item) => item.name.toLowerCase() === sessionUser.toLowerCase() && !item.disabled)) {
    try {
      await enterApp(sessionUser, { rememberSession: false });
      return;
    } catch (err) {
      localStorage.removeItem(SESSION_USER_KEY);
      toast(err.message);
    }
  }
  showLogin();
}

async function loadUsers() {
  state.users = await api("/api/users");
  renderUsers();
}

function renderUsers() {
  setText("accountLabel", `账号：${state.user || "未登录"}`);
  setText("userName", state.user || "未登录");
  document.body.classList.toggle("is-admin", (state.user || "").toLowerCase() === "admin");
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
  const lastUser = localStorage.getItem(LAST_USER_KEY) || "admin";
  $("loginUserSelect").value = lastUser;
  $("loginUserSelect").focus();
}

async function login() {
  const user = $("loginUserSelect").value.trim() || "admin";
  const password = $("loginPassword").value;
  const result = await api("/api/auth/login", {
    method: "POST",
    headers: { "Content-Type": "application/json; charset=utf-8" },
    body: JSON.stringify({ user, password }),
  });
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
  await api("/api/auth/change-password", {
    method: "POST",
    headers: { "Content-Type": "application/json; charset=utf-8" },
    body: JSON.stringify({
      user: state.pendingPasswordUser,
      oldPassword: state.pendingOldPassword,
      newPassword,
    }),
  });
  await enterApp(state.pendingPasswordUser);
  toast("密码已修改");
}

async function enterApp(user, options = {}) {
  if (options.rememberSession !== false) localStorage.setItem(SESSION_USER_KEY, user);
  await loadUserData(user);
  $("loginView").classList.add("hidden");
  $("appShell").classList.remove("hidden");
  document.body.classList.remove("admin-view");
  setMobileMenu(false);
  setMobileActions(false);
  setMobileTools(false);
  $("adminDashboard").classList.add("hidden");
  $("questionView").classList.remove("hidden");
  state.storage.profile.lastLoginAt = nowText();
  if (!isAdmin()) state.mode = "training";
  await loadCourses();
  if (isAdmin()) {
    renderAdminDashboard();
  } else {
    await restoreLastCourse();
  }
  scheduleSave();
}

async function logout() {
  await saveUserData().catch(() => {});
  stopExamTimer();
  localStorage.removeItem(SESSION_USER_KEY);
  clearTimeout(state.saveTimer);
  state.saveTimer = null;
  state.user = "";
  state.currentCourse = null;
  state.questions = [];
  state.answers = {};
  document.body.classList.remove("is-admin", "admin-view", "picker-open", "mobile-menu-open", "mobile-actions-open", "mobile-tools-open");
  showLogin();
}

async function loadUserData(user) {
  const res = await api(`/api/user/load?user=${encodeURIComponent(user)}`);
  state.user = res.user;
  state.storage = normalizeStorage(res.data || {});
  state.zoom = normalizeZoom(state.storage.settings?.zoom);
  localStorage.setItem(LAST_USER_KEY, state.user);
  renderUsers();
}

function normalizeZoom(value) {
  const zoom = Number(value || 1);
  if (!Number.isFinite(zoom)) return 1;
  return Math.max(0.8, Math.min(1.5, zoom));
}

function normalizeStorage(data) {
  const tags = data.tags && typeof data.tags === "object" ? data.tags : {};
  const tagLabels = [...new Set([...(Array.isArray(data.tagLabels) ? data.tagLabels : Object.keys(tags || {})), ...DEFAULT_TAG_LABELS])];
  return {
    profile: data.profile || {},
    courses: data.courses || {},
    wrong: data.wrong || {},
    favorite: data.favorite || {},
    notes: data.notes || {},
    corrections: Array.isArray(data.corrections) ? data.corrections : [],
    history: Array.isArray(data.history) ? data.history : [],
    examHistory: Array.isArray(data.examHistory) ? data.examHistory : [],
    examDraft: data.examDraft && typeof data.examDraft === "object" ? data.examDraft : null,
    dailyReports: Array.isArray(data.dailyReports) ? data.dailyReports : [],
    dailyActivity: data.dailyActivity && typeof data.dailyActivity === "object" ? data.dailyActivity : {},
    confidence: data.confidence && typeof data.confidence === "object" ? data.confidence : {},
    wrongReasons: data.wrongReasons && typeof data.wrongReasons === "object" ? data.wrongReasons : {},
    smartPractice: data.smartPractice && typeof data.smartPractice === "object" ? data.smartPractice : null,
    trainingSessions: Array.isArray(data.trainingSessions) ? data.trainingSessions : [],
    activeTrainingSessionId: data.activeTrainingSessionId || "",
    tags,
    tagLabels,
    settings: data.settings || {},
    plan: data.plan || null,
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

function scheduleSave() {
  clearTimeout(state.saveTimer);
  state.saveTimer = setTimeout(saveUserData, 450);
}

async function saveUserData() {
  if (!state.user) return;
  await api(`/api/user/save?user=${encodeURIComponent(state.user)}`, {
    method: "POST",
    headers: { "Content-Type": "application/json; charset=utf-8" },
    body: JSON.stringify(state.storage),
  });
}

async function switchUser(name) {
  await saveUserData().catch(() => {});
  localStorage.removeItem(SESSION_USER_KEY);
  localStorage.setItem(LAST_USER_KEY, name);
  $("loginUserSelect").value = name;
  state.user = "";
  document.body.classList.remove("is-admin", "admin-view", "picker-open");
  showLogin();
}

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
  const pickerBtn = $("coursePickerBtn");
  if (pickerBtn) pickerBtn.textContent = state.pickerOpen ? "收起科目" : "切换科目";
  updatePickerHint();
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
  stopExamTimer();
  state.exam = null;
  if (state.currentCourse?.id !== course.id) {
    state.analysisQuestions = [];
    state.analysisCourseId = 0;
    state.examExpandedChapters = new Set();
    state.examSelectedChapters = null;
    state.analysisExpandedChapters = new Set();
  }
  const restoreChapterId = options.restoreChapter ? Number(state.storage.profile.lastChapterId || 0) : 0;
  state.currentCourse = course;
  if (state.storage.smartPractice && Number(state.storage.smartPractice.courseId || 0) !== Number(course.id)) {
    state.storage.smartPractice = null;
    state.smartPracticeFreshStart = false;
  }
  const activeSession = activeTrainingSession();
  if (activeSession && Number(activeSession.courseId || 0) !== Number(course.id)) state.storage.activeTrainingSessionId = "";
  state.currentChapter = null;
  state.currentIndex = -1;
  state.answers = { ...userCourseStore(course.id).answers };
  state.submitted = false;
  state.answerVisible = false;
  state.storage.profile.lastCourseId = course.id;
  state.storage.profile.lastCourseName = course.name;
  state.storage.profile.lastChapterId = 0;
  setText("courseTitle", course.name);
  const courseLabel = [course.category, course.subcategory].filter(Boolean).join(" ");
  setText("courseMeta", `${courseLabel ? `${courseLabel} · ` : ""}${course.questionCount}题 · 更新 ${formatRelativeTime(course.changedAt)}`);
  renderCourses();
  await loadChapters();
  if (restoreChapterId) {
    const chapter = state.chapters.find((item) => item.id === restoreChapterId);
    if (chapter) state.currentChapter = chapter;
    renderChapters();
  }
  await loadTypes();
  await loadQuestions();
  if (!isAdmin() && options.closePicker) setCoursePicker(false);
  updatePickerHint();
  scheduleSave();
}

async function loadChapters() {
  $("chapterList").innerHTML = `<div class="muted">正在读取章节...</div>`;
  state.chapters = await api(`/api/chapters?courseId=${state.currentCourse.id}`);
  state.expandedChapters = new Set();
  state.chapterAutoExpanded = false;
  renderChapters();
}

function renderChapters() {
  $("chapterList").innerHTML = "";
  const all = document.createElement("button");
  all.className = "chapter-item" + (!state.currentChapter ? " active" : "");
  all.textContent = "全部章节";
  all.onclick = () => selectChapter(null);
  $("chapterList").appendChild(all);

  const roots = buildChapterTree(state.chapters);
  state.chapterTreeRoots = roots;
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
    btn.innerHTML = `<span>${escapeHtml(chapter.name)}</span><small>(${chapterTotalCount(chapter)})</small>`;
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
  if (state.mode === "smart") state.mode = "practice";
  state.currentChapter = chapter;
  state.currentIndex = -1;
  state.submitted = false;
  state.answerVisible = false;
  state.storage.profile.lastChapterId = chapter ? chapter.id : 0;
  renderChapters();
  await loadTypes();
  await loadQuestions();
  updatePickerHint();
  scheduleSave();
}

async function loadTypes(options = {}) {
  if (!state.currentCourse) return;
  const params = new URLSearchParams({ courseId: state.currentCourse.id });
  if (!options.ignoreChapter) applyChapterParams(params);
  state.types = await api(`/api/types?${params}`);
  $("typeSelect").innerHTML = `<option value="">全部题型</option>` + state.types.map((t) =>
    `<option value="${t.id}">${escapeHtml(t.name || "题型")} (${t.questionCount})</option>`
  ).join("");
  ensureTagFilterControl();
  renderTagFilterOptions();
}

function ensureTagFilterControl() {
  if ($("tagSelect")) return;
  const select = document.createElement("select");
  select.id = "tagSelect";
  select.title = "按标签筛选";
  select.onchange = () => {
    state.tagFilter = select.value;
    loadQuestions().catch((err) => toast(err.message));
  };
  $("typeSelect").insertAdjacentElement("afterend", select);
}

async function loadQuestions() {
  if (!state.currentCourse) return;
  if (state.mode === "training") {
    setPanelPage(true);
    state.questions = [];
    state.currentIndex = -1;
    state.answerVisible = false;
    state.answerCardPage = 0;
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
    await loadAnalysisQuestions();
    renderAll();
    return;
  }
  setPanelPage(false);
  if (state.mode === "exam" && state.exam && !state.submitted) {
    renderSearchMatches();
    return;
  }
  state.answerCardPage = 0;
  const orderValue = $("orderSelect").value;
  const courseStore = userCourseStore();
  if (state.mode !== "exam") state.answers = { ...courseStore.answers };
  const effectiveLimit = effectiveQuestionLimit();
  const params = new URLSearchParams({
    courseId: state.currentCourse.id,
    limit: String(effectiveLimit),
  });
  applyChapterParams(params);
  if ($("typeSelect").value) params.set("typeId", $("typeSelect").value);
  if (orderValue && orderValue !== "random") params.set("order", orderValue);
  const q = $("questionSearch").value.trim();
  if (q) params.set("q", q);
  const idFilter = getModeQuestionIds(courseStore);
  const tagIds = getTagFilterIds();
  const mergedIds = intersectIdFilters(idFilter, tagIds);
  if (mergedIds) {
    if (!mergedIds.length) {
      state.questions = [];
      state.currentIndex = -1;
      state.answerCardPage = 0;
      renderAll();
      return;
    }
    params.set("ids", mergedIds.join(","));
    params.set("limit", String(Math.max(mergedIds.length, effectiveLimit)));
  }

  let items = await api(`/api/questions?${params}`);
  if (state.mode === "smart" && mergedIds) items = orderItemsByIds(items, mergedIds);
  const shouldShuffle = orderValue === "random" || state.mode === "exam";
  const shuffleKey = JSON.stringify({
    courseId: state.currentCourse.id,
    chapterId: state.currentChapter?.id || 0,
    typeId: $("typeSelect").value || "",
    q,
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

  state.questions = items;
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
  if (state.currentIndex >= 0 && state.mode !== "progress") await loadCurrentQuestion();
}

async function loadAnalysisQuestions() {
  if (!state.currentCourse) return;
  if (state.analysisCourseId === Number(state.currentCourse.id) && state.analysisQuestions.length) return;
  const params = new URLSearchParams({
    courseId: state.currentCourse.id,
    limit: "30000",
  });
  state.analysisQuestions = await api(`/api/questions?${params}`);
  state.analysisCourseId = Number(state.currentCourse.id);
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

function getTagFilterIds() {
  const label = state.tagFilter || $("tagSelect")?.value || "";
  if (!label) return null;
  return (state.storage.tags?.[label] || []).map(Number).filter(Boolean);
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

function getModeQuestionIds(courseStore) {
  if (state.mode === "smart") return getSmartPracticeIds(courseStore);
  if (state.mode === "wrong") {
    let ids = getWrongRecordsForCurrentCourse(courseStore)
      .filter(([id, item]) => wrongRecordMatchesFilter(id, item, courseStore))
      .map(([id]) => Number(id))
      .filter(Boolean);
    const session = activeTrainingSession();
    if (session?.mode === "wrong" && Array.isArray(session.ids) && session.ids.length) {
      const allowed = new Set(session.ids.map(Number));
      ids = ids.filter((id) => allowed.has(Number(id)));
    }
    return ids;
  }
  if (state.mode === "favorite") {
    return Object.entries(state.storage.favorite || {})
      .filter(([, item]) => Number(item?.courseId || 0) === Number(state.currentCourse.id))
      .map(([id]) => Number(id))
      .filter(Boolean);
  }
  return null;
}

function getSmartPracticeIds(courseStore = userCourseStore()) {
  const saved = state.storage.smartPractice;
  if (saved && Number(saved.courseId || 0) === Number(state.currentCourse?.id || 0) && Array.isArray(saved.ids) && saved.ids.length) {
    return saved.ids.map(Number).filter(Boolean);
  }
  const ids = buildSmartPracticeIds(courseStore);
  saveSmartPracticeSession(ids);
  return ids;
}

function buildSmartPracticeIds(courseStore = userCourseStore(), options = {}) {
  const ids = [];
  const randomize = !!options.randomize;
  const prepare = (items) => randomize ? shuffleItems(items) : items;
  const pushUnique = (id) => {
    const n = Number(id);
    if (n && !ids.includes(n)) ids.push(n);
  };
  const pushMany = (items, limit = items.length) => {
    prepare(items).slice(0, limit).forEach(pushUnique);
  };
  const allItems = state.analysisQuestions.length ? state.analysisQuestions : state.questions;
  pushMany(getWrongRecordsForCurrentCourse(courseStore)
    .filter(([, item]) => isReviewDue(item) || Number(item.count || 0) >= 2)
    .map(([id]) => id), 20);
  pushMany(Object.keys(courseStore.wrong || {}));
  pushMany(Object.keys(courseStore.done || {})
    .filter((id) => !courseStore.correct?.[id]));
  getWeakChapterRows(courseStore, allItems).slice(0, 5).forEach((chapter) => {
    pushMany(allItems
      .filter((item) => chapterPathForId(item.chapterId).slice(0, 2).some((node) => Number(node.id) === Number(chapter.id)))
      .filter((item) => !courseStore.correct?.[item.id])
      .map((item) => item.id), 10);
  });
  if (ids.length >= 30) return ids.slice(0, 60);
  pushMany(allItems
    .filter((item) => !courseStore.done?.[item.id])
    .map((item) => item.id), 60 - ids.length);
  return ids.slice(0, 60);
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

function findActiveTrainingSessionByType(type, courseId = state.currentCourse?.id) {
  const sessions = getTrainingSessionsForCourse(courseId);
  return sessions.find((session) => {
    if (session.type !== type) return false;
    syncTrainingSessionStats(session);
    return session.status !== "completed" && normalizeSessionIds(session.ids).length;
  }) || null;
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
  const courseStore = peekCourseStore(session.courseId || state.currentCourse?.id);
  const ids = normalizeSessionIds(session.ids);
  const done = ids.filter((id) => courseStore.done?.[id] || normalizeAnswer(courseStore.answers?.[id]).length).length;
  const verified = ids.filter((id) => courseStore.verified?.[id]).length;
  const correct = ids.filter((id) => courseStore.correct?.[id]).length;
  const wrong = ids.filter((id) => courseStore.wrong?.[id] || state.storage.wrong?.[id]).length;
  session.total = ids.length;
  session.done = done;
  session.verified = verified;
  session.correct = correct;
  session.wrong = wrong;
  session.rate = verified ? Math.round((correct / verified) * 100) : done ? Math.round((correct / done) * 100) : 0;
  if (done >= ids.length && ids.length) {
    session.status = "completed";
    session.completedAt ||= nowText();
    if (session.type === "plan" && Number(session.courseId || 0) === Number(state.currentCourse?.id || 0)) {
      state.storage.plan ||= { dailyLog: {} };
      state.storage.plan.dailyLog ||= {};
      const key = session.planKey || dateKey(parseDate(session.createdAt) || new Date());
      state.storage.plan.dailyLog[key] ||= {
        newDone: Number(session.taskCounts?.new || 0),
        reviewDone: Number(session.taskCounts?.review || 0) + Number(session.taskCounts?.carryover || 0),
        weakDone: Number(session.taskCounts?.weak || 0),
        at: session.completedAt,
      };
    }
  } else if (session.status === "completed" && done < ids.length) {
    session.status = "active";
    session.completedAt = "";
  }
  return session;
}

function syncCurrentTrainingSession(questionId = 0) {
  const session = activeTrainingSession();
  if (!session || Number(session.courseId || 0) !== Number(state.currentCourse?.id || 0)) return;
  session.updatedAt = nowText();
  if (questionId) session.lastSubjectId = Number(questionId);
  session.currentIndex = Math.max(0, state.currentIndex);
  syncTrainingSessionStats(session);
}

function clearActiveTrainingSession() {
  state.storage.activeTrainingSessionId = "";
  scheduleSave();
}

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
  });
  state.storage.smartPractice = {
    sessionId: session.id,
    courseId: state.currentCourse?.id || 0,
    courseName: state.currentCourse?.name || "",
    sourceTitle,
    ids: cleanIds,
    createdAt: session.createdAt,
  };
  state.smartPracticeFreshStart = true;
  scheduleSave();
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
  const item = state.questions[state.currentIndex];
  if (!item) return;
  item.detail = item.detail || await api(`/api/question?id=${item.id}`);
  if (state.mode === "exam") saveExamDraft();
  else {
    userCourseStore().lastSubjectId = item.id;
    syncCurrentTrainingSession(item.id);
  }
  scheduleSave();
  state.answerVisible = state.submitted || isQuestionVerified(item.id);
  renderQuestion();
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
    const message = state.mode === "wrong" && (state.wrongFilters.review || "due") === "due"
      ? ["当前无待复习错题", "可以切换为全部错题，或稍后按复习计划回来。"]
      : ["当前范围没有题目", "可以换章节、题型或清空搜索条件。"];
    $("emptyState").innerHTML = `<strong>${message[0]}</strong><span>${message[1]}</span>`;
  }
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
  ["questionTagPanel", "examReviewPanel", "answerBox", "noteEditor"].forEach((id) => {
    const el = $(id);
    if (el && el.parentNode !== panel) panel.appendChild(el);
  });
  panel.classList.remove("hidden");
  return panel;
}

function renderQuestion() {
  const item = state.questions[state.currentIndex];
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
  setText("answerToggleBtn", revealAnswer && state.answerVisible ? "隐藏答案" : "显示答案");
  setText("favoriteBtn", isFavorite(q.id) ? "取消收藏" : "收藏本题");
  setText("wrongBtn", state.mode === "wrong" ? "移出错题" : "纠错");
  $("noteEditor").value = getNote(q.id);
  renderOptions(q);
  ensureQuestionSidePanel();
  renderQuestionTags(q);
  renderExamReview(q);
  ensureQuestionSidePanel();
  renderExamStatus();
  renderVerifyModeControls();
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

function renderQuestionTags(q) {
  let panel = $("questionTagPanel");
  if (!panel) {
    panel = document.createElement("div");
    panel.id = "questionTagPanel";
    panel.className = "question-tag-panel";
    $("answerBox").parentNode.insertBefore(panel, $("answerBox"));
  }
  const currentTags = questionTags(q.id);
  const tagSummary = currentTags.length ? currentTags.join("、") : "未添加";
  const confidence = state.storage.confidence?.[q.id] || "";
  const wrongReason = questionWrongReason(q.id);
  const favoriteGroup = favoriteGroupOf(q.id);
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
          ${renderToggleGroup(DEFAULT_TAG_LABELS, "data-toggle-tag", currentTags)}
          ${currentTags.filter((label) => !DEFAULT_TAG_LABELS.includes(label)).map((label) => `<button type="button" class="quick-mark active" data-toggle-tag="${escapeHtml(label)}">${escapeHtml(label)} ×</button>`).join("")}
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
  setMobileTagsOpen(false);
  $("tagPanelToggleBtn").onclick = () => setMobileTagsOpen(!state.mobileTagsOpen);
  panel.querySelectorAll("[data-toggle-tag]").forEach((btn) => {
    btn.onclick = () => toggleQuestionTag(q.id, btn.dataset.toggleTag);
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

function applyNoteTemplate(questionId) {
  const editor = $("noteEditor");
  if (!editor) return;
  const current = editor.value.trim();
  if (!current) editor.value = NOTE_TEMPLATE;
  else if (!current.includes("考点：") && !current.includes("易错点：")) editor.value = `${NOTE_TEMPLATE}\n${current}`;
  editor.classList.remove("hidden");
  saveNote();
  editor.focus();
  toast("已套用笔记模板");
}

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

function renderTagFilterOptions() {
  const select = $("tagSelect");
  if (!select) return;
  const previous = state.tagFilter || select.value || "";
  select.innerHTML = `<option value="">全部标签</option>` + tagLabels().map((label) =>
    `<option value="${escapeHtml(label)}" ${label === previous ? "selected" : ""}>${escapeHtml(label)}</option>`
  ).join("");
  select.value = previous;
}

function renderExamReview() {
  $("examReviewPanel")?.remove();
}

function renderWrongFilters() {
  let panel = $("wrongFilterPanel");
  if (state.mode !== "wrong" || isAdmin()) {
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
  panel.innerHTML = `
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
  const smart = state.storage.smartPractice || {};
  const session = syncTrainingSessionStats(activeSession);
  const title = session?.sourceTitle || smart.sourceTitle || "智能练习";
  const createdAt = session?.createdAt || smart.createdAt || "";
  panel.innerHTML = `
    <div>
      <span>${escapeHtml(title)}</span>
      <b>${state.questions.length} 题 · 已做 ${session?.done ?? done} · 已确认 ${session?.verified ?? verified} · 正确率 ${session?.rate ?? 0}%</b>
      <small>${escapeHtml(session?.courseName || smart.courseName || state.currentCourse.name || "")}${createdAt ? ` · 生成于 ${escapeHtml(createdAt)}` : ""}</small>
    </div>
    <button class="practice-context-toggle" id="practiceContextToggleBtn" type="button">${isPracticeContextCollapsed() ? "展开" : "收起"}</button>
    <div class="practice-context-actions">
      ${state.mode === "smart" ? `<button type="button" id="regenerateSmartBtn">重新生成</button>` : ""}
      <button type="button" id="exitSmartBtn">${state.mode === "smart" ? "退出智能练习" : "退出本次复习"}</button>
    </div>
  `;
  panel.classList.toggle("collapsed", isPracticeContextCollapsed());
  $("practiceContextToggleBtn").onclick = () => {
    state.practiceContextCollapsed = !isPracticeContextCollapsed();
    renderPracticeContextPanel();
  };
  if ($("regenerateSmartBtn")) $("regenerateSmartBtn").onclick = () => startSmartPractice({ force: true }).catch((err) => toast(err.message));
  $("exitSmartBtn").onclick = () => exitTrainingSession().catch((err) => toast(err.message));
}

function isPracticeContextCollapsed() {
  if (state.practiceContextCollapsed === null) {
    return window.matchMedia?.("(max-width: 760px)")?.matches || false;
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
  await loadCurrentQuestion();
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
  for (const option of q.options || []) {
    const el = document.createElement("button");
    el.className = "option";
    el.dataset.label = option.label;
    if (selected.has(option.label)) el.classList.add("selected");
    if (shouldRevealCurrentAnswer(q)) {
      if ((q.answer || "").includes(option.label)) el.classList.add("correct");
      if (selected.has(option.label) && !(q.answer || "").includes(option.label)) el.classList.add("wrong");
    }
    el.innerHTML = `<b>${escapeHtml(option.label)}</b><span>${escapeHtml(option.text)}</span>`;
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
  $("printView").innerHTML = `
    <div class="print-document">
      <header class="print-document-head">
        <strong>云习题库</strong>
        <span>${escapeHtml(scopeLabel)} · ${escapeHtml(state.currentCourse?.name || "")}</span>
      </header>
      ${questions.map((q, index) => renderPrintQuestion(q, index + 1, questions.length)).join("")}
      ${includeAnswers ? renderPrintAnswerSection(questions) : ""}
    </div>
  `;
  $("printView").classList.remove("hidden");
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

function cleanupPrintView() {
  state.printAfterRender = false;
  document.body.classList.remove("print-mode");
  $("printView")?.classList.add("hidden");
  if ($("printView")) $("printView").innerHTML = "";
}

function saveSubjectiveAnswer(q, answer) {
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
  courseStore.answers[q.id] = answer;
  resetQuestionVerification(q.id);
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
  renderVerifyModeControls();
  renderAnswerCardPage({ updateNav: false });
  updateStats();
}

function chooseOption(q, label) {
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
  courseStore.answers[q.id] = answer;
  resetQuestionVerification(q.id);
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
  renderQuestion();
}

function markResult(q) {
  const courseStore = userCourseStore();
  const isRight = isAnswerCorrect(q);
  if (isRight) {
    courseStore.correct[q.id] = true;
    delete courseStore.wrong[q.id];
    if (state.mode === "wrong") {
      const record = ensureWrongReviewFields(state.storage.wrong[q.id] || {}, q);
      record.lastReviewAt = nowText();
      record.stage = Math.min(REVIEW_INTERVAL_DAYS.length - 1, reviewStage(record) + 1);
      record.resolved = record.stage >= REVIEW_INTERVAL_DAYS.length - 1;
      record.resolvedAt = record.resolved ? nowText() : "";
      state.storage.wrong[q.id] = record;
      if (record.resolved) delete courseStore.wrong[q.id];
      else courseStore.wrong[q.id] = true;
    } else {
      delete courseStore.wrong[q.id];
      if (state.storage.wrong[q.id]) {
        state.storage.wrong[q.id].resolved = true;
        state.storage.wrong[q.id].resolvedAt = nowText();
        state.storage.wrong[q.id].lastReviewAt = nowText();
      }
    }
  } else {
    delete courseStore.correct[q.id];
    courseStore.wrong[q.id] = true;
    const record = ensureWrongReviewFields(state.storage.wrong[q.id] || {}, q);
    record.at = nowText();
    record.wrongAt = nowText();
    record.lastReviewAt = nowText();
    record.stage = 0;
    record.count = Number(record.count || 0) + 1;
    record.resolved = false;
    record.resolvedAt = "";
    state.storage.wrong[q.id] = record;
  }
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
  for (let index = startIndex; index < endIndex; index++) {
    const item = state.questions[index];
    const btn = document.createElement("button");
    btn.textContent = index + 1;
    btn.className = "card-cell";
    btn.dataset.questionId = item.id;
    if (index === state.currentIndex) btn.classList.add("current");
    if (Number(item.id) === Number(state.lastMarkedQuestionId)) btn.classList.add("just-marked");
    if (matched.has(index)) btn.classList.add("search-match");
    if (hasAnswer(item.id)) btn.classList.add("done");
    if (state.submitted || isQuestionVerified(item.id)) {
      if (!hasAnswer(item.id)) btn.classList.add("wrong");
      else if (item.detail) btn.classList.add(isAnswerCorrect(item.detail) ? "correct" : "wrong");
    }
    btn.onclick = async () => {
      state.currentIndex = index;
      state.answerCardPage = Math.floor(index / state.answerCardPageSize);
      renderAnswerCardPage({ updateNav: false });
      await loadCurrentQuestion();
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
  const done = state.questions.filter((q) => hasAnswer(q.id)).length;
  setText("totalCount", total);
  setText("doneCount", done);
  setText("todoCount", Math.max(0, total - done));
}

function renderMode() {
  const visualMode = state.mode === "smart" ? "training" : state.mode;
  document.querySelectorAll(".nav-item[data-mode]").forEach((btn) => {
    btn.classList.toggle("active", btn.dataset.mode === visualMode);
  });
  document.body.dataset.mode = state.mode;
  scheduleNavIndicator();
  renderVerifyModeControls();
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

function currentVerifyMode() {
  const stored = state.storage.settings?.verifyMode;
  if (stored === "single") return "instant";
  return ["paper", "instant", "review"].includes(stored) ? stored : "paper";
}

function isQuestionVerified(questionId) {
  if (state.mode === "exam") return false;
  const verified = userCourseStore().verified?.[questionId];
  if (!verified) return false;
  if (verified === true) return true;
  if (typeof verified === "object" && "answer" in verified) {
    return verified.answer === currentAnswerSnapshot(questionId);
  }
  return false;
}

function shouldRevealCurrentAnswer(q) {
  return !!(state.answerVisible || state.submitted || currentVerifyMode() === "review" || isQuestionVerified(q?.id));
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
    modeBtn.classList.toggle("hidden", isExam || isAdmin());
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
    submitBtn.textContent = state.mode === "exam" ? "提交答卷" : "提交答卷";
    submitBtn.title = state.mode === "exam"
      ? "交卷并计算模拟考试成绩"
      : "统一验证本次练习中的所有题目";
  }
}

function toggleVerifyMode() {
  state.storage.settings ||= {};
  const modes = ["paper", "instant", "review"];
  const current = currentVerifyMode();
  const next = modes[(Math.max(0, modes.indexOf(current)) + 1) % modes.length];
  state.storage.settings.verifyMode = next;
  state.verifyMode = next;
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
  courseStore.verified[q.id] = {
    answer: currentAnswerSnapshot(q.id),
    at: nowText(),
  };
}

function resetQuestionVerification(questionId) {
  if (state.mode === "exam" || !questionId) return;
  const courseStore = userCourseStore();
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
  const favTotal = Object.keys(state.storage.favorite || {}).length;
  const noteTotal = Object.keys(state.storage.notes || {}).filter((id) => state.storage.notes[id]).length;
  const rate = stats.done ? Math.round((stats.correct / stats.done) * 100) : 0;

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
        <div><b>${rate}%</b><span>正确率</span></div>
        <div><b>${wrongTotal}</b><span>待解决错题</span></div>
        <div><b>${favTotal}</b><span>收藏题</span></div>
        <div><b>${noteTotal}</b><span>笔记</span></div>
        <div><b>${analysisItems.length}</b><span>当前科目题量</span></div>
      </div>
      <div class="trend-panel">
        <div class="trend-title">近期正确率趋势</div>
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
  const due = getWrongRecordsForCurrentCourse(courseStore).filter(([, item]) => isReviewDue(item)).length;
  const weak = getWeakChapterRows(courseStore, items).slice(0, 3);
  const smart = state.storage.smartPractice;
  const canContinueSmart = smart && Number(smart.courseId || 0) === Number(state.currentCourse?.id || 0) && Array.isArray(smart.ids) && smart.ids.length;
  const analysisReady = state.analysisCourseId === Number(state.currentCourse?.id || 0) && state.analysisQuestions.length;
  const previewContext = { items, stats, due, weak, analysisReady };
  const smartPreview = previewPracticePlan("smart", courseStore, previewContext);
  const similarPreview = previewPracticePlan("similar", courseStore, previewContext);
  const sprintPreview = previewPracticePlan("sprint", courseStore, previewContext);
  const activeSmartSession = findActiveTrainingSessionByType("smart");
  const hasReviewPlan = !!activeReviewPlan().examDate;
  const planOverview = hasReviewPlan
    ? buildReviewPlanOverview(stats, Object.keys(courseStore.wrong || {}).length, items)
    : { hasPlan: false, task: { ids: [], counts: {} }, days: 0, debt: 0, todayCompleted: false, todaySession: null };
  const mainAdvice = due
    ? `优先复习 ${due} 道到期错题`
    : weak[0]
      ? `优先强化 ${weak[0].name}`
      : today.total
        ? "继续做一组今日强化"
        : "建议从今日强化开始";
  const adviceDetail = due
    ? "这些题已经到复习周期，先处理能减少反复遗忘。"
    : weak[0]
      ? `当前薄弱章节正确率 ${weak[0].rate}%，系统会优先混入相关错题和未做题。`
      : "系统会按错题、薄弱章节和未做题自动生成练习。";
  $("questionBody").classList.add("hidden");
  $("emptyState").classList.remove("hidden");
  $("emptyState").innerHTML = `
    <div class="training-panel">
      <div class="training-hero">
        <div>
          <strong>智能训练</strong>
          <p>${escapeHtml(state.currentCourse?.name || "当前科目")} · 今日已练 ${today.total} 题 · 确认正确率 ${today.rate}%</p>
        </div>
        <button class="primary-action" id="trainingSmartBtn" type="button">${activeSmartSession ? "继续今日强化" : "开始今日强化"}</button>
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
          <button id="trainingPlanBtn" type="button" ${planOverview.task.ids.length && !planOverview.todayCompleted ? "" : "disabled"}>${planOverview.todaySession && !planOverview.todayCompleted ? "继续今日计划" : planOverview.todayCompleted ? "今日计划已完成" : "开始今日计划"}</button>
        </div>
      ` : ""}
      <div class="training-summary-grid">
        <div><b>${due}</b><span>到期错题</span></div>
        <div><b>${today.total}</b><span>今日已练</span></div>
        <div><b>${weak[0] ? weak[0].rate : 0}%</b><span>${weak[0] ? escapeHtml(weak[0].name) : "暂无薄弱章节"}</span></div>
        <div><b>${Math.max(0, items.length - stats.done)}</b><span>未做题</span></div>
      </div>
      <div class="training-card-grid">
        ${renderTrainingCard("smart", "今日强化", "错题、薄弱章节和未做题自动混合，适合每天打开就练。", smartPreview, "开始训练")}
        ${renderTrainingCard("due", "今日错题复习", "按记忆周期推送今天该复习的错题，避免错题堆积。", `${due} 道到期`, "开始复习")}
        ${renderTrainingCard("similar", "相似错题重练", "围绕反复错的章节、题型和关键词抽同类题。", similarPreview, "开始重练")}
        ${renderTrainingCard("sprint", "考前冲刺", "近 7 天错题、反复错、薄弱章节、未做题组合。", sprintPreview, "开始冲刺")}
      </div>
      <div class="training-tools">
        <button type="button" id="trainingContinueSmartBtn" ${canContinueSmart ? "" : "disabled"}>继续上次：${canContinueSmart ? escapeHtml(smart.sourceTitle || "智能训练") : "暂无题单"}</button>
        <button type="button" id="trainingDashboardBtn">查看学习看板</button>
        <button type="button" id="trainingWrongBtn">查看错题本</button>
        <button type="button" id="trainingPracticeBtn">返回考试题库</button>
      </div>
    </div>
  `;
  bindTrainingActions();
}

function renderTrainingCard(type, title, desc, meta, action) {
  return `
    <section class="training-card">
      <div>
        <span>${escapeHtml(title)}</span>
        <p>${escapeHtml(desc)}</p>
      </div>
      <strong>${escapeHtml(meta || "自动生成")}</strong>
      <button type="button" data-training-action="${type}">${escapeHtml(action)}</button>
    </section>
  `;
}

function previewPracticePlan(type, courseStore, context = {}) {
  const items = context.items || (state.analysisQuestions.length ? state.analysisQuestions : state.questions);
  const loading = !context.analysisReady && state.analysisLoadingCourseId === Number(state.currentCourse?.id || 0);
  const doneCount = Object.keys(courseStore.done || {}).length;
  const wrongRecords = getWrongRecordsForCurrentCourse(courseStore);
  const dueCount = Number(context.due || 0);
  const weakCount = Array.isArray(context.weak) ? context.weak.length : 0;
  const undoneCount = items.length ? Math.max(0, items.length - doneCount) : 0;
  if (loading && !items.length) return "正在计算";
  if (type === "smart") {
    const estimate = Math.min(60, Math.max(0, dueCount + wrongRecords.length + weakCount * 8 + Math.min(undoneCount, 20)));
    return estimate ? `${estimate} 题 · 错题优先` : "自动生成 · 错题优先";
  }
  if (type === "similar") {
    const repeated = wrongRecords.filter(([, item]) => Number(item.count || 0) >= 2).length;
    const estimate = Math.min(80, repeated ? repeated * 6 : wrongRecords.length * 4);
    return `${estimate || 0} 题 · 同章同型`;
  }
  if (type === "sprint") {
    const estimate = Math.min(100, dueCount + wrongRecords.length + weakCount * 10 + Math.min(undoneCount, 40));
    return estimate ? `${estimate} 题 · 冲刺组合` : "自动生成 · 冲刺组合";
  }
  return "";
}

function bindTrainingActions() {
  $("trainingSmartBtn").onclick = () => startSmartPractice().catch((err) => toast(err.message));
  $("trainingContinueSmartBtn").onclick = () => continueSmartPractice().catch((err) => toast(err.message));
  if ($("trainingPlanBtn")) $("trainingPlanBtn").onclick = () => startTodayReviewPlan().catch((err) => toast(err.message));
  $("trainingDashboardBtn").onclick = async () => {
    state.mode = "progress";
    setCoursePicker(false);
    renderMode();
    await loadQuestions();
  };
  $("trainingWrongBtn").onclick = async () => {
    clearActiveTrainingSession();
    state.mode = "wrong";
    await loadQuestions();
  };
  $("trainingPracticeBtn").onclick = async () => {
    clearActiveTrainingSession();
    state.mode = "practice";
    await loadQuestions();
  };
  document.querySelectorAll("[data-training-action]").forEach((btn) => {
    btn.onclick = () => {
      const action = btn.dataset.trainingAction;
      if (action === "smart") startSmartPractice().catch((err) => toast(err.message));
      if (action === "due") startDueWrongReview().catch((err) => toast(err.message));
      if (action === "similar") startSimilarWrongPractice().catch((err) => toast(err.message));
      if (action === "sprint") startSprintPractice().catch((err) => toast(err.message));
    };
  });
}

function renderStudyDashboard(courseStore, stats, items) {
  const records = getWrongRecordsForCurrentCourse(courseStore);
  const due = records.filter(([, item]) => isReviewDue(item)).length;
  const weak = getWeakChapterRows(courseStore, items).slice(0, 3);
  const report = getTodayReport() || buildDailyReport(courseStore, stats, items);
  const today = getTodayActivity();
  const reportText = report
    ? `已记录快照：${report.done} 题 · 正确率 ${report.rate}% · ${escapeHtml(report.focus || "继续保持")}`
    : buildStudyReportSummary(courseStore, stats, items).summary;
  const smart = state.storage.smartPractice;
  const canContinueSmart = smart && Number(smart.courseId || 0) === Number(state.currentCourse?.id || 0) && Array.isArray(smart.ids) && smart.ids.length;
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
        <small>已确认 ${today.verified} 题 · 答对 ${today.correct} 题 · ${today.rate}%</small>
      </div>
      <div class="study-card">
        <span>薄弱章节</span>
        <b>${weak[0] ? escapeHtml(weak[0].name) : "暂无明显短板"}</b>
        <small>${weak.map((item) => `${item.name} ${item.rate}%`).join(" · ") || "继续积累做题数据后会更准"}</small>
      </div>
      <div class="study-card">
        <span>学习日报</span>
        <b>${report.todayTotal || today.total} 题</b>
        <small>${escapeHtml(report.tomorrow || "明天继续保持节奏")}</small>
        <button id="dailyReportBtn" type="button">更新日报</button>
      </div>
      <div class="study-card ${canContinueSmart ? "" : "muted-card"}">
        <span>智能练习</span>
        <b>${canContinueSmart ? `${smart.ids.length} 题可继续` : "暂无练习批次"}</b>
        <button id="continueSmartBtn" type="button" ${canContinueSmart ? "" : "disabled"}>继续上次</button>
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
}

function renderTrainingHistoryPanel(courseStore) {
  const sessions = getTrainingSessionsForCourse().map((session) => syncTrainingSessionStats(session)).filter(Boolean);
  const recent = sessions.slice(0, 8);
  const finished = sessions.filter((item) => item.status === "completed").length;
  const active = sessions.filter((item) => item.status !== "completed").length;
  const totalDone = sessions.reduce((sum, item) => sum + Number(item.done || 0), 0);
  const totalCorrect = sessions.reduce((sum, item) => sum + Number(item.correct || 0), 0);
  const totalVerified = sessions.reduce((sum, item) => sum + Number(item.verified || 0), 0);
  const overallRate = totalVerified ? Math.round(totalCorrect / totalVerified * 100) : 0;
  return `
    <div class="training-history-panel">
      <div class="training-history-head">
        <div>
          <span>个人训练看板</span>
          <strong>${sessions.length} 套训练 · ${active} 套进行中 · ${finished} 套已完成</strong>
        </div>
        <b>${totalDone} 题 · ${overallRate}%</b>
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
  const canContinue = total && progress < 100;
  return `
    <section class="training-history-row ${isActive ? "active" : ""}">
      <div class="training-history-main">
        <span>${escapeHtml(session.sourceTitle || "智能训练")}</span>
        <strong>${done}/${total} 题 · 正确率 ${Number(session.rate || 0)}% · 错题 ${Number(session.wrong || 0)}</strong>
        <small>${escapeHtml(session.courseName || "")} · ${escapeHtml(session.createdAt || "")}${session.completedAt ? ` · 完成 ${escapeHtml(session.completedAt)}` : ""}</small>
      </div>
      <div class="training-history-progress" aria-hidden="true"><i style="width:${Math.max(2, progress)}%"></i></div>
      <button type="button" data-training-session="${escapeHtml(session.id)}" ${canContinue ? "" : "disabled"}>${canContinue ? "继续" : "已完成"}</button>
    </section>
  `;
}

function bindTrainingHistoryActions() {
  document.querySelectorAll("[data-training-session]").forEach((btn) => {
    btn.onclick = () => continueTrainingSession(btn.dataset.trainingSession).catch((err) => toast(err.message));
  });
}

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
  state.storage.activeTrainingSessionId = session.id;
  resetGeneratedPracticeFilters();
  if (session.mode === "wrong") {
    state.mode = "wrong";
    state.wrongFilters = { ...(state.wrongFilters || {}), review: session.type === "due" ? "due" : "all", status: "active" };
    setCoursePicker(false);
    await loadQuestions();
    toast(`已继续${session.sourceTitle || "错题复习"}`);
    return;
  }
  state.storage.smartPractice = {
    sessionId: session.id,
    courseId: session.courseId,
    courseName: session.courseName || "",
    sourceTitle: session.sourceTitle || "智能训练",
    ids: normalizeSessionIds(session.ids),
    createdAt: session.createdAt || nowText(),
  };
  state.mode = "smart";
  setCoursePicker(false);
  await loadQuestions();
  toast(`已继续${session.sourceTitle || "智能训练"}`);
}

function getTodayReport() {
  const key = todayKey();
  return (state.storage.dailyReports || []).find((item) => item.key === key);
}

function buildStudyReportSummary(courseStore, stats, items) {
  const rate = stats.done ? Math.round(stats.correct / stats.done * 100) : 0;
  const weak = getWeakChapterRows(courseStore, items)[0];
  const due = getWrongRecordsForCurrentCourse(courseStore).filter(([, item]) => isReviewDue(item)).length;
  const focus = weak ? `优先练 ${weak.name}` : due ? "优先复习到期错题" : "保持当前节奏";
  return {
    done: stats.done,
    correct: stats.correct,
    wrong: stats.wrong,
    rate,
    due,
    focus,
    summary: `当前进度快照：累计 ${stats.done} 题 · 正确率 ${rate}% · ${focus}`,
  };
}

function buildDailyReport(courseStore, stats, items) {
  const base = buildStudyReportSummary(courseStore, stats, items);
  const today = getTodayActivity();
  const weak = getWeakChapterRows(courseStore, items)[0];
  const due = getWrongRecordsForCurrentCourse(courseStore).filter(([, item]) => isReviewDue(item)).length;
  const tomorrow = weak
    ? `明天优先练「${weak.name}」+ 到期错题 ${due} 道`
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

function getWeakChapterRows(courseStore, items) {
  const answeredIds = new Set(Object.keys(courseStore.done || {}).map(Number));
  const correctIds = new Set(Object.keys(courseStore.correct || {}).map(Number));
  const byChapter = new Map();
  items.forEach((item) => {
    if (!answeredIds.has(Number(item.id))) return;
    const path = chapterPathForId(item.chapterId).slice(0, 2);
    const node = path[1] || path[0] || { id: item.chapterId || 0, name: item.chapterName || "未分章节" };
    const id = Number(node.id || 0);
    if (!id) return;
    const row = byChapter.get(id) || { id, name: node.name || "未分章节", done: 0, correct: 0 };
    row.done++;
    if (correctIds.has(Number(item.id))) row.correct++;
    byChapter.set(id, row);
  });
  return [...byChapter.values()]
    .filter((row) => row.done >= 3)
    .map((row) => ({ ...row, rate: Math.round(row.correct / row.done * 100) }))
    .sort((a, b) => a.rate - b.rate || b.done - a.done);
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

function buildFavoriteGroupRows(courseStore) {
  const counts = new Map();
  const validCourseId = Number(state.currentCourse?.id || 0);
  Object.entries(state.storage.favorite || {}).forEach(([id, item]) => {
    if (validCourseId && Number(item?.courseId || 0) !== validCourseId) return;
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
    resetGeneratedPracticeFilters();
    saveSmartPracticeSession(match.ids.slice(0, 80), `收藏：${label}`, { type: "favorite" });
    state.mode = "smart";
    setCoursePicker(false);
    await loadQuestions();
    toast(`已生成「${label}」收藏练习`);
    return;
  }
  const source = type === "confidence" ? state.storage.confidence : state.storage.wrongReasons;
  const rows = buildSignalRows(source || {}, courseStore, type);
  const match = rows.find((row) => row.label === label);
  if (!match?.ids?.length) {
    toast("当前科目没有匹配题目");
    return;
  }
  resetGeneratedPracticeFilters();
  saveSmartPracticeSession(match.ids.slice(0, 80), `${type === "confidence" ? "信心" : "错因"}：${label}`, { type });
  state.mode = "smart";
  setCoursePicker(false);
  await loadQuestions();
  toast(`已生成「${label}」专项练习`);
}

async function startSmartPractice(options = {}) {
  if (!state.currentCourse) return;
  if (!options.force) {
    const activeSmart = findActiveTrainingSessionByType("smart");
    if (activeSmart) {
      await continueTrainingSession(activeSmart.id);
      return;
    }
  }
  if (!state.analysisQuestions.length) await loadAnalysisQuestions();
  if (options.force) state.storage.smartPractice = null;
  const ids = buildSmartPracticeIds(userCourseStore(), { randomize: !!options.force });
  if (!ids.length) {
    toast("暂无可生成的智能练习");
    return;
  }
  resetGeneratedPracticeFilters();
  saveSmartPracticeSession(ids, "今日强化", { type: "smart" });
  state.mode = "smart";
  setCoursePicker(false);
  await loadQuestions();
  toast("已生成智能练习");
}

async function exitSmartPractice() {
  if (state.mode !== "smart") return;
  syncCurrentTrainingSession();
  state.mode = "practice";
  state.storage.smartPractice = null;
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

async function continueSmartPractice() {
  if (!state.currentCourse) return;
  const smart = state.storage.smartPractice;
  if (!smart || Number(smart.courseId || 0) !== Number(state.currentCourse.id) || !Array.isArray(smart.ids) || !smart.ids.length) {
    toast("没有可继续的智能练习");
    return;
  }
  if (!smart.sessionId) {
    const session = upsertTrainingSession({
      id: trainingSessionId("smart"),
      type: "smart",
      mode: "smart",
      sourceTitle: smart.sourceTitle || "智能训练",
      courseId: state.currentCourse.id,
      courseName: state.currentCourse.name || "",
      ids: smart.ids,
      createdAt: smart.createdAt || nowText(),
    });
    smart.sessionId = session.id;
  }
  if (smart.sessionId) state.storage.activeTrainingSessionId = smart.sessionId;
  resetGeneratedPracticeFilters();
  state.mode = "smart";
  setCoursePicker(false);
  await loadQuestions();
  toast("已继续上次智能练习");
}

async function startDueWrongReview() {
  if (!state.currentCourse) return;
  const dueIds = getWrongRecordsForCurrentCourse(userCourseStore())
    .filter(([, item]) => isReviewDue(item) && !item.resolved)
    .map(([id]) => Number(id))
    .filter(Boolean);
  if (!dueIds.length) {
    toast("今天没有到期错题");
    return;
  }
  resetGeneratedPracticeFilters();
  upsertTrainingSession({
    id: trainingSessionId("due"),
    type: "due",
    mode: "wrong",
    sourceTitle: "今日错题复习",
    courseId: state.currentCourse.id,
    courseName: state.currentCourse.name || "",
    ids: dueIds,
  });
  state.mode = "wrong";
  state.wrongFilters = { ...(state.wrongFilters || {}), review: "due", status: "active" };
  setCoursePicker(false);
  await loadQuestions();
  toast("已进入今日错题复习");
}

async function startSimilarWrongPractice() {
  if (!state.currentCourse) return;
  if (!state.analysisQuestions.length) await loadAnalysisQuestions();
  const ids = buildSimilarWrongIds(userCourseStore());
  if (!ids.length) {
    toast("暂无可重练的相似错题");
    return;
  }
  resetGeneratedPracticeFilters();
  saveSmartPracticeSession(ids.slice(0, 80), "相似错题重练", { type: "similar" });
  state.mode = "smart";
  setCoursePicker(false);
  await loadQuestions();
  toast("已生成相似错题重练");
}

function buildSimilarWrongIds(courseStore = userCourseStore()) {
  const wrongRecords = getWrongRecordsForCurrentCourse(courseStore)
    .sort((a, b) => Number(b[1]?.count || 0) - Number(a[1]?.count || 0))
    .slice(0, 12);
  const allItems = state.analysisQuestions.length ? state.analysisQuestions : state.questions;
  const ids = [];
  const pushUnique = (id) => {
    const n = Number(id);
    if (n && !ids.includes(n)) ids.push(n);
  };
  wrongRecords.forEach(([id, record]) => {
    pushUnique(id);
    const keywords = extractKeywords(record.title || "");
    let added = 0;
    for (const item of allItems) {
      if (added >= 6) break;
      if (Number(item.id) === Number(id)) continue;
      const sameChapter = Number(item.chapterId || 0) === Number(record.chapterId || 0) || item.chapterName === record.chapterName;
      const sameType = !record.type || item.type === record.type;
      const text = sameChapter ? "" : fastPlainQuestionText(item);
      const keywordHit = keywords.length && keywords.some((word) => text.includes(word));
      if ((sameChapter || keywordHit) && sameType) {
        pushUnique(item.id);
        added++;
      }
    }
  });
  return ids;
}

function fastPlainQuestionText(item) {
  const raw = String(item.title || item.stem || item.detail?.stem || "");
  return raw.includes("<") ? stripText(raw) : raw;
}

function extractKeywords(text) {
  return [...new Set(String(text || "")
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
  resetGeneratedPracticeFilters();
  saveSmartPracticeSession(ids.slice(0, 100), "考前冲刺", { type: "sprint" });
  state.mode = "smart";
  setCoursePicker(false);
  await loadQuestions();
  toast("已生成考前冲刺题");
}

function buildSprintPracticeIds(courseStore = userCourseStore()) {
  const allItems = state.analysisQuestions.length ? state.analysisQuestions : state.questions;
  const ids = [];
  const pushUnique = (id) => {
    const n = Number(id);
    if (n && !ids.includes(n)) ids.push(n);
  };
  const cutoff = Date.now() - 7 * 86400000;
  getWrongRecordsForCurrentCourse(courseStore)
    .filter(([, item]) => {
      const at = parseDate(item?.at || item?.wrongAt);
      return !at || at.getTime() >= cutoff || Number(item.count || 0) >= 2;
    })
    .slice(0, 30)
    .forEach(([id]) => pushUnique(id));
  getWeakChapterRows(courseStore, allItems).slice(0, 6).forEach((chapter) => {
    allItems
      .filter((item) => chapterPathForId(item.chapterId).slice(0, 2).some((node) => Number(node.id) === Number(chapter.id)))
      .filter((item) => !courseStore.correct?.[item.id])
      .slice(0, 10)
      .forEach((item) => pushUnique(item.id));
  });
  allItems
    .filter((item) => !courseStore.done?.[item.id])
    .slice(0, 100 - ids.length)
    .forEach((item) => pushUnique(item.id));
  return ids;
}

function renderDeepAnalysis(courseStore, items = state.questions) {
  const answeredIds = new Set(Object.keys(courseStore.done || {}).map(Number));
  const correctIds = new Set(Object.keys(courseStore.correct || {}).map(Number));
  const byChapter = new Map();
  const byType = new Map();
  const ensureChapterRow = (node, depth) => {
    const id = Number(node?.id || 0);
    if (!id) return null;
    if (!byChapter.has(id)) {
      byChapter.set(id, {
        id,
        name: node.name || "未分章节",
        total: 0,
        done: 0,
        correct: 0,
        depth,
      });
    }
    return byChapter.get(id);
  };
  items.forEach((item) => {
    const path = chapterPathForId(item.chapterId).slice(0, 2);
    const targets = path.length ? path : [{ id: item.chapterId || 0, name: item.chapterName || "未分章节" }];
    targets.forEach((node, index) => {
      const chapter = ensureChapterRow(node, index + 1);
      if (!chapter) return;
      chapter.total++;
      if (answeredIds.has(Number(item.id))) chapter.done++;
      if (correctIds.has(Number(item.id))) chapter.correct++;
    });

    const typeKey = item.subjectType ?? item.type ?? "题目";
    const type = byType.get(typeKey) || { id: typeKey, name: item.type || "题目", total: 0, done: 0, correct: 0 };
    type.total++;
    if (answeredIds.has(Number(item.id))) type.done++;
    if (correctIds.has(Number(item.id))) type.correct++;
    byType.set(typeKey, type);
  });
  const chapterRows = [...byChapter.values()]
    .sort((a, b) => {
      const aPath = chapterPathForId(a.id);
      const bPath = chapterPathForId(b.id);
      const aCode = aPath.map((node) => String(node.code || "").padEnd(8, "0")).join(".");
      const bCode = bPath.map((node) => String(node.code || "").padEnd(8, "0")).join(".");
      return aCode.localeCompare(bCode, "zh-CN") || (a.depth || 9) - (b.depth || 9);
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
    const rate = row.done ? Math.round(row.correct / row.done * 100) : 0;
    const level = rate >= 80 ? "good" : rate >= 60 ? "warn" : "bad";
    const mastery = chapterMastery(row);
    return `
      <button type="button" class="analysis-row ${level}" disabled>
        <span>${escapeHtml(row.name)}</span>
        <em>${row.total}题 · 已做${row.done} · 对${row.correct}</em>
        <i class="mastery-badge ${mastery.level}">${mastery.label}</i>
        <b>${rate}%</b>
      </button>
    `;
  }).join("");
}

function chapterMastery(row) {
  const done = Number(row.done || 0);
  const rate = done ? Math.round(Number(row.correct || 0) / done * 100) : 0;
  if (!done) return { label: "未开始", level: "none" };
  if (done < 5 || rate < 60) return { label: "薄弱", level: "weak" };
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
    const rate = row.done ? Math.round(row.correct / row.done * 100) : 0;
    const level = rate >= 80 ? "good" : rate >= 60 ? "warn" : "bad";
    const mastery = chapterMastery(row);
    return `
      <div class="analysis-tree-node level-${depth}">
        <div class="analysis-tree-row">
          <button type="button" class="analysis-toggle ${expanded ? "expanded" : "collapsed"}" data-analysis-toggle="${node.id}" ${children.length ? "" : "disabled"}>${children.length ? (expanded ? "−" : "+") : ""}</button>
          <button type="button" class="analysis-row ${level} ${depth === 1 ? "heading" : ""}" ${depth === 1 ? "disabled" : `data-analysis-chapter="${row.id}"`}>
            <span>${escapeHtml(row.name)}</span>
            <em>${row.total}题 · 已做${row.done} · 对${row.correct}</em>
            <i class="mastery-badge ${mastery.level}">${mastery.label}</i>
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
  const plan = state.storage.plan && typeof state.storage.plan === "object" ? state.storage.plan : {};
  const courseId = Number(state.currentCourse?.id || 0);
  if (courseId && plan.courseId && Number(plan.courseId) !== courseId) return {};
  return plan;
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
  const weakChapterIds = new Set(getWeakChapterRows(courseStore, allItems).slice(0, 6).map((row) => Number(row.id)));
  const isWeakItem = (item) => chapterPathForId(item.chapterId).slice(0, 2).some((node) => weakChapterIds.has(Number(node.id)));
  const weakProblemIds = [];
  const weakUndoneIds = [];
  const undoneIds = [];
  allItems.forEach((item) => {
    const id = Number(item.id);
    if (!id) return;
    const done = !!courseStore.done?.[id];
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
  const done = new Set(Object.keys(courseStore.done || {}).map(Number));
  const ids = new Set((activeReviewPlan().carryoverIds || []).map(Number).filter(Boolean));
  const today = todayKey();
  (state.storage.trainingSessions || []).forEach((session) => {
    if (session.type !== "plan" || Number(session.courseId || 0) !== Number(state.currentCourse?.id || 0)) return;
    syncTrainingSessionStats(session);
    const key = session.planKey || dateKey(parseDate(session.createdAt) || new Date());
    if (key >= today || session.status === "completed") return;
    normalizeSessionIds(session.ids).forEach((id) => {
      if (!done.has(Number(id))) ids.add(Number(id));
    });
  });
  return [...ids].filter((id) => !done.has(Number(id)));
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
  const push = (list, type, options = {}) => {
    for (const raw of list || []) {
      const id = Number(raw);
      if (!id || seen.has(id)) continue;
      if (!options.allowOverCap && cap && ids.length >= cap) break;
      seen.add(id);
      ids.push(id);
      counts[type] = Number(counts[type] || 0) + 1;
    }
  };
  const importantCarryover = pools.carryoverIds.filter((id) => pools.dueIds.includes(id) || pools.repeatedIds.includes(id) || isWeakId(id));
  const ordinaryCarryover = pools.carryoverIds.filter((id) => !importantCarryover.includes(id));
  push(importantCarryover, "carryover");
  push(pools.dueIds, "review", { allowOverCap: pools.dueIds.length > cap });
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
        <button id="startTodayPlanBtn" type="button" ${overview.hasPlan && taskCount && !overview.todayCompleted ? "" : "disabled"}>${overview.todaySession && !overview.todayCompleted ? "继续今日计划" : overview.todayCompleted ? "今日计划已完成" : "开始今日计划"}</button>
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
      state.storage.plan = {
        ...(state.storage.plan || {}),
        examDate,
        courseId: state.currentCourse?.id,
        dailyTarget,
        dailyTargetManual: userChangedTarget && dailyTarget !== suggestedTarget,
        lastSuggestedTarget: suggestedTarget,
        lastRebalancedAt: nowText(),
        carryoverIds: overview.pools.carryoverIds,
        createdAt: state.storage.plan?.createdAt || nowText(),
        dailyLog: state.storage.plan?.dailyLog || {},
        snapshot: {
          total: totalItems,
          done: stats.done,
          correct: stats.correct,
          wrong: wrongTotal,
        },
      };
      scheduleSave();
      renderProgress();
      toast("复习计划已保存");
    };
  }
  const startBtn = $("startTodayPlanBtn");
  if (startBtn) startBtn.onclick = () => startTodayReviewPlan().catch((err) => toast(err.message));
  document.querySelectorAll("[data-plan-day]").forEach((checkbox) => {
    checkbox.onchange = () => {
      state.storage.plan ||= { dailyLog: {} };
      state.storage.plan.dailyLog ||= {};
      const key = checkbox.dataset.planDay;
      if (checkbox.checked) {
        state.storage.plan.dailyLog[key] = {
          newDone: Number(checkbox.dataset.newTask || 0),
          reviewDone: Number(checkbox.dataset.reviewTask || 0),
          weakDone: Number(checkbox.dataset.weakTask || 0),
          at: nowText(),
        };
      } else {
        delete state.storage.plan.dailyLog[key];
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
  resetGeneratedPracticeFilters();
  const session = saveSmartPracticeSession(ids, "今日计划", { type: "plan" });
  session.planKey = todayKey();
  session.taskCounts = overview.task.counts;
  state.storage.plan = {
    ...(state.storage.plan || {}),
    courseId: state.currentCourse.id,
    todaySessionId: session.id,
    lastSuggestedTarget: overview.suggestedTarget,
    lastRebalancedAt: nowText(),
    carryoverIds: overview.pools.carryoverIds,
  };
  scheduleSave();
  state.mode = "smart";
  setCoursePicker(false);
  await loadQuestions();
  toast("已生成今日计划");
}

function todayKey() {
  return dateKey(new Date());
}

function getTodayActivity(courseId = state.currentCourse?.id) {
  const day = state.storage.dailyActivity?.[todayKey()];
  if (!day) return { total: 0, verified: 0, correct: 0, rate: 0 };
  if (!courseId) {
    const rate = day.verified ? Math.round(Number(day.correct || 0) / Number(day.verified || 1) * 100) : 0;
    return { total: Number(day.total || 0), verified: Number(day.verified || 0), correct: Number(day.correct || 0), rate };
  }
  const course = day.courses?.[String(courseId)] || {};
  const verified = Number(course.verified || 0);
  const correct = Number(course.correct || 0);
  return {
    total: Number(course.total || 0),
    verified,
    correct,
    rate: verified ? Math.round(correct / verified * 100) : 0,
  };
}

function dateKey(d) {
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

function renderProgressTrend() {
  const canvas = $("progressTrend");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  const history = (state.storage.history || [])
    .filter((item) => !state.currentCourse || Number(item.courseId || 0) === Number(state.currentCourse.id))
    .slice(0, 20)
    .reverse();
  const w = canvas.width;
  const h = canvas.height;
  ctx.clearRect(0, 0, w, h);
  ctx.fillStyle = "#f8fafc";
  ctx.fillRect(0, 0, w, h);
  ctx.strokeStyle = "#e2e8f0";
  ctx.lineWidth = 1;
  for (let i = 0; i <= 4; i++) {
    const y = 18 + i * ((h - 44) / 4);
    ctx.beginPath();
    ctx.moveTo(36, y);
    ctx.lineTo(w - 18, y);
    ctx.stroke();
  }
  ctx.fillStyle = "#64748b";
  ctx.font = "13px Inter, Noto Sans SC, Microsoft YaHei, sans-serif";
  ctx.fillText("100%", 4, 22);
  ctx.fillText("0%", 14, h - 24);
  if (!history.length) {
    ctx.fillStyle = "#64748b";
    ctx.fillText("暂无提交记录", Math.max(36, w / 2 - 42), h / 2 + 4);
    return;
  }
  const points = history.map((item, index) => {
    const done = Number(item.done || 0);
    const rate = done ? Math.round((Number(item.correct || 0) / done) * 100) : 0;
    const x = history.length === 1 ? w / 2 : 42 + index * ((w - 72) / (history.length - 1));
    const y = 18 + (100 - rate) * ((h - 44) / 100);
    return { x, y, rate };
  });
  ctx.strokeStyle = "#6366f1";
  ctx.lineWidth = 3;
  ctx.beginPath();
  points.forEach((point, index) => {
    if (index) ctx.lineTo(point.x, point.y);
    else ctx.moveTo(point.x, point.y);
  });
  ctx.stroke();
  points.forEach((point) => {
    ctx.beginPath();
    ctx.fillStyle = "#fff";
    ctx.arc(point.x, point.y, 5, 0, Math.PI * 2);
    ctx.fill();
    ctx.strokeStyle = "#6366f1";
    ctx.lineWidth = 2;
    ctx.stroke();
  });
  const last = points[points.length - 1];
  ctx.fillStyle = "#0f172a";
  ctx.fillText(`${last.rate}%`, Math.min(w - 54, last.x + 8), Math.max(16, last.y - 8));
}

async function renderAdminDashboard() {
  document.body.classList.add("admin-view");
  setText("courseTitle", "管理员数据看板");
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
      return buildUserSummary(res.user, data, meta);
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

function buildUserSummary(user, data, meta = {}) {
  const courseStores = Object.values(data.courses || {});
  const done = uniqueCount(courseStores.flatMap((course) => Object.keys(course.done || {})));
  const correct = uniqueCount(courseStores.flatMap((course) => Object.keys(course.correct || {})));
  const wrong = uniqueCount(courseStores.flatMap((course) => Object.keys(course.wrong || {})));
  const rate = done ? Math.round((correct / done) * 100) : 0;
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
      rate: done ? Math.round((correct / done) * 100) : 0,
      lastSubjectId: course.lastSubjectId || 0,
    };
  });
  return result;
}

function renderAdminRows(rows, failedCount = 0) {
  state.adminView = state.adminView || "users";
  $("adminDashboard").innerHTML = `
    <div class="admin-toolbar">
      <button id="adminAddUserBtn" class="primary-action" type="button">添加用户</button>
      <button class="admin-view-btn ${state.adminView === "users" ? "active" : ""}" data-admin-view="users" type="button">账号视图</button>
      <button class="admin-view-btn ${state.adminView === "courses" ? "active" : ""}" data-admin-view="courses" type="button">按课程视图</button>
      <button class="admin-view-btn ${state.adminView === "corrections" ? "active" : ""}" data-admin-view="corrections" type="button">纠错反馈</button>
      <button class="admin-view-btn ${state.adminView === "banks" ? "active" : ""}" data-admin-view="banks" type="button">题库更新</button>
      <button class="admin-view-btn ${state.adminView === "bankManager" ? "active" : ""}" data-admin-view="bankManager" type="button">题库管理器</button>
      <button class="admin-view-btn ${state.adminView === "bankEditor" ? "active" : ""}" data-admin-view="bankEditor" type="button">题库编辑</button>
      <button class="admin-view-btn ${state.adminView === "data" ? "active" : ""}" data-admin-view="data" type="button">数据管理</button>
      ${failedCount ? `<span class="admin-warning">${failedCount} 个用户数据加载失败，已跳过</span>` : ""}
    </div>
    ${state.adminView === "courses" ? renderAdminCourseTable(rows) : state.adminView === "banks" ? renderAdminBankTable() : state.adminView === "bankManager" ? renderAdminBankManagerPanel() : state.adminView === "bankEditor" ? renderAdminBankEditorPanel() : state.adminView === "corrections" ? renderAdminCorrectionTable(rows) : state.adminView === "data" ? renderAdminDataPanel() : renderAdminUserTable(rows)}
  `;
  $("adminAddUserBtn").onclick = openAdminUserDialog;
  document.querySelectorAll("[data-admin-view]").forEach((btn) => {
    btn.onclick = () => {
      state.adminView = btn.dataset.adminView;
      renderAdminRows(state.adminRows, state.adminFailedCount);
    };
  });
  if ($("adminBankSearch")) {
    $("adminBankSearch").oninput = () => {
      state.adminBankQuery = $("adminBankSearch").value;
      refreshAdminBankTableDebounced();
    };
  }
  bindAdminBankUpdateButtons();
  bindAdminBankManagerActions();
  bindAdminBankEditorActions();
  document.querySelectorAll("[data-admin-action]").forEach((btn) => {
    btn.onclick = () => adminUserAction(btn.dataset.user, btn.dataset.adminAction).catch((err) => toast(err.message));
  });
  document.querySelectorAll("[data-correction-action]").forEach((btn) => {
    btn.onclick = () => adminCorrectionAction(btn.dataset.user, btn.dataset.correctionId, btn.dataset.correctionAction).catch((err) => toast(err.message));
  });
  bindAdminDataActions();
}

function bindAdminBankUpdateButtons() {
  ensureAdminBankUpdateMode().catch(() => {});
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
    const result = await api(`/api/admin/update-bank?${query}`, { method: "POST" });
    state.adminBankUpdateMode = result.reserved || result.mode === "upload" ? "upload" : "pull";
    if (state.adminView === "bankManager") renderAdminRows(state.adminRows, state.adminFailedCount);
    else refreshAdminBankTable();
    return state.adminBankUpdateMode;
  })();
  try {
    return await state.adminBankUpdateModePromise;
  } finally {
    state.adminBankUpdateModePromise = null;
  }
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
              </div>
            </div>
            <div class="admin-actions admin-user-actions">
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
              const correct = stats.reduce((sum, item) => sum + item.correct, 0);
              const rate = done ? Math.round((correct / done) * 100) : 0;
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
              <td><span class="status-pill ${item.status === "resolved" ? "active" : "disabled"}">${item.status === "resolved" ? "已处理" : "待处理"}</span></td>
              <td class="admin-user-cell">${escapeHtml(item.user)}</td>
              <td>${escapeHtml(item.courseName || "")}<span>${escapeHtml(item.chapterName || "")}</span></td>
              <td><b>${escapeHtml(String(item.questionNo || item.questionId || ""))}</b><span>${escapeHtml(item.title || "")}</span></td>
              <td>${escapeHtml(item.type || "其他")}</td>
              <td class="correction-note">${escapeHtml(item.note || "")}</td>
              <td>${escapeHtml(item.at || "未记录")}</td>
              <td class="admin-actions">
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

function renderAdminBankTable() {
  const courses = state.adminCourses || [];
  if (!courses.length) return `<div class="admin-empty">暂无题库数据</div>`;
  const visibleCourses = getFilteredAdminCourses();
  return `
    <div class="admin-bank-tools">
      <input id="adminBankSearch" value="${escapeHtml(state.adminBankQuery || "")}" placeholder="搜索题库名称、分类或ID">
      <span id="adminBankSummary">${renderAdminBankSummary(visibleCourses.length)}</span>
    </div>
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

function getFilteredAdminCourses() {
  const courses = state.adminCourses || [];
  const query = (state.adminBankQuery || "").trim().toLowerCase();
  if (!query) return courses;
  return courses.filter((course) => [course.name, course.category, course.subcategory, course.id]
    .some((value) => String(value || "").toLowerCase().includes(query)));
}

function renderAdminBankSummary(visibleCount = getFilteredAdminCourses().length) {
  const courses = state.adminCourses || [];
  const canUpdateCount = courses.filter((course) => !!course.owned && Number(course.questionCount || 0) > 0).length;
  const modeText = state.adminBankUpdateMode === "upload" ? "上传更新" : state.adminBankUpdateMode === "pull" ? "拉取更新" : "可更新";
  return `共 ${courses.length} 门 · ${modeText} ${canUpdateCount} 门 · 当前显示 ${visibleCount} 门`;
}

function renderAdminBankRows(visibleCourses) {
  return visibleCourses.map((course) => {
    const hasLocal = Number(course.questionCount || 0) > 0;
    const canUpdate = !!course.owned && hasLocal;
    const status = !course.owned ? "未授权" : hasLocal ? "已授权 / 已下载" : "已授权 / 无本地题库";
    const updateText = state.adminBankUpdateMode === "upload" ? "上传更新" : state.adminBankUpdateMode === "pull" ? "拉取更新" : "更新题库";
    return `
      <tr class="${canUpdate ? "" : "muted-row"}">
        <td><b>${escapeHtml(course.name)}</b><span class="admin-course-id">ID ${course.id}</span></td>
        <td>${escapeHtml(course.category || "")}<span>${escapeHtml(course.subcategory || "")}</span></td>
        <td>${Number(course.questionCount || 0)} 题</td>
        <td>${escapeHtml(formatRelativeTime(course.changedAt))}<span>${escapeHtml(course.changedAt || "未记录")}</span></td>
        <td><span class="status-pill ${canUpdate ? "active" : "disabled"}">${escapeHtml(status)}</span></td>
        <td>
          <button class="primary-action secondary compact" data-update-course="${course.id}" ${canUpdate ? "" : "disabled"}>${canUpdate ? updateText : "不可更新"}</button>
        </td>
      </tr>
    `;
  }).join("") || `<tr><td colspan="6">没有匹配的题库</td></tr>`;
}

function refreshAdminBankTable() {
  const visibleCourses = getFilteredAdminCourses();
  const summary = $("adminBankSummary");
  const rows = $("adminBankRows");
  if (summary) summary.textContent = renderAdminBankSummary(visibleCourses.length);
  if (rows) rows.innerHTML = renderAdminBankRows(visibleCourses);
  bindAdminBankUpdateButtons();
}

function renderAdminBankManagerPanel() {
  if (!state.adminBankUpdateMode) {
    ensureAdminBankUpdateMode().catch((err) => toast(err.message));
    return `
      <div class="admin-data-card bank-manager-card">
        <div class="admin-data-card-head">
          <div>
            <b>题库管理器</b>
            <span>正在识别当前部署的题库更新方式...</span>
          </div>
          <span class="status-pill disabled">检测中</span>
        </div>
      </div>
    `;
  }
  if (state.adminBankUpdateMode === "upload") {
    return `
      <div class="admin-data-card bank-manager-card">
        <div class="admin-data-card-head">
          <div>
            <b>Docker 版题库更新</b>
            <span>Docker 只接收已经在本地服务器版校验过的题库包。</span>
          </div>
          <span class="status-pill disabled">上传版</span>
        </div>
        <div class="admin-empty bank-manager-empty">
          请先在 Windows 本地服务器版使用题库管理器拉取/上传到临时区，完成对比校验并导出 Docker 包；确认无 ID 大幅变动、答案异常和图片缺失后，再回到 Docker 的“题库更新”或“数据管理”上传。
        </div>
        <div class="admin-data-actions">
          <button class="primary-action" data-admin-jump-view="banks" type="button">去题库更新</button>
          <button data-admin-jump-view="data" type="button">去数据管理</button>
        </div>
      </div>
    `;
  }

  const courses = getBankManagerCourses();
  if (!courses.length) {
    return `<div class="admin-empty">暂无可管理题库，请先确认服务器版已加载课程数据。</div>`;
  }
  const selected = getBankManagerCourse(courses);
  const report = Number(state.adminBankManagerReport?.courseId || 0) === Number(selected.id)
    ? state.adminBankManagerReport
    : null;
  const busy = state.adminBankManagerBusy;
  return `
    <div class="bank-manager-panel">
      <section class="admin-data-card bank-manager-card">
        <div class="admin-data-card-head">
          <div>
            <b>题库管理器</b>
            <span>先拉取或上传到临时区，对比校验通过后再发布正式题库，并导出 Docker 包。</span>
          </div>
          <span class="status-pill active">服务器版</span>
        </div>
        <div class="bank-manager-picker">
          <label>
            <span>选择题库</span>
            <select id="bankManagerCourseSelect">
              ${courses.map((course) => `<option value="${course.id}" ${Number(course.id) === Number(selected.id) ? "selected" : ""}>${escapeHtml(course.name)} · ID ${course.id} · ${Number(course.questionCount || 0)}题</option>`).join("")}
            </select>
          </label>
          <div class="bank-manager-course-meta">
            <b>${escapeHtml(selected.name || "")}</b>
            <span>${escapeHtml(selected.category || "未分类")} ${selected.subcategory ? "· " + escapeHtml(selected.subcategory) : ""}</span>
            <span>本地题量 ${Number(selected.questionCount || 0)} 题 · 更新 ${escapeHtml(formatRelativeTime(selected.changedAt))}</span>
          </div>
        </div>
        <div class="admin-data-actions bank-manager-actions">
          <button class="primary-action" data-bank-manager-action="pull" type="button" ${busy ? "disabled" : ""}>${busy === "pull" ? "拉取中..." : "拉取到临时区"}</button>
          <button data-bank-manager-action="upload" type="button" ${busy ? "disabled" : ""}>${busy === "upload" ? "上传中..." : "上传到临时区"}</button>
          <button data-bank-manager-action="report" type="button" ${busy ? "disabled" : ""}>${busy === "report" ? "生成中..." : "生成/刷新报告"}</button>
          <button class="primary-action secondary" data-bank-manager-action="publish" type="button" ${busy || !report?.stagingExists ? "disabled" : ""}>发布正式题库</button>
          <button class="danger" data-bank-manager-action="force" type="button" ${busy || !report?.stagingExists ? "disabled" : ""}>强制发布</button>
          <button data-bank-manager-action="export" type="button" ${busy || !report?.stagingExists ? "disabled" : ""}>导出 Docker 包</button>
        </div>
        <small>临时区不会影响当前正式题库；发布前会自动备份正式数据库和图片。若报告提示 ID 大幅变动，错题、收藏、笔记、训练计划可能无法准确映射。</small>
      </section>
      ${renderBankManagerReport(report)}
    </div>
  `;
}

function getBankManagerCourses() {
  const courses = state.adminCourses || [];
  const preferred = courses.filter((course) => !!course.owned || Number(course.questionCount || 0) > 0);
  return (preferred.length ? preferred : courses).slice().sort((a, b) => {
    const localDiff = Number(b.questionCount || 0) - Number(a.questionCount || 0);
    if (localDiff !== 0) return localDiff;
    return Number(a.id || 0) - Number(b.id || 0);
  });
}

function getBankManagerCourse(courses = getBankManagerCourses()) {
  const current = courses.find((course) => Number(course.id) === Number(state.adminBankManagerCourseId));
  const selected = current || courses[0] || {};
  state.adminBankManagerCourseId = Number(selected.id || 0);
  return selected;
}

function renderBankManagerReport(report) {
  if (!report) {
    return `
      <section class="admin-data-card bank-manager-card">
        <div class="admin-data-card-head">
          <div>
            <b>校验报告</b>
            <span>点击“拉取到临时区”“上传到临时区”或“生成/刷新报告”后显示。</span>
          </div>
          <span class="status-pill disabled">暂无报告</span>
        </div>
      </section>
    `;
  }
  if (report.ok === false) {
    return `<section class="admin-data-card bank-manager-card"><div class="admin-empty">报告生成失败：${escapeHtml(report.error || "未知错误")}</div></section>`;
  }
  const summary = report.summary || {};
  const warnings = Array.isArray(report.warnings) ? report.warnings : [];
  const samples = report.samples || {};
  const risk = report.riskLevel || "none";
  return `
    <section class="admin-data-card bank-manager-card">
      <div class="admin-data-card-head">
        <div>
          <b>校验报告</b>
          <span>${escapeHtml(report.generatedAt || "")}</span>
        </div>
        <span class="risk-pill risk-${escapeHtml(risk)}">${escapeHtml(report.riskText || risk)}</span>
      </div>
      <div class="admin-data-metrics bank-manager-metrics">
        <div><b>${Number(summary.currentSubjects || 0)}</b><span>当前题量</span></div>
        <div><b>${Number(summary.stagingSubjects || 0)}</b><span>临时题量</span></div>
        <div><b>${Number(summary.added || 0)}</b><span>新增</span></div>
        <div><b>${Number(summary.deleted || 0)}</b><span>删除</span></div>
        <div><b>${Number(summary.modified || 0)}</b><span>内容变化</span></div>
        <div><b>${Number(summary.answerChanged || 0)}</b><span>答案变化</span></div>
        <div><b>${Number(summary.possibleIdChanged || 0)}</b><span>疑似ID变动</span></div>
        <div><b>${Number(summary.missingAssets || 0)}</b><span>缺图</span></div>
      </div>
      <div class="bank-manager-report-grid">
        <div>
          <b>ID / 题量波动</b>
          <span>ID 疑似变动 ${Number(summary.idChangePercent || 0).toFixed(1)}% · 新旧题量波动 ${Number(summary.churnPercent || 0).toFixed(1)}%</span>
        </div>
        <div>
          <b>章节变化</b>
          <span>当前 ${Number(summary.currentChapters || 0)} 章 · 临时 ${Number(summary.stagingChapters || 0)} 章</span>
        </div>
      </div>
      ${warnings.length ? `<div class="bank-manager-warnings">${warnings.map((item) => `<p>${escapeHtml(item)}</p>`).join("")}</div>` : `<div class="bank-manager-ok">未发现阻塞问题。仍建议抽查答案变化和图片显示后再发布。</div>`}
      ${renderBankManagerSamples(samples)}
    </section>
  `;
}

function renderBankManagerSamples(samples) {
  const rows = [
    ["新增样例", samples.added],
    ["删除样例", samples.deleted],
    ["修改样例", samples.modified],
  ].map(([label, values]) => {
    const list = Array.isArray(values) ? values : [];
    return `<div><b>${label}</b><span>${list.length ? list.map((id) => `#${escapeHtml(String(id))}`).join("、") : "无"}</span></div>`;
  }).join("");
  return `<div class="bank-manager-samples">${rows}</div>`;
}

function bindAdminBankManagerActions() {
  document.querySelectorAll("[data-admin-jump-view]").forEach((btn) => {
    btn.onclick = () => {
      state.adminView = btn.dataset.adminJumpView || "banks";
      renderAdminRows(state.adminRows, state.adminFailedCount);
    };
  });
  const select = $("bankManagerCourseSelect");
  if (select) {
    select.onchange = () => {
      state.adminBankManagerCourseId = Number(select.value || 0);
      state.adminBankManagerReport = null;
      renderAdminRows(state.adminRows, state.adminFailedCount);
    };
  }
  document.querySelectorAll("[data-bank-manager-action]").forEach((btn) => {
    btn.onclick = () => runBankManagerAction(btn.dataset.bankManagerAction, btn).catch((err) => {
      state.adminBankManagerBusy = "";
      renderAdminRows(state.adminRows, state.adminFailedCount);
      toast(err.message);
    });
  });
}

async function runBankManagerAction(action, btn = null) {
  const courseId = Number(state.adminBankManagerCourseId || getBankManagerCourse().id || 0);
  if (!courseId) {
    toast("请选择题库");
    return;
  }
  if (action === "export") {
    window.location.href = `/api/admin/bank-manager/export?user=${encodeURIComponent(state.user)}&courseId=${courseId}`;
    return;
  }
  if (action === "upload") {
    await uploadBankManagerStaging(courseId);
    return;
  }
  if (action === "pull" && !confirm("确定拉取该题库到临时区吗？这不会影响当前正式题库。")) return;
  if (action === "publish" && !confirm("确定发布临时题库到正式题库吗？发布前会自动备份当前题库。")) return;
  if (action === "force" && !confirm("风险报告可能提示 ID 大幅变动。确定强制发布吗？错题、收藏、笔记和训练计划可能受影响。")) return;

  state.adminBankManagerBusy = action || "report";
  renderAdminRows(state.adminRows, state.adminFailedCount);
  try {
    let result;
    if (action === "pull") {
      result = await api(`/api/admin/bank-manager/pull-staging?user=${encodeURIComponent(state.user)}&courseId=${courseId}`, { method: "POST" });
    } else if (action === "publish" || action === "force") {
      result = await api(`/api/admin/bank-manager/publish?user=${encodeURIComponent(state.user)}&courseId=${courseId}&force=${action === "force" ? "true" : "false"}`, { method: "POST" });
    } else {
      result = await api(`/api/admin/bank-manager/report?user=${encodeURIComponent(state.user)}&courseId=${courseId}`);
    }
    state.adminBankManagerReport = result.report || result;
    toast(result.message || "题库管理器操作完成");
    if (action === "publish" || action === "force") {
      state.adminCourses = [];
      state.courses = [];
      state.chapters = [];
      state.types = [];
      state.questions = [];
      await loadCourses();
      await loadAdminCourses().catch(() => {});
    }
  } finally {
    state.adminBankManagerBusy = "";
    renderAdminRows(state.adminRows, state.adminFailedCount);
  }
}

async function uploadBankManagerStaging(courseId) {
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
    if (!confirm("确定上传该题库包到临时区吗？上传后只生成校验报告，不会立即发布正式题库。")) return;
    state.adminBankManagerBusy = "upload";
    renderAdminRows(state.adminRows, state.adminFailedCount);
    const form = new FormData();
    form.append("file", file);
    const result = await api(`/api/admin/bank-manager/upload-staging?user=${encodeURIComponent(state.user)}&courseId=${courseId}`, {
      method: "POST",
      body: form,
    });
    state.adminBankManagerReport = result.report || result;
    toast(result.message || "题库包已上传到临时区");
  } finally {
    input.remove();
    state.adminBankManagerBusy = "";
    renderAdminRows(state.adminRows, state.adminFailedCount);
  }
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
  if (state.adminView === "bankEditor") renderAdminRows(state.adminRows, state.adminFailedCount);
}

function renderBankEditorResults() {
  if (state.adminEditorBusy === "search") return `<div class="admin-empty">正在搜索题目...</div>`;
  if (!state.adminEditorResults.length) return `<div class="admin-empty">暂无题目。可选择章节、输入关键词，或勾选“只看纠错反馈”。</div>`;
  const correctionIds = new Set(getEditorCorrectionIds(Number(state.adminEditorCourseId)));
  return state.adminEditorResults.map((item) => `
    <button class="bank-editor-result ${Number(item.id) === Number(state.adminEditorSelectedId) ? "active" : ""}" data-bank-editor-open="${item.id}" data-editor-course="${item.courseId}" type="button">
      <b>#${item.id} ${escapeHtml(item.type || "题目")}</b>
      <span>${escapeHtml(item.chapterName || "")}${correctionIds.has(Number(item.id)) ? " · 有纠错反馈" : ""}</span>
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
  return `
    <div class="admin-data-card-head">
      <div>
        <b>#${detail.id} ${escapeHtml(detail.type || "题目")}</b>
        <span>${escapeHtml(detail.courseName || "")} · ${escapeHtml(detail.updatedAt || "未记录")}</span>
      </div>
      <button class="primary-action" id="bankEditorSaveBtn" type="button">${state.adminEditorBusy === "save" ? "保存中..." : "保存修改"}</button>
    </div>
    <div class="bank-editor-form">
      <div class="bank-editor-chapter-fields">
        ${path.map((chapter, index) => `
          <label>
            <span>${["一级标题", "二级标题", "三级标题", "四级标题"][index] || `第 ${index + 1} 级标题`} · ID ${chapter.id}</span>
            <input data-editor-chapter-id="${chapter.id}" value="${escapeHtml(chapter.name || "")}">
          </label>
        `).join("") || `<div class="admin-empty">未找到章节路径</div>`}
      </div>
      <label>
        <span>题目 / 选项</span>
        <textarea id="bankEditorTitleInput" rows="8">${escapeHtml(detail.title || "")}</textarea>
      </label>
      <label>
        <span>附加题干 / 案例材料</span>
        <textarea id="bankEditorQuestionInput" rows="5">${escapeHtml(detail.question || "")}</textarea>
      </label>
      <label>
        <span>答案</span>
        <textarea id="bankEditorAnswerInput" rows="3">${escapeHtml(detail.answer || "")}</textarea>
      </label>
      <label>
        <span>解析</span>
        <textarea id="bankEditorDescriptionInput" rows="6">${escapeHtml(detail.description || "")}</textarea>
      </label>
    </div>
  `;
}

function bindAdminBankEditorActions() {
  document.querySelectorAll("[data-bank-editor-open]").forEach((btn) => {
    btn.onclick = () => openBankEditorQuestion(Number(btn.dataset.bankEditorOpen || 0), Number(btn.dataset.editorCourse || 0)).catch((err) => toast(err.message));
  });
  const courseSelect = $("bankEditorCourseSelect");
  if (courseSelect) {
    courseSelect.onchange = () => {
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
  if (searchBtn) searchBtn.onclick = () => loadBankEditorQuestions().catch((err) => toast(err.message));
  const saveBtn = $("bankEditorSaveBtn");
  if (saveBtn) saveBtn.onclick = () => saveBankEditorQuestion().catch((err) => toast(err.message));
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

async function openBankEditorQuestion(questionId, courseId = 0) {
  if (!questionId) return;
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
  if (!detail?.id) return;
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
    return;
  }
  if (!confirm("确定保存题库修改吗？系统会先自动备份当前数据库。")) return;
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
    </div>
  `;
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
      state.chapters = [];
      state.types = [];
      state.questions = [];
      state.currentCourse = null;
      state.currentChapter = null;
      state.currentIndex = -1;
      await loadCourses();
      await loadAdminCourses().catch(() => {});
      state.adminView = "banks";
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
  await api(`/api/admin/user-action?user=${encodeURIComponent(state.user)}`, {
    method: "POST",
    headers: { "Content-Type": "application/json; charset=utf-8" },
    body: JSON.stringify({ target: user, action: "create" }),
  });
  await loadUsers();
  await renderAdminDashboard();
  closeAdminUserDialog();
  toast(`已添加用户：${user}，默认密码 123456`);
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
    const ok = confirm(`确定要${names[action]}「${target}」吗？`);
    if (!ok) return;
  }
  const result = await api(`/api/admin/user-action?user=${encodeURIComponent(state.user)}`, {
    method: "POST",
    headers: { "Content-Type": "application/json; charset=utf-8" },
    body: JSON.stringify({ target, action }),
  });
  toast(result.message || `${names[action] || "操作"}成功`);
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
    headers: { "Content-Type": "application/json; charset=utf-8" },
    body: JSON.stringify(data),
  });
  toast("纠错反馈已更新");
  await renderAdminDashboard();
}

function getCourseStats(courseId = state.currentCourse?.id) {
  const data = peekCourseStore(courseId);
  return {
    done: Object.keys(data.done || {}).length,
    correct: Object.keys(data.correct || {}).length,
    wrong: Object.keys(data.wrong || {}).length,
  };
}

async function moveQuestion(delta) {
  if (!state.questions.length) return;
  state.currentIndex = Math.max(0, Math.min(state.questions.length - 1, state.currentIndex + delta));
  await loadCurrentQuestion();
}

function isSwipeIgnoredTarget(target) {
  if (!target?.closest) return false;
  if (target.closest(".option")) return false;
  return !!target.closest("input, textarea, select, label, a, .answer-card-wrap, .question-tag-panel, .practice-context-panel, .question-actions, .toolbar, .main-nav, .modal");
}

function canSwipeQuestions() {
  return !isAdmin()
    && !state.pickerOpen
    && state.questions.length > 1
    && state.currentIndex >= 0
    && !["training", "progress"].includes(state.mode)
    && !$("questionBody")?.classList.contains("hidden");
}

function bindQuestionSwipe() {
  const view = $("questionView");
  if (!view || view.dataset.swipeBound) return;
  view.dataset.swipeBound = "1";
  const startSwipe = (point, target) => {
    if (!point || isSwipeIgnoredTarget(target) || !canSwipeQuestions()) {
      state.touchStart = null;
      return;
    }
    state.touchStart = {
      x: point.clientX,
      y: point.clientY,
      at: Date.now(),
      active: false,
    };
  };
  const finishSwipe = (point) => {
    const start = state.touchStart;
    state.touchStart = null;
    if (!start || !point || !canSwipeQuestions()) return;
    const dx = point.clientX - start.x;
    const dy = point.clientY - start.y;
    const elapsed = Date.now() - start.at;
    if (elapsed > 650 || Math.abs(dx) < 58 || Math.abs(dx) < Math.abs(dy) * 1.25 || Math.abs(dy) > 90) return;
    moveQuestion(dx < 0 ? 1 : -1).catch((err) => toast(err.message));
  };
  const updateSwipe = (point, event) => {
    const start = state.touchStart;
    if (!start || !point) return;
    const dx = point.clientX - start.x;
    const dy = point.clientY - start.y;
    if (Math.abs(dx) > 18 && Math.abs(dx) > Math.abs(dy) * 1.15) {
      start.active = true;
      event?.preventDefault?.();
    }
  };
  view.addEventListener("touchstart", (event) => {
    startSwipe(event.changedTouches?.[0], event.target);
  }, { passive: true });
  view.addEventListener("touchmove", (event) => {
    updateSwipe(event.changedTouches?.[0], event);
  }, { passive: false });
  view.addEventListener("touchend", (event) => {
    finishSwipe(event.changedTouches?.[0]);
  }, { passive: true });
  view.addEventListener("pointerdown", (event) => {
    if (event.button && event.button !== 0) return;
    startSwipe(event, event.target);
  });
  view.addEventListener("pointermove", (event) => {
    updateSwipe(event, event);
  });
  view.addEventListener("pointerup", (event) => {
    finishSwipe(event);
  });
  view.addEventListener("pointercancel", () => {
    state.touchStart = null;
  });
}

async function submitPaper(autoSubmit = false) {
  if (state.mode === "exam" && state.submitted && state.exam?.result) {
    toast("已经交卷");
    return;
  }
  state.submitted = true;
  let correct = 0;
  let checked = 0;
  let score = 0;
  let totalScore = 0;
  let subjective = 0;
  const examDetails = [];
  for (const item of state.questions) {
    item.detail = item.detail || await api(`/api/question?id=${item.id}`);
    const itemScore = getQuestionScore(item.detail);
    const itemSubjective = isSubjective(item.detail);
    const answered = hasAnswer(item.id);
    const itemCorrect = answered && !itemSubjective && isAnswerCorrect(item.detail);
    const earnedScore = answered ? calculateQuestionScore(item.detail) : 0;
    totalScore += itemScore;
    if (itemSubjective) subjective++;
    if (state.mode === "exam") {
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
    if (state.mode !== "exam") markQuestionVerified(item.detail);
    if (!itemSubjective) markResult(item.detail);
    if (state.mode !== "exam") recordPracticeActivity(item.detail, { verified: true });
  }
  const rate = checked ? Math.round((correct / checked) * 100) : 0;
  const submittedAt = nowText();
  if (state.mode === "exam" && state.exam) {
    state.exam.submittedAt = Date.now();
    state.exam.result = { score, totalScore, correct, checked, rate, subjective };
    recordExamHistory({ score, totalScore, correct, checked, rate, subjective, autoSubmit, submittedAt }, examDetails);
    clearExamDraft();
    stopExamTimer();
  }
  state.storage.history.unshift({
    user: state.user,
    courseId: state.currentCourse?.id,
    courseName: state.currentCourse?.name,
    mode: state.mode,
    total: state.questions.length,
    done: checked,
    correct,
    score,
    totalScore,
    at: submittedAt,
  });
  state.storage.history = state.storage.history.slice(0, 200);
  scheduleSave();
  state.answerVisible = true;
  renderQuestion();
  renderPracticeContextPanel();
  if (state.mode === "exam") {
    toast(`${autoSubmit ? "时间到，已自动交卷" : "已交卷"}：${formatScore(score)}/${formatScore(totalScore)} 分`);
  } else {
    toast(`已提交：${correct}/${checked} 题正确`);
  }
}

function resetPractice() {
  stopExamTimer();
  const resettingExam = state.mode === "exam";
  if (resettingExam) {
    clearExamDraft();
    state.exam = null;
  }
  state.answers = {};
  state.submitted = false;
  state.answerVisible = false;
  if (state.currentCourse && !resettingExam) userCourseStore().answers = {};
  if (!resettingExam) clearVerifiedForCourse();
  scheduleSave();
  if (resettingExam) {
    state.questions = [];
    state.currentIndex = -1;
    renderExamHome();
    toast("已放弃本次模拟考");
    return;
  }
  if (!state.questions.length) {
    if (state.mode === "exam") renderExamHome();
    else renderAll();
    toast("已清空本次作答");
    return;
  }
  renderQuestion();
  toast("已清空本次作答");
}

function isFavorite(id) {
  return !!state.storage.favorite[id];
}

function toggleFavorite() {
  const q = state.questions[state.currentIndex]?.detail;
  if (!q) return;
  if (state.storage.favorite[q.id]) delete state.storage.favorite[q.id];
  else ensureFavoriteRecord(q, "考前速看");
  scheduleSave();
  renderQuestion();
}

function getNote(id) {
  return state.storage.notes[id] || "";
}

function saveNote() {
  const q = state.questions[state.currentIndex]?.detail;
  if (!q) return;
  state.storage.notes[q.id] = $("noteEditor").value;
  scheduleSave();
}

function toggleAnswer() {
  if (state.currentIndex < 0) return;
  state.answerVisible = !state.answerVisible;
  scheduleSave();
  renderQuestion();
}

function examDraftForCurrentCourse() {
  const draft = state.storage.examDraft;
  if (!state.currentCourse) return null;
  if (!draft || typeof draft !== "object") return null;
  if (!Array.isArray(draft.questionIds) || !draft.questionIds.length) return null;
  if (Number(draft.courseId || 0) !== Number(state.currentCourse.id || 0)) return null;
  return draft;
}

function examDraftAnswerCount(draft = examDraftForCurrentCourse()) {
  if (!draft?.answers) return 0;
  return Object.values(draft.answers).filter((answer) => normalizeAnswer(answer).length > 0).length;
}

function saveExamDraft() {
  if (state.mode !== "exam" || !state.exam || state.submitted || !state.currentCourse || !state.questions.length) return;
  state.storage.examDraft = {
    id: state.exam.draftId || `${state.exam.startedAt}-${state.currentCourse.id}`,
    courseId: state.currentCourse.id,
    courseName: state.currentCourse.name || "",
    rule: state.exam.rule,
    scoreMap: state.exam.scoreMap || {},
    questionIds: state.questions.map((item) => Number(item.id)).filter(Boolean),
    answers: { ...state.answers },
    currentIndex: Math.max(0, state.currentIndex),
    answerCardPage: Math.max(0, state.answerCardPage),
    startedAt: Number(state.exam.startedAt || Date.now()),
    endsAt: Number(state.exam.endsAt || Date.now()),
    savedAt: Date.now(),
    savedAtText: nowText(),
  };
  state.exam.draftId = state.storage.examDraft.id;
  scheduleSave();
}

function clearExamDraft() {
  if (!state.storage.examDraft) return;
  state.storage.examDraft = null;
  scheduleSave();
}

function renderExamDraftCard(draft = examDraftForCurrentCourse()) {
  if (!draft) return "";
  const leftMs = Number(draft.endsAt || 0) - Date.now();
  const expired = leftMs <= 0;
  const answered = examDraftAnswerCount(draft);
  const total = draft.questionIds?.length || 0;
  return `
    <div class="exam-draft-card">
      <div>
        <strong>未完成考试</strong>
        <span>${escapeHtml(draft.rule?.name || "模拟卷")} · 已答 ${answered}/${total} 题 · ${expired ? "已到交卷时间" : `剩余 ${formatSeconds(leftMs / 1000)}`}</span>
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

async function resumeExamDraft() {
  const draft = examDraftForCurrentCourse();
  if (!draft) {
    toast("没有可继续的模拟考试");
    renderExamHome();
    return;
  }
  const ids = draft.questionIds.map(Number).filter(Boolean);
  if (!ids.length) {
    clearExamDraft();
    toast("考试草稿已失效");
    renderExamHome();
    return;
  }
  setPanelPage(false);
  stopExamTimer();
  state.mode = "exam";
  state.questions = [];
  state.answers = { ...(draft.answers || {}) };
  state.submitted = false;
  state.answerVisible = false;
  state.answerCardPage = Number(draft.answerCardPage || 0);
  const params = new URLSearchParams({
    courseId: String(draft.courseId || state.currentCourse.id),
    ids: ids.join(","),
    limit: String(ids.length),
  });
  const items = await api(`/api/questions?${params}`);
  state.questions = orderItemsByIds(items, ids);
  if (!state.questions.length) {
    clearExamDraft();
    throw new Error("考试草稿中的题目已不存在");
  }
  state.currentIndex = Math.min(Math.max(0, Number(draft.currentIndex || 0)), state.questions.length - 1);
  state.exam = {
    rule: draft.rule || getExamRule(),
    scoreMap: draft.scoreMap || {},
    startedAt: Number(draft.startedAt || Date.now()),
    endsAt: Number(draft.endsAt || Date.now()),
    submittedAt: null,
    result: null,
    draftId: draft.id || `${Date.now()}-${draft.courseId || 0}`,
  };
  startExamTimer();
  renderAll();
  await loadCurrentQuestion();
  if (Date.now() >= state.exam.endsAt) {
    await submitPaper(true);
  } else {
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

function recordExamHistory(result, details = []) {
  if (!state.exam) return;
  state.storage.examHistory ||= [];
  const submittedAtMs = state.exam.submittedAt || Date.now();
  const usedSeconds = Math.max(0, Math.round((submittedAtMs - state.exam.startedAt) / 1000));
  state.storage.examHistory.unshift({
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
  });
  state.storage.examHistory = state.storage.examHistory.slice(0, 50);
}

function renderExamHistory() {
  const currentCourseId = Number(state.currentCourse?.id || 0);
  const records = (state.storage.examHistory || [])
    .filter((item) => !currentCourseId || Number(item.courseId || 0) === currentCourseId)
    .slice(0, 5);
  if (!records.length) {
    return `
      <div class="exam-history-card">
        <div class="exam-section-title">模拟记录</div>
        <span class="muted">暂无模拟考记录</span>
      </div>
    `;
  }
  return `
    <div class="exam-history-card">
      <div class="exam-section-title">模拟记录</div>
      <div class="exam-history-list">
        ${records.map((item) => {
          const used = Number(item.usedSeconds || 0);
          const minutes = Math.floor(used / 60);
          const seconds = used % 60;
          return `
            <div class="exam-history-row">
              <b>${formatScore(item.score)} / ${formatScore(item.totalScore)} 分</b>
              <span>${Number(item.correct || 0)}/${Number(item.done || 0)} 题 · ${Number(item.rate || 0)}%</span>
              <small>${escapeHtml(item.at || "")} · 用时 ${minutes}:${String(seconds).padStart(2, "0")}${item.autoSubmit ? " · 自动交卷" : ""}</small>
              ${renderExamHistoryDetails(item)}
            </div>
          `;
        }).join("")}
      </div>
    </div>
  `;
}

function renderExamHistoryDetails(item) {
  const details = Array.isArray(item.details) ? item.details : [];
  if (!details.length) return `<small>此记录暂无逐题明细</small>`;
  const wrongRows = details.filter((row) => row.answered && !row.subjective && !row.correct);
  const subjectiveRows = details.filter((row) => row.subjective);
  const rows = wrongRows.length ? wrongRows : details.slice(0, 12);
  return `
    <details class="exam-history-detail">
      <summary>查看明细：${wrongRows.length ? `错题 ${wrongRows.length} 道` : "本卷前 12 题"}${subjectiveRows.length ? ` · 主观题 ${subjectiveRows.length} 道` : ""}</summary>
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

function renderExamHome() {
  setPanelPage(true);
  stopExamTimer();
  state.exam = null;
  state.answerVisible = false;
  setText("answerToggleBtn", "显示答案");
  const rule = getExamRule();
  const parts = normalizeExamParts(rule);
  state.examSelectedChapters = null;
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
          <input id="examDurationInput" type="number" min="1" value="${rule.durationMinutes}">
        </label>
      </div>
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
      ${renderExamHistory()}
      <button class="primary-action" id="startExamBtn" ${parts.length ? "" : "disabled"}>开始组卷</button>
    </div>
  `;
  bindExamDraftActions();
  $("examSelectAllChapters").onclick = () => {
    state.examSelectedChapters ||= new Set();
    document.querySelectorAll("[data-exam-chapter]").forEach((input) => {
      input.checked = true;
      state.examSelectedChapters.add(Number(input.value || 0));
    });
  };
  $("examSelectNoChapters").onclick = () => {
    state.examSelectedChapters.clear();
    document.querySelectorAll("[data-exam-chapter]").forEach((input) => input.checked = false);
  };
  bindExamChapterTree();
  $("startExamBtn").onclick = () => {
    if (!parts.length) {
      toast("当前题库没有可用于组卷的题型");
      return;
    }
    state.examConfig = getExamConfigFromForm(rule);
    startExamPaper().catch((err) => toast(err.message));
  };
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
  state.storage.settings ||= {};
  state.storage.settings.verifyMode = "paper";
  state.verifyMode = "paper";
  scheduleSave();
  $("emptyState").innerHTML = `<strong>正在组卷...</strong><span>按章节比例抽取试题。</span>`;
  const scoreMap = {};
  const seen = new Set();
  state.questions = [];
  const selectedIds = new Set((rule.selectedChapterIds || []).map(Number));
  const examChapters = selectedIds.size ? examChaptersFromIds(Array.from(selectedIds)) : examChapterGroups();
  for (const part of parts) {
    const selected = [];
    const allocations = allocateExamCounts(part, examChapters);
    for (const allocation of allocations) {
      const items = await fetchExamPartQuestions(part, allocation.count, allocation.chapterId);
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
        const chunk = await fetchExamPartQuestions(part, part.count * 3, Number(chapter.id || 0));
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
    state.questions.push(...shuffleItems(selected));
  }
  if (!state.questions.length) throw new Error("组卷失败，当前题库没有可抽取的题目");
  state.questions = state.questions.slice(0);
  state.answers = {};
  state.submitted = false;
  state.answerVisible = false;
  state.currentIndex = 0;
  state.answerCardPage = 0;
  state.exam = {
    rule,
    scoreMap,
    startedAt: Date.now(),
    endsAt: Date.now() + rule.durationMinutes * 60000,
    submittedAt: null,
    result: null,
  };
  saveExamDraft();
  startExamTimer();
  renderAll();
  await loadCurrentQuestion();
  toast(`已生成模拟卷：${state.questions.length} 题`);
}

async function fetchExamPartQuestions(part, count, chapterId = 0) {
  if (!count) return [];
  const items = [];
  for (const typeId of part.availableTypeIds) {
    const params = new URLSearchParams({
      courseId: state.currentCourse.id,
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

function startExamTimer() {
  stopExamTimer();
  state.examTimer = setInterval(() => {
    renderExamStatus();
    if (state.mode === "exam" && state.exam && !state.submitted && Date.now() >= state.exam.endsAt) {
      submitPaper(true).catch((err) => toast(err.message));
    }
  }, 1000);
  renderExamStatus();
}

function stopExamTimer() {
  if (state.examTimer) clearInterval(state.examTimer);
  state.examTimer = null;
}

function syncExamTimer() {
  if (state.mode !== "exam" || !state.exam) return;
  renderExamStatus();
  if (!state.submitted && Date.now() >= state.exam.endsAt) {
    submitPaper(true).catch((err) => toast(err.message));
  }
}

function renderExamStatus() {
  const host = $("examStatus");
  if (!host) return;
  if (state.mode !== "exam" || !state.exam) {
    host.classList.add("hidden");
    host.innerHTML = "";
    return;
  }
  const left = Math.max(0, state.exam.endsAt - Date.now());
  const minutes = Math.floor(left / 60000);
  const seconds = Math.floor((left % 60000) / 1000);
  const result = state.exam.result;
  host.classList.remove("hidden");
  host.innerHTML = `
    <span>模拟考场</span>
    <b>${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}</b>
    <span>满分 ${state.exam.rule.totalScore} 分</span>
    ${result ? `<span>得分 <b>${formatScore(result.score)}</b> / ${formatScore(result.totalScore)}，正确率 ${result.rate}%</span>` : ""}
  `;
}

function stripText(html) {
  const div = document.createElement("div");
  div.innerHTML = html || "";
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
  const query = new URLSearchParams({ user: state.user });
  query.set("courseId", String(courseId));
  if (!confirm("确定从服务器拉取并更新该题库吗？更新完成后会重新导出题库数据。")) return;
  await pullQuestionBankUpdate(query, btn);
}

async function pullQuestionBankUpdate(query, btn = null) {
  const oldText = btn?.textContent || "";
  if (btn) {
    btn.disabled = true;
    btn.textContent = "更新中...";
  }
  const result = await api(`/api/admin/update-bank?${query}`, { method: "POST" })
    .finally(() => {
      if (btn) {
        btn.disabled = false;
        btn.textContent = oldText || "更新题库";
      }
    });
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
    }
  } finally {
    input.remove();
  }
}

async function handleQuestionBankUpdateResult(result) {
  if (result.reserved || result.mode === "upload") {
    toast(result.message || "当前部署使用上传题库包更新");
    if (isAdmin() && state.adminView === "banks") renderAdminRows(state.adminRows, state.adminFailedCount);
    return;
  }
  if (result.ok && Array.isArray(result.results)) {
    const updated = result.results.filter((r) => Number(r.chapters || 0) > 0 || Number(r.subjects || 0) > 0);
    if (updated.length) {
      toast(`更新完成：${updated.map((r) => `课程${r.courseId}(${r.chapters || 0}章${r.subjects || 0}题)`).join("、")}`);
    } else {
      toast("所有题库已是最新");
    }
    state.adminCourses = [];
    state.adminDataStatus = null;
    state.courses = [];
    state.chapters = [];
    state.types = [];
    state.questions = [];
    await loadCourses();
    if (isAdmin()) await loadAdminCourses().catch(() => {});
    if (isAdmin() && state.adminView === "banks") renderAdminRows(state.adminRows, state.adminFailedCount);
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
  if (state.currentIndex >= 0) await loadCurrentQuestion();
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

function hasOpenModal() {
  return ["correctionModal", "adminUserModal"].some((id) => {
    const el = $(id);
    return el && !el.classList.contains("hidden");
  });
}

function handleGlobalShortcuts(event) {
  if (!state.user || isAdmin() || isTypingTarget(event.target) || hasOpenModal() || event.altKey || event.metaKey) return;
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
    submitPaper().catch((err) => toast(err.message));
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
  if (!event.ctrlKey && !event.shiftKey && event.code === "Space" && state.currentIndex >= 0) {
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
      return;
    }
    event.preventDefault();
    toggleAnswer();
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

document.querySelectorAll(".nav-item[data-mode]").forEach((btn) => {
  btn.onclick = async () => {
    setMobileMenu(false);
    setMobileTools(false);
    setMobileActions(false);
    if (isAdmin()) {
      renderAdminDashboard();
      return;
    }
    const nextMode = btn.dataset.mode;
    setPanelPage(nextMode === "exam" || nextMode === "progress" || nextMode === "training");
    if (nextMode === "practice" && state.mode === "practice") {
      setCoursePicker(!state.pickerOpen);
      renderMode();
      return;
    }
    if (state.mode === "exam" && state.exam && !state.submitted) saveExamDraft();
    if (state.mode === "exam" && nextMode !== "exam") stopExamTimer();
    if (["practice", "wrong", "favorite"].includes(nextMode)) clearActiveTrainingSession();
    state.mode = nextMode;
    if (state.mode === "practice") setCoursePicker(true);
    else setCoursePicker(false);
    renderMode();
    renderWrongFilters();
    renderPracticeContextPanel();
    if (!state.currentCourse) {
      if (state.mode === "exam") renderExamHome();
      return;
    }
    if (state.mode === "exam") {
      if (state.exam && !state.submitted) saveExamDraft();
      state.questions = [];
      state.currentIndex = -1;
      state.submitted = false;
      state.answerVisible = false;
      await loadTypes({ ignoreChapter: true });
      renderAnswerCard();
      updateStats();
      renderExamHome();
      return;
    }
    if (state.mode === "progress" || state.mode === "training") {
      $("questionBody").classList.add("hidden");
      $("emptyState").classList.remove("hidden");
      $("emptyState").innerHTML = state.mode === "training"
        ? `<strong>正在生成智能训练</strong><span>正在读取错题、薄弱章节和今日练习建议。</span>`
        : `<strong>正在生成学习看板</strong><span>正在读取当前科目的进度、训练记录和学习信号。</span>`;
    }
    if (!["exam", "progress", "training"].includes(state.mode)) await loadTypes();
    await loadQuestions();
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
$("mobileMenuBtn").onclick = () => setMobileMenu(!state.mobileMenuOpen);
$("mobileToolsBtn").onclick = () => setMobileTools(!state.mobileToolsOpen);
$("answerCardCollapseBtn").onclick = () => setAnswerCardCollapsed(!state.answerCardCollapsed);
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
$("submitBtn").onclick = () => submitPaper().catch((err) => toast(err.message));
$("resetBtn").onclick = resetPractice;
$("favoriteBtn").onclick = toggleFavorite;
$("mobileActionsBtn").onclick = () => setMobileActions(!state.mobileActionsOpen);
$("noteBtn").onclick = () => $("noteEditor").classList.toggle("hidden");
$("noteEditor").addEventListener("input", debounce(saveNote, 300));
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
if ($("coursePickerBtn")) $("coursePickerBtn").onclick = () => setCoursePicker(!state.pickerOpen);
$("searchToggleBtn").onclick = () => $("searchPanel").classList.toggle("hidden");
$("filterBtn").onclick = () => $("searchPanel").classList.toggle("hidden");
$("printBtn").onclick = openPrintDialog;
$("printCloseBtn").onclick = closePrintDialog;
$("printCancelBtn").onclick = closePrintDialog;
$("printCurrentBtn").onclick = () => printQuestions("current").catch((err) => toast(err.message));
$("printAllBtn").onclick = () => printQuestions("all").catch((err) => toast(err.message));
$("printModal").addEventListener("click", (event) => {
  if (event.target === $("printModal")) closePrintDialog();
});
window.addEventListener("afterprint", cleanupPrintView);
$("zoomInBtn").onclick = () => changeZoom(0.1);
$("zoomOutBtn").onclick = () => changeZoom(-0.1);
$("fullscreenBtn").onclick = () => toggleFullscreen().catch((err) => toast(err.message || "无法进入全屏"));
document.addEventListener("fullscreenchange", updateFullscreenState);
document.addEventListener("keydown", handleGlobalShortcuts);
window.addEventListener("resize", debounce(scheduleNavIndicator, 120));
document.addEventListener("visibilitychange", () => {
  if (state.mode !== "exam" || !state.exam) return;
  if (document.hidden) {
    saveExamDraft();
    stopExamTimer();
  }
  else {
    startExamTimer();
    syncExamTimer();
  }
});
document.querySelector('[data-action="refresh"]').onclick = () => loadCourses().then(() => toast("已重新读取本地题库"));

$("courseSearch").addEventListener("input", debounce(loadCourses));
$("questionSearch").addEventListener("input", debounce(loadQuestions));
$("typeSelect").addEventListener("change", loadQuestions);
$("orderSelect").addEventListener("change", loadQuestions);
$("limitSelect").addEventListener("change", loadQuestions);

window.addEventListener("beforeunload", () => {
  syncCurrentTrainingSession();
  if (state.mode === "exam" && state.exam && !state.submitted) saveExamDraft();
  if (state.user && state.saveTimer) navigator.sendBeacon(`/api/user/save?user=${encodeURIComponent(state.user)}`, JSON.stringify(state.storage));
});

init().catch((err) => {
  $("courseList").innerHTML = `<div class="muted">${escapeHtml(err.message)}</div>`;
});


