// 回归场景。每条场景都对应一个真实发生过的故障，注释里写清「坏掉时会看到什么」。
//
// 约定：场景函数拿到 { page, mock, check, log, sleep }，用 check(条件, 说明) 断言；
// 一条场景里的断言会全部跑完再汇总失败，不会因为第一处失败就丢掉后面的信息。

/** 造一份接近线上的用户数据（~1MB）：beacon 上限 64KB 这个前提必须靠它复现。 */
export function makeSeed({ answers = 4000, notes = 3000, history = 3000 } = {}) {
  const store = { answers: {}, done: {}, correct: {}, wrong: {}, verified: {}, lastSubjectId: 0 };
  for (let i = 0; i < answers; i++) {
    const id = String(10000 + i);
    store.answers[id] = "ABCD"[i % 4];
    store.done[id] = 1;
    if (i % 5 === 0) store.wrong[id] = 1; else store.correct[id] = 1;
  }
  const noteMap = {};
  for (let i = 0; i < notes; i++) {
    noteMap[String(10000 + i)] = "第" + i + "题的笔记：" + "审题时先看设问方向，再回头看题干里的限定词。".repeat(2);
  }
  const hist = [];
  for (let i = 0; i < history; i++) {
    hist.push({ qid: String(10000 + i), at: `2026-0${(i % 9) + 1}-1${i % 9} 10:0${i % 9}:00`, ok: i % 5 !== 0 });
  }
  const data = {
    profile: { name: "e2e", createdAt: "2026-01-01 00:00:00" },
    courses: { "1": store },
    history: hist,
    notes: noteMap,
    settings: {},
  };
  // 不够 1MB 就补笔记正文，保证 payload 远超 64KB（否则这条回归守不住任何东西）
  let pad = 0;
  while (JSON.stringify(data).length < 1_000_000) {
    data.notes[String(900000 + pad)] = "填充笔记 " + pad + " ".padEnd(120, "题");
    pad++;
  }
  return data;
}

const setSaveDelay = (mock, ms) => mock.ctl("/__ctl/config", {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ saveDelay: ms }),
});

const setLoadDelay = (mock, ms) => mock.ctl("/__ctl/config", {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ loadDelay: ms }),
});

/* 让接下来 n 次增量请求被服务端拒掉（400），用来验证「退回整包」这条退路。 */
const rejectNextPatch = (mock, n) => mock.ctl("/__ctl/config", {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({ rejectPatch: n }),
});

