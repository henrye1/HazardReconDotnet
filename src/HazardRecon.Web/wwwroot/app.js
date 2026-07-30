/* Hazard-Rate Reconciliation - front end (.NET) */

const $ = (s) => document.querySelector(s);
const el = (t, c, h) => { const n = document.createElement(t); if (c) n.className = c; if (h !== undefined) n.innerHTML = h; return n; };
const fmt = (n) => (n === null || n === undefined) ? "&mdash;" : Number(n).toLocaleString();

let RUN_ID = null;
let POLL = null;

/* The finished run's payload, held so "View results" can open it without refetching. */
let RESULT = null;

/* The log that goes with the run on screen, so the stages tab can show it. */
let DETAIL_LOG = [];
let seen = 0;
let pollFails = 0;

/* Tolerate a body that isn't JSON (an empty 500, an error page) so a bad
   response surfaces as a message instead of a rejected promise nobody is
   listening to - that rejection is what leaves the UI stuck on "running". */
function readJson(r) {
  return r.text().then(t => {
    let j = null;
    if (t) { try { j = JSON.parse(t); } catch (_) { } }
    return { ok: r.ok, status: r.status, j };
  });
}

/* ---------- session ---------- */
let SB = null;          // supabase client
let TOKEN = null;       // current access token

/* Every API call goes through here: the token is injected, and a 401 means the
   session died underneath us, so we drop back to the gate rather than leaving
   the UI wedged. A 401 with no token in hand is just the pre-login state - the
   gate is already up, so saying "expired" there would be a lie. */
function api(path, options) {
  const opts = options || {};
  const headers = Object.assign({}, opts.headers || {});
  if (TOKEN) headers.Authorization = "Bearer " + TOKEN;
  return fetch(path, Object.assign({}, opts, { headers })).then((r) => {
    if (r.status === 401 && TOKEN) showGate("Your session expired - please sign in again.");
    return r;
  });
}

function showGate(message) {
  TOKEN = null;
  $("#auth-gate").classList.remove("hide");
  if (message) $("#auth-msg").textContent = message;
}

function hideGate() {
  $("#auth-gate").classList.add("hide");
  $("#auth-msg").textContent = "";
}

/* The dashboard loads in an iframe and the artifacts are plain links, neither of
   which can carry an Authorization header. Hand the token to the server once so
   it can hand back a /runs-scoped cookie the browser will attach by itself. */
function openDownloadSession() {
  return api("/api/session", { method: "POST" });
}

function startSession() {
  return fetch("/api/config")
    .then(readJson)
    .then(({ j }) => {
      const cfg = j || {};
      if (cfg.maxBytesPerSet) MAX_SET_BYTES = cfg.maxBytesPerSet;
      SB = supabase.createClient(cfg.supabaseUrl, cfg.supabaseAnonKey);
      return SB.auth.getSession();
    })
    .then((res) => {
      const session = res && res.data ? res.data.session : null;
      if (session) {
        TOKEN = session.access_token;
        showIdentity(session);
        hideGate(); openDownloadSession(); loadModels(); loadHistory();
      }
      else { showGate(""); }
    });
}

/* Supabase reads a sign-up carrying no email as an ANONYMOUS sign-in, and
   answers "Anonymous sign-ins are disabled" - which says nothing about the empty
   box that actually caused it, and names a feature nobody asked for. Worse, with
   no email the password is ignored entirely. So both fields are checked here,
   before the call. See https://github.com/supabase/auth-js/issues/943 */
function credentials() {
  const email = $("#auth-email").value.trim();
  const password = $("#auth-password").value;
  if (!email) { $("#auth-msg").textContent = "Enter your email address."; return null; }
  if (!password) { $("#auth-msg").textContent = "Enter your password."; return null; }
  return { email, password };
}

$("#btn-signin").addEventListener("click", () => {
  const c = credentials();
  if (!c) return;
  SB.auth.signInWithPassword(c).then(({ data, error }) => {
    if (error) { $("#auth-msg").textContent = error.message; return; }
    TOKEN = data.session.access_token;
    showIdentity(data.session);
    hideGate();
    openDownloadSession();
    loadModels();
    loadHistory();
  });
});

$("#btn-signup").addEventListener("click", () => {
  const c = credentials();
  if (!c) return;
  SB.auth.signUp(c).then(({ error }) => {
    $("#auth-msg").textContent = error
      ? error.message
      : "Check your email for a confirmation link, then sign in.";
  });
});

$("#btn-signout").addEventListener("click", () => {
  // drop the download cookie too, or the artifacts stay reachable after sign-out
  SB.auth.signOut()
    .then(() => fetch("/api/session", { method: "DELETE" }))
    .then(() => showGate("Signed out."));
});

startSession();

/* ---------- screens ---------- */
/* Two screens share one page: the run list and the new-run wizard. Nothing is
   fetched on switching - the wizard keeps whatever state it already had, so
   flipping to the list mid-run and back does not lose the progress log. */
function showScreen(name) {
  ["runs", "wizard", "detail"].forEach(s =>
    $("#screen-" + s).classList.toggle("hide", s !== name));

  // the detail screen belongs to a run, so the run list stays the active nav item
  $("#nav-runs").classList.toggle("on", name !== "wizard");
  $("#nav-new").classList.toggle("on", name === "wizard");

  // leaving a run behind closes its conversation
  if (name !== "detail") setChatOpen(false);
  if (name === "runs") loadHistory();
}

