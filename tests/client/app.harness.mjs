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
    // Appended children first. Failing that, the element's own markup: the set
    // rows are built as an innerHTML string and then queried for the spans
    // inside it (row.querySelector(".meta")), which a children-only lookup
    // answers with null. The stand-in is cached per selector so the app sees a
    // stable node, and so a scenario can read back what was written to it.
    _q: {},
    querySelector(sel) {
      const s = String(sel);
      if (s.startsWith(".")) {
        const kid = kids.find(k => (k.className || "").split(" ").includes(s.slice(1)));
        if (kid) return kid;
      }
      if (el._q[s]) return el._q[s];
      const re = s.startsWith(".") ? new RegExp('class="[^"]*\\b' + s.slice(1) + '\\b[^"]*"')
        : s.startsWith("#") ? new RegExp('id="' + s.slice(1) + '"')
        : new RegExp("<" + s + "\\b");
      if (!re.test(el._html)) return null;
      return (el._q[s] = makeEl());
    },
    // Scans the assigned innerHTML, because the app wires handlers onto markup it
    // generated as a string. Without this the calls throw, and loadHistory's catch
    // was quietly swallowing exactly that for the run rows.
    querySelectorAll(sel) {
      const m = String(sel).match(/^([a-z]*)\.([\w-]+)$/i);
      if (!m) return [];
      const tag = m[1] || "[a-z]+";
      const re = new RegExp("<" + tag + "\\b([^>]*class=\"[^\"]*\\b" + m[2] + "\\b[^\"]*\"[^>]*)>", "gi");
      const found = [];
      let hit;
      while ((hit = re.exec(el._html)) !== null) {
        const attrs = hit[1];
        found.push({
          getAttribute: (n) => {
            const a = attrs.match(new RegExp(n + "=\"([^\"]*)\""));
            return a ? a[1] : null;
          },
          addEventListener() {},
          classList: { contains: (c) => new RegExp("\\b" + c + "\\b").test(attrs) },
        });
      }
      return found;
    },
    get children() { return kids; },
    // the mapping cards write their title into the first node of markup they
    // built as a string, so this needs the same stand-in querySelector uses
    get firstChild() {
      if (kids.length) return kids[0];
      if (!el._html) return null;
      return el._q[":first"] || (el._q[":first"] = makeEl());
    },
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
      // the page binds a global keydown for the shortcut keys; recorded rather
      // than dropped so a scenario can fire one
      _h: {},
      addEventListener(type, fn) { (this._h[type] = this._h[type] || []).push(fn); },
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

/* ---------------- O: file picking ---------------- */
const mkFile = (name, size) => ({ name: name.split("/").pop(), size });

/* renderSets() builds the slots, so the file inputs carry no ids and have to be
   reached through #sets. A set block is [head, ...one row per FILE_KIND] and a
   row is [input, pick button, (clear button)]. Every pick redraws, so nothing
   here holds a reference across a change. */
const KIND_AT = { exposure: 0, writeoff: 1, debug: 2, scenario: 3 };
const setBlock = (h, i) => h.$get("#sets").children[i];
const setHead = (h, i) => setBlock(h, i).children[0].innerHTML;
const slotRow = (h, i, kind) => setBlock(h, i).children[1 + KIND_AT[kind]];
// the line under a slot's label is written to a span inside the row's markup
const slotSubText = (h, i, kind) => {
  const meta = slotRow(h, i, kind)._q[".meta"];
  return meta ? meta.textContent : "";
};
function pickInto(h, i, kind, files) {
  const input = slotRow(h, i, kind).children[0];
  input.files = files;
  input._fire("change");
}
// the two roles the server insists on, which is what makes a set runnable
const fillSet = (h, i) => {
  pickInto(h, i, "exposure", [mkFile("ifrs9.csv", 2048)]);
  pickInto(h, i, "debug", [mkFile("debug.zip", 1024)]);
};

