/* Headless harness for wwwroot/app.js: stubs just enough DOM/fetch/timers to
   drive the run+poll state machine and assert the UI can never be left stuck
   on "running".

   Run:  node tests/client/app.harness.mjs
   Or point it at another copy of the file to compare behaviour:
         node tests/client/app.harness.mjs <path-to-app.js>          */
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

// optional argv[2] lets the same scenarios run against another copy of app.js
const TARGET = process.argv[2] ?? fileURLToPath(new URL("../../src/HazardRecon.Web/wwwroot/app.js", import.meta.url));
const SRC = readFileSync(TARGET, "utf8");
console.log(`target: ${TARGET}\n`);

function makeEl(id = "") {
  const kids = [];
  const el = {
    id, className: "", textContent: "", value: "", src: "", href: "",
    // file inputs expose a FileList; the picker code reads .files and each
    // file's webkitRelativePath, which is the only clue to the folder's name
    files: [],
    disabled: false, scrollTop: 0, scrollHeight: 0, parentNode: null,
    // the progress bar sets style.width, so a plain bag of properties is enough
    style: {},
    classList: {
      _s: new Set(),
      add(c) { this._s.add(c); }, remove(c) { this._s.delete(c); },
      contains(c) { return this._s.has(c); },
      toggle(c, force) {
        const on = force === undefined ? !this._s.has(c) : !!force;
        if (on) this._s.add(c); else this._s.delete(c);
        return on;
      },
    },
    // assigning innerHTML replaces the children, as it does in a browser.
    // loadModels() relies on this to clear stale <option>s before refilling.
    _html: "",
    get innerHTML() { return el._html; },
    set innerHTML(v) { el._html = v; kids.length = 0; },
    appendChild(c) { kids.push(c); c.parentNode = el; return c; },
    remove() { const i = kids.indexOf(el); if (i >= 0) kids.splice(i, 1); },
    // handlers are recorded, not discarded, so a scenario can fire a real click
    // - the sign-in path is only reachable through its listener
    _h: {},
    addEventListener(type, fn) { (el._h[type] = el._h[type] || []).push(fn); },
    _fire(type) { (el._h[type] || []).forEach(f => f()); },
    scrollIntoView() {}, focus() {},
    querySelector(sel) {
      const want = sel.replace(".", "");
      return kids.find(k => (k.className || "").split(" ").includes(want)) || null;
    },
    get children() { return kids; },
  };
  return el;
}

function newCtx() {
  const els = new Map();
  const $get = (sel) => {
    const id = sel.startsWith("#") ? sel.slice(1) : sel;
    if (!els.has(id)) els.set(id, makeEl(id));
    return els.get(id);
  };
  const timers = { armed: null, cleared: 0, nextId: 1, id: null };
  const ctx = {
    console,
    document: {
      querySelector: $get,
      createElement: () => makeEl(),
    },
    localStorage: { _d: {}, getItem(k) { return this._d[k] ?? null; }, setItem(k, v) { this._d[k] = v; } },
    setInterval: (fn) => { timers.armed = fn; timers.id = timers.nextId++; return timers.id; },
    clearInterval: () => { timers.cleared++; timers.armed = null; },
    fetch: null,
    // stands in for the supabase-js UMD bundle the page loads from CDN. Set
    // _session before running app.js to boot as a signed-in user.
    supabase: {
      _session: null,
      _signInError: null,
      _signUpCalls: [],
      createClient() {
        const self = this;
        return {
          auth: {
            getSession: () => Promise.resolve({ data: { session: self._session } }),
            signInWithPassword: ({ email }) =>
              Promise.resolve(self._signInError
                ? { data: { session: null }, error: { message: self._signInError } }
                : { data: { session: { access_token: "tok-" + email } }, error: null }),
            signUp: (creds) => {
              self._signUpCalls.push(creds);
              // mirrors the real service: no email means an anonymous sign-up,
              // which a project with anonymous sign-ins off rejects
              if (!creds || !creds.email) {
                return Promise.resolve({
                  data: { session: null },
                  error: { message: "Anonymous sign-ins are disabled" },
                });
              }
              return Promise.resolve({ data: { session: null }, error: null });
            },
            signOut: () => { self._session = null; return Promise.resolve({ error: null }); },
          },
        };
      },
    },
    // records what was appended, so a scenario can assert on the upload shape
    FormData: class { constructor() { this._parts = []; }
      append(field, value, filename) { this._parts.push({ field, value, filename }); } },
    Number, JSON, String, Date, Object, Array, Math, Promise, encodeURIComponent,
  };
  ctx.globalThis = ctx;
  vm.createContext(ctx);
  return { ctx, els, timers, $get };
}

// Both text() and json() are provided so the fixed client (text-based) and the
// unfixed one (r.json()) can be driven by the same scenarios. json() rejects on
// a non-JSON body, exactly as the browser does.
const mkRes = (status, body) => ({
  ok: status >= 200 && status < 300, status,
  text: () => Promise.resolve(body),
  json: () => new Promise((res, rej) => {
    try { res(JSON.parse(body)); } catch (e) { rej(new SyntaxError("Unexpected end of JSON input")); }
  }),
});
const jsonRes = (status, obj) => mkRes(status, JSON.stringify(obj));
const rawRes = (status, body) => mkRes(status, body);

// initialStorage optionally seeds localStorage before app.js runs, so a
// scenario can assert on restored state (e.g. a remembered model id) without
// touching the localStorage stub itself. Defaults to none - existing callers
// that pass only fetchImpl are unaffected.
function boot(fetchImpl, initialStorage = {}) {
  const h = newCtx();
  h.ctx.fetch = fetchImpl;
  Object.assign(h.ctx.localStorage._d, initialStorage);
  // signed in: /api/models needs a token, so the model picker only ever fills
  // for a user with a session
  h.ctx.supabase._session = { access_token: "tok-boot" };
  vm.runInContext(SRC, h.ctx);
  // give the module a RUN_ID the way discovery does
  h.ctx.showInventory({ run_id: "RID1", inventory: { root: "r", sets: [{ key: "K", label: "L" }] }, problems: [] });
  return h;
}