/* Puts the wizard back at step 1 with nothing carried over. Reopening a stored
   run leaves the later cards showing, so starting a new one has to clear them
   or the previous run's results sit under a fresh folder picker. */
function resetWizard() {
  RUN_ID = null;
  stopPolling();
  setStep(0);
  $("#step-folders").classList.remove("hide");
  ["#step-confirm", "#step-run"].forEach(s => $(s).classList.add("hide"));
  RESULT = null;
  DETAIL_LOG = [];
  $("#chat-log").innerHTML = "";
  showScreen("wizard");
}

/* Shows who is signed in, from the session rather than a second lookup. */
function showIdentity(session) {
  const email = (session && session.user && session.user.email) || "";
  $("#user-email").textContent = email;
  $("#user-initials").textContent = email ? email.slice(0, 2).toUpperCase() : "—";
}

/* The rail is the only place the wizard's position is expressed; each card
   still shows and hides itself as the flow moves. */
const STEP_TITLES = [
  "Choose your analysis folders",
  "Confirm what was found",
  "Running the reconciliation",
  "Results",
];

function setStep(n) {
  $("#step-title").textContent = STEP_TITLES[n] || STEP_TITLES[0];
  const rail = $("#rail");
  const steps = rail.children || [];
  for (let i = 0; i < steps.length; i++) {
    const st = steps[i];
    if (!st.classList) continue;
    st.classList.remove("on");
    st.classList.remove("was");
    if (i < n) st.classList.add("was");
    else if (i === n) st.classList.add("on");
  }
}

$("#run-search").addEventListener("input", () => {
  RUN_FILTER = $("#run-search").value || "";
  renderHistoryRows();
});

$("#nav-runs").addEventListener("click", () => showScreen("runs"));
$("#nav-new").addEventListener("click", resetWizard);
$("#btn-new-run").addEventListener("click", resetWizard);
$("#btn-cancel").addEventListener("click", () => showScreen("runs"));

/* ---------- run history ---------- */
const STATUS_LABEL = {
  done: "Completed", running: "Running", error: "Failed",
  interrupted: "Interrupted", ready: "Draft",
};

// what the search box is filtering on; the list is fetched once and filtered here
let RUNS = [];
let RUN_FILTER = "";

/* Runs are identified by a uuid, which is too long to read in a table. The
   leading block is short enough to scan and still enough to tell rows apart. */
function runRef(id) {
  return String(id || "").split("-")[0].toUpperCase();
}

/* Wall-clock time the run took, from queued to finished. */
function duration(run) {
  if (!run.created_at || !run.finished_at) return "&mdash;";
  const ms = new Date(run.finished_at) - new Date(run.created_at);
  if (!(ms >= 0)) return "&mdash;";

  const secs = Math.round(ms / 1000);
  if (secs < 60) return secs + "s";

  const mins = Math.floor(secs / 60);
  if (mins < 60) return `${mins}m ${String(secs % 60).padStart(2, "0")}s`;
  return `${Math.floor(mins / 60)}h ${String(mins % 60).padStart(2, "0")}m`;
}

function renderStats(runs) {
  const done = runs.filter(r => r.status === "done");
  const month = new Date();
  month.setDate(1); month.setHours(0, 0, 0, 0);

  const thisMonth = runs.filter(r => new Date(r.created_at) >= month).length;
  const sets = done.reduce((n, r) => n + (r.sets || 0), 0);
  const exceptions = done.reduce((n, r) => n + (r.exceptions || 0), 0);
  const rated = done.filter(r => r.trace_rate > 0);
  const avg = rated.length
    ? (rated.reduce((n, r) => n + r.trace_rate, 0) / rated.length).toFixed(1) + "%"
    : "&mdash;";

  const tiles = [
    ["Runs this month", fmt(thisMonth)],
    ["Sets reconciled", fmt(sets)],
    ["Average traced", avg],
    ["Open exceptions", fmt(exceptions)],
  ];

  $("#stat-tiles").innerHTML = tiles
    .map(([label, value]) => `<div class="tile"><p class="lbl">${label}</p><span class="num">${value}</span></div>`)
    .join("");
}

function renderTrend(runs) {
  // newest last, so the bars read left to right in time order
  const recent = runs.filter(r => r.status === "done").slice(0, 6).reverse();
  // the reference always shows this card; only an empty chart is worth hiding
  if (recent.length === 0) { $("#card-trend").classList.add("hide"); return; }

  const peak = Math.max(1, ...recent.map(r => r.untraced || 0));
  $("#card-trend").classList.remove("hide");
  $("#trend").innerHTML = recent.map(r => {
    const v = r.untraced || 0;
    const pct = Math.max(2, Math.round((v / peak) * 100));
    const when = new Date(r.created_at).toLocaleDateString(undefined, { day: "numeric", month: "short" });
    return `<div class="bar"><b>${fmt(v)}</b><i style="height:${pct}%"></i><span>${when}</span></div>`;
  }).join("");
}

function loadHistory() {
  return api("/api/runs")
    .then(readJson)
    .then(({ ok, j }) => {
      const runs = (ok && Array.isArray(j)) ? j : [];

      renderStats(runs);
      renderTrend(runs);

      RUNS = runs;
      renderHistoryRows();
    })
    .catch(() => { /* history is a convenience; never block the app on it */ });
}