async function scenarioO() {
  console.log("O) picking the required files enables the check");
  const h = bootAuth({ access_token: "tok-abc" });
  await tick(); await tick(); await tick();

  check("check button starts disabled", h.$get("#btn-check").disabled === true);
  check("the empty state names the set limit",
    /No files chosen yet, up to 4 sets/.test(h.$get("#set-count").textContent),
    `count='${h.$get("#set-count").textContent}'`);

  // the exposure file alone is a half-filled set: the receiver would reject it,
  // so it must not be offered for upload
  pickInto(h, 0, "exposure", [mkFile("ifrs9.csv", 2048)]);
  check("a half-filled set does not enable the check", h.$get("#btn-check").disabled === true);
  check("the set says what it still needs", /still needs its required files/.test(setHead(h, 0)),
    `head='${setHead(h, 0)}'`);
  check("the chosen file is named with its size", /ifrs9\.csv · 2 KB/.test(slotSubText(h, 0, "exposure")),
    `sub='${slotSubText(h, 0, "exposure")}'`);

  pickInto(h, 0, "debug", [mkFile("debug.zip", 1024)]);
  check("check button enabled once the required files are in", h.$get("#btn-check").disabled === false);
  check("the set reports how many files it has", /2 of 4 files chosen/.test(setHead(h, 0)),
    `head='${setHead(h, 0)}'`);
  check("the ready count is shown", /1 of 1 set ready/.test(h.$get("#set-count").textContent),
    `count='${h.$get("#set-count").textContent}'`);

  // a real 159 MB debug file must be accepted, not blocked
  pickInto(h, 0, "debug", [mkFile("debug.zip", 159 * 1024 * 1024)]);
  check("a 159 MB debug file is accepted", h.$get("#btn-check").disabled === false, `head='${setHead(h, 0)}'`);

  // oversized must block the button rather than fail after the upload is paid for
  pickInto(h, 0, "debug", [mkFile("huge.zip", 600 * 1024 * 1024)]);
  check("oversized set disables the check", h.$get("#btn-check").disabled === true);
  check("the size limit is explained", /is over the 512 MB limit/.test(setHead(h, 0)),
    `head='${setHead(h, 0)}'`);
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
  check("title is step 1", /Choose your input files/.test(h.$get("#step-title").textContent),
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
  check("folder picker shown again", !h.$get("#step-files").classList.contains("hide"));
  check("back to step 1", /Choose your input files/.test(h.$get("#step-title").textContent));
}

async function scenarioQ() {
  console.log("Q) the browser adopts the server's size limit rather than its own");
  // server says 100 MB; a 150 MB set must be refused even though the built-in
  // fallback (512 MB) would have allowed it
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config"
      ? Object.assign({}, CFG, { maxBytesPerSet: 100 * 1024 * 1024 })
      : [])));
  await tick(); await tick(); await tick();

  pickInto(h, 0, "debug", [mkFile("big.zip", 150 * 1024 * 1024)]);

  check("the server's smaller limit is enforced", h.$get("#btn-check").disabled === true);
  check("the message quotes the server's limit", /is over the 100 MB limit/.test(setHead(h, 0)),
    `head='${setHead(h, 0)}'`);
}