const badge = (h) => h.$get("#run-badge").textContent;
// the progress card is a step container now, and errors are appended to it
const errText = (h) => { const e = h.$get("#step-run").querySelector(".err"); return e ? e.textContent : null; };
const tick = () => new Promise(r => setTimeout(r, 0));

// An unhandled rejection is the bug under test, not a harness crash: in a
// browser it is a silent console error that leaves the UI mid-flight.
process.on("unhandledRejection", (e) => {
  console.log(`  (unhandled promise rejection: ${e && e.message} - silent in a browser)`);
});

let failures = 0;
function check(name, cond, detail) {
  if (cond) { console.log(`  PASS  ${name}`); }
  else { console.log(`  FAIL  ${name}${detail ? " -> " + detail : ""}`); failures++; }
}

/* ---------------- A: server forgot the run (restart) ---------------- */
async function scenarioA() {
  console.log("A) poll hits 404 'Unknown run' (server restarted mid-run)");
  let runCalls = 0;
  const h = boot((url) => {
    if (url === "/api/run") { runCalls++; return Promise.resolve(jsonRes(200, { run_id: "RID1", status: "running" })); }
    if (url.startsWith("/api/job/")) return Promise.resolve(jsonRes(404, { error: "Unknown run" }));
    return Promise.resolve(jsonRes(200, {}));
  });
  h.ctx.beginRun();
  await tick(); await tick();
  check("interval armed after /api/run", h.timers.armed !== null);
  h.timers.armed();               // one poll -> 404
  await tick(); await tick();
  check("polling stopped", h.timers.armed === null, "still polling");
  check("badge is Failed, not Running", badge(h) === "Failed", `badge='${badge(h)}'`);
  check("explains the restart", /restarted/i.test(errText(h) || ""), `err='${errText(h)}'`);
  check("Run button re-enabled", h.$get("#btn-run").disabled === false);
}

/* ---------------- B: /api/run 500 with an empty body ---------------- */
async function scenarioB() {
  console.log("B) /api/run returns 500 with an empty, non-JSON body");
  const h = boot((url) => {
    if (url === "/api/run") return Promise.resolve(rawRes(500, ""));
    return Promise.resolve(jsonRes(200, {}));
  });
  h.ctx.beginRun();
  await tick(); await tick(); await tick();
  check("interval never armed", h.timers.armed === null);
  check("badge is Failed, not Running", badge(h) === "Failed", `badge='${badge(h)}'`);
  check("surfaces the 500", /500/.test(errText(h) || ""), `err='${errText(h)}'`);
  check("Run button re-enabled", h.$get("#btn-run").disabled === false);
}

/* ---------------- C: transient blips then success ---------------- */
async function scenarioC() {
  console.log("C) 3 transient network failures, then the run completes");
  let jobCalls = 0;
  const result = { sets: [{ key: "K", label: "L", files: [] }], workbook: "w.xlsx", dashboard: "d.html", memo: null };
  const h = boot((url) => {
    if (url === "/api/run") return Promise.resolve(jsonRes(200, { run_id: "RID1", status: "running" }));
    if (url.startsWith("/api/job/")) {
      jobCalls++;
      if (jobCalls <= 3) return Promise.reject(new Error("network down"));
      return Promise.resolve(jsonRes(200, { id: "RID1", status: "done", log: [], error: null, result }));
    }
    return Promise.resolve(jsonRes(200, {}));
  });
  h.ctx.beginRun();
  await tick(); await tick();
  for (let i = 0; i < 3; i++) { h.timers.armed(); await tick(); await tick(); }
  check("survives 3 blips, still polling", h.timers.armed !== null, "gave up too early");
  check("badge still Running during blips", badge(h) === "Running", `badge='${badge(h)}'`);
  h.timers.armed();
  await tick(); await tick();
  check("reaches Completed", badge(h) === "Completed", `badge='${badge(h)}'`);
  check("polling stopped", h.timers.armed === null);
  // results are a step of their own now, reached from the button rather than
  // appearing under the progress card
  check("results offered", h.$get("#btn-view-results").disabled === false);
  h.$get("#btn-view-results")._fire("click");
  // results are their own screen, so the wizard is left behind entirely
  check("the run detail opens", h.$get("#screen-detail").classList.contains("hide") === false);
  check("the wizard is left behind", h.$get("#screen-wizard").classList.contains("hide"));
  check("the summary tab is the one showing", h.$get("#tab-summary").classList.contains("hide") === false);
}

/* ---------------- D: server gone for good ---------------- */
async function scenarioD() {
  console.log("D) server dies permanently mid-run");
  const h = boot((url) => {
    if (url === "/api/run") return Promise.resolve(jsonRes(200, { run_id: "RID1", status: "running" }));
    return Promise.reject(new Error("ECONNREFUSED"));
  });
  h.ctx.beginRun();
  await tick(); await tick();
  for (let i = 0; i < 12; i++) { if (h.timers.armed) { h.timers.armed(); await tick(); await tick(); } }
  check("gives up rather than spinning forever", h.timers.armed === null);
  check("badge is Failed", badge(h) === "Failed", `badge='${badge(h)}'`);
  check("Run button re-enabled", h.$get("#btn-run").disabled === false);
}

/* ---------------- E: done but result missing ---------------- */
async function scenarioE() {
  console.log("E) status 'done' but result is null");
  const h = boot((url) => {
    if (url === "/api/run") return Promise.resolve(jsonRes(200, { run_id: "RID1", status: "running" }));
    if (url.startsWith("/api/job/")) return Promise.resolve(jsonRes(200, { status: "done", log: [], result: null }));
    return Promise.resolve(jsonRes(200, {}));
  });
  h.ctx.beginRun();
  await tick(); await tick();
  h.timers.armed();
  await tick(); await tick();
  check("does not sit on a bare Completed with no results", badge(h) === "Failed", `badge='${badge(h)}'`);
  check("polling stopped", h.timers.armed === null);
}