/* Split from the fetch so the search box can refilter without a round trip. */
function renderHistoryRows() {
  const q = RUN_FILTER.trim().toLowerCase();
  const runs = q
    ? RUNS.filter(r => (runRef(r.id) + " " + (r.set_labels || []).join(" ") +
        " " + (STATUS_LABEL[r.status] || r.status)).toLowerCase().includes(q))
    : RUNS;

  $("#history-empty").classList.toggle("hide", runs.length > 0);
  $("#history-table").classList.toggle("hide", runs.length === 0);
  $("#history-foot").classList.toggle("hide", runs.length === 0);
  $("#history-count").textContent = `Showing ${runs.length} of ${RUNS.length} runs`;
  if (runs.length === 0) return;

  $("#history-table").innerHTML =
    "<thead><tr><th>Run</th><th>Started</th><th>Sets</th><th>Status</th>" +
    "<th class='num'>Traced</th><th class='num'>Untraced</th>" +
    "<th class='num'>Exceptions</th><th class='num'>Duration</th>" +
    "<th></th></tr></thead><tbody>" +
    runs.map(r => {
      const done = r.status === "done";
      const when = new Date(r.created_at).toLocaleString();
      const labels = (r.set_labels || []).join(", ") || "&mdash;";
      const traced = r.trace_rate > 0 ? r.trace_rate.toFixed(1) + "%" : "&mdash;";
      const untraced = done ? fmt(r.untraced || 0) : "&mdash;";
      const exceptions = done ? fmt(r.exceptions || 0) : "&mdash;";
      const label = STATUS_LABEL[r.status] || r.status;
      // only a completed run has anything to open
      const go = done
        ? `<span class="ms-icon rowgo" title="Open this run">arrow_forward</span>`
        : "";

      return `<tr${done ? ` class="clickable" data-run="${r.id}"` : ""}>` +
             `<td><div class="runid"><b>${runRef(r.id)}</b><span>${labels}</span></div></td>` +
             `<td style="white-space:nowrap">${when}</td>` +
             `<td>${fmt(r.sets || 0)}</td>` +
             `<td><span class="chip ${r.status}">${label}</span></td>` +
             `<td class="num">${traced}</td>` +
             `<td class="num untraced${done && r.untraced > 0 ? " no" : ""}">${untraced}</td>` +
             `<td class="num">${exceptions}</td>` +
             `<td class="num dur">${duration(r)}</td>` +
             `<td class="num">${go}</td></tr>`;
    }).join("") + "</tbody>";

  Array.from($("#history-table").querySelectorAll("tr.clickable"))
    .forEach(tr => tr.addEventListener("click", () => openRun(tr.getAttribute("data-run"))));
}

/* Reopens a stored run: its summaries, downloads and dashboard come back from
   the database and object storage, so they survive a restart. */
function openRun(id) {
  return api("/api/runs/" + id)
    .then(readJson)
    .then(({ ok, j }) => {
      if (!ok || !j || !j.result) return;
      RUN_ID = j.id;

      // a stored run opens straight on its detail; there is nothing to pick or
      // confirm, so the wizard is not involved at all
      showResults(j.result, j.log);

      // replay the conversation that went with this run
      $("#chat-log").innerHTML = "";
      (j.chat || []).forEach(m => addChatBubble(
        m.role === "user" ? "user" : "bot",
        m.role === "user" ? escapeHtml(m.content) : (m.content_html || escapeHtml(m.content))));
    });
}

/* ---------- step 1: folder paths ---------- */
const MAX_SETS = 4;
let PATHS = 0;

/* Comes from /api/config, so it cannot drift from the server's own limit and
   reject folders the server would have accepted. Checked here to catch an
   oversized folder before the upload, and again on the server because a browser
   check protects nobody. The fallback only applies before config arrives. */
let MAX_SET_BYTES = 512 * 1024 * 1024;

function addPathRow() {
  if (PATHS >= MAX_SETS) return;
  PATHS += 1;
  const i = PATHS;
  const d = el("div", "slot", `
    <span class="ms-icon">folder_open</span>
    <div class="sx">
      <span class="num">Folder ${i}</span>
      <input type="file" id="path${i}" webkitdirectory directory multiple>
      <button type="button" class="pick" id="path${i}-pick">Choose a folder&hellip;</button>
      <span class="meta" id="path${i}-info"></span>
    </div>
    <button class="x" id="path${i}-clear" title="Clear"><span class="ms-icon" style="font-size:20px">close</span></button>`);
  $("#paths").appendChild(d);

  // the file input is hidden, so the folder name doubles as the picker
  $("#path" + i + "-pick").addEventListener("click", () => $("#path" + i).click());
  $("#path" + i).addEventListener("change", () => { describeSet(i); updateReady(); });
  $("#path" + i + "-clear").addEventListener("click", () => {
    $("#path" + i).value = "";
    describeSet(i);
    updateReady();
  });
  $("#btn-add-path").disabled = PATHS >= MAX_SETS;
  describeSet(i);
}

function setFiles(i) {
  const input = $("#path" + i);
  return input && input.files ? Array.from(input.files) : [];
}

