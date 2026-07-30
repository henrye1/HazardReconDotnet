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
    classList: {
      _s: new Set(),
      add(c) { this._s.add(c); }, remove(c) { this._s.delete(c); },
      contains(c) { return this._s.has(c); },
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
const errText = (h) => { const e = h.$get("#card-run").querySelector(".err"); return e ? e.textContent : null; };
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
  check("badge is 'error', not 'running'", badge(h) === "error", `badge='${badge(h)}'`);
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
  check("badge is 'error', not 'running'", badge(h) === "error", `badge='${badge(h)}'`);
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
  check("badge still 'running' during blips", badge(h) === "running", `badge='${badge(h)}'`);
  h.timers.armed();
  await tick(); await tick();
  check("reaches 'complete'", badge(h) === "complete", `badge='${badge(h)}'`);
  check("polling stopped", h.timers.armed === null);
  check("results card shown", h.$get("#card-res").classList.contains("hide") === false);
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
  check("badge is 'error'", badge(h) === "error", `badge='${badge(h)}'`);
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
  check("does not sit on a bare 'complete' with no results", badge(h) === "error", `badge='${badge(h)}'`);
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
  check("the folder name and size are shown",
    /JUNE 2026 0\.5 PERCENT/.test(h.$get("#path1-info").textContent) &&
    /2 files/.test(h.$get("#path1-info").textContent),
    `info='${h.$get("#path1-info").textContent}'`);

  // an oversized folder must block the button rather than fail after upload
  h.$get("#path1").files = [mkFile("BIG/huge.zip", 51 * 1024 * 1024)];
  h.$get("#path1")._fire("change");

  check("oversized folder disables the check", h.$get("#btn-check").disabled === true);
  check("the size limit is explained",
    /limit is 50 MB/.test(h.$get("#path1-info").textContent),
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

for (const s of [scenarioA, scenarioB, scenarioC, scenarioD, scenarioE, scenarioF, scenarioG, scenarioH,
                 scenarioI, scenarioJ, scenarioK, scenarioL, scenarioM, scenarioN,
                 scenarioO, scenarioP]) { await s(); console.log(""); }
console.log(failures === 0 ? "ALL SCENARIOS PASSED" : `${failures} CHECK(S) FAILED`);
process.exit(failures === 0 ? 0 : 1);