/* ---------------- F: model selection ---------------- */
async function scenarioF() {
  console.log("F) model picker populates and is sent with the run");
  let runBody = null;
  const models = [
    { id: "72e110c8", provider: 1, friendlyName: "Google Gemini 2.5 Pro", modelName: "gemini-2.5-pro" },
    { id: "5f3283d8", provider: 0, friendlyName: "Azure OpenAI GPT-4o", modelName: "gpt4o" },
  ];
  const h = boot((url, opts) => {
    if (url === "/api/models") return Promise.resolve(jsonRes(200, models));
    if (url === "/api/run") { runBody = JSON.parse(opts.body); return Promise.resolve(jsonRes(200, { run_id: "RID1", status: "running" })); }
    if (url.startsWith("/api/job/")) return Promise.resolve(jsonRes(200, { status: "running", log: [] }));
    return Promise.resolve(jsonRes(200, {}));
  });

  await tick(); await tick(); await tick();
  const sel = h.$get("#model");
  check("skip option is present and first", sel.children[0]?.value === "");
  check("both models added as options", sel.children.length === 3, `got ${sel.children.length}`);

  sel.value = "5f3283d8";
  h.ctx.beginRun();
  await tick(); await tick();
  check("model_id sent with the run", runBody?.model_id === "5f3283d8", `sent ${JSON.stringify(runBody)}`);
}

/* ---------------- G: model list unavailable ---------------- */
async function scenarioG() {
  console.log("G) /api/models fails");
  let runBody = null;
  const h = boot((url, opts) => {
    if (url === "/api/models") return Promise.resolve(jsonRes(503, { error: "gateway not configured" }));
    if (url === "/api/run") { runBody = JSON.parse(opts.body); return Promise.resolve(jsonRes(200, { run_id: "RID1", status: "running" })); }
    if (url.startsWith("/api/job/")) return Promise.resolve(jsonRes(200, { status: "running", log: [] }));
    return Promise.resolve(jsonRes(200, {}));
  });

  await tick(); await tick(); await tick();
  check("select disabled", h.$get("#model").disabled === true);
  check("reason shown", /not configured/.test(h.$get("#model-note").textContent || ""),
    `note='${h.$get("#model-note").textContent}'`);

  h.ctx.beginRun();
  await tick(); await tick();
  check("run still starts, without a model", runBody !== null && !runBody.model_id);
}

/* ---------------- H: remembered model - restore or fall back ---------------- */
async function scenarioH() {
  console.log("H) remembered model id is restored if valid, or falls back to Skip if stale");
  const models = [
    { id: "72e110c8", provider: 1, friendlyName: "Google Gemini 2.5 Pro", modelName: "gemini-2.5-pro" },
    { id: "5f3283d8", provider: 0, friendlyName: "Azure OpenAI GPT-4o", modelName: "gpt4o" },
  ];
  const fetchImpl = (url) => {
    if (url === "/api/models") return Promise.resolve(jsonRes(200, models));
    return Promise.resolve(jsonRes(200, {}));
  };

  // half 1: a stale id (not in the current list) must fall back to Skip ("")
  const stale = boot(fetchImpl, { hr_model: "does-not-exist" });
  await tick(); await tick(); await tick();
  check("stale remembered id falls back to Skip", stale.$get("#model").value === "",
    `value='${stale.$get("#model").value}'`);

  // half 2: an id still present in the list must be restored, not discarded
  const present = boot(fetchImpl, { hr_model: "5f3283d8" });
  await tick(); await tick(); await tick();
  check("still-present remembered id is restored", present.$get("#model").value === "5f3283d8",
    `value='${present.$get("#model").value}'`);
}

/* ---------------- I: no session leaves the gate up ---------------- */
const CFG = { supabaseUrl: "https://x.supabase.co", supabaseAnonKey: "anon" };

// boots app.js without seeding a run, so the auth path is what is under test
function bootAuth(session, fetchImpl) {
  const h = newCtx();
  h.ctx.supabase._session = session;
  h.ctx.fetch = fetchImpl || ((url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : {})));
  vm.runInContext(SRC, h.ctx);
  return h;
}

const gateUp = (h) => !h.$get("#auth-gate").classList.contains("hide");

async function scenarioI() {
  console.log("I) with no session the gate stays up and no token is sent");
  const seen = [];
  const h = bootAuth(null, (url, opts) => {
    seen.push({ url, auth: opts && opts.headers && opts.headers.Authorization });
    return Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : {}));
  });
  await tick(); await tick(); await tick();

  check("gate is visible", gateUp(h));
  check("no call carried an Authorization header",
    seen.every(s => s.auth === undefined), JSON.stringify(seen));
}

/* ---------------- J: an existing session opens the app ---------------- */
async function scenarioJ() {
  console.log("J) an existing session hides the gate and authorises API calls");
  const seen = [];
  const h = bootAuth({ access_token: "tok-abc" }, (url, opts) => {
    seen.push({ url, auth: opts && opts.headers && opts.headers.Authorization });
    return Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : []));
  });
  await tick(); await tick(); await tick();

  check("gate is hidden", !gateUp(h));
  // the load-time loadModels() fires before the session resolves, so the
  // authorised call is the retry that startSession triggers
  check("a /api/models call carried the bearer token",
    seen.some(s => s.url === "/api/models" && s.auth === "Bearer tok-abc"),
    JSON.stringify(seen));
}