const setBytes = (files) => files.reduce((n, f) => n + (f.size || 0), 0);

/* The folder's own name is only knowable from a file's relative path - the
   picker never reveals where on disk it came from. */
const setLabel = (files) =>
  files.length ? (files[0].webkitRelativePath || files[0].name || "").split("/")[0] : "";

function describeSet(i) {
  const info = $("#path" + i + "-info");
  const pick = $("#path" + i + "-pick");
  const files = setFiles(i);

  if (!files.length) {
    pick.innerHTML = "Choose a folder&hellip;";
    pick.className = "pick";
    info.textContent = "";
    info.className = "meta";
    return;
  }

  // once chosen, the folder's own name is the heading and the picker becomes it
  pick.textContent = setLabel(files);
  pick.className = "pick name";

  const bytes = setBytes(files);
  const mb = bytes / (1024 * 1024);
  const tooBig = bytes > MAX_SET_BYTES;
  info.textContent = `${files.length} files, ${mb.toFixed(1)} MB` +
    (tooBig ? ` - too large, the limit is ${Math.round(MAX_SET_BYTES / (1024 * 1024))} MB per folder` : "");
  info.className = tooBig ? "meta bad" : "meta";
}

function updateReady() {
  let chosen = 0;
  let oversized = false;
  for (let i = 1; i <= PATHS; i++) {
    const files = setFiles(i);
    if (!files.length) continue;
    chosen += 1;
    if (setBytes(files) > MAX_SET_BYTES) oversized = true;
  }
  $("#btn-check").disabled = chosen === 0 || oversized;

  $("#folder-count").textContent = chosen === 0
    ? `No folders chosen yet, up to ${MAX_SETS}`
    : `${chosen} of ${MAX_SETS} folders chosen`;
}

addPathRow();
$("#btn-add-path").addEventListener("click", addPathRow);
updateReady();
// no restore of a previous choice: a file input cannot be repopulated from
// script, so there is nothing to put back. Run history replaces this.

/* ---------- step 2: model ---------- */
function addModelOption(value, label) {
  const o = document.createElement("option");
  o.value = value;
  o.textContent = label;
  $("#model").appendChild(o);
  return o;
}

/* Safe to call more than once: a sign-in after a failed attempt has to be able
   to recover. Without the reset, a first failure latches sel.disabled on and no
   later success clears it, leaving a greyed-out picker full of duplicate
   "Skip AI analysis" rows. */
function loadModels() {
  const sel = $("#model");
  const note = $("#model-note");
  sel.innerHTML = "";
  sel.disabled = false;
  note.textContent = "";
  addModelOption("", "Skip AI analysis");
  return api("/api/models")
    .then(readJson)
    .then(({ ok, j }) => {
      if (!ok || !Array.isArray(j)) {
        sel.disabled = true;
        note.textContent = (j && j.error) || "Model list unavailable - runs will skip AI analysis.";
        return;
      }
      j.forEach(m => addModelOption(m.id, m.friendlyName));
      const saved = localStorage.getItem("hr_model") || "";
      sel.value = j.some(m => m.id === saved) ? saved : "";
      note.textContent = "Analysis adds roughly 25 seconds to a run and produces the memo " +
        "alongside the workbook.";
    })
    .catch(e => {
      sel.disabled = true;
      note.textContent = "Model list unavailable - " + e.message;
    });
}

$("#model").addEventListener("change", () => localStorage.setItem("hr_model", $("#model").value));
// no bare loadModels() here: /api/models needs a token, so it is called from the
// session bootstrap and after sign-in. Calling it at load only ever produced a
// guaranteed 401.

function discover() {
  const fd = new FormData();
  let sets = 0;
  for (let i = 1; i <= PATHS; i++) {
    const files = setFiles(i);
    if (!files.length) continue;
    // one field per file, named for its set; the third argument carries the
    // path relative to the picked folder, which is what rebuilds the structure
    files.forEach(f => fd.append("set" + sets, f, f.webkitRelativePath || f.name));
    sets += 1;
  }

  if (sets === 0) return Promise.reject(new Error("Please choose at least one folder."));

  return api("/api/discover", { method: "POST", body: fd })
    .then(readJson)
    .then(({ ok, status, j }) => {
      if (!ok || !j) throw new Error((j && j.error) || `Discovery failed (server returned ${status}).`);
      showInventory(j);
      return j;
    });
}

$("#btn-check").addEventListener("click", () => {
  const btn = $("#btn-check");
  btn.disabled = true;
  $("#btn-check-tx").textContent = "Checking...";
  discover()
    .catch(e => showError($("#step-confirm"), e.message))
    .finally(() => {
      btn.disabled = false;
      $("#btn-check-tx").textContent = "Check folders";
      updateReady();
    });
});

function showError(card, msg) {
  card.classList.remove("hide");
  const box = card.querySelector(".err") || el("div", "err");
  box.textContent = msg;
  if (!box.parentNode) card.appendChild(box);
}

