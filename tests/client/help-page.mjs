/* Checks the static page itself, which the DOM stub in app.harness.mjs cannot:
   there every "$('#whatever')" invents an element on demand, so a control that
   was never added to index.html - or an id renamed on one side only - still
   passes. This reads the real markup.

   Run:  node tests/client/help-page.mjs */
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const root = new URL("../../src/HazardRecon.Web/wwwroot/", import.meta.url);
const html = readFileSync(fileURLToPath(new URL("index.html", root)), "utf8");
const js = readFileSync(fileURLToPath(new URL("app.js", root)), "utf8");

let failures = 0;
const check = (name, cond, detail) => {
  if (cond) console.log(`  PASS  ${name}`);
  else { console.log(`  FAIL  ${name}${detail ? " -> " + detail : ""}`); failures++; }
};

const ids = new Set([...html.matchAll(/\bid="([^"]+)"/g)].map(m => m[1]));

/* Cards the AI analysis panel builds from script when a run has one, so they are
   never in index.html. Everything else app.js addresses by id must be. */
const BUILT_AT_RUNTIME = new Set(["btn-ai", "ai-rest", "ai-tx", "ai-ic"]);

console.log("1) every id app.js addresses exists in the page");
const refs = [...new Set([...js.matchAll(/\$\("#([\w-]+)"\)/g)].map(m => m[1]))];
const missing = refs.filter(r => !ids.has(r) && !BUILT_AT_RUNTIME.has(r));
check(`all ${refs.length} referenced ids are in index.html`, missing.length === 0,
  `absent: ${missing.join(", ")}`);

console.log("\n2) the Help nav item is wired up rather than decorative");
const helpNav = html.match(/<(\w+)([^>]*\bid="nav-help"[^>]*)>/);
check("a #nav-help element exists", helpNav !== null);
check("it is a button, so it can be clicked and focused", helpNav?.[1] === "button",
  `it is a <${helpNav?.[1]}>`);
check("it is not marked dead", !/\bdead\b/.test(helpNav?.[2] || ""));
check("app.js listens to it", /\$\("#nav-help"\)\.addEventListener\("click"/.test(js));
check("showScreen knows the help screen",
  /\["runs",\s*"wizard",\s*"detail",\s*"help"\]/.test(js));
check("a #screen-help section exists", /<section id="screen-help"/.test(html));
check("it starts hidden", /<section id="screen-help" class="hide">/.test(html));

console.log("\n3) Reports is still the only placeholder left in the nav");
const dead = [...html.matchAll(/class="navitem dead"[\s\S]{0,140}?<\/div>/g)].map(m => m[0]);
check("exactly one dead nav item remains", dead.length === 1, `found ${dead.length}`);
check("and it is Reports", /Reports/.test(dead[0] || ""));

console.log("\n4) every help jump link lands somewhere");
const toc = html.match(/<nav class="helptoc"[\s\S]*?<\/nav>/);
check("the contents list is present", toc !== null);
const anchors = [...(toc?.[0] || "").matchAll(/href="#([\w-]+)"/g)].map(m => m[1]);
check("it links to more than a couple of sections", anchors.length >= 8, `${anchors.length} links`);
const dangling = anchors.filter(a => !ids.has(a));
check("no link points at a section that is not there", dangling.length === 0,
  `dangling: ${dangling.join(", ")}`);

/* The other direction: a section added without a link is unreachable from the
   top of a page this long, which is the same defect seen from the other end. */
const sections = [...html.matchAll(/<div class="card" id="(help-[\w-]+)"/g)].map(m => m[1]);
const unlisted = sections.filter(s => !anchors.includes(s));
check("every help section is listed in the contents", unlisted.length === 0,
  `unlisted: ${unlisted.join(", ")}`);

console.log("\n5) the quoted upload limits are placeholders the server overwrites");
["help-max-sets", "help-max-files", "help-max-bytes"].forEach(id => {
  check(`#${id} is in the page`, ids.has(id));
  check(`#${id} is written from config`, new RegExp(`\\$\\("#${id}"\\)\\.textContent`).test(js));
});
check("showLimits runs off the /api/config response", /showLimits\(cfg\)/.test(js));

console.log(failures === 0 ? "\nALL HELP-PAGE CHECKS PASSED" : `\n${failures} CHECK(S) FAILED`);
process.exit(failures === 0 ? 0 : 1);