/* ---------------- K: signing in and out ---------------- */
async function scenarioK() {
  console.log("K) sign-in opens the app, sign-out closes it again");
  const h = bootAuth(null);
  await tick(); await tick(); await tick();
  check("gate up before sign-in", gateUp(h));

  h.$get("#auth-email").value = "a@b.com";
  h.$get("#auth-password").value = "pw";
  h.$get("#btn-signin")._fire("click");
  await tick(); await tick(); await tick();

  check("gate hidden after sign-in", !gateUp(h));

  h.$get("#btn-signout")._fire("click");
  await tick(); await tick();

  check("gate back up after sign-out", gateUp(h));
  check("sign-out message shown",
    /signed out/i.test(h.$get("#auth-msg").textContent || ""),
    `msg='${h.$get("#auth-msg").textContent}'`);
}

/* ---------------- L: a 401 mid-session returns to the gate ---------------- */
async function scenarioL() {
  console.log("L) a 401 during a session drops back to the gate");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(url === "/api/config" ? jsonRes(200, CFG) : jsonRes(401, { error: "no" })));
  await tick(); await tick(); await tick();

  check("gate is up again after a 401", gateUp(h));
  check("expiry is explained",
    /expired/i.test(h.$get("#auth-msg").textContent || ""),
    `msg='${h.$get("#auth-msg").textContent}'`);
}

/* ---------------- M: empty fields never reach Supabase ---------------- */
async function scenarioM() {
  console.log("M) blank credentials are refused locally, not by the auth service");
  const h = bootAuth(null);
  await tick(); await tick(); await tick();

  // both boxes empty - the bug was sending this, which Supabase reads as an
  // anonymous sign-up and rejects with a message about a feature nobody used
  h.$get("#auth-email").value = "";
  h.$get("#auth-password").value = "";
  h.$get("#btn-signup")._fire("click");
  await tick(); await tick();

  check("no signUp call was made", h.ctx.supabase._signUpCalls.length === 0,
    JSON.stringify(h.ctx.supabase._signUpCalls));
  check("the empty email is named", /email/i.test(h.$get("#auth-msg").textContent || ""),
    `msg='${h.$get("#auth-msg").textContent}'`);
  check("no anonymous wording leaks through",
    !/anonymous/i.test(h.$get("#auth-msg").textContent || ""),
    `msg='${h.$get("#auth-msg").textContent}'`);

  // an email but no password must be caught too
  h.$get("#auth-email").value = "a@b.com";
  h.$get("#auth-password").value = "";
  h.$get("#btn-signup")._fire("click");
  await tick(); await tick();

  check("still no signUp call", h.ctx.supabase._signUpCalls.length === 0);
  check("the empty password is named", /password/i.test(h.$get("#auth-msg").textContent || ""),
    `msg='${h.$get("#auth-msg").textContent}'`);

  // and a complete pair goes through
  h.$get("#auth-password").value = "pw";
  h.$get("#btn-signup")._fire("click");
  await tick(); await tick();

  check("a complete pair reaches signUp", h.ctx.supabase._signUpCalls.length === 1,
    JSON.stringify(h.ctx.supabase._signUpCalls));
  check("confirmation is explained",
    /confirmation link/i.test(h.$get("#auth-msg").textContent || ""),
    `msg='${h.$get("#auth-msg").textContent}'`);
}

/* ---------------- O: folder picking ---------------- */
const mkFile = (relPath, size) => ({ name: relPath.split("/").pop(), size, webkitRelativePath: relPath });

async function scenarioO() {
  console.log("O) picking a folder enables the check and uploads it per set");
  const h = bootAuth({ access_token: "tok-abc" });
  await tick(); await tick(); await tick();

  check("check button starts disabled", h.$get("#btn-check").disabled === true);

  h.$get("#path1").files = [
    mkFile("JUNE 2026 0.5 PERCENT/debug.zip", 1024),
    mkFile("JUNE 2026 0.5 PERCENT/write-off.csv", 2048),
  ];
  h.$get("#path1")._fire("change");

  check("check button enabled once a folder is chosen", h.$get("#btn-check").disabled === false);
  // the folder's name is the row's heading; the size sits under it
  check("the folder name becomes the heading",
    /JUNE 2026 0\.5 PERCENT/.test(h.$get("#path1-pick").textContent),
    `pick='${h.$get("#path1-pick").textContent}'`);
  check("the size is shown", /2 files/.test(h.$get("#path1-info").textContent),
    `info='${h.$get("#path1-info").textContent}'`);
  check("the folder count is shown", /1 of 4/.test(h.$get("#folder-count").textContent),
    `count='${h.$get("#folder-count").textContent}'`);

  // a real 159 MB debug folder must be accepted, not blocked
  h.$get("#path1").files = [mkFile("DEBUG 3 MONTHS/debug.zip", 159 * 1024 * 1024)];
  h.$get("#path1")._fire("change");

  check("a 159 MB folder is accepted", h.$get("#btn-check").disabled === false,
    `info='${h.$get("#path1-info").textContent}'`);

  // an oversized folder must block the button rather than fail after upload
  h.$get("#path1").files = [mkFile("BIG/huge.zip", 600 * 1024 * 1024)];
  h.$get("#path1")._fire("change");

  check("oversized folder disables the check", h.$get("#btn-check").disabled === true);
  check("the size limit is explained",
    /limit is 512 MB/.test(h.$get("#path1-info").textContent),
    `info='${h.$get("#path1-info").textContent}'`);
}

