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
];
