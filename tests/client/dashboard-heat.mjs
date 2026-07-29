/* Exercises the migration heat map's client-side script straight out of a
   generated dashboard: counts mode, row-% mode, and period switching.

   Run:  node tests/client/dashboard-heat.mjs <path-to-reconciliation_dashboard.html> */
import { readFileSync } from "node:fs";
import vm from "node:vm";

const target = process.argv[2];
if (!target) { console.error("usage: node dashboard-heat.mjs <dashboard.html>"); process.exit(2); }
const html = readFileSync(target, "utf8");
console.log(`target: ${target}\n`);

const scriptMatch = html.match(/<script>([\s\S]*?)<\/script>/);
if (!scriptMatch) { console.error("no <script> in the dashboard"); process.exit(1); }
const migMatch = html.match(/const MIG = (\{[\s\S]*?\});\r?\n/);
if (!migMatch) { console.error("no MIG payload in the dashboard"); process.exit(1); }

const MIG = JSON.parse(migMatch[1]);
const slugs = Object.keys(MIG);
console.log(`sets: ${slugs.join(", ")}`);

// minimal DOM: a select we can drive, a target div, and toggle buttons
const nodes = new Map();
function node(id) {
  if (!nodes.has(id)) nodes.set(id, { id, value: "", innerHTML: "", classList: { _on: false, toggle(c, on) { this._on = on; } } });
  return nodes.get(id);
}
for (const s of slugs) {
  node("sel_" + s).value = Object.keys(MIG[s])[0];   // "All months"
  node("heat_" + s); node("c_" + s); node("p_" + s);
}

const ctx = {
  console, JSON, Object, Math, Number, String, Array,
  document: { getElementById: (id) => nodes.get(id) ?? null, readyState: "complete", addEventListener() {} },
};
vm.createContext(ctx);
vm.runInContext(scriptMatch[1], ctx);

let failures = 0;
const check = (name, cond, detail) => {
  if (cond) console.log(`  PASS  ${name}`);
  else { console.log(`  FAIL  ${name}${detail ? " -> " + detail : ""}`); failures++; }
};

for (const s of slugs) {
  const heat = node("heat_" + s);
  const periods = Object.keys(MIG[s]);

  // --- counts mode (initHeat already ran on load) ---
  console.log(`\n[${s}] counts mode, period "${node("sel_" + s).value}"`);
  check("heat table rendered on load", /<table class="heat">/.test(heat.innerHTML));
  check("row totals column present", /Row total/.test(heat.innerHTML));
  const countCells = heat.innerHTML.match(/rgba\(47,111,176,[0-9.]+\)/g) || [];
  check("36 cells shaded", countCells.length === 36, `got ${countCells.length}`);

  // row totals must equal the sum of that row in the source data
  const m = MIG[s][node("sel_" + s).value];
  const rendered = [...heat.innerHTML.matchAll(/<td class="rt">([\d,]+)<\/td>/g)].map(x => Number(x[1].replace(/,/g, "")));
  const expected = m.map(r => r.reduce((a, b) => a + b, 0));
  check("row totals match the data", JSON.stringify(rendered) === JSON.stringify(expected),
    `rendered=${JSON.stringify(rendered)} expected=${JSON.stringify(expected)}`);

  // --- row-% mode ---
  ctx.setMode(s, "pct");
  console.log(`[${s}] row-% mode`);
  const pcts = [...heat.innerHTML.matchAll(/>([\d.]+)%</g)].map(x => Number(x[1]));
  check("percentages rendered", pcts.length > 0, `got ${pcts.length}`);
  // each non-empty row must sum to ~100%
  const perRow = heat.innerHTML.split("<tr>").slice(2);   // skip header row
  let badRow = null;
  perRow.forEach((row, i) => {
    const vals = [...row.matchAll(/>([\d.]+)%</g)].map(x => Number(x[1]));
    if (vals.length === 6) {
      const sum = vals.reduce((a, b) => a + b, 0);
      if (Math.abs(sum - 100) > 0.5) badRow = `row ${i + 1} sums to ${sum.toFixed(2)}%`;
    }
  });
  check("every populated row sums to 100%", badRow === null, badRow);
  check("counts button marked off", node("c_" + s).classList._on === false);
  check("row-% button marked on", node("p_" + s).classList._on === true);

  // --- period switching ---
  if (periods.length > 1) {
    const other = periods[1];
    node("sel_" + s).value = other;
    ctx.setMode(s, "count");
    ctx.renderHeat(s);
    console.log(`[${s}] switched to period "${other}"`);
    const rendered2 = [...heat.innerHTML.matchAll(/<td class="rt">([\d,]+)<\/td>/g)].map(x => Number(x[1].replace(/,/g, "")));
    const expected2 = MIG[s][other].map(r => r.reduce((a, b) => a + b, 0));
    check("re-rendered with that period's data", JSON.stringify(rendered2) === JSON.stringify(expected2),
      `rendered=${JSON.stringify(rendered2)} expected=${JSON.stringify(expected2)}`);
    check("period total differs from all-months", JSON.stringify(rendered2) !== JSON.stringify(expected));
  }
}

console.log(failures === 0 ? "\nALL HEAT-MAP CHECKS PASSED" : `\n${failures} CHECK(S) FAILED`);
process.exit(failures === 0 ? 0 : 1);