/* ---------------- R: the two screens and the step rail ---------------- */
async function scenarioR() {
  console.log("R) screens switch and the rail tracks the wizard's position");
  const runs = [
    { id: "r1", status: "done", set_labels: ["JUN 2026"], created_at: "2026-07-01T09:00:00Z",
      sets: 2, untraced: 12, trace_rate: 97.5 },
    { id: "r2", status: "error", set_labels: ["MAY 2026"], created_at: "2026-06-01T09:00:00Z",
      sets: 0, untraced: 0, trace_rate: 0 },
  ];
  const h = bootAuth({ access_token: "tok-abc" }, (url) => {
    if (url === "/api/config") return Promise.resolve(jsonRes(200, CFG));
    if (url === "/api/runs") return Promise.resolve(jsonRes(200, runs));
    return Promise.resolve(jsonRes(200, []));
  });
  await tick(); await tick(); await tick();

  // note: the stub builds elements on demand with no classes, so it cannot model
  // the class="hide" the markup starts with - only explicit toggles are checked
  check("stat tiles rendered", /Runs this month/.test(h.$get("#stat-tiles").innerHTML));
  check("history shows both runs", /JUN 2026/.test(h.$get("#history-table").innerHTML) &&
    /MAY 2026/.test(h.$get("#history-table").innerHTML));
  check("a failed run gets no Open button",
    (h.$get("#history-table").innerHTML.match(/data-run/g) || []).length === 1,
    h.$get("#history-table").innerHTML.slice(0, 200));

  // New run swaps screens and resets to step 1
  h.$get("#btn-new-run")._fire("click");
  check("wizard shown after New run", !h.$get("#screen-wizard").classList.contains("hide"));
  check("runs screen hidden", h.$get("#screen-runs").classList.contains("hide"));
  check("title is step 1", /Choose your analysis folders/.test(h.$get("#step-title").textContent),
    `title='${h.$get("#step-title").textContent}'`);

  // the flow moves the rail forward
  h.ctx.showInventory({ run_id: "RID1", inventory: { root: "r", sets: [] }, problems: [] });
  check("title moves to step 2", /Confirm what was found/.test(h.$get("#step-title").textContent),
    `title='${h.$get("#step-title").textContent}'`);

  // Cancel returns to the list
  h.$get("#btn-cancel")._fire("click");
  check("cancel returns to runs", !h.$get("#screen-runs").classList.contains("hide"));
  check("cancel hides the wizard", h.$get("#screen-wizard").classList.contains("hide"));
}

/* ---------------- S: a new run clears the previous one ---------------- */
async function scenarioS() {
  console.log("S) starting a new run clears the results of the last one");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  // land on results, as reopening a stored run does
  h.ctx.showResults({ sets: [], workbook: "w.xlsx", dashboard: "d.html", memo: null });
  check("detail visible", !h.$get("#screen-detail").classList.contains("hide"));

  h.$get("#nav-new")._fire("click");

  // without the reset the old run's detail stays reachable behind a fresh picker
  check("the detail screen is left for the new run", h.$get("#screen-detail").classList.contains("hide"));
  check("the conversation is closed", h.$get("#chat-drawer").classList.contains("hide"));
  check("folder picker shown again", !h.$get("#step-folders").classList.contains("hide"));
  check("back to step 1", /Choose your analysis folders/.test(h.$get("#step-title").textContent));
}

async function scenarioQ() {
  console.log("Q) the browser adopts the server's size limit rather than its own");
  // server says 100 MB; a 150 MB folder must be refused even though the
  // built-in fallback (512 MB) would have allowed it
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config"
      ? Object.assign({}, CFG, { maxBytesPerSet: 100 * 1024 * 1024 })
      : [])));
  await tick(); await tick(); await tick();

  h.$get("#path1").files = [mkFile("SET/big.zip", 150 * 1024 * 1024)];
  h.$get("#path1")._fire("change");

  check("the server's smaller limit is enforced", h.$get("#btn-check").disabled === true);
  check("the message quotes the server's limit",
    /limit is 100 MB/.test(h.$get("#path1-info").textContent),
    `info='${h.$get("#path1-info").textContent}'`);
}

async function scenarioP() {
  console.log("P) each file is posted under its own set field, keeping its relative path");
  const posted = [];
  const h = bootAuth({ access_token: "tok-abc" }, (url, opts) => {
    if (url === "/api/config") return Promise.resolve(jsonRes(200, CFG));
    if (url === "/api/discover") {
      posted.push(...(opts.body._parts || []));
      return Promise.resolve(jsonRes(200,
        { run_id: "RID9", inventory: { root: "r", sets: [] }, problems: [] }));
    }
    return Promise.resolve(jsonRes(200, []));
  });
  await tick(); await tick(); await tick();

  h.$get("#path1").files = [mkFile("SET A/debug.zip", 10)];
  h.$get("#path1")._fire("change");
  h.ctx.addPathRow();
  h.$get("#path2").files = [mkFile("SET B/debug.zip", 10)];
  h.$get("#path2")._fire("change");

  await h.ctx.discover();

  check("both folders were sent", posted.length === 2, JSON.stringify(posted));
  check("fields are named per set",
    posted[0].field === "set0" && posted[1].field === "set1",
    JSON.stringify(posted.map(p => p.field)));
  check("the relative path travels as the filename",
    posted[0].filename === "SET A/debug.zip" && posted[1].filename === "SET B/debug.zip",
    JSON.stringify(posted.map(p => p.filename)));
}

/* ---------------- N: the picker recovers after a failed attempt ---------------- */
async function scenarioN() {
  console.log("N) a second loadModels after a failure re-enables and refills the picker");
  const models = [
    { id: "aaa", provider: 1, friendlyName: "Model A", modelName: "a" },
    { id: "bbb", provider: 0, friendlyName: "Model B", modelName: "b" },
  ];

  // first call fails, second succeeds - exactly the pre-session 401 then
  // post-sign-in retry that left the picker greyed out with duplicate options
  let modelCalls = 0;
  const h = bootAuth({ access_token: "tok-abc" }, (url) => {
    if (url === "/api/config") return Promise.resolve(jsonRes(200, CFG));
    if (url === "/api/models") {
      modelCalls++;
      return Promise.resolve(modelCalls === 1
        ? jsonRes(503, { error: "gateway down" })
        : jsonRes(200, models));
    }
    return Promise.resolve(jsonRes(200, {}));
  });
  await tick(); await tick(); await tick();

  check("first attempt disabled the picker", h.$get("#model").disabled === true);

  h.ctx.loadModels();
  await tick(); await tick(); await tick();

  const sel = h.$get("#model");
  check("picker is re-enabled", sel.disabled === false);
  check("no duplicate Skip option", sel.children.length === 3,
    `options=${sel.children.length} (${sel.children.map(o => o.textContent).join("|")})`);
  check("the stale failure note is gone",
    !/unavailable|down/i.test(h.$get("#model-note").textContent || ""),
    `note='${h.$get("#model-note").textContent}'`);
}