function showInventory(j) {
  RUN_ID = j.run_id;
  setStep(1);
  $("#step-confirm").classList.remove("hide");
  const card = $("#card-inv");
  const old = card.querySelector(".err"); if (old) old.remove();

  $("#inv-root").innerHTML = "Reading from <code>" + (j.inventory.root || "") + "</code>";

  const probs = $("#inv-problems");
  probs.innerHTML = "";
  const clean = !(j.problems && j.problems.length);
  if (!clean) {
    probs.appendChild(el("div", "warn",
      "<b>Worth knowing before you run:</b><ul>" +
      j.problems.map(p => "<li>" + p + "</li>").join("") + "</ul>"));
  }
  // saying so explicitly is worth more than saying nothing
  $("#inv-ok").classList.toggle("hide", !clean);

  const t = $("#inv-table");
  t.innerHTML =
    "<thead><tr><th>Set</th><th>Folder</th><th>Write-off</th><th>Defaults</th>" +
    "<th>Scored</th><th>IFRS9</th><th>Engine</th></tr></thead><tbody>" +
    j.inventory.sets.map(s =>
      "<tr><td class='setkey'>" + s.key + "</td><td class='dim'>" + s.label + "</td>" +
      cell(s.writeoff) + cell(s.lgd_defaults) + cell(s.pd_scored) +
      cell(s.ifrs9) + cell(s.scenario) + "</tr>").join("") + "</tbody>";

  $("#btn-run").disabled = !(j.inventory.sets || []).length;
  $("#step-confirm").scrollIntoView({ behavior: "smooth", block: "start" });
}

const cell = (v) => "<td style='white-space:nowrap'>" + (v
  ? "<span class='ms-icon found'>check_circle</span>" + v
  : "<span class='ms-icon missing'>cancel</span>missing") + "</td>";

/* ---------- step 3: run ---------- */
function stopPolling() {
  if (POLL) { clearInterval(POLL); POLL = null; }
  seen = 0;
  pollFails = 0;
}

/* Every dead end goes through here: stop polling, say why, give the button
   back. Nothing may leave the badge on "running" with no way forward. */
function failRun(msg) {
  stopPolling();
  setBadge("Failed", "error");
  $("#run-headline").textContent = "The run stopped";
  showError($("#step-run"), msg);
  $("#btn-run").disabled = false;
}

function beginRun() {
  if (!RUN_ID) return;
  stopPolling();
  // Run again is offered from the run detail, so come back to the wizard first
  showScreen("wizard");
  setStep(2);
  $("#step-run").classList.remove("hide");
  setChatOpen(false);
  const stale = $("#step-run").querySelector(".err"); if (stale) stale.remove();

  $("#log").innerHTML = "";
  setLogOpen(false);
  $("#stages").innerHTML = "";
  $("#run-bar").style.width = "0%";
  $("#run-meta").textContent = "";
  $("#run-headline").textContent = "Starting the run…";
  RESULT = null;
  $("#btn-view-results").disabled = true;
  setBadge("Running", "running");

  $("#btn-run").disabled = true;
  $("#step-run").scrollIntoView({ behavior: "smooth", block: "start" });
  startRun(false);
}

/* ---------- stage tracker ---------- */
const STAGE_ICON = {
  pending: "radio_button_unchecked",
  running: "progress_activity",
  done: "check_circle",
  warn: "warning",
  skipped: "remove_circle",
  error: "error",
};

/* m:ss, for the elapsed clock */
function clock(secs) {
  const t = Math.max(0, Math.round(secs));
  return Math.floor(t / 60) + ":" + String(t % 60).padStart(2, "0");
}

/* Per-stage durations read better short: 4.2s, then 1m 05s. */
function stageTime(secs) {
  if (secs == null) return "";
  if (secs < 60) return secs + "s";
  const t = Math.round(secs);
  return Math.floor(t / 60) + "m " + String(t % 60).padStart(2, "0") + "s";
}

function renderStages(stages, target) {
  $(target || "#stages").innerHTML = stages.map(s => {
    const st = STAGE_ICON[s.status] ? s.status : "pending";
    return `<div class="stage ${st}">` +
      `<span class="ms-icon">${STAGE_ICON[st]}</span>` +
      `<div class="sx"><b>${escapeHtml(s.name || "")}</b>` +
      `<span>${escapeHtml(s.detail || "")}</span></div>` +
      `<span class="t">${stageTime(s.seconds)}</span></div>`;
  }).join("");
}

/* The bar, the counter and the headline all read off the same stage list, so they
   cannot disagree about how far along the run is. */
function renderProgress(j) {
  const stages = Array.isArray(j.stages) ? j.stages : [];
  renderStages(stages);

  const total = stages.length;
  const settled = stages.filter(s => s.status !== "pending" && s.status !== "running").length;
  $("#run-bar").style.width = (total ? Math.round((settled / total) * 100) : 0) + "%";

  const running = stages.findIndex(s => s.status === "running");
  const bits = [];
  if (j.elapsed_seconds != null) bits.push("Elapsed " + clock(j.elapsed_seconds));
  if (total) bits.push(`stage ${running >= 0 ? running + 1 : settled} of ${total}`);
  $("#run-meta").textContent = bits.join(" · ");

  $("#run-headline").textContent =
    j.status === "done" ? "Reconciliation complete"
      : j.status === "error" ? "The run stopped"
      : running >= 0 ? stages[running].name
      : "Starting the run…";
}