export const scenarios = [
  {
    name: "idle-no-draft",
    title: "开着不动：不该囤草稿、不该弹框",
    async run({ page, mock, check, sleep }) {
      await page.goto();
      await sleep(3000);
      const snap = await page.snapshot();
      const server = await mock.ctl("/__ctl/body");
      check(snap.modalVisible === false, "首屏不该弹冲突框");
      check(snap.drafts.length === 0, `首屏不该留下草稿，实有 ${snap.drafts.length} 条`);
      check(snap.rev === server.revision.slice(0, 8), `页面 revision(${snap.rev}) 应与服务器一致(${server.revision.slice(0, 8)})`);
      check(snap.dirty === false, "首屏落定后不该还是脏的");
      return snap;
    },
  },

  {
    name: "save-roundtrip",
    title: "改一笔再等 450ms：应走增量保存，且服务端内容不被合并写坏",
    async run({ page, mock, check, sleep }) {
      await page.goto();
      const before = JSON.parse((await mock.ctl("/__ctl/body")).body);
      await page.evaluate(`state.storage.settings.e2e = "roundtrip"; scheduleSave(); void 0`);
      await page.waitFor("state.storageDirty === false && state.storageRevision && state.storageRevision.length > 8", {
        label: "保存完成并拿到新 revision",
      });
      await sleep(500);
      const snap = await page.snapshot();
      const server = await mock.ctl("/__ctl/body");
      const saves = await mock.ctl("/__ctl/saves");
      check(snap.modalVisible === false, "正常保存不该弹框");
      check(snap.drafts.length === 0, `正常保存不该留草稿，实有 ${snap.drafts.length} 条`);
      check(snap.rev === server.revision.slice(0, 8), `页面 revision(${snap.rev}) 应等于服务器(${server.revision.slice(0, 8)})`);
      check(!saves.some((s) => s.status === 409), "不该出现 409");

      /* 增量保存：1MB 整包在移动网络下是几百毫秒的等待，而这次真正变的只有 settings.e2e。
         坏掉时看到的是「又发了 1MB」或者 ops 为空却回 200。 */
      const ok = saves.filter((s) => s.status === 200).at(-1);
      check(ok.kind === "patch", `改一笔应走增量保存（/api/user/patch），实际「${ok.kind}」`);
      check(ok.ops > 0 && ok.ops < 50, `增量操作数应很小，实际 ${ok.ops} 条`);
      check(ok.wireBytes < 4000, `增量上传应只有几百字节，实际 ${ok.wireBytes}`);

      /* 合并必须只动该动的：笔记 / 答题记录这些大块一条都不能丢（丢数据比慢严重得多）。 */
      const body = JSON.parse(server.body);
      const notesBefore = Object.keys(before.notes || {}).length;
      const answersBefore = Object.keys(before.courses?.["1"]?.answers || {}).length;
      check(body.settings.e2e === "roundtrip", "服务端应收到这次改动");
      check(Object.keys(body.notes || {}).length === notesBefore, `合并后笔记条数应不变，实际 ${Object.keys(body.notes || {}).length} / ${notesBefore}`);
      check(Object.keys(body.courses?.["1"]?.answers || {}).length === answersBefore, `合并后答题记录条数应不变，实际 ${Object.keys(body.courses?.["1"]?.answers || {}).length} / ${answersBefore}`);
      return snap;
    },
  },

  {
    name: "conditional-load-304",
    title: "刷新时内容没变：用 If-None-Match 拿 304，别再把整包下回来",
    async run({ page, mock, check, sleep }) {
      await page.goto();
      await page.waitFor("typeof state !== 'undefined' && !!state.storageRevision", { label: "首屏加载完成" });
      /* 先让本机缓存跟上服务端：改一笔 → 保存落地 → 缓存 3 秒后写入，等下这个窗口。 */
      await page.evaluate(`state.storage.settings.e2eConditional = "1"; scheduleSave(); void 0`);
      await page.waitFor("state.storageDirty === false && state.saveStatus === 'idle'", { label: "保存落地" });
      await sleep(3500);
      const revBefore = await page.evaluate("state.storageRevision");

      await page.reload();
      await page.waitFor("typeof state !== 'undefined' && !!state.storageRevision && state.storageDirty === false", { label: "刷新后加载完成" });
      await sleep(300);
      const snap = await page.snapshot();
      const loads = await mock.ctl("/__ctl/loads");
      const last = loads.at(-1);
      check(last.notModified === true, "刷新后的读取应命中 304（服务端内容没变）");
      check(!!last.conditional, "这次读取应带 If-None-Match（说明用了本机缓存做校验）");
      check(snap.rev === revBefore.slice(0, 8), `304 后页面 revision 应保持 ${revBefore.slice(0, 8)}，实际 ${snap.rev}`);
      check(snap.drafts.length === 0 && snap.modalVisible === false, "304 路径不该留草稿、不该弹框");

      /* 反向用例：服务端被别的设备改过，校验器对不上 → 必须回全量，且页面要看到新内容。 */
      const body = JSON.parse((await mock.ctl("/__ctl/body")).body);
      body.settings.e2eRemote = "written-by-another-device";
      await mock.ctl("/__ctl/seed", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      await page.reload();
      await page.waitFor("typeof state !== 'undefined' && state.storageDirty === false && !!state.storageRevision", { label: "第二次刷新加载完成" });
      await sleep(300);
      const loads2 = await mock.ctl("/__ctl/loads");
      check(loads2.at(-1).notModified !== true, "服务端内容变了就不该回 304");
      check(
        await page.evaluate("state.storage.settings.e2eRemote === 'written-by-another-device'"),
        "回全量后页面应拿到别的设备写入的内容（不能继续吃旧缓存）",
      );
      return snap;
    },
  },

  {
    name: "full-save-gzip",
    title: "没有增量基线时退回整包：仍要 gzip 上传",
    async run({ page, mock, check, sleep }) {
      await page.goto();
      /* 清掉基线 = 客户端手里没有「服务端已知内容」（换账号 / 首次保存 / 刚应用草稿都是这种状态），
         此时必须退回整包，而且整包仍要压缩。 */
      await page.evaluate(`state.syncedBody = ""; state.storage.settings.e2eFull = "1"; scheduleSave(); void 0`);
      await page.waitFor("state.storageDirty === false && state.storageRevision && state.storageRevision.length > 8", {
        label: "整包保存完成",
      });
      await sleep(400);
      const saves = await mock.ctl("/__ctl/saves");
      const ok = saves.filter((s) => s.status === 200).at(-1);
      const server = await mock.ctl("/__ctl/body");
      check(ok.kind !== "patch", `没有基线时不该走增量，实际「${ok.kind}」`);
      check(ok.encoding === "gzip", `约 1MB 的整包应带 Content-Encoding: gzip，实际「${ok.encoding}」`);
      check(ok.wireBytes < ok.bytes / 3, `线上字节数应远小于原文，实际 ${ok.wireBytes} / ${ok.bytes}`);
      check(JSON.parse(server.body).settings.e2eFull === "1", "服务端应收到解压后的原文（revision 仍按原文算）");
      return null;
    },
  },

  {
    name: "patch-rejected-falls-back",
    title: "服务端拒绝增量（400）时：这一次改动必须改走整包保存，不能丢",
    async run({ page, mock, check, sleep }) {
      await page.goto();
      /* 服务端在结构分叉（中间层不是对象、op 非法）时回 400 + user_data_patch_rejected。
         早期实现只在 404/405 时退回整包，400 会把这次改动直接扔掉 —— 用户看到「保存失败」，
         再一刷新改动就没了。这里让 mock 的前 1 次 patch 请求返回 400。 */
      await rejectNextPatch(mock, 1);
      await page.evaluate(`state.storage.settings.e2eFallback = "1"; scheduleSave(); void 0`);
      await page.waitFor("state.storageDirty === false && state.saveStatus === 'idle'", { label: "保存最终落定", timeout: 20000 });
      await sleep(400);

      const saves = await mock.ctl("/__ctl/saves");
      const rejected = saves.filter((s) => s.kind === "patch" && s.status === 400);
      const full = saves.filter((s) => s.kind !== "patch" && s.status === 200).at(-1);
      const server = await mock.ctl("/__ctl/body");
      const snap = await page.snapshot();
      check(rejected.length === 1, `应发出 1 次被拒的增量请求，实际 ${rejected.length} 次`);
      check(!!full, "被拒之后必须退回整包保存，实际没有整包请求");
      check(full && full.encoding === "gzip", `退回整包时仍要 gzip，实际「${full?.encoding}」`);
      check(JSON.parse(server.body).settings.e2eFallback === "1", "整包里应带上这次改动");
      check(snap.rev === server.revision.slice(0, 8), `页面 revision(${snap.rev}) 应等于服务器(${server.revision.slice(0, 8)})`);
      check(snap.modalVisible === false, "退回整包不该弹冲突框");
      check(snap.drafts.length === 0, `不该留草稿，实有 ${snap.drafts.length} 条`);
      check(!saves.some((s) => s.status === 409), "不该出现 409");
      return { saves, snap };
    },
  },

  {
    name: "unload-beacon-toobig",
    title: "1MB 数据 + 保存途中刷新：beacon 一个字节都不发，且不该弹框",
    timeout: 60000,
    async run({ page, mock, check, sleep, log }) {
      await setSaveDelay(mock, 1200);
      await page.goto();
      // 触发一次保存，然后在 POST 还在飞的时候刷新
      await page.evaluate(`state.storage.settings.e2e = "inflight"; scheduleSave(); void 0`);
      await page.waitFor("state.saveStatus === 'saving'", { label: "保存进入 saving（POST 已在飞）", timeout: 8000 });
      await sleep(300);
      await page.reload();
      await sleep(2500); // 等在飞的那次 POST 落地 + 新页面 load 稳定
      const snap1 = await page.snapshot();
      log("刷新后第一次快照", snap1);

      const saves = await mock.ctl("/__ctl/saves");
      const beacon = saves.filter((s) => s.channel === "beacon");
      check(beacon.length === 0, `超 64KB 的 payload 不该走 sendBeacon，实发 ${beacon.length} 条`);
      check(snap1.modalVisible === false, "第一次刷新后不该弹冲突框");
      check(snap1.drafts.length === 0, `第一次刷新后不该留草稿，实有 ${snap1.drafts.length} 条`);

      // 再刷一次：旧实现这里会「每次刷新都弹」，且不再发任何请求
      await page.reload();
      await sleep(1500);
      const snap2 = await page.snapshot();
      log("刷新后第二次快照", snap2);
      check(snap2.modalVisible === false, "第二次刷新后仍不该弹框（这正是用户报的现象）");
      check(snap2.drafts.length === 0, `第二次刷新后草稿应为 0，实有 ${snap2.drafts.length} 条`);
      check((await mock.ctl("/__ctl/saves")).filter((s) => s.channel === "beacon").length === 0, "整个过程都不该出现 beacon 保存");
      return { snap1, snap2, saves };
    },
  },

  {
    name: "stale-draft-no-popup",
    title: "过期草稿：开页不弹框；点「加载服务器」时正好有保存在飞，也不该 409 卡住",
    async run({ page, mock, check, sleep, log }) {
      /* 排查用：把「客户端自己认为的基线」打出来。增量保存出问题时，第一件事就是看
         storageRevision / syncedBody 是否跟服务端一致 —— 不一致就必然会 409。 */
      const probe = async (tag) => {
        const st = await page.evaluate(`({ rev: String(state.storageRevision || "").slice(0, 8), synced: (state.syncedBody || "").length, seq: state.saveSuccessSeq, gen: state.loadGeneration, dirty: state.storageDirty, status: state.saveStatus })`);
        log(tag, st);
        return st;
      };
      await page.goto();
      await probe("开页");
      // ① 模拟「旧版本客户端留下的过期草稿」：revision 与服务器不符
      const injected = await page.evaluate(`(() => {
        const stale = JSON.parse(JSON.stringify(state.storage));
        stale.settings = Object.assign({}, stale.settings, { e2eStale: "local-only" });
        return stashPendingSaveDraft("e2e-stale", { revision: "0".repeat(64), storage: stale }) || "";
      })()`);
      check(!!injected, "注入过期草稿应成功");

      await page.reload();
      await sleep(1200);
      const snap = await page.snapshot();
      log("过期草稿开页快照", snap);
      check(snap.modalVisible === false, "开页不该自动弹冲突框（用户抱怨的就是这个）");
      check(/未补存/.test(snap.label), `状态条应提示「未补存」，实际是「${snap.label}」`);
      check(snap.drafts.length >= 1, "草稿应还在（还没被处理）");
      /* 这里故意**不**要求 storageDirty=true：开页只挂入口、不动 storage，
         卸载时也就不会再多落一条 revision 更旧的草稿。 */
      check(!(await mock.ctl("/__ctl/saves")).some((s) => s.status === 409), "开页阶段不该自动发起补存并撞 409");

      // ② 竞态：保存慢（1.2s 后落地）、load 更慢（2s 后回包）。
      //    load 取的是保存**之前**的快照，却晚于保存回包 —— 这是线上真实出现过的顺序。
      await setSaveDelay(mock, 1200);
      await setLoadDelay(mock, 2000);
      await page.evaluate(`state.storage.settings.e2eRace = "1"; scheduleSave(); void 0`);
      await page.waitFor("state.saveStatus === 'saving'", { label: "保存已发出", timeout: 8000 });
      await probe("② 保存已发出");

      // 用户主动点状态条 → 此时才允许弹框
      await page.click("#saveStatusLabel");
      await sleep(300);
      const opened = await page.snapshot();
      check(opened.modalVisible === true, "点「未补存」应打开处理框");
      await probe("② 弹框后");

      await page.click("#conflictReloadBtn");
      await page.waitFor("document.getElementById('conflictModal').classList.contains('hidden')", { label: "处理框关闭", timeout: 15000 });
      await sleep(2500); // 等在飞的保存和 load 都落定
      await probe("② 处理框关闭 +3.5s");
      const after = await page.snapshot();
      log("加载服务器后快照", after);
      /* 这条是那次「刷新后又弹冲突」的真正断点：读到的快照若早于刚落地的那次保存，
         基线就比服务端旧，下一次保存必然 409。断言在这里，失败信息比等待超时清楚得多。 */
      const serverAfter = await mock.ctl("/__ctl/body");
      check(
        after.rev === serverAfter.revision.slice(0, 8),
        `加载服务器后基线应等于服务端：页面 ${after.rev} / 服务端 ${serverAfter.revision.slice(0, 8)}`,
      );
      check(after.drafts.length === 0, `选「加载服务器数据」后草稿应清空，实有 ${after.drafts.length} 条`);
      check(!/未补存/.test(after.label), `处理后状态条不该再挂「未补存」，实际是「${after.label}」`);

      /* ③ 用户接着答题 —— 这一步才是关键：如果上一步把 revision 拽回了旧值，
         这之后的第一次保存就会 409，状态条又变脏，用户再刷新又是一条过期草稿。 */
      await setLoadDelay(mock, 0);
      await probe("③ 保存前");
      await page.evaluate(`state.storage.settings.e2eAfter = "1"; scheduleSave(); void 0`);
      try {
        await page.waitFor("state.storageDirty === false && state.saveStatus === 'idle'", { label: "继续答题后保存落定", timeout: 15000 });
      } catch (err) {
        await probe("③ 超时现场");
        throw err;
      }
      await sleep(400);
      const post = await page.snapshot();
      const saves = await mock.ctl("/__ctl/saves");
      log("继续答题后快照", post);
      const conflicts = saves.filter((s) => s.status === 409);
      check(conflicts.length === 0, `整个流程不该出现 409，实有 ${conflicts.length} 次（最后一次 exp=${conflicts.at(-1)?.expected} cur=${conflicts.at(-1)?.current}）`);
      check(post.dirty === false, "继续答题保存后不该还是脏的");
      check(post.drafts.length === 0, `不该留下草稿，实有 ${post.drafts.length} 条`);

      await page.reload();
      await sleep(1000);
      const final = await page.snapshot();
      check(final.modalVisible === false, "再刷新一次仍不该弹框");
      check(final.drafts.length === 0, "再刷新一次仍不该有草稿");
      return { snap, opened, after, final };
    },
  },

  {
    name: "calibration-blind-spot",
    title: "掌握校准：确定却答错=盲区、蒙对=侥幸，专练只发这一批题",
    async run({ page, mock, check, sleep }) {
      /* mock 题库只有 4 道题、正确答案都是 A。这里把四种组合各造一道：
           101 确定 + 答对   → 确定·答对
           102 确定 + 答错   → 危险盲区（卡上要报警）
           103 蒙的 + 答对   → 侥幸
           104 不确定 + 答错 → 没把握·答错
         坏掉时看到的是：四个格子数字串位，或者「练盲区」发出来的题单不是 102。 */
      const data = {
        profile: { name: "e2e", createdAt: "2026-01-01 00:00:00" },
        courses: {
          "1": {
            answers: { 101: "A", 102: "B", 103: "A", 104: "B" },
            done: { 101: 1, 102: 1, 103: 1, 104: 1 },
            correct: { 101: 1, 103: 1 },
            wrong: { 102: 1, 104: 1 },
            verified: {
              101: { answer: "A", at: "2026-01-01 10:00:00" },
              102: { answer: "B", at: "2026-01-01 10:01:00" },
              103: { answer: "A", at: "2026-01-01 10:02:00" },
              104: { answer: "B", at: "2026-01-01 10:03:00" },
            },
            /* 只给 101 留一条带 correct 的账：验证「新口径优先、老数据靠归属兜底」两条路都能算对。 */
            resultLog: { 101: { scope: "round:practice:1:2026-01-01", answer: "A", at: "2026-01-01 10:00:00", correct: true } },
            lastSubjectId: 101,
          },
        },
        confidence: { 101: "确定", 102: "确定", 103: "蒙的", 104: "不确定" },
        /* 105 从没标过信心，只是反复错（count 3 / stage 2）——
           「疑似盲区」这一路的唯一输入，用来验证没标信心的人也能得到入口。 */
        wrong: {
          105: {
            courseId: 1,
            count: 3,
            stage: 2,
            at: "2026-01-01 09:00:00",
            lastReviewAt: "2026-01-01 09:30:00",
          },
        },
        settings: {},
      };
      await mock.ctl("/__ctl/seed", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(data),
      });
      await page.goto();
      await page.waitFor("typeof state !== 'undefined' && !!state.storageRevision", { label: "首屏加载完成" });
      await page.waitFor("state.analysisCourseId === 1 && state.analysisQuestions.length > 0", { label: "全科分析题加载完成" });

      const card = await page.evaluate(`(() => {
        state.mode = "progress";
        renderProgress();
        const el = document.querySelector(".calibration-card");
        return {
          text: el ? el.textContent.replace(/\\s+/g, " ").trim() : "",
          cells: Array.from(document.querySelectorAll(".cal-cell b")).map((b) => b.textContent.trim()),
          blindDisabled: document.querySelector("#blindSpotBtn")?.disabled,
          luckyDisabled: document.querySelector("#luckyReviewBtn")?.disabled,
          suspectedDisabled: document.querySelector("#suspectedBtn")?.disabled,
          suspectedText: document.querySelector("#suspectedBtn")?.textContent.trim() || "",
          cells: Array.from(document.querySelectorAll(".cal-cell b")).map((b) => b.textContent.trim()),
        };
      })()`);
      check(!!card.text, "学习看板上应有「掌握校准」卡");
      check(card.cells.join(",") === "1,1,1,1", `四个格子应各 1 题（确定错/确定对/蒙对/没把握错），实际 ${card.cells.join(",")}`);
      check(card.text.includes("盲区 1 题"), `卡上应写「盲区 1 题」，实际：${card.text}`);
      check(card.text.includes("侥幸 1 题"), `卡上应写「侥幸 1 题」，实际：${card.text}`);
      check(card.blindDisabled === false && card.luckyDisabled === false, "有盲区题时两个入口都该可点");
      /* 105 没标信心 → 不能进四象限（否则等于凭空捏造「确定/不确定」），但必须有独立入口。 */
      check(card.cells.join(",") === "1,1,1,1" && card.suspectedText.includes("1"),
        `疑似盲区应单列 1 题且不污染四象限，实际按钮「${card.suspectedText}」、格子 ${card.cells.join(",")}`);
      check(card.suspectedDisabled === false, `有反复错的题时「练疑似盲区」该可点，实际 disabled=${card.suspectedDisabled}`);

      /* 盲区专练：发出去的必须只有 102 —— 卡上写几道，点进去就几道。 */
      await page.evaluate("document.querySelector('#blindSpotBtn').click()");
      await page.waitFor("state.mode === 'smart' && !!activeTrainingSession()", { label: "进入盲区专练" });
      const blind = await page.evaluate(`(() => {
        const s = activeTrainingSession();
        return { type: s.type, ids: (s.ids || []).map(String), title: s.sourceTitle || "" };
      })()`);
      check(blind.type === "blind", `会话 type 应为 blind（不能顶掉今日强化），实际 ${blind.type}`);
      check(blind.ids.join(",") === "102", `盲区专练题单应只含 102，实际 ${blind.ids.join(",")}`);
      check(blind.title.includes("盲区"), `会话标题应写明是盲区，实际「${blind.title}」`);

      /* 侥幸复盘：刷新回到看板再点，题单必须换成 103（不是把上一轮的盲区题单续上）。 */
      await page.reload();
      await page.waitFor("typeof state !== 'undefined' && state.analysisCourseId === 1 && state.analysisQuestions.length > 0", { label: "刷新后分析题就绪" });
      await page.evaluate(`(() => { state.mode = "progress"; renderProgress(); document.querySelector("#luckyReviewBtn").click(); })()`);
      await page.waitFor("state.mode === 'smart' && activeTrainingSession()?.type === 'lucky'", { label: "进入侥幸复盘" });
      const lucky = await page.evaluate("(() => { const s = activeTrainingSession(); return (s.ids || []).map(String); })()");
      check(lucky.join(",") === "103", `侥幸复盘题单应只含 103，实际 ${lucky.join(",")}`);

      /* 疑似盲区专练：题单必须只含 105（没标过信心、只靠反复错推断出来的那道）。 */
      await page.reload();
      await page.waitFor("typeof state !== 'undefined' && state.analysisCourseId === 1 && state.analysisQuestions.length > 0", { label: "刷新后分析题就绪（疑似盲区）" });
      await page.evaluate(`(() => { state.mode = "progress"; renderProgress(); document.querySelector("#suspectedBtn").click(); })()`);
      await page.waitFor("state.mode === 'smart' && !!activeTrainingSession()", { label: "进入疑似盲区专练" });
      const suspected = await page.evaluate(`(() => {
        const s = activeTrainingSession();
        return { type: s.type, ids: (s.ids || []).map(String), title: s.sourceTitle || "", loaded: state.questions.length };
      })()`);
      check(suspected.ids.join(",") === "105", `疑似盲区专练题单应只含 105，实际 ${suspected.ids.join(",")}`);
      check(suspected.type === "blind", `疑似盲区也走盲区会话，type 应为 blind，实际 ${suspected.type}`);
      check(suspected.loaded === 1, `疑似盲区应能真的取到 1 道题，实际 ${suspected.loaded}`);
      const snap = await page.snapshot();
      check(snap.modalVisible === false, "校准入口不该牵出冲突框");
      return { card, blind, lucky, suspected };
    },
  },

  {
    name: "wrong-reason-plan",
    title: "错因处方：按错因给不同练法（概念不清掺同章新题、计算错误只重做）",
    async run({ page, mock, check }) {
      /* 守两件事：
         1) 错因不是标签，而是分支 ——「概念不清」补同章没做过的新题，
            「计算错误」只重做原题（掺新题会掩盖过程习惯问题）；
         2) 错因从哪来：答错当场问一句，点一下就能归类（事后补标注没人做）。 */
      const data = {
        profile: { name: "e2e", createdAt: "2026-01-01 00:00:00" },
        courses: {
          "1": {
            answers: { 101: "B", 102: "B", 103: "B" },
            done: { 101: 1, 102: 1, 103: 1 },
            correct: {},
            wrong: { 101: 1, 102: 1, 103: 1 },
            verified: {
              101: { answer: "B", at: "2026-01-01 10:00:00" },
              102: { answer: "B", at: "2026-01-01 10:01:00" },
              103: { answer: "B", at: "2026-01-01 10:02:00" },
            },
            lastSubjectId: 101,
          },
        },
        /* 三条错题：两条概念不清、一条计算错误。104/105 从没答过，是同章候选新题。 */
        wrong: {
          101: { courseId: 1, count: 1, stage: 0, at: "2026-01-01 10:00:00" },
          102: { courseId: 1, count: 1, stage: 0, at: "2026-01-01 10:01:00" },
          103: { courseId: 1, count: 1, stage: 0, at: "2026-01-01 10:02:00" },
        },
        wrongReasons: { 101: "概念不清", 102: "概念不清", 103: "计算错误" },
        settings: {},
      };
      await mock.ctl("/__ctl/seed", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(data),
      });
      await page.goto();
      await page.waitFor("typeof state !== 'undefined' && !!state.storageRevision", { label: "首屏加载完成" });
      await page.waitFor("state.analysisCourseId === 1 && state.analysisQuestions.length > 0", { label: "全科分析题加载完成" });

      const card = await page.evaluate(`(() => {
        state.mode = "progress";
        renderProgress();
        const el = document.querySelector(".reason-card");
        return {
          text: el ? el.textContent.replace(/\\s+/g, " ").trim() : "",
          rows: Array.from(document.querySelectorAll("[data-reason-practice]")).map((b) => b.textContent.replace(/\\s+/g, " ").trim()),
        };
      })()`);
      check(card.rows.some((t) => t.includes("概念不清") && t.includes("2 题")), `处方卡应列出「概念不清 · 2 题」，实际 ${JSON.stringify(card.rows)}`);
      check(card.rows.some((t) => t.includes("计算错误") && t.includes("1 题")), `处方卡应列出「计算错误 · 1 题」，实际 ${JSON.stringify(card.rows)}`);

      /* 概念不清 = 先重做错题，再补同章没做过的新题（104/105）。 */
      await page.click('[data-reason-practice="概念不清"]');
      await page.waitFor("typeof state !== 'undefined' && state.mode === 'smart'", { label: "进入概念不清处方" });
      const concept = await page.evaluate(`(() => {
        const s = activeTrainingSession();
        return { ids: s.ids.map(Number), type: s.type, title: s.sourceTitle };
      })()`);
      check(concept.type === "reason", `处方训练的类型应是 reason，实际「${concept.type}」`);
      check(concept.title.includes("概念不清"), `题单标题应带错因，实际「${concept.title}」`);
      check(JSON.stringify(concept.ids) === JSON.stringify([101, 102, 104, 105]),
        `概念不清应重做 101/102 并补同章新题 104/105，实际 ${JSON.stringify(concept.ids)}`);

      /* 计算错误 = 只重做错题，不掺新题。 */
      await page.evaluate('state.mode = "progress"; renderProgress()');
      await page.click('[data-reason-practice="计算错误"]');
      await page.waitFor("typeof state !== 'undefined' && state.mode === 'smart'", { label: "进入计算错误处方" });
      const calc = await page.evaluate(`(() => {
        const s = activeTrainingSession();
        return { ids: s.ids.map(Number), title: s.sourceTitle };
      })()`);
      check(JSON.stringify(calc.ids) === JSON.stringify([103]),
        `计算错误只该重做原题 103，实际 ${JSON.stringify(calc.ids)}`);

      /* 错因要从答题现场来：105 答错后应出现「错在哪」，点一下即写库。 */
      await page.reload();
      await page.waitFor("typeof state !== 'undefined' && !!state.storageRevision", { label: "刷新后加载完成" });
      await page.evaluate(`(async () => {
        state.mode = "practice";
        await loadQuestions();
        state.currentIndex = state.questions.findIndex((q) => Number(q.id) === 105);
        await loadCurrentQuestion();
      })()`);
      await page.waitFor("state.questions.some((q) => Number(q.id) === 105 && !!q.detail)", { label: "105 题题干就绪" });
      await page.evaluate(`(() => {
        const idx = state.questions.findIndex((q) => Number(q.id) === 105 && !!q.detail);
        state.currentIndex = idx;
        renderQuestion();
        const q = state.questions[idx].detail;
        saveSubjectiveAnswer(q, "B");
        confirmCurrentAnswer();
      })()`);
      const ask = await page.evaluate(`(() => {
        const el = document.getElementById("wrongReasonAsk");
        return { text: el ? el.textContent.replace(/\\s+/g, " ").trim() : "", buttons: el ? el.querySelectorAll("[data-ask-reason]").length : 0 };
      })()`);
      check(ask.text.includes("错在哪"), `答错后应就地问「错在哪」，实际「${ask.text}」`);
      check(ask.buttons === 5, `应有五个错因按钮，实际 ${ask.buttons}`);

      await page.click('#wrongReasonAsk [data-ask-reason="计算错误"]');
      const labeled = await page.evaluate(`(() => ({
        value: (state.storage.wrongReasons || {})[105] || "",
        gone: !document.getElementById("wrongReasonAsk"),
        dirty: state.storageDirty,
      }))()`);
      check(labeled.value === "计算错误", `点「计算错误」应写进 wrongReasons[105]，实际「${labeled.value}」`);
      check(labeled.gone, "标完错因后提示应立刻消失");
      await page.waitFor("!state.storageDirty", { label: "错因保存完成" });

      /* 刷新后处方卡要把新标的那条算进去：概念不清 2 + 计算错误 2。 */
      await page.reload();
      await page.waitFor("typeof state !== 'undefined' && !!state.storageRevision", { label: "再次刷新完成" });
      const after = await page.evaluate(`(async () => {
        /* 上次退出时停在处方训练里，刷新会恢复成 smart 模式；处方卡只读错题与错因，
           不依赖分析题，直接切回看板即可。 */
        state.mode = "progress";
        renderProgress();
        const el = document.querySelector(".reason-card");
        return el ? el.textContent.replace(/\\s+/g, " ").trim() : "";
      })()`);
      check(after.includes("4 题已归类"), `刷新后应统计到 4 条已归类，实际「${after}」`);
      check(after.includes("计算错误 · 2 题"), `刷新后计算错误应为 2 题，实际「${after}」`);
    },
  },
  {
    name: "confidence-ask",
    title: "答错就地标信心：点一下即写库，看板立刻能分出盲区",
    async run({ page, mock, check }) {
      /* 这条守的是「数据从哪来」：信心按钮原来只藏在侧栏标签面板里，
         真实账号抽样答过上千题只标了几次 —— 等于没有数据。
         这里验证：答错当场出现提示 → 点一下写进 storage.confidence →
         提示消失 → 看板把这道题算成「确定·答错」（盲区）；答对时不打扰。 */
      const data = {
        profile: { name: "e2e", createdAt: "2026-01-01 00:00:00" },
        courses: {
          "1": {
            answers: {},
            done: {},
            correct: {},
            wrong: {},
            verified: {},
            lastSubjectId: 101,
          },
        },
        settings: {},
      };
      await mock.ctl("/__ctl/seed", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(data),
      });
      await page.goto();
      await page.waitFor("typeof state !== 'undefined' && !!state.storageRevision", { label: "首屏加载完成" });
      /* 空进度账号开页停在「智能训练」页（mode=training，没有题）。
         先进答题模式取一份题单，再切到 101。
         注意：首屏的 restoreLastCourse 可能在后台再拉一次题单把 state.questions
         覆盖掉，单次 loadQuestions 有概率抢不过它 —— 这里重试到 101 真的带 detail 为止。 */
      await page.evaluate(`(async () => {
        state.mode = "practice";
        for (let i = 0; i < 20; i += 1) {
          await loadQuestions();
          const idx = state.questions.findIndex((q) => Number(q.id) === 101);
          if (idx >= 0) {
            state.currentIndex = idx;
            await loadCurrentQuestion();
            renderQuestion();
            if (state.questions[idx] && state.questions[idx].detail) return;
          }
          await new Promise((resolve) => setTimeout(resolve, 300));
        }
        throw new Error("101 题没进题单");
      })()`);
      await page.waitFor("state.questions.some((q) => Number(q.id) === 101 && !!q.detail)",
        { label: "101 题题干就绪" });

      /* 走真实路径答题：saveSubjectiveAnswer 同时写 state.answers 与 courseStore.answers
         （校准卡读的是后者，直接改 state.answers 不算数）。 */
      await page.evaluate(`(() => {
        const idx = state.questions.findIndex((q) => Number(q.id) === 101 && !!q.detail);
        if (idx < 0) throw new Error("101 题没在题单里");
        state.currentIndex = idx;
        renderQuestion();
        const q = state.questions[idx].detail;
        saveSubjectiveAnswer(q, "B");
        confirmCurrentAnswer();
      })()`);
      const ask = await page.evaluate(`(() => {
        const el = document.getElementById("confidenceAsk");
        return { text: el ? el.textContent.replace(/\\s+/g, " ").trim() : "", buttons: el ? el.querySelectorAll("[data-ask-confidence]").length : 0 };
      })()`);
      check(ask.text.includes("以为"), `答错后应就地问「你以为自己会吗」，实际「${ask.text}」`);
      check(ask.buttons === 3, `应有三个信心按钮，实际 ${ask.buttons}`);

      await page.click('#confidenceAsk [data-ask-confidence="确定"]');
      const labeled = await page.evaluate(`(() => ({
        value: (state.storage.confidence || {})[101] || "",
        gone: !document.getElementById("confidenceAsk"),
      }))()`);
      check(labeled.value === "确定", `点「以为会」应写进 confidence[101]，实际「${labeled.value}」`);
      check(labeled.gone, "标完之后提示应立刻消失");

      /* 答对不打扰：102 选 A（正确）交卷后不该出现同一个提示。 */
      await page.evaluate(`(async () => {
        state.currentIndex = state.questions.findIndex((q) => Number(q.id) === 102);
        await loadCurrentQuestion();
      })()`);
      await page.waitFor("Number(state.questions[state.currentIndex]?.id) === 102 && !!state.questions[state.currentIndex]?.detail",
        { label: "102 题题干就绪" });
      await page.evaluate(`(() => {
        saveSubjectiveAnswer(state.questions[state.currentIndex].detail, "A");
        confirmCurrentAnswer();
      })()`);
      await page.waitFor("Number(state.questions[state.currentIndex]?.id) === 102", { label: "切到 102" });
      const afterRight = await page.evaluate("!!document.getElementById('confidenceAsk')");
      check(afterRight === false, "答对时不该出现信心提示");

      /* 等这几次改动落盘再刷新，刷新后看板应把它算成盲区。 */
      await page.waitFor("!state.storageDirty", { label: "改动已保存", timeout: 20000 });
      await page.reload();
      await page.waitFor("typeof state !== 'undefined' && !!state.storageRevision", { label: "刷新后加载完成" });
      /* 刷新会恢复上次的答题模式（不会自动拉全科分析），直接取一次，
         否则看板的四象限永远是「分析还在加载」。 */
      const card = await page.evaluate(`(async () => {
        await loadAnalysisQuestions();
        state.mode = "progress";
        renderProgress();
        const el = document.querySelector(".calibration-card");
        return {
          text: el ? el.textContent.replace(/\\s+/g, " ").trim() : "",
          cells: Array.from(document.querySelectorAll(".cal-cell b")).map((b) => b.textContent.trim()),
          confidence: (state.storage.confidence || {})[101] || "",
        };
      })()`);
      check(card.confidence === "确定", `刷新后 confidence[101] 应仍是「确定」，实际「${card.confidence}」`);
      check(card.cells[0] === "1", `看板应把这题算进「确定·答错」，实际四格 ${card.cells.join(",")}`);
      check(card.text.includes("盲区 1 题"), `卡上应写「盲区 1 题」，实际：${card.text}`);

      const snap = await page.snapshot();
      check(snap.modalVisible === false, "标注流程不该牵出冲突框");
      return { ask, labeled, card };
    },
  },
  {
    name: "crowd-stats",
    title: "全站答题统计：答完一题看到「全站 N 次作答 · M% 答错」，上报真的落到服务端",
    async run({ page, mock, check }) {
      /* 这条同时守三件容易一起坏的事：
         ① 上报队列真的会发出去（含会话令牌），发完清空；失败时**不能**清；
         ② 答案没展开之前不能提前透露全站比例，展开后才渲染；
         ③ 学习看板的「全站参照」用同一份数据，且把服务端的**答错率**换算成正确率。 */
      const data = {
        profile: { name: "e2e", createdAt: "2026-01-01 00:00:00" },
        courses: {
          "1": {
            answers: {},
            done: {},
            correct: {},
            wrong: {},
            verified: {},
            lastSubjectId: 101,
          },
        },
        settings: {},
      };
      await mock.ctl("/__ctl/seed", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(data),
      });
      /* 预置：101 已经被作答 9 次、其中 6 次答错（67% 答错），已经过了「满 3 次」门槛。 */
      await mock.ctl("/__ctl/stats", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ rows: [{ q: 101, attempts: 9, wrong: 6 }] }),
      });
      await page.goto();
      await page.waitFor("typeof state !== 'undefined' && !!state.storageRevision", { label: "首屏加载完成" });
      await page.evaluate(`(async () => {
        state.mode = "practice";
        for (let i = 0; i < 20; i += 1) {
          await loadQuestions();
          const idx = state.questions.findIndex((q) => Number(q.id) === 101);
          if (idx >= 0) {
            state.currentIndex = idx;
            await loadCurrentQuestion();
            renderQuestion();
            if (state.questions[idx] && state.questions[idx].detail) return;
          }
          await new Promise((resolve) => setTimeout(resolve, 300));
        }
        throw new Error("101 题没进题单");
      })()`);
      await page.waitFor("state.questions.some((q) => Number(q.id) === 101 && !!q.detail)",
        { label: "101 题题干就绪" });

      /* 答案还藏着的时候，全站行不该出现（提前看到比例会影响作答）。 */
      const before = await page.evaluate("!!document.getElementById('crowdStat')");
      check(before === false, "答案未展开时不该出现全站比例");

      /* 走真实路径答错：101 的正确答案是 A，这里选 B。 */
      await page.evaluate(`(() => {
        const idx = state.questions.findIndex((q) => Number(q.id) === 101 && !!q.detail);
        if (idx < 0) throw new Error("101 题没在题单里");
        state.currentIndex = idx;
        renderQuestion();
        const q = state.questions[idx].detail;
        saveSubjectiveAnswer(q, "B");
        confirmCurrentAnswer();
      })()`);
      await page.waitFor("!!document.getElementById('crowdStat')", { label: "全站行出现", timeout: 10000 });
      await page.waitFor(`document.getElementById("crowdStat").textContent.includes("9 次作答")`,
        { label: "全站比例渲染完成", timeout: 10000 });
      const line = await page.evaluate(`document.getElementById("crowdStat").textContent.replace(/\\s+/g, " ").trim()`);
      check(line.includes("9 次作答") && line.includes("67%") && line.includes("答错"),
        `应显示「9 次作答 · 67% 答错」，实际「${line}」`);
      check(line.includes("你答错了"), `答错后应标出「你答错了」，实际「${line}」`);

      /* 等 4 秒的去抖上报真的发出、队列清空（失败会留着重试，所以「清空」本身是断言）。 */
      await page.waitFor("(() => { try { return JSON.parse(localStorage.getItem('yt.crowdQueue.v1') || '[]').length === 0; } catch (error) { return false; } })()",
        { label: "统计队列已发完", timeout: 15000 });
      const statsCtl = await mock.ctl("/__ctl/stats");
      check(Array.isArray(statsCtl.posts) && statsCtl.posts.length >= 1, `应至少上报过一次，实际 ${statsCtl.posts?.length || 0} 次`);
      const posted = (statsCtl.posts || []).flatMap((post) => post.items || []);
      check(posted.some((item) => Number(item.q) === 101 && item.ok === false), "上报内容应包含「101 答错」");
      check((statsCtl.posts || []).every((post) => Array.isArray(post.items) && post.items.length > 0),
        "每次上报都该带内容（空批次不该发请求）");
      check(Number(statsCtl.stats["101"]?.attempts) === 10 && Number(statsCtl.stats["101"]?.wrong) === 7,
        `服务端应把 101 累加到 10 次 / 7 错，实际 ${JSON.stringify(statsCtl.stats["101"])}`);

      /* 学习看板的「全站参照」：10 次作答、答错 7 次 → 正确率 30%。 */
      await page.evaluate(`(async () => {
        await loadAnalysisQuestions();
        state.mode = "progress";
        renderProgress();
      })()`);
      await page.waitFor(`(() => { const el = document.querySelector(".study-dashboard"); const text = el ? el.textContent : ""; return text.includes("全站参照") && text.includes("全站："); })()`,
        { label: "全站参照卡渲染完成", timeout: 10000 });
      const card = await page.evaluate(`(() => {
        const el = Array.from(document.querySelectorAll(".study-card")).find((node) => node.textContent.includes("全站参照"));
        return el ? el.textContent.replace(/\\s+/g, " ").trim() : "";
      })()`);
      check(card.includes("10 次作答"), `看板应显示全站 10 次作答，实际「${card}」`);
      check(card.includes("正确率 30%"), `看板应把答错率换算成正确率 30%，实际「${card}」`);
      check(card.includes("你：已确认 1 题"), `看板应写出自己的口径，实际「${card}」`);

      const snap = await page.snapshot();
      check(snap.modalVisible === false, "统计流程不该牵出冲突框");
      return { line, card, posted: posted.length };
    },
  },

  {
    name: "ai-explain",
    title: "AI 解析：后台没开就不出现，开了才生成，同一题全站只生成一次",
    async run({ page, mock, check }) {
      /* 守的是三件事：① 开关关闭时前台完全看不到入口；② 生成走的是真实接口并且正文能落进 DOM；
         ③ 第二个人（同一题重开）读的是服务端缓存，不再消耗一次调用。 */
      await page.goto();
      await page.waitFor("typeof state !== 'undefined' && !!state.storageRevision", { label: "首屏加载完成" });

      const openQuestion = (id) => `(async () => {
        state.mode = "practice";
        await loadQuestions();
        state.currentIndex = state.questions.findIndex((q) => Number(q.id) === ${id});
        await loadCurrentQuestion();
        state.answerVisible = true;
        renderQuestion();
      })()`;

      await page.evaluate(openQuestion(101));
      await page.waitFor("Number(state.questions[state.currentIndex]?.id) === 101 && !!state.questions[state.currentIndex]?.detail", { label: "101 题就绪" });
      await page.waitFor("state.aiStatus !== null", { label: "AI 状态已取回" });
      const closed = await page.evaluate(`(() => ({
        enabled: state.aiStatus && state.aiStatus.enabled,
        btn: !!document.getElementById("aiExplainBtn"),
      }))()`);
      check(closed.enabled === false, "后台没开时状态应为关闭");
      check(closed.btn === false, "后台没开时不该出现 AI 按钮");

      /* 管理员打开开关（真实部署里这一步在后台「数据」页做）。
         这里不整页刷新：刷新会跟应用启动时的 restoreLastCourse 抢题单，
         而本场景要验的是「状态重新取回后按钮出现」——清掉内存里的状态缓存重绘即可。 */
      await mock.ctl("/__ctl/ai", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ enabled: true }),
      });
      await page.evaluate(`(() => {
        state.aiStatus = null;
        state.aiStatusPromise = null;
        renderQuestion();
      })()`);
      await page.waitFor("!!document.getElementById('aiExplainBtn')", { label: "AI 按钮出现" });

      await page.click("#aiExplainBtn");
      await page.waitFor("(document.querySelector('.ai-explain-body')?.textContent || '').length > 0", { label: "讲解正文出现" });
      const first = await page.evaluate(`(document.querySelector('.ai-explain-body')?.textContent || "").trim()`);
      const callsAfterFirst = (await mock.ctl("/__ctl/ai")).calls;
      check(first.includes("第 1 次生成"), `正文应是服务端返回的内容，实际「${first}」`);
      check(callsAfterFirst === 1, `第一次生成应只调用一次上游，实际 ${callsAfterFirst} 次`);

      /* 同题重开（模拟另一个人，内存缓存清空）：服务端直接回缓存，调用次数不能再涨。 */
      await page.evaluate(`(() => {
        delete state.aiExplains[101];
        state.answerVisible = true;
        renderQuestion();
      })()`);
      await page.waitFor("!!document.getElementById('aiExplainBtn')", { label: "重开后 AI 按钮出现" });
      await page.click("#aiExplainBtn");
      await page.waitFor("(document.querySelector('.ai-explain-body')?.textContent || '').length > 0", { label: "缓存正文出现" });
      const second = await page.evaluate(`(document.querySelector('.ai-explain-body')?.textContent || "").trim()`);
      const callsAfterSecond = (await mock.ctl("/__ctl/ai")).calls;
      check(second === first, `同题应拿到同一份缓存正文，实际「${second}」`);
      check(callsAfterSecond === 1, `缓存命中不该再调用上游，实际 ${callsAfterSecond} 次`);

      /* 上游失败要能看见原因、并且允许重试（按钮回到可点状态）。 */
      await mock.ctl("/__ctl/ai", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ fail: true }),
      });
      await page.evaluate(`(async () => {
        state.currentIndex = state.questions.findIndex((q) => Number(q.id) === 102);
        await loadCurrentQuestion();
        /* 换题会收起答案（loadCurrentQuestion 重置 answerVisible）；
           AI 入口只在答案可见时出现，这里显式展开。 */
        state.answerVisible = true;
        renderQuestion();
      })()`);
      await page.waitFor("!!document.getElementById('aiExplainBtn')", { label: "102 的 AI 按钮出现" });
      await page.click("#aiExplainBtn");
      await page.waitFor("(document.querySelector('.ai-explain-error')?.textContent || '').length > 0", { label: "失败原因出现" });
      const failed = await page.evaluate(`(() => {
        const btn = document.getElementById("aiExplainBtn");
        return {
          error: (document.querySelector(".ai-explain-error")?.textContent || "").trim(),
          retry: btn ? btn.textContent.trim() : "",
          disabled: btn ? btn.disabled : null,
        };
      })()`);
      check(failed.error.includes("暂时不可用"), `失败原因应显示给用户，实际「${failed.error}」`);
      check(failed.retry === "再试一次" && failed.disabled === false, `失败后按钮应可重试，实际「${failed.retry}」/${failed.disabled}`);

      const snap = await page.snapshot();
      check(snap.modalVisible === false, "AI 流程不该牵出冲突框");
      return { first, callsAfterFirst, callsAfterSecond, failed };
    },
  },

  {
    name: "ai-admin-config",
    title: "后台配置 AI：读回掩码、保存生效，前台立刻能生成",
    async run({ page, mock, check }) {
      await mock.ctl("/__ctl/admin", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ on: true }),
      });
      await page.goto();
      await page.waitFor("typeof state !== 'undefined' && !!state.storageRevision && !!state.user", { label: "管理员首屏加载完成" });
      await page.waitFor("!document.getElementById('adminDashboard')?.classList.contains('hidden')", { label: "管理看板打开" });
      /* 看板先去掉 hidden、再逐个用户拉数据，工具栏（页签）是最后才写进去的：
         等页签出现再点，否则点到的还是上一屏的旧 DOM。 */
      await page.waitFor("!!document.querySelector('[data-admin-view=\"data\"]')", { label: "管理页签就绪" });
      await page.click('[data-admin-view="data"]');
      await page.waitFor("!!document.getElementById('aiCfgBaseUrl')", { label: "AI 配置表单出现" });

      const initial = await page.evaluate(`(() => ({
        enabled: document.getElementById("aiCfgEnabled").checked,
        baseUrl: document.getElementById("aiCfgBaseUrl").value,
        model: document.getElementById("aiCfgModel").value,
        keyPlaceholder: document.getElementById("aiCfgKey").placeholder,
      }))()`);
      check(initial.enabled === false, "默认应为关闭");
      check(initial.baseUrl.includes("openai.com"), `应读回已保存的接口地址，实际「${initial.baseUrl}」`);

      /* 填密钥 + 打开开关后保存；密钥留空语义另测：保存后 placeholder 应变成掩码。 */
      await page.evaluate(`(() => {
        document.getElementById("aiCfgKey").value = "sk-e2e-1234";
        document.getElementById("aiCfgEnabled").checked = true;
        document.getElementById("aiCfgSave").click();
      })()`);
      await page.waitFor("document.getElementById('aiCfgKey')?.placeholder.includes('sk-****1234')", { label: "保存后读到密钥掩码" });
      const saved = await mock.ctl("/__ctl/ai");
      check(saved.ai.enabled === true, "保存后服务端开关应为打开");
      check(saved.ai.apiKeyMasked === "sk-****1234", `服务端应只存/回掩码，实际「${saved.ai.apiKeyMasked}」`);
      const status = await page.evaluate(`fetch("/api/ai/status").then((r) => r.json())`);
      check(status.enabled === true, "前台状态接口应立刻读到开启（无需重启）");
      return { initial, saved: saved.ai };
    },
  },
];
