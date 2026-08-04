/* Hazard-Rate Reconciliation - front end (.NET) */

const $ = (s) => document.querySelector(s);
const el = (t, c, h) => { const n = document.createElement(t); if (c) n.className = c; if (h !== undefined) n.innerHTML = h; return n; };
/* Label as text, not markup: a select's options carry column names read out of an
   uploaded file, which may contain anything. */
const option = (value, label) => { const o = el("option"); o.value = value; o.textContent = label; return o; };
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
  RESULT = null;
  DETAIL_LOG = [];
  // the previous run's files and mapping must not be carried into a new one
  DISCOVERED = null;
  MAP_FILES = [];
  MAP_EDITS = {};
  $("#map-files").innerHTML = "";
  SETS = [emptySet()];
  renderSets();
  ["#step-files", "#step-mapping"].forEach(sel => {
    const stale = $(sel).querySelector(".err"); if (stale) stale.remove();
  });
  // a fresh run has been nowhere, so nothing is reachable from the rail yet
  STEP_REACHED = 0;
  setStep(0);
  $("#chat-log").innerHTML = "";
  showScreen("wizard");
}

/* Shows who is signed in, from the session rather than a second lookup. */
function showIdentity(session) {
  const email = (session && session.user && session.user.email) || "";
  $("#user-email").textContent = email;
  $("#user-initials").textContent = email ? email.slice(0, 2).toUpperCase() : "—";
}

const STEP_TITLES = [
  "Choose your input files",
  "Map columns to engine fields",
  "Confirm what was found",
  "Running the reconciliation",
  "Results",
];

/* One body per step. The last step is the run detail, which is a screen of its
   own, so it has no entry here. */
const STEP_BODIES = ["#step-files", "#step-mapping", "#step-confirm", "#step-run"];

/* The results step, which lives on its own screen rather than in a wizard body. */
const STEP_RESULTS = STEP_TITLES.length - 1;

let STEP_AT = 0;

/* The furthest step this run has got to. A step already visited can be returned
   to from the rail; one not yet reached cannot be jumped to, because getting
   there means doing the work the step before it asks for. */
let STEP_REACHED = 0;

/* The rail expresses the wizard's position and the step bodies follow it, so a
   step can never be left on screen under the one that replaced it. */
function setStep(n) {
  STEP_AT = n;
  if (n > STEP_REACHED) STEP_REACHED = n;

  $("#step-title").textContent = STEP_TITLES[n] || STEP_TITLES[0];
  STEP_BODIES.forEach((sel, i) => $(sel).classList.toggle("hide", i !== n));

  // addressed by id rather than by walking the rail's children, so the state is
  // set explicitly for each step instead of depending on the markup's shape
  for (let i = 0; i < STEP_TITLES.length; i++) {
    $("#st-" + i).classList.toggle("on", i === n);
    $("#st-" + i).classList.toggle("was", i < n);

    // the results step is only reachable once there is a result to show
    const canGo = i !== n && i <= STEP_REACHED && (i !== STEP_RESULTS || RESULT !== null);
    $("#st-" + i).classList.toggle("nav", canGo);
    $("#rail-" + i).disabled = !canGo;
  }
}

function goStep(n) {
  if (n === STEP_AT || n > STEP_REACHED) return;
  // the results step is the run detail rather than a wizard body
  if (n === STEP_RESULTS) { if (RESULT) showResults(RESULT); return; }
  setStep(n);
  $(STEP_BODIES[n]).scrollIntoView({ behavior: "smooth", block: "start" });
}

STEP_TITLES.forEach((_, i) => $("#rail-" + i).addEventListener("click", () => goStep(i)));

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
    // a floor so a run with nothing untraced still shows a hairline
    const frac = Math.max(0.02, v / peak).toFixed(4);
    const when = new Date(r.created_at).toLocaleDateString(undefined, { day: "numeric", month: "short" });
    return `<div class="bar"><b>${fmt(v)}</b><i style="--f:${frac}"></i><span>${when}</span></div>`;
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
      // a live run is still writing its own folder, so the server refuses to
      // delete it - no point offering the button
      const del = r.status === "running"
        ? ""
        : `<button class="rowdel" data-del="${r.id}" title="Delete run">` +
          `<span class="ms-icon" style="font-size:20px">delete</span></button>`;

      return `<tr${done ? ` class="clickable" data-run="${r.id}"` : ""}>` +
             `<td><div class="runid"><b>${runRef(r.id)}</b><span>${labels}</span></div></td>` +
             `<td style="white-space:nowrap">${when}</td>` +
             `<td>${fmt(r.sets || 0)}</td>` +
             `<td><span class="chip ${r.status}">${label}</span></td>` +
             `<td class="num">${traced}</td>` +
             `<td class="num untraced${done && r.untraced > 0 ? " no" : ""}">${untraced}</td>` +
             `<td class="num">${exceptions}</td>` +
             `<td class="num dur">${duration(r)}</td>` +
             `<td><div class="rowacts">${del}${go}</div></td></tr>`;
    }).join("") + "</tbody>";

  Array.from($("#history-table").querySelectorAll("tr.clickable"))
    .forEach(tr => tr.addEventListener("click", () => openRun(tr.getAttribute("data-run"))));

  // stops the row's own click: deleting a run must not also open it
  Array.from($("#history-table").querySelectorAll("button[data-del]")).forEach(btn =>
    btn.addEventListener("click", e => {
      e.stopPropagation();
      askDelete(btn.getAttribute("data-del"));
    }));
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

/* ---------- deleting a run ---------- */

/* The run the open confirmation is about. Null whenever it is closed, which is
   also what stops a stray Enter or a second click deleting anything. */
let DELETE_ID = null;

function askDelete(id) {
  if (!id) return;
  DELETE_ID = id;
  $("#confirm-title").textContent = `Delete run ${runRef(id)}?`;
  $("#confirm-error").classList.add("hide");
  $("#confirm-error").textContent = "";
  $("#btn-confirm-delete").disabled = false;
  $("#btn-confirm-delete-tx").textContent = "Delete run";
  $("#confirm-delete").classList.remove("hide");
  $("#btn-confirm-cancel").focus();
}

function closeDelete() {
  DELETE_ID = null;
  $("#confirm-delete").classList.add("hide");
}

function doDelete() {
  if (!DELETE_ID) return;
  const id = DELETE_ID;

  $("#btn-confirm-delete").disabled = true;
  $("#btn-confirm-delete-tx").textContent = "Deleting...";
  $("#confirm-error").classList.add("hide");

  api("/api/runs/" + id, { method: "DELETE" })
    .then(readJson)
    .then(({ ok, status, j }) => {
      if (!ok) {
        // a run that started between drawing the list and clicking delete comes
        // back as a conflict, which is worth reading rather than swallowing
        throw new Error((j && j.error) ||
          (status === 409
            ? "This run is still going. Wait for it to finish, then delete it."
            : `Could not delete the run (server returned ${status}).`));
      }

      // the run on screen is the one that just went, so nothing may still be
      // polling it or offering its results
      if (RUN_ID === id) {
        stopPolling();
        RUN_ID = null;
        RESULT = null;
        DETAIL_LOG = [];
        $("#chat-log").innerHTML = "";
      }

      closeDelete();
      showScreen("runs");
      return loadHistory();
    })
    .catch(e => {
      $("#confirm-error").textContent = e.message;
      $("#confirm-error").classList.remove("hide");
      $("#btn-confirm-delete").disabled = false;
      $("#btn-confirm-delete-tx").textContent = "Delete run";
    });
}

$("#btn-confirm-cancel").addEventListener("click", closeDelete);
$("#btn-confirm-delete").addEventListener("click", doDelete);
// the backdrop, but not the card sitting on it
$("#confirm-delete").addEventListener("click", e => {
  if (e.target === $("#confirm-delete")) closeDelete();
});
document.addEventListener("keydown", e => {
  if (e.key === "Escape" && DELETE_ID) closeDelete();
});
$("#btn-delete-run").addEventListener("click", () => askDelete(RUN_ID));

