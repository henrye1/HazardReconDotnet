/* Hazard-Rate Reconciliation - front end (.NET) */

const $ = (s) => document.querySelector(s);
const el = (t, c, h) => { const n = document.createElement(t); if (c) n.className = c; if (h !== undefined) n.innerHTML = h; return n; };
const fmt = (n) => (n === null || n === undefined) ? "&mdash;" : Number(n).toLocaleString();

let RUN_ID = null;
let POLL = null;
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
      SB = supabase.createClient(cfg.supabaseUrl, cfg.supabaseAnonKey);
      return SB.auth.getSession();
    })
    .then((res) => {
      const session = res && res.data ? res.data.session : null;
      if (session) { TOKEN = session.access_token; hideGate(); openDownloadSession(); loadModels(); }
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
    hideGate();
    openDownloadSession();
    loadModels();
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

/* ---------- step 1: folder paths ---------- */
const MAX_SETS = 4;
let PATHS = 0;

function addPathRow() {
  if (PATHS >= MAX_SETS) return;
  PATHS += 1;
  const i = PATHS;
  const d = el("div", "slot", `
    <div class="slothead"><b>Folder ${i}</b>
      <button class="btn clear" id="path${i}-clear" title="Clear">&#10005;</button></div>
    <div class="row">
      <input type="text" class="pathbox" id="path${i}" spellcheck="false"
             placeholder="C:\\...\\DEBUG FILE 30 JUNE 2026 0.5 PERCENT">
    </div>`);
  $("#paths").appendChild(d);
  $("#path" + i).addEventListener("input", updateReady);
  $("#path" + i + "-clear").addEventListener("click", () => {
    $("#path" + i).value = ""; updateReady();
  });
  $("#btn-add-path").disabled = PATHS >= MAX_SETS;
}

function pathValues() {
  const out = [];
  for (let i = 1; i <= PATHS; i++) {
    const v = $("#path" + i).value.trim();
    if (v) out.push(v);
  }
  return out;
}

function updateReady() { $("#btn-check").disabled = pathValues().length === 0; }

addPathRow();
$("#btn-add-path").addEventListener("click", addPathRow);

(function restorePaths() {
  let saved = [];
  try { saved = JSON.parse(localStorage.getItem("hr_paths") || "[]"); } catch (_) { }
  saved.slice(0, MAX_SETS).forEach((p, i) => {
    while (PATHS < i + 1) addPathRow();
    $("#path" + (i + 1)).value = p;
  });
  updateReady();
})();

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
      note.textContent = "Analysis adds roughly 25 seconds to a run.";
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
  const paths = pathValues();
  const fd = new FormData();
  paths.forEach(p => fd.append("paths", p));
  return api("/api/discover", { method: "POST", body: fd })
    .then(readJson)
    .then(({ ok, status, j }) => {
      if (!ok || !j) throw new Error((j && j.error) || `Discovery failed (server returned ${status}).`);
      localStorage.setItem("hr_paths", JSON.stringify(paths));
      showInventory(j);
      return j;
    });
}

$("#btn-check").addEventListener("click", () => {
  const btn = $("#btn-check");
  btn.disabled = true; btn.textContent = "Checking...";
  discover()
    .catch(e => showError($("#card-inv"), e.message))
    .finally(() => { btn.disabled = false; btn.textContent = "Check folders"; updateReady(); });
});

function showError(card, msg) {
  card.classList.remove("hide");
  const box = card.querySelector(".err") || el("div", "err");
  box.textContent = msg;
  if (!box.parentNode) card.appendChild(box);
}

function showInventory(j) {
  RUN_ID = j.run_id;
  const card = $("#card-inv");
  card.classList.remove("hide");
  const old = card.querySelector(".err"); if (old) old.remove();

  $("#inv-root").innerHTML = "Reading from <code>" + (j.inventory.root || "") + "</code>";

  const probs = $("#inv-problems");
  probs.innerHTML = "";
  if (j.problems && j.problems.length) {
    probs.appendChild(el("div", "warn",
      "<b>Worth knowing before you run:</b><ul>" +
      j.problems.map(p => "<li>" + p + "</li>").join("") + "</ul>"));
  }

  const t = $("#inv-table");
  t.innerHTML =
    "<tr><th>Set</th><th>Folder</th><th>Write-off</th><th>Defaults</th>" +
    "<th>Scored</th><th>IFRS9</th><th>Engine</th></tr>" +
    j.inventory.sets.map(s =>
      "<tr><td><b>" + s.key + "</b></td><td>" + s.label + "</td>" +
      cell(s.writeoff) + cell(s.lgd_defaults) + cell(s.pd_scored) +
      cell(s.ifrs9) + cell(s.scenario) + "</tr>").join("");

  $("#btn-run").disabled = !(j.inventory.sets || []).length;
  card.scrollIntoView({ behavior: "smooth", block: "start" });
}

const cell = (v) => "<td>" + (v ? "<span class='ok'>&#10003;</span> " + v
                                : "<span class='no'>&#10007;</span>") + "</td>";

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
  setBadge("error", "err");
  showError($("#card-run"), msg);
  $("#btn-run").disabled = false;
}