/* The log is secondary here, so it starts closed on every run. */
let LOG_OPEN = false;
function setLogOpen(open) {
  LOG_OPEN = open;
  $("#card-log").classList.toggle("hide", !open);
  $("#btn-log-tx").textContent = open ? "Hide raw log" : "Show raw log";
  $("#btn-log-ic").textContent = open ? "expand_less" : "expand_more";
}
$("#btn-log").addEventListener("click", () => setLogOpen(!LOG_OPEN));

$("#btn-back-runs").addEventListener("click", () => showScreen("runs"));
$("#btn-view-results").addEventListener("click", () => {
  if (RESULT) showResults(RESULT);
});

function startRun(hasRetried) {
  api("/api/run", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ run_id: RUN_ID, model_id: $("#model").value || null })
  }).then(readJson)
    .then(({ ok, status, j }) => {
      if (status === 404 && !hasRetried && pathValues().length) {
        // the app was restarted and forgot this run - quietly re-check the
        // same folders, then start again with the fresh run id
        return discover()
          .then(() => startRun(true))
          .catch(e => failRun(e.message));
      }
      if (!ok || !j) {
        failRun((j && j.error) || `The server returned ${status} when starting the run.`);
        return;
      }
      if (j.error) { failRun(j.error); return; }
      stopPolling();
      POLL = setInterval(poll, 700);
    })
    .catch(e => failRun("Could not start the run - " + e.message));
}

$("#btn-run").addEventListener("click", beginRun);
$("#btn-rerun").addEventListener("click", beginRun);

/* Back to the folders, keeping whatever was picked - the inventory is discarded
   because the folders may change before the next check. */
$("#btn-back-folders").addEventListener("click", () => {
  setStep(0);
  $("#step-confirm").classList.add("hide");
  $("#step-folders").classList.remove("hide");
  $("#step-folders").scrollIntoView({ behavior: "smooth", block: "start" });
});

function setBadge(text, cls) {
  const b = $("#run-badge");
  b.textContent = text;
  b.className = "chip " + (cls || "");
}

// consecutive failed polls tolerated (~6s) before we stop and say so
const MAX_POLL_FAILS = 8;

function pollFailed(msg) {
  if (++pollFails >= MAX_POLL_FAILS) failRun(msg);
}

function poll() {
  api("/api/job/" + RUN_ID).then(readJson).then(({ ok, status, j }) => {
    // the server forgot this run (it was restarted) - polling can never
    // succeed again, so stop instead of spinning on "running" forever
    if (status === 404) {
      failRun("The server no longer knows about this run - it was restarted. " +
              "Check the folders again to start a new run.");
      return;
    }
    if (!ok || !j || typeof j.status !== "string") {
      pollFailed(`The server returned ${status} while the run was in progress.`);
      return;
    }
    pollFails = 0;

    const box = $("#log");
    (j.log || []).slice(seen).forEach(l => {
      const d = el("div");
      d.innerHTML = "<span class='t'>" + l.t + "</span><span class='" + l.kind + "'>" +
        mark(l.kind) + escapeHtml(l.msg) + "</span>";
      box.appendChild(d);
    });
    if ((j.log || []).length !== seen) { seen = (j.log || []).length; box.scrollTop = box.scrollHeight; }

    renderProgress(j);

    if (j.status === "done") {
      stopPolling();
      if (!j.result) { failRun("The run finished but returned no results."); return; }
      setBadge("Completed", "done");
      $("#btn-run").disabled = false;
      // the results are a screen of their own, reached from the button
      RESULT = j.result;
      DETAIL_LOG = j.log || [];
      $("#btn-view-results").disabled = false;
      loadHistory();
    } else if (j.status === "error") {
      stopPolling();
      setBadge("Failed", "error");
      $("#btn-run").disabled = false;
      showError($("#step-run"), j.error || "The run failed.");
      loadHistory();
    }
  }).catch(() => pollFailed("Lost contact with the server while the run was in progress."));
}

const mark = (k) => k === "tool" ? "→ " : k === "ok" ? "✓ " : k === "warn" ? "! " : k === "head" ? "■ " : "  ";
const escapeHtml = (s) => String(s).replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]));

/* ---------- run detail ---------- */
function showResults(res, log) {
  if (!res) return;
  RESULT = res;
  if (Array.isArray(log)) DETAIL_LOG = log;

  renderDetailHeader(res);
  renderSummaryTab(res);
  renderStagesTab(res);
  renderDashboardTab(res);
  renderFilesTab(res);
  showTab("summary");
  // a freshly opened run starts with its conversation closed
  setChatOpen(false);
  showScreen("detail");
}

function renderDetailHeader(res) {
  $("#detail-title").textContent = runRef(RUN_ID);

  const labels = (res.sets || []).map(s => s.label || s.key).filter(Boolean);
  const bits = [];
  if (labels.length) bits.push(labels.join(", "));
  bits.push(`${(res.sets || []).length} set${(res.sets || []).length === 1 ? "" : "s"}`);
  if (res.elapsed_seconds != null) bits.push("ran in " + stageTime(res.elapsed_seconds));
  $("#detail-meta").textContent = bits.join(" · ");

  $("#chat-run").textContent = [runRef(RUN_ID)].concat(labels.slice(0, 1)).join(" · ");
}