/* ---------- step 1: the input files, one slot per role ---------- */
const MAX_SETS = 4;

/* Comes from /api/config, so it cannot drift from the server's own limit and
   reject a set the server would have accepted. Checked here to catch an
   oversized set before the upload, and again on the server because a browser
   check protects nobody. The fallback only applies before config arrives. */
let MAX_SET_BYTES = 512 * 1024 * 1024;

/* The four roles a set's files are uploaded under. `field` is the SetFileKind
   the server parses out of the form field name, so these strings are a contract
   with SetFileReceiver rather than labels - see /api/discover.

   Two are required, matching what the server insists on: the exposure file,
   which the receiver rejects a set without, and the debug file, without which
   there is no lgd_defaults.csv and discovery finds no set at all. The write-off
   file and the scenario are optional, and the inventory step spells out what a
   run gives up when either is missing. */
const FILE_KINDS = [
  {
    key: "exposure", field: "Exposure", label: "Exposure file", icon: "assessment",
    hint: "The IFRS9 population for the reporting date", required: true, multiple: false,
    accept: ".csv,text/csv",
  },
  {
    key: "writeoff", field: "Writeoff", label: "Write-off file", icon: "receipt_long",
    hint: "One row per written-off account (optional, but check 2 needs it)",
    required: false, multiple: false,
    accept: ".csv,text/csv",
  },
  {
    key: "debug", field: "Debug", label: "Debug file", icon: "folder_zip",
    hint: "debug.zip, or the extracted debug files", required: true, multiple: true,
    accept: ".zip,.csv,.json",
  },
  {
    key: "scenario", field: "Scenario", label: "Scenario file", icon: "settings",
    hint: "Hazard matrix and LGD term structures (optional)", required: false, multiple: false,
    accept: ".json",
  },
];

/* The picked files live here rather than in the inputs, because a set can be
   removed from the middle of the list and the rows after it have to be redrawn -
   which throws away whatever those inputs were holding. */
let SETS = [];
let SET_SEQ = 0;

const emptySet = () => {
  SET_SEQ += 1;
  const files = {};
  FILE_KINDS.forEach(k => { files[k.key] = []; });
  return { id: SET_SEQ, files };
};

const setBytes = (set) =>
  FILE_KINDS.reduce((n, k) => n + set.files[k.key].reduce((m, f) => m + (f.size || 0), 0), 0);

const kindsPicked = (set) => FILE_KINDS.filter(k => set.files[k.key].length);

/* A set the server can actually take: every required role filled. */
const setComplete = (set) => FILE_KINDS.every(k => !k.required || set.files[k.key].length);

const setStarted = (set) => kindsPicked(set).length > 0;

const sizeLabel = (bytes) => bytes >= 1024 * 1024
  ? (bytes / (1024 * 1024)).toFixed(1) + " MB"
  : Math.max(1, Math.round(bytes / 1024)) + " KB";

/* What a slot says under its label: the chosen file and its size, or the hint
   for a role still empty. Several debug files are summarised rather than listed,
   since three extracted names do not fit on one line. */
function slotSub(kind, files) {
  if (!files.length) return kind.hint;
  if (files.length === 1) return files[0].name + " · " + sizeLabel(files[0].size || 0);
  const bytes = files.reduce((n, f) => n + (f.size || 0), 0);
  return files.length + " files · " + sizeLabel(bytes);
}

function renderSets() {
  const host = $("#sets");
  host.innerHTML = "";

  SETS.forEach((set, idx) => {
    const wrap = el("div", "setblock");
    const picked = kindsPicked(set).length;
    const bytes = setBytes(set);
    const tooBig = bytes > MAX_SET_BYTES;

    const head = el("div", "sethead", `
      <span class="sn">Set ${idx + 1}</span>
      <span class="ss">${picked} of ${FILE_KINDS.length} files chosen${
        tooBig
          ? ` &mdash; ${sizeLabel(bytes)} is over the ${Math.round(MAX_SET_BYTES / (1024 * 1024))} MB limit`
          : (setStarted(set) && !setComplete(set) ? " &mdash; still needs its required files" : "")
      }</span>`);
    if (tooBig) head.querySelector(".ss").classList.add("bad");

    // one set has to remain, so it is cleared rather than removed
    const drop = el("button", "x", '<span class="ms-icon" style="font-size:20px">close</span>');
    drop.title = SETS.length > 1 ? "Remove set" : "Clear set";
    drop.addEventListener("click", () => {
      if (SETS.length > 1) SETS.splice(idx, 1);
      else SETS[idx] = emptySet();
      renderSets();
    });
    head.appendChild(drop);
    wrap.appendChild(head);

    FILE_KINDS.forEach(kind => {
      const files = set.files[kind.key];
      const on = files.length > 0;
      const row = el("div", "slot" + (on ? " on" : ""), `
        <span class="ms-icon">${kind.icon}</span>
        <div class="sx">
          <span class="num">${kind.label}</span>
          <span class="meta${on ? " name" : ""}"></span>
        </div>`);
      // the file's own name, so as text rather than markup
      row.querySelector(".meta").textContent = slotSub(kind, files);

      const input = el("input");
      input.type = "file";
      input.accept = kind.accept;
      if (kind.multiple) input.multiple = true;
      input.addEventListener("change", () => {
        if (input.files && input.files.length) {
          set.files[kind.key] = Array.from(input.files);
          renderSets();
        }
      });
      row.appendChild(input);

      const pick = el("button", "pick",
        `<span class="ms-icon" style="font-size:18px">${on ? "swap_horiz" : "upload"}</span>` +
        (on ? "Replace" : "Choose file"));
      pick.type = "button";
      pick.addEventListener("click", () => input.click());
      row.appendChild(pick);

      if (on) {
        const clear = el("button", "x", '<span class="ms-icon" style="font-size:20px">close</span>');
        clear.title = "Remove this file";
        clear.addEventListener("click", () => { set.files[kind.key] = []; renderSets(); });
        row.appendChild(clear);
      }

      wrap.appendChild(row);
    });

    host.appendChild(wrap);
  });

  $("#btn-add-set").disabled = SETS.length >= MAX_SETS;
  updateReady();
}

function updateReady() {
  const started = SETS.filter(setStarted);
  const ready = started.filter(setComplete);
  const oversized = started.some(s => setBytes(s) > MAX_SET_BYTES);

  // a half-filled set would be rejected by the receiver, so it blocks here
  // rather than after the upload has already been paid for
  $("#btn-check").disabled = ready.length === 0 || ready.length !== started.length || oversized;

  $("#set-count").textContent = started.length === 0
    ? `No files chosen yet, up to ${MAX_SETS} sets`
    : `${ready.length} of ${started.length} set${started.length === 1 ? "" : "s"} ready`;
}

SETS = [emptySet()];
renderSets();
$("#btn-add-set").addEventListener("click", () => {
  if (SETS.length >= MAX_SETS) return;
  SETS.push(emptySet());
  renderSets();
});
// no restore of a previous choice: a file input cannot be repopulated from
// script, so there is nothing to put back. Run history replaces this.

/* ---------- step 2: model ---------- */
/* The models the gateway offered, kept so the conversation's own picker can be
   filled from the same list the wizard's was, without a second round trip. */
let MODELS = [];

function addModelOption(sel, value, label) {
  const o = document.createElement("option");
  o.value = value;
  o.textContent = label;
  sel.appendChild(o);
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
  addModelOption(sel, "", "Skip AI analysis");
  return api("/api/models")
    .then(readJson)
    .then(({ ok, j }) => {
      if (!ok || !Array.isArray(j)) {
        sel.disabled = true;
        note.textContent = (j && j.error) || "Model list unavailable - runs will skip AI analysis.";
        MODELS = [];
        fillChatModels();
        return;
      }
      j.forEach(m => addModelOption(sel, m.id, m.friendlyName));
      const saved = localStorage.getItem("hr_model") || "";
      sel.value = j.some(m => m.id === saved) ? saved : "";
      note.textContent = "Analysis adds roughly 25 seconds to a run and produces the memo " +
        "alongside the workbook.";
      MODELS = j;
      fillChatModels();
    })
    .catch(e => {
      sel.disabled = true;
      note.textContent = "Model list unavailable - " + e.message;
      MODELS = [];
      fillChatModels();
    });
}