function beginRun() {
  if (!RUN_ID) return;
  stopPolling();
  $("#card-run").classList.remove("hide");
  $("#card-res").classList.add("hide");
  $("#log").innerHTML = "";
  setBadge("running", "run");
  $("#btn-run").disabled = true;
  $("#card-run").scrollIntoView({ behavior: "smooth", block: "start" });
  startRun(false);
}

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

function setBadge(text, cls) {
  const b = $("#run-badge");
  b.textContent = text;
  b.className = "badge " + (cls || "");
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

    if (j.status === "done") {
      stopPolling();
      if (!j.result) { failRun("The run finished but returned no results."); return; }
      setBadge("complete", "done");
      $("#btn-run").disabled = false;
      showResults(j.result);
    } else if (j.status === "error") {
      stopPolling();
      setBadge("error", "err");
      $("#btn-run").disabled = false;
      showError($("#card-run"), j.error || "The run failed.");
    }
  }).catch(() => pollFailed("Lost contact with the server while the run was in progress."));
}

const mark = (k) => k === "tool" ? "→ " : k === "ok" ? "✓ " : k === "warn" ? "! " : k === "head" ? "■ " : "  ";
const escapeHtml = (s) => String(s).replace(/[&<>]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]));

/* ---------- step 4: results ---------- */
function showResults(res) {
  if (!res) return;
  const card = $("#card-res");
  card.classList.remove("hide");

  $("#res-sets").innerHTML = res.sets.map(s => `
    <div class="setcard">
      <h4>${s.key} <span class="muted">&mdash; ${s.label || ""}</span></h4>
      <p class="muted">Scoring window ${s.window || "n/a"} &middot;
         ${fmt(s.scored)} scored accounts${s.ifrs9_overlap === 0
        ? " &middot; <b>IFRS9 could not be matched for this set</b> (different account numbering)" : ""}</p>
      <div class="tiles">
        <div class="tile"><div class="l">Defaults (Bucket 0)</div>
          <div class="v">${fmt(s.defaults)}</div><div class="s">${s.exposure_fmt}</div></div>
        <div class="tile"><div class="l">Traced</div>
          <div class="v" style="color:var(--green)">${fmt(s.traced)}</div>
          <div class="s">${s.trace_rate}% &middot; W/O ${fmt(s.traced_writeoff)} / IFRS9 ${fmt(s.traced_ifrs9)}</div></div>
        <div class="tile"><div class="l">Untraced defaults</div>
          <div class="v" style="color:var(--red)">${fmt(s.untraced)}</div>
          <div class="s">${s.untraced_fmt}</div></div>
        <div class="tile"><div class="l">Written off, never defaulted</div>
          <div class="v" style="color:var(--amber)">${fmt(s.wo_in_window)}</div>
          <div class="s">in window &middot; ${s.wo_in_window_fmt}</div></div>
      </div>
      <p class="hint">Check 2 found ${fmt(s.wo_total)} in total &mdash; ${fmt(s.wo_in_window)} inside the
        scoring window (the priority exceptions) and ${fmt(s.wo_post_window)} written off after it closed.</p>
    </div>`).join("");

  const base = "/runs/" + RUN_ID + "/output/";
  const bust = "?v=" + Date.now();
  const links = [`<a class="dl x" href="${base}${encodeURIComponent(res.workbook)}${bust}">&#128202; ${res.workbook}</a>`];
  if (res.memo) {
    links.unshift(`<a class="dl x" href="${base}${encodeURIComponent(res.memo)}${bust}">&#128221; ${res.memo}</a>`);
  }
  res.sets.forEach(s => (s.files || []).forEach(f => {
    const key = f.includes("writeoff_not_default") ? "&#9888;" :
      f.includes("untraced") ? "&#128203;" : "&#128196;";
    links.push(`<a class="dl" href="${base}${encodeURIComponent(f)}${bust}">${key} ${f}</a>`);
  }));
  $("#res-downloads").innerHTML = links.join("");

  const durl = base + encodeURIComponent(res.dashboard) + bust;
  $("#res-frame").src = durl;
  $("#res-open").href = durl;
  $("#card-chat").classList.remove("hide");
  card.scrollIntoView({ behavior: "smooth", block: "start" });
}

/* ---------- step 5: ask about this run ---------- */
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