/* ---------- summary tab ---------- */
function renderSummaryTab(res) {
  $("#tab-summary").innerHTML = (res.sets || []).map(s => {
    const head = `${escapeHtml(s.key)} — ${escapeHtml(s.label || "")}`;
    const window = `Scoring window ${escapeHtml(s.window || "n/a")} · ${fmt(s.scored)} scored accounts`;

    const mismatch = s.ifrs9_overlap === 0
      ? `<p class="warn" style="margin:0">IFRS9 could not be matched for this set — the two files
           number accounts differently, so every default traced through the write-off file alone.</p>`
      : "";

    return `
      <div class="card">
        <div class="cardhead strong"><span>${head}</span><span class="sub">${window}</span></div>
        <div class="cardbody" style="display:grid;gap:15px">
          ${mismatch}
          <div class="ktiles">
            <div class="ktile"><span class="l">Defaults (Bucket 0)</span>
              <span class="v">${fmt(s.defaults)}</span><span class="s">${escapeHtml(s.exposure_fmt || "")}</span></div>
            <div class="ktile"><span class="l">Traced</span>
              <span class="v good">${fmt(s.traced)}</span>
              <span class="s">${s.trace_rate}% · W/O ${fmt(s.traced_writeoff)} / IFRS9 ${fmt(s.traced_ifrs9)}</span></div>
            <div class="ktile"><span class="l">Untraced defaults</span>
              <span class="v ${s.untraced > 0 ? "bad" : ""}">${fmt(s.untraced)}</span>
              <span class="s">${escapeHtml(s.untraced_fmt || "")}</span></div>
            <div class="ktile"><span class="l">Written off, never defaulted</span>
              <span class="v ${s.wo_in_window > 0 ? "warn" : ""}">${fmt(s.wo_in_window)}</span>
              <span class="s">in window · ${escapeHtml(s.wo_in_window_fmt || "")}</span></div>
          </div>
          <p class="hint" style="margin:0">Check 2 found ${fmt(s.wo_total)} in total —
            ${fmt(s.wo_in_window)} inside the scoring window (the priority exceptions) and
            ${fmt(s.wo_post_window)} written off after it closed.</p>
        </div>
      </div>

      <div class="cols2">
        <div class="card">
          <div class="cardhead"><span>Check 1 — trace every default</span></div>
          <div class="checkbody">
            <p>Every default at Bucket 0 should appear in the write-off file or the IFRS9 file.
               Those in neither are <b>untraced</b>.</p>
            <div class="rag">
              <span class="green">${fmt(s.traced)} traced · ${s.trace_rate}%</span>
              <span class="${s.untraced > 0 ? "red" : "grey"}">${fmt(s.untraced)} untraced${
                s.untraced > 0 ? " · " + escapeHtml(s.untraced_fmt || "") : ""}</span>
            </div>
          </div>
        </div>
        <div class="card">
          <div class="cardhead"><span>Check 2 — the reverse trace</span></div>
          <div class="checkbody">
            <p>Scored accounts written off but <b>never flagged as a default</b>. Those written off
               inside the scoring window are the priority exceptions.</p>
            <div class="rag">
              <span class="${s.wo_in_window > 0 ? "amber" : "grey"}">${fmt(s.wo_in_window)} in window${
                s.wo_in_window > 0 ? " · " + escapeHtml(s.wo_in_window_fmt || "") : ""}</span>
              <span class="grey">${fmt(s.wo_post_window)} after window close</span>
            </div>
          </div>
        </div>
      </div>

      <div class="card">
        <div class="cardhead"><span>Validation</span></div>
        ${validationRow(s)}
      </div>`;
  }).join("");
}

/* The rebuilt matrix against the engine's own. A failure is a finding to read,
   not an error to hide. */
function validationRow(s) {
  const status = s.mig_validation || "N/A";
  const diff = s.mig_max_diff;

  if (status === "N/A") {
    return `<div class="validrow">
      <span class="ms-icon" style="color:var(--cl-off-text-color)">help</span>
      <span class="tx">The debug file carried no <code>CohortNlambda</code> to compare against, so the
        rebuilt migration matrix could not be validated.</span></div>`;
  }

  const pass = status === "PASS";
  return `<div class="validrow">
    <span class="ms-icon" style="color:var(--cl-${pass ? "action" : "error"}-color)">${
      pass ? "verified" : "error"}</span>
    <span class="tx">Rebuilt migration matrix vs <code>debug.json</code> CohortNlambda —
      <b>${escapeHtml(status)}</b>${diff == null ? "" : `, max cell difference ${diff}`}.</span></div>`;
}

/* ---------- stages tab ---------- */
function renderStagesTab(res) {
  const stages = Array.isArray(res.stages) ? res.stages : [];
  renderStages(stages, "#detail-stages");

  const done = stages.filter(s => s.status !== "pending" && s.status !== "running").length;
  const parts = stages.length ? [`${done} of ${stages.length} complete`] : [];
  if (res.elapsed_seconds != null) parts.push(stageTime(res.elapsed_seconds) + " total");
  $("#detail-stage-count").textContent = parts.join(" · ");

  $("#detail-log").innerHTML = DETAIL_LOG.map(l =>
    `<div><span class="t">${escapeHtml(l.t || "")}</span>` +
    `<span class="${escapeHtml(l.kind || "")}">${escapeHtml(l.msg || "")}</span></div>`).join("");

  // each run opens with its log tucked away, however the last one was left
  setDetailLogOpen(false);
}