$("#model").addEventListener("change", () => localStorage.setItem("hr_model", $("#model").value));
// no bare loadModels() here: /api/models needs a token, so it is called from the
// session bootstrap and after sign-in. Calling it at load only ever produced a
// guaranteed 401.

function discover() {
  const fd = new FormData();
  let sets = 0;

  // one field per file, named "set<n>.<Kind>" - the server splits that back into
  // the set it belongs to and the role it was picked for, and stores it under the
  // canonical name discovery looks for. Sets are numbered over those that were
  // filled in, so clearing the middle one does not leave a gap.
  SETS.forEach(set => {
    const kinds = kindsPicked(set);
    if (!kinds.length) return;
    kinds.forEach(kind =>
      set.files[kind.key].forEach(f => fd.append("set" + sets + "." + kind.field, f, f.name)));
    sets += 1;
  });

  if (sets === 0) return Promise.reject(new Error("Please choose the files for at least one set."));

  return api("/api/discover", { method: "POST", body: fd })
    .then(readJson)
    .then(({ ok, status, j }) => {
      if (!ok || !j) throw new Error((j && j.error) || `Discovery failed (server returned ${status}).`);
      showMapping(j);
      return j;
    });
}

$("#btn-check").addEventListener("click", () => {
  const btn = $("#btn-check");
  btn.disabled = true;
  $("#btn-check-tx").textContent = "Uploading...";
  // Checking again supersedes anything in flight. Now that the rail can walk
  // back mid-run, a live poll would otherwise carry on against the run id this
  // discovery is about to replace, and report that as a failure.
  stopPolling();
  discover()
    .catch(e => showError($("#step-files"), e.message))
    .finally(() => {
      btn.disabled = false;
      $("#btn-check-tx").textContent = "Check columns";
      updateReady();
    });
});

function showError(card, msg) {
  card.classList.remove("hide");
  const box = card.querySelector(".err") || el("div", "err");
  box.textContent = msg;
  if (!box.parentNode) card.appendChild(box);
}

/* ---------- step 2: column mapping ---------- */

/* The discovery response, kept because the inventory is only drawn once the
   mapping has been confirmed - a step later than the call that produced it. */
let DISCOVERED = null;

/* The files needing a mapping, flattened out of the response into one entry per
   set per file so each can be drawn as its own table. */
let MAP_FILES = [];

/* Columns chosen by hand, keyed by set/file/field. Separate from the suggestion
   in MAP_FILES so "Reset to suggestions" is just clearing this, and so a field
   the user has touched can be labelled as theirs rather than the AI's. */
let MAP_EDITS = {};

const FILE_KIND_TITLES = { writeoff: "Write-off file", exposure: "Exposure file" };

const editKey = (setKey, fileKind, field) => setKey + " " + fileKind + " " + field;

/* The file's rows as the file has them, header row included. Discovery splits
   them according to its own guess - headers separately when it found some, all of
   them in samples when it did not - so putting them back together is what lets
   the header toggle redraw either way without another upload. CsvSniffer.Reinterpret
   does the same on the server. */
function mapRowsOf(view) {
  const samples = view.samples || [];
  return view.has_headers && view.headers ? [view.headers].concat(samples) : samples;
}

/* The columns a field may be mapped to, for one reading of the file. With a
   header row it is addressed by header name; without, by 0-based index as a
   string, which is what ColumnMap resolves against - so the option's value is the
   index and only its label is human-readable. */
function columnsFor(rows, hasHeaders) {
  if (hasHeaders) {
    const headers = rows[0] || [];
    const first = rows[1] || [];
    return headers.map((h, i) => ({
      value: h,
      label: h || "(unnamed column " + (i + 1) + ")",
      sample: first[i] || "",
    }));
  }

  const width = rows.reduce((n, r) => Math.max(n, (r || []).length), 0);
  const first = rows[0] || [];
  const columns = [];
  for (let i = 0; i < width; i++) {
    columns.push({ value: String(i), label: "Column " + (i + 1), sample: first[i] || "" });
  }
  return columns;
}

function buildMapFiles(j) {
  const files = [];
  (j.mapping || []).forEach(set => {
    ["writeoff", "exposure"].forEach(fileKind => {
      const view = set[fileKind];
      if (!view) return;
      files.push({
        setKey: set.key,
        fileKind,
        title: FILE_KIND_TITLES[fileKind] + " · " + set.key,
        // what discovery guessed, kept so the toggle can say when it was overruled
        sniffedHasHeaders: !!view.has_headers,
        hasHeaders: !!view.has_headers,
        fileRows: mapRowsOf(view),
        rows: (view.fields || []).map(f => ({
          field: f.field,
          note: f.note,
          suggested: f.column || "",
          confidence: f.confidence,
          source: f.source,
        })),
      });
    });
  });
  return files;
}

/* The column in force for a field: the user's if they have set one, otherwise
   whatever discovery suggested. An empty string is a real value here - it is how
   "not mapped" is chosen deliberately. */
function mappedColumn(file, row) {
  const k = editKey(file.setKey, file.fileKind, row.field);
  return MAP_EDITS[k] !== undefined ? MAP_EDITS[k] : row.suggested;
}

const SOURCE_LABEL = { header_match: "Header match", saved: "Saved mapping" };

function rowStatus(file, row) {
  const column = mappedColumn(file, row);
  const edited = MAP_EDITS[editKey(file.setKey, file.fileKind, row.field)] !== undefined;

  if (!column) return { label: "Needs mapping", tone: "warn", icon: "error" };
  if (edited) return { label: "Set by you", tone: "mine", icon: "edit" };
  if (row.source === "ai_guess") {
    const pct = row.confidence === null || row.confidence === undefined
      ? "" : " " + Math.round(row.confidence * 100) + "%";
    return { label: "AI" + pct, tone: "mine", icon: "auto_awesome" };
  }
  return { label: SOURCE_LABEL[row.source] || "Mapped", tone: "ok", icon: "check_circle" };
}

function showMapping(j) {
  RUN_ID = j.run_id;
  DISCOVERED = j;
  MAP_FILES = buildMapFiles(j);
  MAP_EDITS = {};

  const stale = $("#step-mapping").querySelector(".err"); if (stale) stale.remove();
  setStep(1);

  // Nothing to map means no set survived discovery, which the inventory explains
  // far better than an empty mapping step would. The rail can still walk back
  // here, so it says why it is empty rather than showing its loading line.
  if (!MAP_FILES.length) {
    $("#map-headline").textContent = "There is nothing to map";
    $("#map-subline").textContent =
      "No set was discovered from these files, so no columns could be read. The inventory says what was missing.";
    $("#map-gate").textContent = "";
    $("#map-gate").classList.remove("bad");
    $("#btn-confirm-map").disabled = true;
    showInventory(j);
    return;
  }

  renderMapping();
  $("#step-mapping").scrollIntoView({ behavior: "smooth", block: "start" });
}

/* Flips one file between "row one is labels" and "row one is data".

   The columns themselves do not move - only how they are addressed - so every
   choice already made is carried across by position rather than thrown away: a
   field on "AmountOutstanding" lands on that column's index, and one on index 2
   lands on whatever header sits there. Each becomes the user's own, because the
   provenance discovery reported was for the other reading of the file. */
function setFileHasHeaders(file, hasHeaders) {
  const before = columnsFor(file.fileRows, file.hasHeaders);
  const after = columnsFor(file.fileRows, hasHeaders);

  file.rows.forEach(row => {
    const current = mappedColumn(file, row);
    const key = editKey(file.setKey, file.fileKind, row.field);

    if (!current) { MAP_EDITS[key] = ""; return; }

    const at = before.findIndex(c => c.value === current);
    // a column the file does not actually have cannot be carried over
    MAP_EDITS[key] = at >= 0 && at < after.length ? after[at].value : "";
  });

  file.hasHeaders = hasHeaders;
  renderMapping();
}