/* ---------------- T: the stage tracker ---------------- */
async function scenarioT() {
  console.log("T) the stage list, progress bar and elapsed clock follow the engine");

  const stagesMid = [
    { key: "discover", name: "Read the analysis folders", detail: "Find the files", status: "done", seconds: 0.4 },
    { key: "K:load", name: "L - load inputs", detail: "Read the CSVs", status: "done", seconds: 2.5 },
    { key: "K:check1", name: "L - check 1", detail: "Trace each default", status: "running", seconds: null },
    { key: "K:export", name: "L - write CSVs", detail: "Export the detail", status: "pending", seconds: null },
  ];
  const stagesEnd = stagesMid.map(s =>
    s.status === "running" ? { ...s, status: "done", seconds: 1.1 }
      : s.status === "pending" ? { ...s, status: "warn", seconds: 95 } : s);

  let jobCalls = 0;
  const h = boot((url) => {
    if (url === "/api/run") return Promise.resolve(jsonRes(200, { run_id: "RID1", status: "running" }));
    if (url.startsWith("/api/job/")) {
      jobCalls++;
      return Promise.resolve(jsonRes(200, jobCalls <= 1
        ? { id: "RID1", status: "running", log: [], stages: stagesMid, elapsed_seconds: 71 }
        : { id: "RID1", status: "done", log: [], result: { sets: [], workbook: "w.xlsx", dashboard: "d.html" },
            stages: stagesEnd, elapsed_seconds: 99.4 }));
    }
    return Promise.resolve(jsonRes(200, {}));
  });

  h.ctx.beginRun();
  await tick(); await tick();
  h.timers.armed();
  await tick(); await tick();

  const mid = h.$get("#stages").innerHTML;
  check("every stage is listed", (mid.match(/class="stage /g) || []).length === 4,
    `rows=${(mid.match(/class="stage /g) || []).length}`);
  check("the running stage spins", /class="stage st-running"[\s\S]*progress_activity/.test(mid));
  check("finished stages are ticked", /class="stage st-done"[\s\S]*check_circle/.test(mid));
  check("a pending stage is not ticked", /class="stage st-pending"[\s\S]*radio_button_unchecked/.test(mid));
  check("durations are shown", /2\.5s/.test(mid), `html=${mid.slice(0, 120)}`);

  // two of four settled
  check("the bar reflects progress", h.$get("#run-bar").style.width === "50%",
    `width='${h.$get("#run-bar").style.width}'`);
  check("the counter names the running stage",
    /stage 3 of 4/.test(h.$get("#run-meta").textContent), `meta='${h.$get("#run-meta").textContent}'`);
  check("the elapsed clock is m:ss",
    /Elapsed 1:11/.test(h.$get("#run-meta").textContent), `meta='${h.$get("#run-meta").textContent}'`);
  check("the headline names the current stage",
    /check 1/.test(h.$get("#run-headline").textContent), `hl='${h.$get("#run-headline").textContent}'`);

  // the log is secondary, so it stays closed until asked for
  check("the log starts closed", h.$get("#card-log").classList.contains("hide"));
  h.$get("#btn-log")._fire("click");
  check("the log opens on request", h.$get("#card-log").classList.contains("hide") === false);
  check("the toggle relabels", /Hide raw log/.test(h.$get("#btn-log-tx").textContent));
  h.$get("#btn-log")._fire("click");
  check("the log closes again", h.$get("#card-log").classList.contains("hide"));

  h.timers.armed();
  await tick(); await tick();

  const end = h.$get("#stages").innerHTML;
  check("a warned stage is flagged", /class="stage st-warn"[\s\S]*warning/.test(end));
  check("long stages read as m ss", /1m 35s/.test(end), "expected 95s as 1m 35s");
  check("the bar completes", h.$get("#run-bar").style.width === "100%",
    `width='${h.$get("#run-bar").style.width}'`);
  check("the headline reports completion",
    /complete/i.test(h.$get("#run-headline").textContent), `hl='${h.$get("#run-headline").textContent}'`);
  check("the elapsed clock stops at the finish",
    /Elapsed 1:39/.test(h.$get("#run-meta").textContent), `meta='${h.$get("#run-meta").textContent}'`);
}

/* ---------------- U: the run detail's four tabs ---------------- */
const DETAIL_RESULT = {
  sets: [{
    key: "JUN2026", label: "3. DEBUG FILE 30 JUNE 2026 3 MONTHS",
    window: "01 Dec 2025 to 30 Jun 2026", scored: 733828,
    defaults: 15813, exposure_fmt: "R 40,101,222.00",
    traced: 15440, trace_rate: 97.6, traced_writeoff: 15100, traced_ifrs9: 340,
    untraced: 373, untraced_fmt: "R 855,159.21",
    wo_total: 4, wo_in_window: 4, wo_in_window_fmt: "R 1.50", wo_post_window: 0,
    ifrs9_overlap: 12, mig_validation: "PASS", mig_max_diff: 0,
    files: ["JUN2026_untraced_defaults.csv"],
  }],
  workbook: "reconciliation.xlsx", dashboard: "reconciliation_dashboard.html",
  // no stages: a stored run does not keep them
  memo: "analysis_memo.docx", elapsed_seconds: 41.2,
  outputs: [
    { name: "analysis_memo.docx", bytes: 24576 },
    { name: "reconciliation.xlsx", bytes: 1572864 },
    { name: "reconciliation_dashboard.html", bytes: 409600 },
    { name: "JUN2026_untraced_defaults.csv", bytes: 900 },
  ],
};

async function scenarioU() {
  console.log("U) the run detail renders its four tabs from the stored result");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  // discovery is what sets the run id; the detail titles itself from it
  h.ctx.showInventory({ run_id: "3f2a9c41-88bd-4e0e-9a11-7c5d2e6f0a12",
    inventory: { root: "r", sets: [] }, problems: [] });
  h.ctx.showResults(DETAIL_RESULT, [{ t: "10:00:01", msg: "CHECK 1: 15,440 traced", kind: "ok" }]);

  check("the detail screen opens", !h.$get("#screen-detail").classList.contains("hide"));
  check("the title is the run reference", /^[0-9A-F]+$/i.test(h.$get("#detail-title").textContent),
    `title='${h.$get("#detail-title").textContent}'`);
  check("the meta names the set and duration",
    /30 JUNE 2026/.test(h.$get("#detail-meta").textContent) &&
    /1 set/.test(h.$get("#detail-meta").textContent) &&
    /41/.test(h.$get("#detail-meta").textContent), `meta='${h.$get("#detail-meta").textContent}'`);

  // summary
  const sum = h.$get("#tab-summary").innerHTML;
  check("the set header carries the window", /Scoring window 01 Dec 2025/.test(sum));
  check("the four figures are shown",
    (sum.match(/class="ktile"/g) || []).length === 4, `tiles=${(sum.match(/class="ktile"/g) || []).length}`);
  check("untraced is flagged red", /class="v bad">373</.test(sum));
  check("in-window write-offs are flagged amber", /class="v amber">4</.test(sum));
  check("both checks get their own card", /Check 1 — trace every default/.test(sum) &&
    /Check 2 — the reverse trace/.test(sum));
  check("the rag pills read from the figures", /15,440 traced · 97.6%/.test(sum) &&
    /373 untraced · R 855,159.21/.test(sum));
  check("validation reports the pass", /verified/.test(sum) && /<b>PASS<\/b>/.test(sum) &&
    /max cell difference 0/.test(sum));

  // logs - last tab, and the stage list does not appear here at all
  h.$get("#tab-btn-logs")._fire("click");
  check("the logs tab shows", !h.$get("#tab-logs").classList.contains("hide"));
  check("the summary tab hides", h.$get("#tab-summary").classList.contains("hide"));
  check("the stored log is shown", /CHECK 1/.test(h.$get("#detail-log").innerHTML));
  check("the log is not hidden behind a toggle", !h.$get("#detail-log").classList.contains("hide"));
  check("the empty state is not shown", h.$get("#detail-log-empty").classList.contains("hide"));
  check("the log is counted and timed", /1 lines/.test(h.$get("#detail-log-count").textContent) &&
    /41\.2s total/.test(h.$get("#detail-log-count").textContent),
    `count='${h.$get("#detail-log-count").textContent}'`);

  // dashboard
  h.$get("#tab-btn-dashboard")._fire("click");
  check("the dashboard is embedded", /reconciliation_dashboard\.html/.test(h.$get("#res-frame").src),
    `src='${h.$get("#res-frame").src}'`);
  check("the open-in-a-tab link matches", /reconciliation_dashboard\.html/.test(h.$get("#res-open").href));

  // files
  h.$get("#tab-btn-files")._fire("click");
  const files = h.$get("#detail-files").innerHTML;
  check("every output is listed", (files.match(/class="frow"/g) || []).length === 4,
    `rows=${(files.match(/class="frow"/g) || []).length}`);
  check("sizes are human readable", /1\.5 MB/.test(files) && /24 KB/.test(files),
    "expected 1572864 as 1.5 MB and 24576 as 24 KB");
  check("the workbook is described", /Workbook — every set/.test(files));
  check("the untraced CSV is called out", /could not be traced/.test(files));
  check("each row offers a download", (files.match(/>Download</g) || []).length === 4);

  // chat
  check("the conversation starts closed", h.$get("#chat-drawer").classList.contains("hide"));
  h.$get("#btn-chat-open")._fire("click");
  check("Ask about this run opens the drawer", !h.$get("#chat-drawer").classList.contains("hide"));
  check("the drawer names the run", /[0-9A-F]/i.test(h.$get("#chat-run").textContent));
  h.$get("#btn-chat-close")._fire("click");
  check("the drawer closes", h.$get("#chat-drawer").classList.contains("hide"));
}

/* ---------------- V: a set whose IFRS9 file never matched ---------------- */
async function scenarioV() {
  console.log("V) a set with no IFRS9 overlap and a failed validation says so");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  const broken = JSON.parse(JSON.stringify(DETAIL_RESULT));
  broken.sets[0].ifrs9_overlap = 0;
  broken.sets[0].mig_validation = "FAIL";
  broken.sets[0].mig_max_diff = 17;
  h.ctx.showResults(broken, []);

  const sum = h.$get("#tab-summary").innerHTML;
  check("the IFRS9 mismatch is explained", /IFRS9 could not be matched/.test(sum));
  check("the failed validation is flagged", /<b>FAIL<\/b>/.test(sum) && /error<\/span>/.test(sum),
    "expected a FAIL with the error icon");
  check("the difference is quoted", /max cell difference 17/.test(sum));

  // and a set that could not be validated at all
  const na = JSON.parse(JSON.stringify(DETAIL_RESULT));
  na.sets[0].mig_validation = "N/A";
  na.sets[0].mig_max_diff = null;
  h.ctx.showResults(na, []);
  check("an unvalidatable set explains why",
    /no <code>CohortNlambda<\/code> to compare/.test(h.$get("#tab-summary").innerHTML));
}

/* ---------------- W: a run stored before sizes were recorded ---------------- */
async function scenarioW() {
  console.log("W) an older stored run with no outputs list still lists its files");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  const old = JSON.parse(JSON.stringify(DETAIL_RESULT));
  delete old.outputs;
  delete old.elapsed_seconds;
  h.ctx.showResults(old, []);

  const files = h.$get("#detail-files").innerHTML;
  check("the files fall back to the names on the result",
    (files.match(/class="frow"/g) || []).length === 4,
    `rows=${(files.match(/class="frow"/g) || []).length}`);
  check("no size is invented", !/undefined|NaN/.test(files));
  check("the summary still renders", /Scoring window/.test(h.$get("#tab-summary").innerHTML));
  check("the meta omits a duration it does not have",
    !/ran in/.test(h.$get("#detail-meta").textContent), `meta='${h.$get("#detail-meta").textContent}'`);
}

const INVENTORY_FIX = {
  run_id: "RID1",
  inventory: { root: "r", sets: [{ key: "K", label: "L", writeoff: "wo.csv" }] },
  problems: [],
};

/* ---------------- Y: one step at a time, and the rail goes back ---------------- */
async function scenarioY() {
  console.log("Y) exactly one wizard step is on screen, and the rail walks back");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  const shown = () => ["folders", "confirm", "run"]
    .filter(s => !h.$get("#step-" + s).classList.contains("hide"));

  h.$get("#nav-new")._fire("click");
  check("folders only, at step 1", shown().join() === "folders", `shown=${shown().join()||"none"}`);

  h.ctx.showInventory(INVENTORY_FIX);
  check("confirm replaces folders", shown().join() === "confirm", `shown=${shown().join()||"none"}`);

  h.ctx.beginRun();
  await tick(); await tick();
  check("run replaces confirm", shown().join() === "run", `shown=${shown().join()||"none"}`);

  // the rail can return to a step already visited
  check("step 1 is offered", h.$get("#rail-0").disabled === false);
  check("step 2 is offered", h.$get("#rail-1").disabled === false);
  check("the current step is not offered", h.$get("#rail-2").disabled === true);
  check("results is not offered before there is one", h.$get("#rail-3").disabled === true);

  h.$get("#rail-0")._fire("click");
  check("the rail returns to folders", shown().join() === "folders", `shown=${shown().join()||"none"}`);
  check("the title follows", /Choose your analysis folders/.test(h.$get("#step-title").textContent));
  check("forward stays reachable once visited", h.$get("#rail-2").disabled === false);

  h.$get("#rail-2")._fire("click");
  check("and forward again to the run", shown().join() === "run", `shown=${shown().join()||"none"}`);

  // a fresh run forgets where the last one got to
  h.$get("#nav-new")._fire("click");
  check("a new run cannot jump ahead", h.$get("#rail-1").disabled === true &&
    h.$get("#rail-2").disabled === true, "later steps should be closed again");
}

/* ---------------- YY: walking back mid-run and checking again ---------------- */
async function scenarioYY() {
  console.log("YY) re-checking folders mid-run drops the poll it supersedes");
  const h = boot((url) => {
    if (url === "/api/run") return Promise.resolve(jsonRes(200, { run_id: "RID1", status: "running" }));
    if (url.startsWith("/api/job/"))
      return Promise.resolve(jsonRes(200, { id: "RID1", status: "running", log: [], stages: [] }));
    return Promise.resolve(jsonRes(200, {}));
  });

  h.ctx.beginRun();
  await tick(); await tick();
  check("polling while the run is live", h.timers.armed !== null);

  // back to the folders, then check again - the old poll must not survive it
  h.$get("#rail-0")._fire("click");
  check("the rail walked back mid-run", !h.$get("#step-folders").classList.contains("hide"));
  check("the run step is put away", h.$get("#step-run").classList.contains("hide"));

  h.$get("#btn-check")._fire("click");
  check("the superseded poll was dropped", h.timers.armed === null,
    "a live poll would chase the run id discovery is replacing");
}

/* ---------------- Z: the Back button uses the same path ---------------- */
async function scenarioZ() {
  console.log("Z) Back on the confirm step returns to folders");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  h.$get("#nav-new")._fire("click");
  h.ctx.showInventory(INVENTORY_FIX);
  h.$get("#btn-back-folders")._fire("click");

  check("folders is back", !h.$get("#step-folders").classList.contains("hide"));
  check("confirm is put away", h.$get("#step-confirm").classList.contains("hide"));
  check("the rail marks step 1 current", h.$get("#rail-0").disabled === true);
}

/* ---------------- X: a run that kept no log ---------------- */
async function scenarioX() {
  console.log("X) a run with no log says so rather than showing an empty panel");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  h.ctx.showResults(DETAIL_RESULT, []);
  h.$get("#tab-btn-logs")._fire("click");

  check("the empty state is shown", !h.$get("#detail-log-empty").classList.contains("hide"));
  check("the empty log panel is hidden", h.$get("#detail-log").classList.contains("hide"));
  check("no line count is claimed", !/lines/.test(h.$get("#detail-log-count").textContent),
    `count='${h.$get("#detail-log-count").textContent}'`);
}

for (const s of [scenarioA, scenarioB, scenarioC, scenarioD, scenarioE, scenarioF, scenarioG, scenarioH,
                 scenarioI, scenarioJ, scenarioK, scenarioL, scenarioM, scenarioN,
                 scenarioO, scenarioP, scenarioQ, scenarioR, scenarioS, scenarioT,
                 scenarioU, scenarioV, scenarioW, scenarioX,
                 scenarioY, scenarioYY, scenarioZ]) { await s(); console.log(""); }
console.log(failures === 0 ? "ALL SCENARIOS PASSED" : `${failures} CHECK(S) FAILED`);
process.exit(failures === 0 ? 0 : 1);