async function scenarioP() {
  console.log("P) each file is posted under its own set field, named for its role");
  const posted = [];
  const h = bootAuth({ access_token: "tok-abc" }, (url, opts) => {
    if (url === "/api/config") return Promise.resolve(jsonRes(200, CFG));
    if (url === "/api/discover") {
      posted.push(...(opts.body._parts || []));
      return Promise.resolve(jsonRes(200,
        { run_id: "RID9", inventory: { root: "r", sets: [] }, problems: [], mapping: [] }));
    }
    return Promise.resolve(jsonRes(200, []));
  });
  await tick(); await tick(); await tick();

  fillSet(h, 0);
  h.$get("#btn-add-set")._fire("click");
  fillSet(h, 1);

  await h.ctx.discover();

  check("both sets were sent", posted.length === 4, JSON.stringify(posted.map(p => p.field)));
  check("fields are numbered per set and named for the role",
    posted.map(p => p.field).join() === "set0.Exposure,set0.Debug,set1.Exposure,set1.Debug",
    JSON.stringify(posted.map(p => p.field)));
  check("the file's own name travels as the filename",
    posted.every(p => p.filename === "ifrs9.csv" || p.filename === "debug.zip"),
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
    defaults: 15813, exposure: 40101222, exposure_fmt: "R 40,101,222.00",
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
  // the tab draws the dashboard natively now and links out to the engine's file
  check("the open-in-a-tab link points at the engine's file",
    /reconciliation_dashboard\.html/.test(h.$get("#res-open").href),
    `href='${h.$get("#res-open").href}'`);

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

/* ---------------- DA: the dashboard's tiles, commentary and AI analysis ---------------- */
async function scenarioDA() {
  console.log("DA) dashboard tiles, commentary and the AI analysis expander");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  const res = JSON.parse(JSON.stringify(DETAIL_RESULT));
  res.commentary = [
    "VERDICT (JUN2026): exceptions found - see the detail below before sign-off.",
    "JUN2026: 373 default account(s) could not be traced to the write-off or IFRS9 file (R 855,159.21 exposure).",
    "JUN2026: Reconciliation validated - the rebuilt migration matrix matches the engine's CohortNlambda counts cell-for-cell.",
  ];
  res.analysis = [
    "## Overview",
    "The reconciliation is materially clean.",
    "## Findings",
    "A small tail of defaults did not trace.",
    "- **Untraced tail** - 373 accounts, mostly small balances.",
    "- plain bullet with no lead",
  ].join("\n");
  res.model_id = "gemini-2.5-pro";

  h.ctx.showResults(res, []);
  h.$get("#tab-btn-dashboard")._fire("click");

  const tiles = h.$get("#dash-tiles").innerHTML;
  check("five tiles", (tiles.match(/class="tile dash"/g) || []).length === 5,
    `tiles=${(tiles.match(/class="tile dash"/g) || []).length}`);
  check("the set count is shown", /Debug sets[\s\S]*?>1</.test(tiles));
  check("the default rate is derived", /2\.15% default rate/.test(tiles),
    "15813/733828 should read 2.15%");
  check("exposure is formatted as rands", /R 40,101,222\.00 exposure/.test(tiles),
    `tiles=${tiles.slice(0, 200)}`);
  check("untraced is flagged", /class="num bad">373</.test(tiles));
  check("in-window is flagged", /class="num bad">4</.test(tiles));

  const com = h.$get("#dash-commentary").innerHTML;
  check("the verdict heads the card", /VERDICT \(JUN2026\)/.test(com));
  check("the pill reads from the verdict", /class="chip error">Exceptions found/.test(com));
  check("the findings follow it", (com.match(/class="cline"/g) || []).length === 2);

  const ai = h.$get("#dash-ai").innerHTML;
  check("the first section shows", /Overview/.test(ai) && /materially clean/.test(ai));
  check("later sections are held back", /id="ai-rest" class="hide"/.test(ai));
  check("a lead-in bullet splits", /<b>Untraced tail<\/b>/.test(ai));
  check("a plain bullet still renders", /plain bullet with no lead/.test(ai));
  check("the model is named", /gemini-2\.5-pro · generated with the run/.test(ai));

  h.$get("#btn-ai")._fire("click");
  check("the expander opens", !h.$get("#ai-rest").classList.contains("hide"));
  check("the expander relabels", /Show less/.test(h.$get("#ai-tx").textContent));
}

/* ---------------- DC: the check and census tables ---------------- */
const DASH_SET_FIX = {
  key: "JUN2026", label: "3. DEBUG FILE 30 JUNE 2026 3 MONTHS",
  scored_in_writeoff: 15073, scored_in_ifrs9: 3194,
  writeoff_distinct: 150226, ifrs9_distinct: 176009,
  wo_pre_window: 0, default_pct_of_scored: 0.0215,
  months: [], migration: {}, monthly_totals: [], lgd: [],
  last_buckets: [], top_untraced: [], wo_exceptions: [],
};

async function scenarioDC() {
  console.log("DC) the check 1, check 2 and census tables");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  const res = JSON.parse(JSON.stringify(DETAIL_RESULT));
  res.dashboard_sets = [JSON.parse(JSON.stringify(DASH_SET_FIX))];
  h.ctx.showResults(res, []);

  const c1 = h.$get("#dash-check1").innerHTML;
  check("check 1 is titled", /are all our defaults accounted for/.test(c1));
  check("check 1 names its inputs", /lgd_defaults\.csv \(Bucket 0\)/.test(c1));
  check("check 1 has nine columns", (c1.match(/<th[ >]/g) || []).length === 9,
    `cols=${(c1.match(/<th[ >]/g) || []).length}`);
  check("untraced is flagged in check 1", /class="num bad">373</.test(c1));
  check("the trace rate is shown", /97\.6%/.test(c1));

  const c2 = h.$get("#dash-check2").innerHTML;
  check("check 2 is titled", /did we miss any defaults/.test(c2));
  check("check 2 has eight columns", (c2.match(/<th[ >]/g) || []).length === 8,
    `cols=${(c2.match(/<th[ >]/g) || []).length}`);
  check("the scoring window is shown", /01 Dec 2025 to 30 Jun 2026/.test(c2));
  check("scored-in-WO comes from the dashboard payload", /15,073/.test(c2));
  check("pre-window comes through", (c2.match(/>0</g) || []).length >= 2);

  const cen = h.$get("#dash-census").innerHTML;
  check("the census is titled", /Distinct account census/.test(cen));
  check("the census has eight columns", (cen.match(/<th[ >]/g) || []).length === 8,
    `cols=${(cen.match(/<th[ >]/g) || []).length}`);
  check("the default share is a percentage of scored", /2\.15%/.test(cen),
    "0.0215 should render as 2.15%");
  check("the write-off population is shown", /150,226/.test(cen));
  check("the IFRS9 population is shown", /176,009/.test(cen));
}

/* ---------------- DE: the interactive migration matrix ---------------- */
const MIG_FIX = {
  "All months": [[10, 5, 0, 0, 0, 0], [0, 20, 4, 0, 0, 0], [0, 0, 0, 0, 0, 0],
                 [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0]],
  "2026-01": [[6, 5, 0, 0, 0, 0], [0, 12, 4, 0, 0, 0], [0, 0, 0, 0, 0, 0],
              [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0]],
  "2026-02": [[4, 0, 0, 0, 0, 0], [0, 8, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0],
              [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0]],
};

async function scenarioDE() {
  console.log("DE) the migration matrix: months, counts/row %, heat and cohort detail");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  const res = JSON.parse(JSON.stringify(DETAIL_RESULT));
  res.outputs.push({ name: "JUN2026_migration_detail.csv", bytes: 96256 });
  res.dashboard_sets = [Object.assign(JSON.parse(JSON.stringify(DASH_SET_FIX)), {
    months: ["All months", "2026-01", "2026-02"],
    migration: MIG_FIX,
    monthly_totals: [27, 12],
  })];
  h.ctx.showResults(res, []);

  const table = () => h.$get("#mig-table").innerHTML;

  check("the matrix is shown", !h.$get("#dash-matrix-row").classList.contains("hide"));
  check("six rows of six cells", (table().match(/class="hc"/g) || []).length === 36,
    `cells=${(table().match(/class="hc"/g) || []).length}`);
  check("the month list offers every month",
    (h.$get("#mig-month").innerHTML.match(/<option/g) || []).length === 3);
  check("counts are shown first", !/%/.test(table()) && />10</.test(table()),
    "counts mode should show no percentages at all");
  check("row cohorts are totalled", />15</.test(table()), "row 1 is 10+5 = 15");
  check("the legend has five stops",
    (h.$get("#mig-legend").innerHTML.match(/<span/g) || []).length === 5);
  check("the summary counts transitions and splits them",
    /39 transitions · 76\.9% stayed, 23\.1% migrated/.test(h.$get("#mig-summary").textContent),
    `summary='${h.$get("#mig-summary").textContent}'`);

  // an empty cell takes the sparse stop, the busiest takes the densest
  check("the busiest cell is densest", /background:var\(--cl-primary-color\);color:#fff">20</.test(table()),
    "20 is the peak so it should take the last stop");

  // row %
  h.$get("#mig-share")._fire("click");
  check("row % is of the row's own cohort", /66\.7%/.test(table()),
    "10 of 15 in row 1 should read 66.7%");
  check("the toggle moves", /class="on"/.test(h.$get("#mig-share").className) ||
    h.$get("#mig-share").classList.contains("on"));
  h.$get("#mig-counts")._fire("click");
  check("counts come back", />10</.test(table()));

  // the cohort detail panel
  check("nothing is selected at first", /Select a cell to inspect/.test(h.$get("#mig-detail").innerHTML));
  h.ctx.pickCell(0, 0);
  let det = h.$get("#mig-detail").innerHTML;
  check("the diagonal reads as stayed", /Stayed in bucket/.test(det) && /Bucket 1</.test(det));
  check("the count is shown", /class="v">10</.test(det));
  check("the share is of the row cohort", /66\.7% of bucket 1/.test(det), `det=${det.slice(0, 300)}`);
  check("the selected cell is marked", /class="hc on"/.test(table()));

  h.ctx.pickCell(0, 1);
  det = h.$get("#mig-detail").innerHTML;
  check("an off-diagonal reads as moved", /Moved between buckets/.test(det) &&
    /Bucket 1 → Bucket 2/.test(det));
  check("the cohort can be exported", /Export cohort/.test(det) &&
    /JUN2026_migration_detail\.csv/.test(det),
    "the export should point at the set's migration detail CSV");

  // changing month clears the selection, since the cell belonged to the old month
  h.$get("#mig-month").value = "2026-02";
  h.$get("#mig-month")._fire("change");
  check("the month switches", />4</.test(table()) && />8</.test(table()));
  check("the selection is dropped", /Select a cell to inspect/.test(h.$get("#mig-detail").innerHTML));

  // monthly movements
  const mo = h.$get("#dash-monthly").innerHTML;
  check("monthly movements exclude the aggregate", !/All months/.test(mo));
  check("monthly totals are listed", /2026-01[\s\S]*?27/.test(mo) && /2026-02[\s\S]*?12/.test(mo));
}

/* ---------------- DL: a run stored before the dashboard kept its own data ---------------- */
async function scenarioDL() {
  console.log("DL) an older run falls back to the engine's dashboard instead of losing eight sections");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  // exactly what a run reconciled before the payload existed looks like
  const old = JSON.parse(JSON.stringify(DETAIL_RESULT));
  delete old.commentary;
  delete old.analysis;
  delete old.dashboard_sets;
  h.ctx.showResults(old, []);

  check("the fallback is shown", !h.$get("#dash-legacy").classList.contains("hide"));
  check("it says why", /reconciled before the dashboard kept its own copy/
    .test(h.$get("#dash-legacy").innerHTML) === false ||
    /before the dashboard kept its/.test(h.$get("#dash-legacy").innerHTML));
  check("the engine's dashboard is embedded",
    /reconciliation_dashboard\.html/.test(h.$get("#res-frame").src),
    `src='${h.$get("#res-frame").src}'`);
  check("what the run does have still renders",
    /15,813/.test(h.$get("#dash-check1").innerHTML) &&
    /733,828/.test(h.$get("#dash-census").innerHTML));

  // and a current run does not get the fallback
  const current = JSON.parse(JSON.stringify(DETAIL_RESULT));
  current.dashboard_sets = [JSON.parse(JSON.stringify(DASH_SET_FIX))];
  h.ctx.showResults(current, []);
  check("a current run has no fallback", h.$get("#dash-legacy").classList.contains("hide"));

  // a run that skipped AI still captured its dashboard data
  const partial = JSON.parse(JSON.stringify(current));
  delete partial.analysis;
  partial.commentary = ["VERDICT (JUN2026): no exceptions - all tie out."];
  h.ctx.showResults(partial, []);
  check("a run that merely skipped AI keeps the native dashboard",
    h.$get("#dash-legacy").classList.contains("hide"),
    "skipping analysis must not trigger the fallback");
}

/* ---------------- DG: engine outputs and the per-set detail ---------------- */
async function scenarioDG() {
  console.log("DG) the hazard matrix, PD by bucket, LGD and the set detail");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  const res = JSON.parse(JSON.stringify(DETAIL_RESULT));
  res.dashboard_sets = [Object.assign(JSON.parse(JSON.stringify(DASH_SET_FIX)), {
    hazard: [
      [0.00004, 0, 0, 0, 0.0251, 0.9748],
      [0, 0.00004, 0, 0, 0.2684, 0.7315],
      [0, 0, 0, 0, 0, 0],
      [0, 0, 0, 0, 0.8949, 0.1051],
      [0, 0, 0, 0, 1, 0],
      [0, 0, 0, 0, 0, 1],
    ],
    cohort: [
      [0, 0, 0, 0, 0.0252, 0], [0, 0, 0, 0, 0.2685, 0], [0, 0, 0, 0, 0, 0],
      [0, 0, 0, 0, 0.8949, 0], [0, 0, 0, 0, 1, 0], [0, 0, 0, 0, 0, 0],
    ],
    lgd: [
      { name: "Lifetime", values: [0.9375, 0.9588, 0.9819, 1.0] },
      { name: "TwelveMonthSingle", values: [0.9375, 0.9588, 0.9819, 1.0] },
    ],
    last_buckets: [{ bucket: "Bucket 6", accounts: 4, share: 100.0, amount: "R 1.50" }],
    top_untraced: [
      { account: "1248927298", cohort_date: "2026-04-30", rating: "5", amount: "R 9,519.38" },
      { account: "27458", cohort_date: "2026-04-30", rating: "5", amount: "R 9,396.13" },
    ],
    wo_exceptions: [
      { account: "1251622562", amount: "R 0.77", date: "30 Apr 2026", window: "IN WINDOW", last_bucket: "6" },
    ],
  })];
  h.ctx.showResults(res, []);

  const hz = h.$get("#dash-hazard").innerHTML;
  check("the hazard matrix is titled", /Engine hazard-rate matrix/.test(hz));
  check("a tiny probability does not round to zero", /&lt;0\.01%/.test(hz),
    "0.000004 should read as <0.01%");
  check("an impossible transition reads as a dash", /&ndash;/.test(hz));
  check("a real probability is shown to two places", /97\.48%/.test(hz));
  check("the engine matrix is not clickable", /class="heat static"/.test(hz));

  const pd = h.$get("#dash-pd").innerHTML;
  check("PD is listed per bucket", (pd.match(/Bucket \d/g) || []).length === 6,
    `buckets=${(pd.match(/Bucket \d/g) || []).length}`);
  check("the absorbing buckets are tagged", (pd.match(/class="tag"/g) || []).length === 2);
  check("a hazard bar is drawn", /class="bar"><span style="width:89%"/.test(pd),
    "0.8949 should give an 89% bar");
  check("a zero PD is spelled out rather than dashed", /0\.00%/.test(pd),
    "in the PD table a zero is a measured figure");

  const lgd = h.$get("#dash-lgd").innerHTML;
  check("LGD names its terms", /0 days/.test(lgd) && /90 days/.test(lgd));
  check("LGD lists each event type", /Lifetime/.test(lgd) && /TwelveMonthSingle/.test(lgd));
  check("LGD values are percentages", /93\.75%/.test(lgd) && /100\.00%/.test(lgd));

  const sd = h.$get("#dash-setdetail").innerHTML;
  check("the set detail is headed by the set", /Set detail: JUN2026/.test(sd));
  check("top untraced defaults are listed", /1248927298/.test(sd) && /R 9,519\.38/.test(sd));
  check("the last-bucket census is shown", /Where the engine last had these accounts/.test(sd) &&
    /100\.0%/.test(sd));
  check("the write-off exceptions are shown", /1251622562/.test(sd) &&
    /class="chip error">In window/.test(sd));

  // A stored run once arrived with PascalCase keys, because the store and the
  // response used different serialisers. That is fixed server-side, but one
  // missing figure must not be able to take the whole dashboard down again.
  const odd = JSON.parse(JSON.stringify(res));
  odd.dashboard_sets[0].last_buckets = [{ bucket: "Bucket 6", accounts: 4, amount: "R 1.50" }];
  let threw = null;
  try { h.ctx.showResults(odd, []); } catch (e) { threw = e.message; }
  check("a row with no share renders instead of throwing", threw === null, `threw: ${threw}`);
  check("the missing share reads as a dash",
    /&mdash;/.test(h.$get("#dash-setdetail").innerHTML));
}

/* ---------------- DH: a run whose scenario had no matrices ---------------- */
async function scenarioDH() {
  console.log("DH) a run with no engine matrices leaves those cards out");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  const res = JSON.parse(JSON.stringify(DETAIL_RESULT));
  res.dashboard_sets = [JSON.parse(JSON.stringify(DASH_SET_FIX))];  // no hazard, no lgd
  h.ctx.showResults(res, []);

  check("the hazard card is hidden", h.$get("#dash-hazard").classList.contains("hide"));
  check("the PD card is hidden", h.$get("#dash-pd").classList.contains("hide"));
  check("the LGD card is hidden", h.$get("#dash-lgd").classList.contains("hide"));
  check("the set detail omits tables it has no rows for",
    !/Top untraced defaults/.test(h.$get("#dash-setdetail").innerHTML));
  check("the tables above still render", /15,813/.test(h.$get("#dash-check1").innerHTML));
}

/* ---------------- DF: a run with no scored file has no matrix ---------------- */
async function scenarioDF() {
  console.log("DF) a run with no migration data hides the matrix rather than drawing an empty one");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  const res = JSON.parse(JSON.stringify(DETAIL_RESULT));
  res.dashboard_sets = [JSON.parse(JSON.stringify(DASH_SET_FIX))];  // months: []
  h.ctx.showResults(res, []);

  check("the matrix row is hidden", h.$get("#dash-matrix-row").classList.contains("hide"));
  check("the tables above still render", /15,813/.test(h.$get("#dash-check1").innerHTML));
}

/* ---------------- DD: a set the dashboard payload knows nothing about ---------------- */
async function scenarioDD() {
  console.log("DD) a set with no dashboard payload still renders its rows");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  // an older stored run, or a set whose scored file was missing
  const res = JSON.parse(JSON.stringify(DETAIL_RESULT));
  delete res.dashboard_sets;
  h.ctx.showResults(res, []);

  check("check 1 still renders", /15,813/.test(h.$get("#dash-check1").innerHTML));
  check("check 2 still renders", /01 Dec 2025/.test(h.$get("#dash-check2").innerHTML));
  check("the census still renders", /733,828/.test(h.$get("#dash-census").innerHTML));
  check("missing figures read as dashes, not undefined",
    !/undefined|NaN/.test(h.$get("#dash-census").innerHTML),
    `census=${h.$get("#dash-census").innerHTML.slice(0, 200)}`);
}

/* ---------------- DB: a clean run, and a run with no analysis ---------------- */
async function scenarioDB() {
  console.log("DB) a clean verdict, and a run with nothing from the model");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  const clean = JSON.parse(JSON.stringify(DETAIL_RESULT));
  clean.sets[0].untraced = 0;
  clean.sets[0].wo_in_window = 0;
  clean.commentary = ["VERDICT (JUN2026): no exceptions - defaults, write-offs and the migration matrix all tie out."];
  delete clean.analysis;

  h.ctx.showResults(clean, []);
  check("a clean verdict reads clean",
    /class="chip done">No exceptions/.test(h.$get("#dash-commentary").innerHTML));
  check("nothing is flagged red", !/class="num bad"/.test(h.$get("#dash-tiles").innerHTML));
  check("no analysis means no card", h.$get("#dash-ai").innerHTML === "",
    "an empty analysis should leave the section out entirely");

  // a run with no commentary at all
  delete clean.commentary;
  h.ctx.showResults(clean, []);
  check("no commentary means no card", h.$get("#dash-commentary").innerHTML === "");
}

/* ---------------- Y: one step at a time, and the rail goes back ---------------- */
async function scenarioY() {
  console.log("Y) exactly one wizard step is on screen, and the rail walks back");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  // steps: 0 files, 1 mapping, 2 confirm, 3 run, 4 results (its own screen)
  const shown = () => ["files", "mapping", "confirm", "run"]
    .filter(s => !h.$get("#step-" + s).classList.contains("hide"));

  h.$get("#nav-new")._fire("click");
  check("files only, at step 1", shown().join() === "files", `shown=${shown().join()||"none"}`);

  h.ctx.showInventory(INVENTORY_FIX);
  check("confirm replaces files", shown().join() === "confirm", `shown=${shown().join()||"none"}`);

  h.ctx.beginRun();
  await tick(); await tick();
  check("run replaces confirm", shown().join() === "run", `shown=${shown().join()||"none"}`);

  // the rail can return to a step already visited
  check("step 1 is offered", h.$get("#rail-0").disabled === false);
  check("the mapping step is offered", h.$get("#rail-1").disabled === false);
  check("the confirm step is offered", h.$get("#rail-2").disabled === false);
  check("the current step is not offered", h.$get("#rail-3").disabled === true);
  check("results is not offered before there is one", h.$get("#rail-4").disabled === true);

  h.$get("#rail-0")._fire("click");
  check("the rail returns to the files", shown().join() === "files", `shown=${shown().join()||"none"}`);
  check("the title follows", /Choose your input files/.test(h.$get("#step-title").textContent));
  check("forward stays reachable once visited", h.$get("#rail-3").disabled === false);

  h.$get("#rail-3")._fire("click");
  check("and forward again to the run", shown().join() === "run", `shown=${shown().join()||"none"}`);

  // a fresh run forgets where the last one got to
  h.$get("#nav-new")._fire("click");
  check("a new run cannot jump ahead", h.$get("#rail-1").disabled === true &&
    h.$get("#rail-3").disabled === true, "later steps should be closed again");
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
  check("the rail walked back mid-run", !h.$get("#step-files").classList.contains("hide"));
  check("the run step is put away", h.$get("#step-run").classList.contains("hide"));

  h.$get("#btn-check")._fire("click");
  check("the superseded poll was dropped", h.timers.armed === null,
    "a live poll would chase the run id discovery is replacing");
}

/* ---------------- DR: Run again after the server forgot the run ----------------
   The app keeps the picked files in the page, so a run the server has forgotten
   (restart, or the job cache evicting it) can be recovered without asking for
   them again. Two regressions live on this path: the guard used to call a helper
   that no longer exists, and the retry used to jump straight from discovery to
   the run - skipping the mapping step, which is the only thing that gives the
   engine its ColumnMaps. */
const REDISCOVER_FIX = {
  run_id: "RID-FRESH",
  inventory: { root: "r", sets: [{ key: "K", label: "L", writeoff: "wo.csv" }] },
  problems: [],
  mapping: [{
    key: "K",
    exposure: {
      has_headers: true,
      headers: ["AccountNo", "Exposure"],
      samples: [["A1", "100"]],
      fields: [
        { field: "AccountNo", column: "AccountNo", confidence: 1, source: "header_match" },
        { field: "Exposure", column: "Exposure", confidence: 1, source: "header_match" },
      ],
    },
  }],
};

async function scenarioDR() {
  console.log("DR) Run again when the server has forgotten the run re-checks and re-maps");
  const calls = [];
  let runCalls = 0;
  let mappingBody = null;
  const h = bootAuth({ access_token: "tok-abc" }, (url, opts) => {
    calls.push(url);
    if (url === "/api/config") return Promise.resolve(jsonRes(200, CFG));
    if (url === "/api/discover") return Promise.resolve(jsonRes(200, REDISCOVER_FIX));
    if (url === "/api/discover/mapping") {
      mappingBody = JSON.parse(opts.body);
      return Promise.resolve(jsonRes(200, { ok: true }));
    }
    if (url === "/api/run") {
      runCalls += 1;
      // the first attempt is against the run id the server has dropped
      return Promise.resolve(runCalls === 1
        ? jsonRes(404, { error: "Unknown run - please run discovery again." })
        : jsonRes(200, { run_id: "RID-FRESH", status: "running" }));
    }
    if (url.startsWith("/api/job/"))
      return Promise.resolve(jsonRes(200, { id: "RID-FRESH", status: "running", log: [], stages: [] }));
    return Promise.resolve(jsonRes(200, []));
  });
  await tick(); await tick(); await tick();

  // the ordinary route to a run: pick the files, check them, confirm the mapping
  fillSet(h, 0);
  await h.ctx.discover();
  await h.ctx.confirmMapping();
  const firstMapping = JSON.stringify(mappingBody);
  check("the mapping was confirmed for the first run", mappingBody !== null &&
    mappingBody.run_id === "RID-FRESH", firstMapping);

  h.ctx.beginRun();
  for (let i = 0; i < 8; i++) await tick();

  check("the retry does not die on a missing helper",
    !/is not defined/.test(errText(h) || ""), `err='${errText(h)}'`);
  check("the run recovers rather than failing", badge(h) === "Running", `badge='${badge(h)}'`);
  check("no error is shown", errText(h) === null, `err='${errText(h)}'`);
  check("the files were re-checked",
    calls.filter(u => u === "/api/discover").length === 2,
    `discover calls=${calls.filter(u => u === "/api/discover").length}`);

  // without this the fresh run id reaches the engine with no ColumnMaps at all
  const lastRun = calls.lastIndexOf("/api/run");
  const lastMap = calls.lastIndexOf("/api/discover/mapping");
  check("the mapping was re-confirmed", lastMap > calls.indexOf("/api/discover/mapping"),
    `calls=${calls.join(" ")}`);
  check("the mapping was re-confirmed before the run started", lastMap < lastRun,
    `calls=${calls.join(" ")}`);
  check("the retried run carries the fresh run id", mappingBody.run_id === "RID-FRESH",
    JSON.stringify(mappingBody));
  check("the mapping sent is the one the user confirmed", JSON.stringify(mappingBody) === firstMapping,
    `first=${firstMapping} second=${JSON.stringify(mappingBody)}`);

  check("the run is polled", h.timers.armed !== null);
  check("the wizard is left on the run step",
    !h.$get("#step-run").classList.contains("hide") &&
    h.$get("#step-confirm").classList.contains("hide"),
    "the retry walks through mapping and confirm, so it has to come back");
  check("it only retries once", runCalls === 2, `run calls=${runCalls}`);
}

/* ---------------- DS: nothing left to re-check ---------------- */
async function scenarioDS() {
  console.log("DS) a forgotten run with no files in the page explains itself");
  // reopening a past run in a fresh session cannot recover: a file input cannot
  // be repopulated from script, so there is nothing to re-upload
  const h = bootAuth({ access_token: "tok-abc" }, (url) => {
    if (url === "/api/config") return Promise.resolve(jsonRes(200, CFG));
    if (url === "/api/run")
      return Promise.resolve(jsonRes(404, { error: "Unknown run - please run discovery again." }));
    return Promise.resolve(jsonRes(200, []));
  });
  await tick(); await tick(); await tick();

  h.ctx.showInventory(INVENTORY_FIX);
  h.ctx.beginRun();
  for (let i = 0; i < 6; i++) await tick();

  check("it does not crash on a missing helper", !/is not defined/.test(errText(h) || ""),
    `err='${errText(h)}'`);
  check("the badge reports the failure", badge(h) === "Failed", `badge='${badge(h)}'`);
  check("the server's advice is shown", /run discovery again/i.test(errText(h) || ""),
    `err='${errText(h)}'`);
  check("the Run button is given back", h.$get("#btn-run").disabled === false);
}

/* ---------------- Z: the Back button uses the same path ---------------- */
async function scenarioZ() {
  console.log("Z) Back on the confirm step returns to folders");
  const h = bootAuth({ access_token: "tok-abc" }, (url) =>
    Promise.resolve(jsonRes(200, url === "/api/config" ? CFG : [])));
  await tick(); await tick(); await tick();

  h.$get("#nav-new")._fire("click");
  h.ctx.showInventory(INVENTORY_FIX);
  h.$get("#btn-back-files")._fire("click");

  check("folders is back", !h.$get("#step-files").classList.contains("hide"));
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
                 scenarioY, scenarioYY, scenarioDR, scenarioDS, scenarioZ, scenarioDA, scenarioDB, scenarioDC, scenarioDD, scenarioDE, scenarioDF, scenarioDG, scenarioDH, scenarioDL]) { await s(); console.log(""); }
console.log(failures === 0 ? "ALL SCENARIOS PASSED" : `${failures} CHECK(S) FAILED`);
process.exit(failures === 0 ? 0 : 1);