function renderMapping() {
  const host = $("#map-files");
  host.innerHTML = "";

  let unmapped = 0;
  let guessed = 0;
  let headerless = 0;

  MAP_FILES.forEach((file, fileIdx) => {
    if (!file.hasHeaders) headerless += 1;

    const columns = columnsFor(file.fileRows, file.hasHeaders);
    const overruled = file.hasHeaders !== file.sniffedHasHeaders;

    const card = el("div", "card");
    const note = file.hasHeaders
      ? '<span class="okflag"><span class="ms-icon" style="font-size:18px">check_circle</span>' +
        (overruled ? "Read as headers &mdash; your choice" : "Headers found &mdash; matched by name") + "</span>"
      : '<span class="okflag ai"><span class="ms-icon" style="font-size:18px">auto_awesome</span>' +
        (overruled ? "Read as data &mdash; your choice" : "No header row &mdash; mapped by column") + "</span>";
    const head = el("div", "cardhead", "<span></span>" + note);
    // the title carries the set key, which came from an uploaded file's name
    head.firstChild.textContent = file.title;
    card.appendChild(head);

    // Whether row one is labels or data is a guess, and for a file of nothing but
    // words an undecidable one, so it is offered rather than imposed.
    const toggleId = "map-hdr-" + fileIdx;
    const toggle = el("div", "hdrtoggle", `
      <input type="checkbox" id="${toggleId}"${file.hasHeaders ? " checked" : ""}>
      <label for="${toggleId}">First row is a header</label>
      <span class="hint" style="margin:0"></span>`);
    const firstRow = (file.fileRows[0] || []).slice(0, 4).join(", ");
    toggle.querySelector(".hint").textContent = firstRow
      ? (file.hasHeaders ? "Read as column names: " : "Read as data: ") + firstRow
      : "";
    toggle.querySelector("input").addEventListener("change", () => setFileHasHeaders(file, !file.hasHeaders));
    card.appendChild(toggle);

    const body = el("div", "maptable");
    const table = el("table", "grid tight maprows");
    table.innerHTML =
      "<thead><tr><th>Engine field</th><th>Column in your file</th><th>Sample</th><th>Match</th></tr>" +
      "</thead><tbody></tbody>";
    const tbody = table.querySelector("tbody");

    file.rows.forEach(row => {
      const column = mappedColumn(file, row);
      if (!column) unmapped += 1;
      if (column && row.source === "ai_guess" &&
          MAP_EDITS[editKey(file.setKey, file.fileKind, row.field)] === undefined) guessed += 1;

      const status = rowStatus(file, row);
      const chosen = columns.find(c => c.value === column);

      const tr = el("tr");
      tr.appendChild(el("td", "mapfield",
        "<b>" + row.field + "</b><span>" + (row.note || "") + "</span>"));

      const pickCell = el("td");
      const sel = el("select");
      sel.className = column ? "mapsel" : "mapsel bad";
      // every option's text is a column name out of the uploaded file, so each is
      // set as text - a header is data, and must never be read as markup
      sel.appendChild(option("", "— not mapped —"));
      columns.forEach(c => sel.appendChild(option(c.value, c.label)));
      // a saved or AI-guessed column can name something this file does not have,
      // so it is offered explicitly rather than silently falling back to blank
      if (column && !chosen) sel.appendChild(option(column, column + " (not in this file)"));
      sel.value = column;
      sel.addEventListener("change", () => {
        MAP_EDITS[editKey(file.setKey, file.fileKind, row.field)] = sel.value;
        renderMapping();
      });
      pickCell.appendChild(sel);
      tr.appendChild(pickCell);

      const sample = el("td", "mapsample");
      // a row straight out of the file, so as text
      sample.textContent = chosen && chosen.sample ? chosen.sample : "—";
      tr.appendChild(sample);

      tr.appendChild(el("td", "nowrap",
        '<span class="mapflag ' + status.tone + '">' +
        '<span class="ms-icon" style="font-size:15px">' + status.icon + "</span>" +
        status.label + "</span>"));

      tbody.appendChild(tr);
    });

    body.appendChild(table);
    card.appendChild(body);
    host.appendChild(card);
  });

  $("#map-headline").textContent = unmapped
    ? (unmapped === 1 ? "1 field still needs a column" : unmapped + " fields still need columns")
    : "Every engine field is mapped";

  const sub = [];
  if (headerless) {
    sub.push(headerless === 1
      ? "One file has no header row, so its columns are offered by position."
      : headerless + " files have no header row, so their columns are offered by position.");
  }
  if (guessed) {
    sub.push(guessed === 1
      ? "1 field was matched for you from the first rows - check it before continuing."
      : guessed + " fields were matched for you from the first rows - check them before continuing.");
  }
  if (!sub.length) sub.push("Each column was matched by its header name.");
  $("#map-subline").textContent = sub.join(" ");

  const gate = $("#map-gate");
  gate.textContent = unmapped
    ? "Map every field to continue"
    : "The mapping is saved with the run, and reused next time these files are uploaded";
  gate.classList.toggle("bad", unmapped > 0);
  $("#btn-confirm-map").disabled = unmapped > 0;
}

/* The confirmed mapping, in the shape /api/discover/mapping reads: one entry per
   set, each carrying a field-to-column object per file. */
function mappingPayload() {
  const bySet = {};
  MAP_FILES.forEach(file => {
    const set = bySet[file.setKey] || (bySet[file.setKey] = { key: file.setKey });
    const mapping = set[file.fileKind] || (set[file.fileKind] = {});
    file.rows.forEach(row => {
      const column = mappedColumn(file, row);
      if (column) mapping[row.field] = column;
    });
    // sent alongside the mapping, never inside it: the server needs to know
    // whether these columns are names or positions, and a saved profile is keyed
    // on that reading of the file
    set[file.fileKind + "_has_headers"] = file.hasHeaders;
  });
  return { run_id: RUN_ID, sets: Object.keys(bySet).map(k => bySet[k]) };
}

function confirmMapping() {
  return api("/api/discover/mapping", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(mappingPayload()),
  })
    .then(readJson)
    .then(({ ok, status, j }) => {
      if (!ok) throw new Error((j && j.error) || `Could not save the mapping (server returned ${status}).`);
      showInventory(DISCOVERED);
    });
}

$("#btn-confirm-map").addEventListener("click", () => {
  const btn = $("#btn-confirm-map");
  btn.disabled = true;
  $("#btn-confirm-map-tx").textContent = "Saving...";
  const stale = $("#step-mapping").querySelector(".err"); if (stale) stale.remove();
  confirmMapping()
    .catch(e => showError($("#step-mapping"), e.message))
    .finally(() => {
      $("#btn-confirm-map-tx").textContent = "Confirm mapping";
      // renderMapping owns the button's state, so it decides whether the retry
      // is allowed rather than this handler assuming it is
      if (MAP_FILES.length) renderMapping();
    });
});

// back to everything discovery worked out, the header reading included
$("#btn-remap").addEventListener("click", () => {
  MAP_EDITS = {};
  MAP_FILES.forEach(f => { f.hasHeaders = f.sniffedHasHeaders; });
  renderMapping();
});
$("#btn-back-files").addEventListener("click", () => goStep(0));

