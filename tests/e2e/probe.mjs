// 一次性排查脚本：开页后把「页面文本 / 请求 / 控制台错误 / state」原样打出来。
// 用法：node tests/e2e/probe.mjs
import { startMockServer } from "./mock-server.mjs";
import { launchBrowser, sleep } from "./driver.mjs";
import { makeSeed } from "./scenarios.mjs";

const mock = await startMockServer();
await mock.ctl("/__ctl/seed", { method: "POST", body: JSON.stringify(makeSeed({ answers: 20, notes: 5, history: 5 })) });
const page = await launchBrowser({ baseUrl: mock.url, artifactDir: "tests/e2e/.artifacts/probe" });
try {
  await page.send("Page.navigate", { url: mock.url + "/" });
  await sleep(4000);
  console.log("--- requests ---");
  for (const r of page.netLog) console.log(JSON.stringify(r));
  console.log("--- console ---");
  for (const c of page.consoleLog) console.log(c.type, "|", c.text);
  console.log("--- state ---");
  console.log(await page.evaluate(`JSON.stringify({
    hasState: typeof state !== "undefined",
    user: typeof state !== "undefined" ? state.user : null,
    loginHidden: document.getElementById("loginView")?.classList.contains("hidden"),
    appHidden: document.getElementById("appShell")?.classList.contains("hidden"),
    token: !!localStorage.getItem("yunxi-token"),
    bodyText: document.body.innerText.replace(/\\s+/g, " ").slice(0, 300)
  })`).catch((e) => "evaluate 失败：" + e.message));
} finally {
  await page.close();
  await mock.close();
}