let DETAIL_LOG_OPEN = false;
function setDetailLogOpen(open) {
  DETAIL_LOG_OPEN = open;
  $("#detail-log").classList.toggle("hide", !open);
  $("#detail-log-tx").textContent = open ? "Hide raw engine log" : "Show raw engine log";
  $("#detail-log-ic").textContent = open ? "expand_less" : "expand_more";
}
$("#btn-detail-log").addEventListener("click", () => setDetailLogOpen(!DETAIL_LOG_OPEN));

/* ---------- dashboard tab ---------- */
function renderDashboardTab(res) {
  // cache-busted: a rerun writes the same filename
  const url = outputUrl(res.dashboard);
  $("#res-frame").src = url;
  $("#res-open").href = url;
}

const outputUrl = (name) =>
  "/runs/" + RUN_ID + "/output/" + encodeURIComponent(name) + "?v=" + Date.now();

/* ---------- files tab ---------- */
/* What each output is, from its name - the server sends only name and size. */
function describeFile(name) {
  const n = String(name).toLowerCase();
  if (n.endsWith(".xlsx")) return ["table_view", "var(--cl-success-color)", "Workbook — every set, every sheet"];
  if (n.endsWith(".docx")) return ["article", "var(--cl-primary-color)", "Analysis memo"];
  if (n.endsWith(".html")) return ["grid_on", "var(--cl-primary-color)", "Interactive dashboard"];
  if (n.includes("writeoff_not_default"))
    return ["warning", "var(--cl-warning-text)", "Written off but never flagged as a default"];
  if (n.includes("untraced"))
    return ["assignment_late", "var(--cl-error-color)", "Defaults that could not be traced"];
  if (n.includes("migration")) return ["grid_on", "var(--cl-primary-color)", "Migration matrix detail"];
  return ["description", "var(--cl-off-text-color)", "Reconciliation detail"];
}

function fileSize(bytes) {
  if (bytes == null || bytes <= 0) return "";
  if (bytes < 1024) return bytes + " B";
  const kb = bytes / 1024;
  if (kb < 1024) return kb.toFixed(kb < 10 ? 1 : 0) + " KB";
  return (kb / 1024).toFixed(1) + " MB";
}

function renderFilesTab(res) {
  // older runs were stored before sizes were recorded, so fall back to the names
  const outputs = Array.isArray(res.outputs) && res.outputs.length
    ? res.outputs
    : [res.memo, res.workbook, res.dashboard]
        .concat((res.sets || []).flatMap(s => s.files || []))
        .filter(Boolean).map(name => ({ name, bytes: 0 }));

  $("#detail-files").innerHTML = outputs.map(f => {
    const [icon, color, note] = describeFile(f.name);
    return `<div class="frow">
      <span class="ms-icon" style="color:${color}">${icon}</span>
      <div class="fx"><span class="n">${escapeHtml(f.name)}</span>
        <span class="note">${note}</span></div>
      <span class="sz">${fileSize(f.bytes)}</span>
      <a class="dl" href="${outputUrl(f.name)}">
        <span class="ms-icon" style="font-size:18px">download</span>Download</a>
    </div>`;
  }).join("");
}

/* ---------- tabs ---------- */
const DETAIL_TABS = ["summary", "stages", "dashboard", "files"];

function showTab(name) {
  DETAIL_TABS.forEach(t => {
    $("#tab-" + t).classList.toggle("hide", t !== name);
    $("#tab-btn-" + t).classList.toggle("on", t === name);
  });
}

DETAIL_TABS.forEach(t => $("#tab-btn-" + t).addEventListener("click", () => showTab(t)));

$("#btn-detail-back").addEventListener("click", () => showScreen("runs"));

/* ---------- ask about this run ---------- */
function setChatOpen(open) {
  $("#chat-drawer").classList.toggle("hide", !open);
  if (open) $("#chat-input").focus();
}

$("#btn-chat-open").addEventListener("click", () => setChatOpen(true));
$("#btn-chat-close").addEventListener("click", () => setChatOpen(false));

function addChatBubble(cls, html) {
  const box = $("#chat-log");
  const d = el("div", "bubble " + cls, html);
  box.appendChild(d);
  box.scrollTop = box.scrollHeight;
  return d;
}

function sendChat() {
  const input = $("#chat-input");
  const msg = input.value.trim();
  if (!msg || !RUN_ID) return;
  addChatBubble("user", escapeHtml(msg));
  input.value = "";
  const btn = $("#btn-chat");
  btn.disabled = true; input.disabled = true;
  const thinking = addChatBubble("bot thinking", "thinking&hellip;");
  api("/api/chat", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ run_id: RUN_ID, message: msg })
  }).then(r => r.json().then(j => ({ ok: r.ok, j })))
    .then(({ ok, j }) => {
      thinking.remove();
      if (!ok) { addChatBubble("bot err", escapeHtml(j.error || "Chat is unavailable.")); return; }
      addChatBubble("bot", j.reply_html);
    })
    .catch(() => { thinking.remove(); addChatBubble("bot err", "Network error - please try again."); })
    .finally(() => { btn.disabled = false; input.disabled = false; input.focus(); });
}

$("#btn-chat").addEventListener("click", sendChat);
$("#chat-input").addEventListener("keydown", (e) => {
  if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); sendChat(); }
});