function showInventory(j) {
  RUN_ID = j.run_id;
  setStep(2);
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
  // cleared before the rail is drawn: the previous run's results must not stay
  // reachable from step 4 while a new run is starting
  RESULT = null;
  // Run again is offered from the run detail, so come back to the wizard first
  showScreen("wizard");
  setStep(3);
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

function renderStages(stages) {
  $("#stages").innerHTML = stages.map(s => {
    const st = STAGE_ICON[s.status] ? s.status : "pending";
    return `<div class="stage st-${st}">` +
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
$("#btn-back-map").addEventListener("click", () => goStep(1));

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
  renderLogsTab(res);
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
              <span class="v ${s.wo_in_window > 0 ? "amber" : ""}">${fmt(s.wo_in_window)}</span>
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

/* ---------- logs tab ---------- */
/* The engine's own log, as it was written. What the run *did* is on the progress
   screen while it runs; here the log is the record that outlives it. */
function renderLogsTab(res) {
  const lines = Array.isArray(DETAIL_LOG) ? DETAIL_LOG : [];

  $("#detail-log").innerHTML = lines.map(l =>
    `<div><span class="t">${escapeHtml(l.t || "")}</span>` +
    `<span class="${escapeHtml(l.kind || "")}">${escapeHtml(l.msg || "")}</span></div>`).join("");

  const parts = lines.length ? [`${fmt(lines.length)} lines`] : [];
  if (res.elapsed_seconds != null) parts.push(stageTime(res.elapsed_seconds) + " total");
  $("#detail-log-count").textContent = parts.join(" · ");

  $("#detail-log").classList.toggle("hide", lines.length === 0);
  $("#detail-log-empty").classList.toggle("hide", lines.length > 0);
}

/* ---------- dashboard tab ---------- */
function renderDashboardTab(res) {
  // the engine still writes the standalone file; the link opens it
  $("#res-open").href = outputUrl(res.dashboard);

  // A run stored before the dashboard captured its own data has none of the
  // sections that read from it. Rather than leave eight cards silently missing,
  // fall back to the engine's file, which does have them.
  const native = (res.commentary || []).length > 0 || !!res.analysis
    || (res.dashboard_sets || []).length > 0;

  $("#dash-legacy").classList.toggle("hide", native);
  if (!native) $("#res-frame").src = outputUrl(res.dashboard);

  renderDashTiles(res);
  renderDashCommentary(res);
  renderDashAi(res);
  renderDashChecks(res);
  renderDashMatrix(res);
  renderDashEngine(res);
  renderSetDetail(res);
}

/* ---------- engine model outputs ---------- */
/* The scenario's own fitted probabilities, shown on the same heat scale as the
   rebuilt matrix so the two can be read against each other. */
function renderDashEngine(res) {
  const d = (res.dashboard_sets || []).find(s => s.hazard && s.hazard.length);

  if (!d) {
    ["#dash-hazard", "#dash-pd", "#dash-lgd"].forEach(s => {
      $(s).innerHTML = "";
      $(s).classList.add("hide");
    });
    return;
  }
  ["#dash-hazard", "#dash-pd", "#dash-lgd"].forEach(s => $(s).classList.remove("hide"));

  renderHazard(d);
  renderPdBuckets(d);
  renderLgd(d);
}

/* A probability, as a percentage that stays honest at the small end. In the
   matrices an impossible transition reads as a dash, but in the PD table a zero
   is a real measured figure, so zeroes are spelled out there instead. */
function prob(v, zeroAsNumber) {
  if (v == null) return "&ndash;";
  const pct = v * 100;
  if (pct === 0) return zeroAsNumber ? "0.00%" : "&ndash;";
  if (pct < 0.01) return "&lt;0.01%";
  return pct.toFixed(2) + "%";
}

function renderHazard(d) {
  const rows = d.hazard || [];
  const peak = Math.max(...rows.flat().filter(v => v != null), 0.0001);

  $("#dash-hazard").innerHTML = `
    <div class="cardhead wrapped">
      <span>Engine hazard-rate matrix</span>
      <span class="sub">scenario.json — the model's own fitted transition probabilities</span>
    </div>
    <div class="cardbody" style="display:grid;gap:12px">
      <p class="hint" style="margin:0">Column 5 is the default state, column 6 is closed / settled;
        buckets 5 and 6 are absorbing.</p>
      <div style="overflow-x:auto">
        <table class="heat static">
          <thead><tr><th></th>${MIG_BUCKETS.map(b => `<th class="bh">${b}</th>`).join("")}</tr></thead>
          <tbody>${rows.map((cells, i) => `<tr><th class="rh">${MIG_BUCKETS[i]}</th>` +
            cells.map(v => {
              const stop = heatOf(v, peak);
              return `<td class="hc" style="background:${stop.bg};color:${stop.fg}">${prob(v)}</td>`;
            }).join("") + `</tr>`).join("")}</tbody>
        </table>
      </div>
    </div>`;
}

/* Per bucket: the chance of defaulting, and the cohort the engine fitted it on.
   Buckets 5 and 6 are absorbing, which is worth saying rather than leaving the
   reader to infer it from a 100%. */
function renderPdBuckets(d) {
  const hazard = d.hazard || [];
  const cohort = d.cohort || [];
  // column 5 (index 4) is the default state
  const defaultCol = 4;

  const rows = hazard.map((row, i) => {
    const hz = row[defaultCol];
    const co = (cohort[i] || [])[defaultCol];
    const absorbing = i >= 4;
    return `<tr>
      <td class="nowrap" style="font-weight:600">Bucket ${MIG_BUCKETS[i]}
        ${absorbing ? `<span class="tag">absorbing</span>` : ""}</td>
      <td class="num">
        <div>${prob(hz, true)}</div>
        <div class="bar"><span style="width:${Math.round((hz || 0) * 100)}%"></span></div>
      </td>
      <td class="num">${prob(co, true)}</td>
    </tr>`;
  }).join("");

  $("#dash-pd").innerHTML = `
    <div class="cardhead"><span>Engine model outputs — PD by bucket</span></div>
    <table class="grid tight figures">
      <thead><tr><th>Bucket</th><th class="num">Hazard</th><th class="num">Cohort</th></tr></thead>
      <tbody>${rows}</tbody>
    </table>`;
}

function renderLgd(d) {
  const rows = d.lgd || [];
  if (!rows.length) { $("#dash-lgd").innerHTML = ""; $("#dash-lgd").classList.add("hide"); return; }

  // the term columns are whatever the engine produced, in order
  const width = rows[0].values.length;
  const heads = ["0 days", "30 days", "60 days", "90 days"].slice(0, width);
  while (heads.length < width) heads.push("+" + (heads.length * 30) + " days");

  $("#dash-lgd").innerHTML = `
    <div class="cardhead"><span>LGD term structure</span></div>
    <div class="cardbody" style="padding:15px 16px 0">
      <p class="hint" style="margin:0">Loss given default by days since default, as produced by the
        engine — recovery is effectively exhausted by 60&ndash;90 days.</p>
    </div>
    <table class="grid tight figures" style="margin-top:12px">
      <thead><tr><th>Event type</th>${heads.map(h => `<th class="num">${h}</th>`).join("")}</tr></thead>
      <tbody>${rows.map(r => `<tr><td style="font-weight:600">${escapeHtml(r.name)}</td>` +
        r.values.map(v => `<td class="num">${prob(v)}</td>`).join("") + `</tr>`).join("")}</tbody>
    </table>`;
}

/* A share to one place. Reads a dash rather than throwing when the figure is
   absent: one missing number used to take the whole dashboard down with it. */
function pct1(v) {
  return typeof v === "number" && isFinite(v) ? v.toFixed(1) + "%" : "&mdash;";
}

/* ---------- per-set detail ---------- */
function renderSetDetail(res) {
  const dash = res.dashboard_sets || [];
  if (!dash.length) { $("#dash-setdetail").innerHTML = ""; return; }

  $("#dash-setdetail").innerHTML = dash.map(d => {
    const untraced = (d.top_untraced || []).map(u => `<tr>
      <td class="num" style="text-align:left">${escapeHtml(u.account)}</td>
      <td>${escapeHtml(u.cohort_date)}</td>
      <td class="num">${escapeHtml(u.rating)}</td>
      <td class="num">${escapeHtml(u.amount)}</td></tr>`).join("");

    const buckets = (d.last_buckets || []).map(b => `<tr>
      <td>${escapeHtml(b.bucket)}</td>
      <td class="num">${fmt(b.accounts)}</td>
      <td class="num">${pct1(b.share)}</td>
      <td class="num">${escapeHtml(b.amount)}</td></tr>`).join("");

    const exceptions = (d.wo_exceptions || []).map(w => `<tr>
      <td class="num" style="text-align:left">${escapeHtml(w.account)}</td>
      <td class="num">${escapeHtml(w.amount)}</td>
      <td>${escapeHtml(w.date)}</td>
      <td><span class="chip error">In window</span></td>
      <td class="num">${escapeHtml(w.last_bucket)}</td></tr>`).join("");

    const block = (title, note, head, body) => body ? `
      <div class="sdblock">
        <span class="sdh">${title}</span>
        ${note ? `<p class="hint" style="margin:0">${note}</p>` : ""}
        <div style="overflow-x:auto"><table class="grid tight figures">
          <thead><tr>${head}</tr></thead><tbody>${body}</tbody></table></div>
      </div>` : "";

    return `
      <div class="card">
        <div class="cardhead strong wrapped">
          <span>Set detail: ${escapeHtml(d.key)}</span>
          <span class="sub">${escapeHtml(d.label || "")}</span>
        </div>
        <div class="cardbody" style="display:grid;gap:24px">
          ${block("Top untraced defaults", "",
            `<th>Account</th><th>Cohort date</th><th class="num">Rating</th>` +
            `<th class="num">Default amount</th>`, untraced)}
          <div class="dashrow">
            ${buckets ? `<div class="grow1" style="display:grid;gap:10px">
              ${block("Where the engine last had these accounts",
                "Bucket 4 is the worst non-default bucket, so a concentration there means accounts " +
                "were written off straight out of bucket 4 without ever moving to the default state.",
                `<th>Last bucket seen</th><th class="num">Accounts</th><th class="num">Share</th>` +
                `<th class="num">Value written off</th>`, buckets)}
            </div>` : ""}
            ${exceptions ? `<div class="grow2" style="display:grid;gap:10px">
              ${block("Write-offs not defaulted — top exceptions", "",
                `<th>Account</th><th class="num">Write-off amount</th><th>Last write-off date</th>` +
                `<th>Window status</th><th class="num">Last bucket</th>`, exceptions)}
            </div>` : ""}
          </div>
        </div>
      </div>`;
  }).join("");
}

/* ---------- migration matrix ---------- */
/* The bucket labels the engine works in. Column 5 is the default state and 6 is
   closed/settled, which is why both are absorbing. */
const MIG_BUCKETS = ["1", "2", "3", "4", "5", "6"];

/* Five steps from sparse to dense. Kept as explicit stops rather than a computed
   ramp so the legend and the cells cannot drift apart. */
const HEAT_STOPS = [
  { bg: "#eef0f8", fg: "var(--cl-off-text-color)" },
  { bg: "#c9cfe8", fg: "var(--cl-text-color)" },
  { bg: "#8f9bd0", fg: "#fff" },
  { bg: "#4a5cb0", fg: "#fff" },
  { bg: "var(--cl-primary-color)", fg: "#fff" },
];

/* What the matrix is currently showing. */
let MIG = { months: [], data: {}, month: "", share: false, pick: null };

function renderDashMatrix(res) {
  // the first set with a matrix; a set with no scored file has nothing to show
  const withMatrix = (res.dashboard_sets || []).filter(d => (d.months || []).length > 0);
  const row = $("#dash-matrix-row");

  if (!withMatrix.length) {
    row.classList.add("hide");
    MIG = { months: [], data: {}, month: "", share: false, pick: null };
    return;
  }
  row.classList.remove("hide");

  const d = withMatrix[0];
  // the per-account movements behind a cell live in the set's migration detail CSV;
  // without that file there is nothing to export, so the button is left out
  const detail = (res.outputs || []).map(f => f.name)
    .find(n => n.startsWith(d.key) && n.includes("migration"));

  MIG = {
    months: d.months, data: d.migration, month: d.months[0],
    share: false, pick: null, detail: detail || null,
  };

  $("#mig-month").innerHTML = d.months
    .map(m => `<option value="${escapeHtml(m)}">${escapeHtml(m)}</option>`).join("");
  $("#mig-month").value = MIG.month;
  $("#mig-legend").innerHTML = HEAT_STOPS
    .map(s => `<span style="background:${s.bg}"></span>`).join("");

  renderMonthly(d);
  drawMatrix();
}

/* The busiest cell sets the top of the scale, so a quiet month still reads. */
function heatOf(value, peak) {
  if (!value) return HEAT_STOPS[0];
  const step = Math.min(HEAT_STOPS.length - 1,
    Math.max(1, Math.ceil(value / peak * (HEAT_STOPS.length - 1))));
  return HEAT_STOPS[step];
}

function drawMatrix() {
  const rows = MIG.data[MIG.month] || [];
  if (!rows.length) { $("#mig-table").innerHTML = ""; return; }

  const rowTotals = rows.map(r => r.reduce((a, v) => a + v, 0));
  const peak = Math.max(1, ...rows.flat());
  const total = rowTotals.reduce((a, v) => a + v, 0);

  const head = `<thead><tr><th class="corner">From &darr; / To &rarr;</th>` +
    MIG_BUCKETS.map(b => `<th class="bh">${b}</th>`).join("") +
    `<th class="cohort">Cohort</th></tr></thead>`;

  const body = rows.map((cells, i) => {
    const tds = cells.map((v, j) => {
      const stop = heatOf(v, peak);
      // row % is of the row's own cohort, so an empty row shows no share at all
      const text = MIG.share
        ? (rowTotals[i] ? (v / rowTotals[i] * 100).toFixed(1) + "%" : "&mdash;")
        : fmt(v);
      const on = MIG.pick && MIG.pick.i === i && MIG.pick.j === j;
      return `<td class="hc${on ? " on" : ""}" data-i="${i}" data-j="${j}"` +
        ` style="background:${stop.bg};color:${stop.fg}">${text}</td>`;
    }).join("");
    return `<tr><th class="rh">${MIG_BUCKETS[i]}</th>${tds}` +
      `<td class="cohort">${fmt(rowTotals[i])}</td></tr>`;
  }).join("");

  $("#mig-table").innerHTML = head + `<tbody>${body}</tbody>`;

  // the diagonal is the population that stayed put
  const stayed = rows.reduce((a, r, i) => a + (r[i] || 0), 0);
  const pct = (v) => total ? (v / total * 100).toFixed(1) + "%" : "0%";
  $("#mig-summary").textContent =
    `${fmt(total)} transitions · ${pct(stayed)} stayed, ${pct(total - stayed)} migrated`;

  Array.from($("#mig-table").querySelectorAll("td.hc")).forEach(td =>
    td.addEventListener("click", () => pickCell(
      Number(td.getAttribute("data-i")), Number(td.getAttribute("data-j")))));

  drawCohortDetail(rows, rowTotals, total);
}

function pickCell(i, j) {
  MIG.pick = { i, j };
  drawMatrix();
}

/* The side panel: what the selected cell actually counts. */
function drawCohortDetail(rows, rowTotals, total) {
  const head = `<div class="cardhead"><span>Cohort detail</span></div>`;

  if (!MIG.pick) {
    $("#mig-detail").innerHTML = head + `
      <div class="cohortempty">
        <span class="ms-icon">touch_app</span>
        <span class="t">Select a cell to inspect the cohort</span>
        <span class="d">Counts are account movements, so one account can appear in several
          months across the window.</span>
      </div>`;
    return;
  }

  const { i, j } = MIG.pick;
  const count = (rows[i] || [])[j] || 0;
  const from = MIG_BUCKETS[i], to = MIG_BUCKETS[j];
  const kind = i === j ? "Stayed in bucket" : "Moved between buckets";
  const title = i === j ? `Bucket ${from}` : `Bucket ${from} → Bucket ${to}`;
  const share = rowTotals[i]
    ? `${(count / rowTotals[i] * 100).toFixed(1)}% of bucket ${from}'s cohort`
    : "no cohort in this bucket";

  $("#mig-detail").innerHTML = head + `
    <div class="cohortbody">
      <div class="ck"><span class="kind">${kind}</span><span class="title">${title}</span></div>
      <div class="cbox">
        <span class="l">Account movements</span>
        <span class="v">${fmt(count)}</span>
        <span class="s">${escapeHtml(share)}</span>
      </div>
      <span class="hint" style="margin:0">${escapeHtml(MIG.month)}</span>
      ${MIG.detail ? `<a class="btn block" href="${outputUrl(MIG.detail)}">
        <span class="ms-icon" style="font-size:18px">download</span>Export cohort</a>` : ""}
    </div>`;
}

$("#mig-month").addEventListener("change", () => {
  MIG.month = $("#mig-month").value;
  // the selection is a cell in the month it was picked from, so it does not carry
  MIG.pick = null;
  drawMatrix();
});

function setMigShare(share) {
  MIG.share = share;
  $("#mig-counts").classList.toggle("on", !share);
  $("#mig-share").classList.toggle("on", share);
  drawMatrix();
}
$("#mig-counts").addEventListener("click", () => setMigShare(false));
$("#mig-share").addEventListener("click", () => setMigShare(true));

/* ---------- monthly account movements ---------- */
function renderMonthly(d) {
  const months = (d.months || []).slice(1);
  const totals = d.monthly_totals || [];

  $("#dash-monthly").innerHTML = `
    <div class="cardhead"><span>Monthly account movements</span></div>
    <table class="grid tight figures">
      <thead><tr><th>Month</th><th class="num">Migrations</th></tr></thead>
      <tbody>${months.map((m, i) =>
        `<tr><td>${escapeHtml(m)}</td><td class="num">${fmt(totals[i])}</td></tr>`).join("")}</tbody>
    </table>`;
}

/* ---------- the two checks, and the population they ran over ---------- */
/* One card per table: a header with its subtitle, then a row per set. Figures are
   right-aligned on tabular numerals so the columns compare down the page. */
function dashCard(target, title, subtitle, headers, rows) {
  const head = headers.map(h =>
    `<th class="${h.num ? "num" : ""}"${h.nowrap ? " style='white-space:nowrap'" : ""}>` +
    `${escapeHtml(h.t)}</th>`).join("");

  $(target).innerHTML = `
    <div class="card">
      <div class="cardhead wrapped">
        <span>${escapeHtml(title)}</span>
        <span class="sub">${escapeHtml(subtitle)}</span>
      </div>
      <div style="overflow-x:auto">
        <table class="grid tight figures">
          <thead><tr>${head}</tr></thead>
          <tbody>${rows}</tbody>
        </table>
      </div>
    </div>`;
}

/* A cell, optionally right-aligned, flagged red, or kept on one line. */
const dcell = (v, o) => {
  const opt = o || {};
  const cls = ["num", "bad", "nowrap"].filter(c =>
    (c === "num" && opt.num) || (c === "bad" && opt.bad) || (c === "nowrap" && opt.nowrap));
  return `<td class="${cls.join(" ")}">${v}</td>`;
};

const setKeyCell = (s) => `<td class="setkey">${escapeHtml(s.key)}</td>`;

function renderDashChecks(res) {
  const sets = res.sets || [];
  const dash = res.dashboard_sets || [];
  const extra = (key) => dash.find(d => d.key === key) || {};

  dashCard("#dash-check1",
    "Check 1 — are all our defaults accounted for?",
    "lgd_defaults.csv (Bucket 0) → write-off & IFRS9 files",
    [{ t: "Set" }, { t: "Defaults", num: true }, { t: "Exposure", num: true },
     { t: "Traced", num: true }, { t: "WO traced", num: true }, { t: "IFRS9 traced", num: true },
     { t: "Untraced", num: true }, { t: "Untraced exposure", num: true }, { t: "Trace rate", num: true }],
    sets.map(s => "<tr>" + setKeyCell(s) +
      dcell(fmt(s.defaults), { num: true }) +
      dcell(escapeHtml(s.exposure_fmt || ""), { num: true, nowrap: true }) +
      dcell(fmt(s.traced), { num: true }) +
      dcell(fmt(s.traced_writeoff), { num: true }) +
      dcell(fmt(s.traced_ifrs9), { num: true }) +
      dcell(fmt(s.untraced), { num: true, bad: s.untraced > 0 }) +
      dcell(escapeHtml(s.untraced_fmt || ""), { num: true, bad: s.untraced > 0, nowrap: true }) +
      dcell(s.trace_rate + "%", { num: true }) + "</tr>").join(""));

  dashCard("#dash-check2",
    "Check 2 — did we miss any defaults?",
    "write-off file → scored population without Bucket 0",
    [{ t: "Set" }, { t: "Scoring window", nowrap: true }, { t: "Scored in WO", num: true },
     { t: "WO not default", num: true }, { t: "In window", num: true },
     { t: "In-window amount", num: true }, { t: "Pre-window", num: true }, { t: "Post-window", num: true }],
    sets.map(s => "<tr>" + setKeyCell(s) +
      dcell(escapeHtml(s.window || "n/a"), { nowrap: true }) +
      dcell(fmt(extra(s.key).scored_in_writeoff), { num: true }) +
      dcell(fmt(s.wo_total), { num: true }) +
      dcell(fmt(s.wo_in_window), { num: true, bad: s.wo_in_window > 0 }) +
      dcell(escapeHtml(s.wo_in_window_fmt || ""), { num: true, bad: s.wo_in_window > 0, nowrap: true }) +
      dcell(fmt(extra(s.key).wo_pre_window), { num: true }) +
      dcell(fmt(s.wo_post_window), { num: true }) + "</tr>").join(""));

  dashCard("#dash-census",
    "Distinct account census",
    "cross-file population overlap",
    [{ t: "Set" }, { t: "Scored", num: true }, { t: "Defaults", num: true },
     { t: "Default %", num: true }, { t: "Write-off", num: true }, { t: "IFRS9", num: true },
     { t: "Scored in WO", num: true }, { t: "Scored in IFRS9", num: true }],
    sets.map(s => {
      const d = extra(s.key);
      // the engine reports the share as a fraction of the scored population
      const pct = d.default_pct_of_scored == null
        ? "&mdash;" : (d.default_pct_of_scored * 100).toFixed(2) + "%";
      return "<tr>" + setKeyCell(s) +
        dcell(fmt(s.scored), { num: true }) +
        // the distinct count, not the row count: this table is a census
        dcell(fmt(d.defaults_distinct == null ? s.defaults : d.defaults_distinct), { num: true }) +
        dcell(pct, { num: true }) +
        dcell(fmt(d.writeoff_distinct), { num: true }) +
        dcell(fmt(d.ifrs9_distinct), { num: true }) +
        dcell(fmt(d.scored_in_writeoff), { num: true }) +
        dcell(d.scored_in_ifrs9 == null ? "&mdash;" : fmt(d.scored_in_ifrs9), { num: true }) + "</tr>";
    }).join(""));
}

/* The five figures the dashboard opens with, summed across sets. */
function renderDashTiles(res) {
  const sets = res.sets || [];
  const sum = (f) => sets.reduce((n, s) => n + (f(s) || 0), 0);

  const scored = sum(s => s.scored);
  const defaults = sum(s => s.defaults);
  const exposure = sum(s => s.exposure);
  const untraced = sum(s => s.untraced);
  const inWindow = sum(s => s.wo_in_window);
  // one rate over the whole population reads better than an average of rates
  const rate = scored > 0 ? (defaults / scored * 100).toFixed(2) + "% default rate" : "";

  const tiles = [
    ["Debug sets", fmt(sets.length), "portfolio slices", ""],
    ["Scored accounts", fmt(scored), rate, ""],
    ["Total defaults", fmt(defaults), money(exposure) + " exposure", ""],
    ["Check 1 untraced", fmt(untraced), "defaults not in WO / IFRS9", untraced > 0 ? "bad" : ""],
    ["Check 2 in-window", fmt(inWindow), "write-off without default flag", inWindow > 0 ? "bad" : ""],
  ];

  $("#dash-tiles").innerHTML = tiles.map(([label, value, sub, cls]) => `
    <div class="tile dash">
      <p class="lbl">${label}</p>
      <span class="num ${cls}">${value}</span>
      <span class="sub">${escapeHtml(sub)}</span>
    </div>`).join("");
}

/* Rands, for the figures the server sends raw rather than pre-formatted. The
   locale is pinned so this matches the server's own "R #,##0.00" - the same page
   shows both, and on a machine with another locale they would disagree. */
function money(v) {
  const n = Number(v) || 0;
  return "R " + n.toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

/* The workbook's own opening sentences. The first is the verdict, so it heads the
   card and decides the pill; the rest are the findings behind it. */
function renderDashCommentary(res) {
  const lines = (res.commentary || []).filter(Boolean);
  if (!lines.length) { $("#dash-commentary").innerHTML = ""; return; }

  const verdict = lines[0];
  const clean = /no exceptions/i.test(verdict);

  $("#dash-commentary").innerHTML = `
    <div class="card accent">
      <div class="cardbody" style="display:grid;gap:8px">
        <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap">
          <span class="ehead">Executive commentary</span>
          <span class="chip ${clean ? "done" : "error"}">${clean ? "No exceptions" : "Exceptions found"}</span>
        </div>
        <p class="verdict">${escapeHtml(verdict)}</p>
        ${lines.slice(1).map(l => `<p class="cline">${escapeHtml(l)}</p>`).join("")}
      </div>
    </div>`;
}

/* ---------- AI analysis ---------- */
/* The model returns markdown. Only headings, paragraphs and bullets are used, which
   is all the design shows, and the sections are what the expander works on. */
function parseAnalysis(md) {
  const sections = [];
  let current = null;

  String(md || "").split(/\r?\n/).forEach(raw => {
    const line = raw.trim();
    if (!line) return;

    const heading = line.match(/^#{1,6}\s+(.*)$/);
    if (heading) {
      current = { h: heading[1].replace(/[*_`]/g, "").trim(), paras: [], bullets: [] };
      sections.push(current);
      return;
    }
    if (!current) { current = { h: "", paras: [], bullets: [] }; sections.push(current); }

    const bullet = line.match(/^[-*+]\s+(.*)$/);
    if (bullet) {
      const text = bullet[1];
      // "**lead** - rest" renders as a lead-in with its explanation
      const split = text.match(/^\*\*(.+?)\*\*\s*[-–—:]\s*(.*)$/);
      current.bullets.push(split
        ? { lead: split[1].trim(), text: split[2].trim() }
        : { lead: "", text: text.replace(/\*\*/g, "") });
      return;
    }
    current.paras.push(line.replace(/\*\*/g, ""));
  });

  return sections.filter(s => s.h || s.paras.length || s.bullets.length);
}

function analysisSection(sec) {
  return `<div class="asec">` +
    (sec.h ? `<span class="ah">${escapeHtml(sec.h)}</span>` : "") +
    sec.paras.map(p => `<p class="ap">${escapeHtml(p)}</p>`).join("") +
    sec.bullets.map(b => `<div class="abul"><span class="dot"></span>` +
      `<p>${b.lead ? `<b>${escapeHtml(b.lead)}</b> &mdash; ` : ""}` +
      `<span class="dim">${escapeHtml(b.text)}</span></p></div>`).join("") +
    `</div>`;
}

let AI_OPEN = false;
function renderDashAi(res) {
  const sections = parseAnalysis(res.analysis);
  if (!sections.length) { $("#dash-ai").innerHTML = ""; return; }

  // the first section always shows; the rest are behind the expander
  const rest = sections.slice(1);
  const model = (res.model_id ? res.model_id + " · " : "") + "generated with the run";

  $("#dash-ai").innerHTML = `
    <div class="card">
      <div class="cardhead">
        <span class="ms-icon" style="font-size:20px">auto_awesome</span>AI analysis
        <span class="sub" style="margin-left:auto">${escapeHtml(model)}</span>
      </div>
      <div class="cardbody" style="display:grid;gap:18px">
        ${analysisSection(sections[0])}
        ${rest.length ? `<div id="ai-rest" class="hide" style="display:grid;gap:18px">
          ${rest.map(analysisSection).join("")}</div>` : ""}
        ${rest.length ? `<button class="linkbtn" id="btn-ai" style="justify-self:start">
          <span class="ms-icon" style="font-size:18px" id="ai-ic">expand_more</span>
          <span id="ai-tx">Show the full analysis</span></button>` : ""}
      </div>
    </div>`;

  if (!rest.length) return;
  setAiOpen(false);
  $("#btn-ai").addEventListener("click", () => setAiOpen(!AI_OPEN));
}

function setAiOpen(open) {
  AI_OPEN = open;
  $("#ai-rest").classList.toggle("hide", !open);
  $("#ai-tx").textContent = open ? "Show less" : "Show the full analysis";
  $("#ai-ic").textContent = open ? "expand_less" : "expand_more";
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
const DETAIL_TABS = ["summary", "dashboard", "files", "logs"];

function showTab(name) {
  DETAIL_TABS.forEach(t => {
    $("#tab-" + t).classList.toggle("hide", t !== name);
    $("#tab-btn-" + t).classList.toggle("on", t === name);
  });
}

DETAIL_TABS.forEach(t => $("#tab-btn-" + t).addEventListener("click", () => showTab(t)));

$("#btn-detail-back").addEventListener("click", () => showScreen("runs"));

/* ---------- ask about this run ---------- */

/* A run that had AI analysis answers with the model that wrote its memo. One
   reconciled without it has none, so the conversation offers its own picker
   rather than refusing the question. */
function runHasModel() {
  return !!(RESULT && RESULT.model_id);
}

function fillChatModels() {
  const sel = $("#chat-model");
  sel.innerHTML = "";
  addModelOption(sel, "", "Choose a model…");
  MODELS.forEach(m => addModelOption(sel, m.id, m.friendlyName));

  // defaults to whatever they last ran with, which is nearly always what they want
  const saved = localStorage.getItem("hr_model") || "";
  sel.value = MODELS.some(m => m.id === saved) ? saved : "";
  sel.disabled = MODELS.length === 0;

  updateChatReady();
}

/* Shows the picker only when it is needed, and keeps Send in step with whether
   anything can actually answer. */
function updateChatReady() {
  const own = runHasModel();
  $("#chat-model-row").classList.toggle("hide", own);

  if (!own) {
    $("#chat-model-note").textContent = MODELS.length === 0
      ? "No models are available, so this run cannot be asked about."
      : "This run was reconciled without AI analysis, so choose a model to answer from its figures.";
  }

  $("#btn-chat").disabled = !(own || $("#chat-model").value);
}

/* The model the question goes to: the run's own, or the one picked here. */
function chatModelId() {
  return runHasModel() ? RESULT.model_id : ($("#chat-model").value || null);
}

$("#chat-model").addEventListener("change", () => {
  localStorage.setItem("hr_model", $("#chat-model").value);
  updateChatReady();
});

function setChatOpen(open) {
  $("#chat-drawer").classList.toggle("hide", !open);
  // the run being asked about may have changed since this drawer last opened
  if (open) { updateChatReady(); $("#chat-input").focus(); }
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
  // Enter sends, so the gate has to hold here too and not only on the button
  if (!chatModelId()) { updateChatReady(); $("#chat-model").focus(); return; }
  addChatBubble("user", escapeHtml(msg));
  input.value = "";
  const btn = $("#btn-chat");
  btn.disabled = true; input.disabled = true;
  const thinking = addChatBubble("bot thinking", "thinking&hellip;");
  api("/api/chat", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ run_id: RUN_ID, message: msg, model_id: chatModelId() })
  }).then(r => r.json().then(j => ({ ok: r.ok, j })))
    .then(({ ok, j }) => {
      thinking.remove();
      if (!ok) { addChatBubble("bot err", escapeHtml(j.error || "Chat is unavailable.")); return; }
      addChatBubble("bot", j.reply_html);
    })
    .catch(() => { thinking.remove(); addChatBubble("bot err", "Network error - please try again."); })
    // updateChatReady owns the button, so a run with nothing to answer with does
    // not get Send handed back to it
    .finally(() => { input.disabled = false; updateChatReady(); input.focus(); });
}

$("#btn-chat").addEventListener("click", sendChat);
$("#chat-input").addEventListener("keydown", (e) => {
  if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); sendChat(); }
});
