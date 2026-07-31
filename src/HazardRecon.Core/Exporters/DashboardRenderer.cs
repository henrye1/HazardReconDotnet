using System.Net;
using System.Text;
using System.Text.Json;
using HazardRecon.Core.Helpers;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;

namespace HazardRecon.Core.Exporters;

public class DashboardRenderer
{
    public static string RenderDashboard(Dictionary<string, SingleSetResult> results, string? analysisMd = null)
    {
        StringBuilder sb = new();
        List<string> sets = results.Keys.ToList();
        string generatedAt = DateTime.Now.ToString("dd MMM yyyy HH:mm");

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang='en'>");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset='utf-8'>");
        sb.AppendLine("  <meta name='viewport' content='width=device-width, initial-scale=1'>");
        sb.AppendLine("  <title>Hazard-Rate Model Data Reconciliation — Anchor Point Risk</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    :root { --ink:#1a2332; --muted:#5b6b7f; --line:#e3e8ef; --bg:#f6f8fb; --card:#ffffff; --blue:#2f6fb0; --green:#2e8b6b; --amber:#c98a1a; --red:#c0492f; --band:#eef3f8; --purple:#6a5194; }");
        sb.AppendLine("    * { box-sizing:border-box; }");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; color:var(--ink); background:var(--bg); margin:0; padding:24px; font-size:14px; line-height:1.5; }");
        sb.AppendLine("    .container { max-width:1280px; margin:0 auto; }");
        sb.AppendLine("    header { margin-bottom:24px; padding-bottom:16px; border-bottom:1px solid var(--line); display:flex; justify-content:space-between; align-items:baseline; }");
        sb.AppendLine("    h1 { font-size:22px; font-weight:700; margin:0; }");
        sb.AppendLine("    .sub { color:var(--muted); font-size:13px; }");
        sb.AppendLine("    .grid { display:grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap:16px; margin-bottom:24px; }");
        sb.AppendLine("    .tile { background:var(--card); border:1px solid var(--line); border-radius:6px; padding:16px; box-shadow:0 1px 3px rgba(0,0,0,0.04); }");
        sb.AppendLine("    .tlabel { font-size:11px; text-transform:uppercase; letter-spacing:0.5px; color:var(--muted); font-weight:600; }");
        sb.AppendLine("    .tval { font-size:24px; font-weight:700; margin:4px 0; }");
        sb.AppendLine("    .tsub { font-size:12px; color:var(--muted); }");
        sb.AppendLine("    .card { background:var(--card); border:1px solid var(--line); border-radius:6px; padding:20px; margin-bottom:24px; box-shadow:0 1px 3px rgba(0,0,0,0.04); }");
        sb.AppendLine("    h2 { font-size:16px; font-weight:600; margin-top:0; margin-bottom:16px; border-bottom:1px solid var(--line); padding-bottom:8px; display:flex; justify-content:space-between; }");
        sb.AppendLine("    table { width:100%; border-collapse:collapse; text-align:left; font-size:13px; }");
        sb.AppendLine("    th, td { padding:8px 12px; border-bottom:1px solid var(--line); }");
        sb.AppendLine("    th { background:var(--band); font-weight:600; color:var(--ink); font-size:12px; }");
        sb.AppendLine("    td.num, th.num { text-align:right; }");
        sb.AppendLine("    td.bad { color:var(--red); font-weight:600; }");
        sb.AppendLine("    .flag { display:inline-block; padding:2px 6px; border-radius:4px; font-size:11px; font-weight:600; }");
        sb.AppendLine("    .flag.red { background:#fde8e8; color:var(--red); }");
        sb.AppendLine("    .flag.grey { background:#f0f2f5; color:var(--muted); }");
        sb.AppendLine("    .chip { font-size:10px; background:#eef3f8; color:var(--blue); padding:2px 4px; border-radius:3px; }");
        sb.AppendLine("    .bar { height:4px; background:#e0e0e0; border-radius:2px; overflow:hidden; margin-top:4px; }");
        sb.AppendLine("    .bar span { display:block; height:100%; background:var(--blue); }");
        sb.AppendLine("    .hz { font-size:10px; color:var(--muted); font-weight:normal; }");
        sb.AppendLine("    .commentary { background:#f0f4f9; border-left:4px solid var(--blue); padding:12px 16px; font-size:13px; line-height:1.6; margin-bottom:24px; border-radius:0 4px 4px 0; }");
        sb.AppendLine("    .ai-analysis { background:#faf8fc; border-left:4px solid var(--purple); padding:16px; margin-bottom:24px; border-radius:0 6px 6px 0; }");
        sb.AppendLine("    .ai-analysis h2 { border-bottom:none; margin-bottom:8px; }");
        sb.AppendLine("    h3 { font-size:14px; margin:16px 0 8px 0; }");
        sb.AppendLine("    .note { color:var(--muted); font-size:12px; margin:4px 0 10px 0; }");
        sb.AppendLine("    .grid2 { display:grid; grid-template-columns:1.3fr 1fr; gap:24px; align-items:start; }");
        sb.AppendLine("    .heatwrap { overflow-x:auto; }");
        sb.AppendLine("    table.heat { width:auto; margin-top:6px; }");
        sb.AppendLine("    table.heat td, table.heat th { padding:8px 12px; text-align:center; border:1px solid var(--line); font-variant-numeric:tabular-nums; font-size:13px; min-width:56px; }");
        sb.AppendLine("    table.heat th { background:var(--band); color:var(--muted); font-size:12px; }");
        sb.AppendLine("    table.heat td.rt { background:#eef2f7; font-weight:600; color:var(--muted); }");
        sb.AppendLine("    table.heat.pctm td { min-width:74px; }");
        sb.AppendLine("    table.mini { width:auto; }");
        sb.AppendLine("    .migctl { display:flex; gap:16px; align-items:center; margin:10px 0 2px; flex-wrap:wrap; }");
        sb.AppendLine("    .migctl select { padding:5px 8px; border:1px solid #cfd8e3; border-radius:6px; font-size:13px; background:#fff; }");
        sb.AppendLine("    .migctl .note { flex:1; min-width:180px; margin:0; }");
        sb.AppendLine("    .tg button { border:1px solid #cfd8e3; background:#fff; padding:5px 13px; font-size:12px; cursor:pointer; color:var(--ink); }");
        sb.AppendLine("    .tg button:first-child { border-radius:6px 0 0 6px; }");
        sb.AppendLine("    .tg button:last-child { border-radius:0 6px 6px 0; border-left:0; }");
        sb.AppendLine("    .tg button.on { background:var(--blue); color:#fff; border-color:var(--blue); }");
        sb.AppendLine("    @media(max-width:820px){ .grid2 { grid-template-columns:1fr; } }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class='container'>");

        // Header
        sb.AppendLine("    <header>");
        sb.AppendLine("      <div>");
        sb.AppendLine("        <h1>Hazard-Rate Model Data Reconciliation</h1>");
        sb.AppendLine("        <div class='sub'>Anchor Point Risk &middot; IFRS 9 Portfolio Validation</div>");
        sb.AppendLine("      </div>");
        sb.AppendLine($"      <div class='sub'>Generated {generatedAt}</div>");
        sb.AppendLine("    </header>");

        // Tiles
        double grandTotalExp = results.Values.Sum(r => r.Summary.TotalExposure);
        int grandTotalDefaults = results.Values.Sum(r => r.Summary.TotalDefaults);
        int grandUntraced = results.Values.Sum(r => r.Summary.UntracedTotal);
        int grandInWindow = results.Values.Sum(r => r.Summary.WoInWindow);

        sb.AppendLine("    <div class='grid'>");
        sb.AppendLine($"      <div class='tile'><div class='tlabel'>Debug Sets</div><div class='tval'>{sets.Count}</div><div class='tsub'>portfolio slices</div></div>");
        sb.AppendLine($"      <div class='tile'><div class='tlabel'>Total Defaults</div><div class='tval'>{grandTotalDefaults:N0}</div><div class='tsub'>{AccountUtils.Money(grandTotalExp)} exposure</div></div>");
        sb.AppendLine($"      <div class='tile'><div class='tlabel'>Check 1 Untraced</div><div class='tval' style='color:{(grandUntraced > 0 ? "var(--red)" : "var(--green)")}'>{grandUntraced:N0}</div><div class='tsub'>defaults not in WO/IFRS9</div></div>");
        sb.AppendLine($"      <div class='tile'><div class='tlabel'>Check 2 In-Window</div><div class='tval' style='color:{(grandInWindow > 0 ? "var(--red)" : "var(--green)")}'>{grandInWindow:N0}</div><div class='tsub'>WO without default flag</div></div>");
        sb.AppendLine("    </div>");

        // Management Commentary
        List<string> cLines = WorkbookExporter.CommentaryLines(results);
        if (cLines.Count > 0)
        {
            sb.AppendLine("    <div class='commentary'>");
            sb.AppendLine("      <strong>Executive Commentary</strong><br>");
            foreach (string line in cLines)
            {
                sb.AppendLine($"      <div>{WebUtility.HtmlEncode(line)}</div>");
            }
            sb.AppendLine("    </div>");
        }

        // Optional AI Analysis
        if (!string.IsNullOrEmpty(analysisMd))
        {
            sb.AppendLine("    <div class='ai-analysis'>");
            sb.AppendLine("      <h2 style='color:var(--purple)'>AI Analysis (generated)</h2>");
            sb.AppendLine("      <div>" + MarkdownHelper.ToHtml(analysisMd) + "</div>");
            sb.AppendLine("    </div>");
        }

        // Check 1 Table
        sb.AppendLine("    <div class='card'>");
        sb.AppendLine("      <h2>Check 1 — Are all our defaults accounted for? <span class='sub'>lgd_defaults.csv (Bucket 0) &rarr; write-off & IFRS9 files</span></h2>");
        sb.AppendLine("      <table>");
        sb.AppendLine("        <thead><tr><th>Set</th><th class='num'>Defaults</th><th class='num'>Exposure</th><th class='num'>Traced</th><th class='num'>WO Traced</th><th class='num'>IFRS9 Traced</th><th class='num'>UNTRACED</th><th class='num'>Untraced Exposure</th><th class='num'>Trace Rate</th></tr></thead>");
        sb.AppendLine("        <tbody>");
        foreach (string k in sets)
        {
            ReconciliationSummary s = results[k].Summary;
            sb.AppendLine($"          <tr><td><b>{WebUtility.HtmlEncode(k)}</b></td><td class='num'>{s.TotalDefaults:N0}</td><td class='num'>{AccountUtils.Money(s.TotalExposure)}</td><td class='num'>{s.TracedTotal:N0}</td><td class='num'>{s.TracedWriteOff:N0}</td><td class='num'>{s.TracedIfrs9:N0}</td><td class='num bad'>{s.UntracedTotal:N0}</td><td class='num bad'>{AccountUtils.Money(s.UntracedExposure)}</td><td class='num'>{s.TraceRate * 100:F1}%</td></tr>");
        }
        sb.AppendLine("        </tbody>");
        sb.AppendLine("      </table>");
        sb.AppendLine("    </div>");

        // Check 2 Table
        sb.AppendLine("    <div class='card'>");
        sb.AppendLine("      <h2>Check 2 — Did we miss any defaults? (Reverse) <span class='sub'>write-off file &rarr; scored population without Bucket 0</span></h2>");
        sb.AppendLine("      <table>");
        sb.AppendLine("        <thead><tr><th>Set</th><th>Scoring Window</th><th class='num'>Scored in WO</th><th class='num'>WO Not Default</th><th class='num'>IN WINDOW</th><th class='num'>In-Window Amount</th><th class='num'>Pre-Window</th><th class='num'>Post-Window</th></tr></thead>");
        sb.AppendLine("        <tbody>");
        foreach (string k in sets)
        {
            ReconciliationSummary s = results[k].Summary;
            sb.AppendLine($"          <tr><td><b>{WebUtility.HtmlEncode(k)}</b></td><td>{WebUtility.HtmlEncode(s.Window)}</td><td class='num'>{s.ScoredInWriteOff:N0}</td><td class='num'>{s.WoNotDefaultTotal:N0}</td><td class='num bad'>{s.WoInWindow:N0}</td><td class='num bad'>{AccountUtils.Money(s.WoInWindowAmount)}</td><td class='num'>{s.WoPreWindow:N0}</td><td class='num'>{s.WoPostWindow:N0}</td></tr>");
        }
        sb.AppendLine("        </tbody>");
        sb.AppendLine("      </table>");
        sb.AppendLine("    </div>");

        // Census Table
        sb.AppendLine("    <div class='card'>");
        sb.AppendLine("      <h2>Distinct Account Census <span class='sub'>cross-file population overlap</span></h2>");
        sb.AppendLine("      <table>");
        sb.AppendLine("        <thead><tr><th>Set</th><th class='num'>Scored</th><th class='num'>Defaults</th><th class='num'>Default %</th><th class='num'>Write-Off</th><th class='num'>IFRS9</th><th class='num'>Scored in WO</th><th class='num'>Scored in IFRS9</th></tr></thead>");
        sb.AppendLine("        <tbody>");
        foreach (string k in sets)
        {
            ReconciliationSummary s = results[k].Summary;
            string defPct = s.DefaultPctOfScored.HasValue ? $"{s.DefaultPctOfScored.Value * 100:F2}%" : "&mdash;";
            string scIfrs9 = s.ScoredInIfrs9.HasValue ? $"{s.ScoredInIfrs9.Value:N0}" : "&mdash;";
            sb.AppendLine($"          <tr><td><b>{WebUtility.HtmlEncode(k)}</b></td><td class='num'>{s.ScoredDistinct:N0}</td><td class='num'>{s.DefaultsDistinct:N0}</td><td class='num'>{defPct}</td><td class='num'>{s.WriteOffDistinct:N0}</td><td class='num'>{s.Ifrs9Distinct:N0}</td><td class='num'>{s.ScoredInWriteOff:N0}</td><td class='num'>{scIfrs9}</td></tr>");
        }
        sb.AppendLine("        </tbody>");
        sb.AppendLine("      </table>");
        sb.AppendLine("    </div>");

        // Engine PD by Bucket Table
        sb.AppendLine("    <div class='card'>");
        sb.AppendLine("      <h2>Engine Model Outputs — PD by Bucket</h2>");
        sb.AppendLine("      <table>");
        sb.AppendLine("        <thead><tr><th>Bucket</th>");
        foreach (string k in sets)
        {
            sb.AppendLine($"          <th class='num'>{WebUtility.HtmlEncode(k)}<br><span class='hz'>hazard</span></th><th class='num'>{WebUtility.HtmlEncode(k)}<br><span class='hz'>cohort</span></th>");
        }
        sb.AppendLine("        </tr></thead>");
        sb.AppendLine("        <tbody>");
        for (int b = 0; b < 6; b++)
        {
            string absorb = b >= 4 ? " <span class='chip'>absorbing</span>" : "";
            sb.Append($"          <tr><th>Bucket {b + 1}{absorb}</th>");
            foreach (string k in sets)
            {
                var hz = results[k].Engine.HazardRateMatrix;
                var co = results[k].Engine.CohortMatrix;
                double? hv = (hz != null && hz.Count > b && hz[b].Count > 4) ? hz[b][4] : null;
                double? cv = (co != null && co.Count > b && co[b].Count > 4) ? co[b][4] : null;

                string bar = hv.HasValue ? $"<div class='bar'><span style='width:{Math.Min(hv.Value, 1.0) * 100:F1}%'></span></div>" : "";
                sb.Append($"<td class='num'>{AccountUtils.Pct(hv)}{bar}</td><td class='num'>{AccountUtils.Pct(cv)}</td>");
            }
            sb.AppendLine("</tr>");
        }
        sb.AppendLine("        </tbody>");
        sb.AppendLine("      </table>");

        // LGD term structure — event types across sets, by days since default
        List<string> eventTypes = new();
        List<int> termDays = new();
        foreach (string k in sets)
        {
            foreach (var (ev, points) in results[k].Engine.Lgd)
            {
                if (!eventTypes.Contains(ev)) eventTypes.Add(ev);
                foreach (EngineLgdTermPoint p in points)
                {
                    if (p.TermDays.HasValue && !termDays.Contains(p.TermDays.Value)) termDays.Add(p.TermDays.Value);
                }
            }
        }
        termDays.Sort();

        sb.AppendLine("      <h3>LGD term structure</h3>");
        if (eventTypes.Count > 0 && termDays.Count > 0)
        {
            sb.AppendLine("      <p class='note'>Loss given default by days since default, as produced by the engine &mdash; recovery is effectively exhausted by 60&ndash;90 days.</p>");
            sb.AppendLine("      <table>");
            sb.Append("        <thead><tr><th>Set</th><th>Event type</th>");
            foreach (int t in termDays) sb.Append($"<th class='num'>{t}&nbsp;days</th>");
            sb.AppendLine("</tr></thead>");
            sb.AppendLine("        <tbody>");
            foreach (string k in sets)
            {
                var lgd = results[k].Engine.Lgd;
                foreach (string ev in eventTypes)
                {
                    if (!lgd.TryGetValue(ev, out var points)) continue;
                    Dictionary<int, double?> byTerm = new();
                    foreach (EngineLgdTermPoint p in points)
                    {
                        if (p.TermDays.HasValue) byTerm[p.TermDays.Value] = p.Value;
                    }
                    sb.Append($"          <tr><td><b>{WebUtility.HtmlEncode(k)}</b></td><td>{WebUtility.HtmlEncode(ev)}</td>");
                    foreach (int t in termDays)
                    {
                        string cell = byTerm.TryGetValue(t, out double? v) && v.HasValue ? AccountUtils.Pct(v) : "&mdash;";
                        sb.Append($"<td class='num'>{cell}</td>");
                    }
                    sb.AppendLine("</tr>");
                }
            }
            sb.AppendLine("        </tbody>");
            sb.AppendLine("      </table>");
        }
        else
        {
            sb.AppendLine("      <p class='note'>No LGD term structure published in scenario.json.</p>");
        }
        sb.AppendLine("    </div>");

        // Per-set detailed previews
        Dictionary<string, Dictionary<string, int[][]>> migData = new();
        foreach (string k in sets)
        {
            SingleSetResult r = results[k];
            sb.AppendLine("    <div class='card'>");
            sb.AppendLine($"      <h2>Set Detail: {WebUtility.HtmlEncode(k)} <span class='sub'>{WebUtility.HtmlEncode(r.Summary.Label)}</span></h2>");

            // Untraced preview
            sb.AppendLine("      <h3 style='font-size:14px; margin:12px 0 8px 0;'>Top Untraced Defaults</h3>");
            sb.AppendLine("      <table>");
            sb.AppendLine("        <thead><tr><th>Account</th><th>Cohort Date</th><th class='num'>Rating</th><th class='num'>Default Amount</th></tr></thead>");
            sb.AppendLine("        <tbody>");
            var topUntraced = r.Untraced.Take(12).ToList();
            if (topUntraced.Count > 0)
            {
                foreach (DefaultAccountRecord u in topUntraced)
                {
                    sb.AppendLine($"          <tr><td>{WebUtility.HtmlEncode(u.AccountNumber)}</td><td>{WebUtility.HtmlEncode(u.CohortDate)}</td><td class='num'>{WebUtility.HtmlEncode(u.Rating)}</td><td class='num'>{AccountUtils.Money(u.DefaultAmount)}</td></tr>");
                }
            }
            else
            {
                sb.AppendLine("          <tr><td colspan='4' class='sub'>No untraced defaults.</td></tr>");
            }
            sb.AppendLine("        </tbody>");
            sb.AppendLine("      </table>");

            // Where the engine last had the in-window exceptions. Share is over
            // every in-window row (not just those with a known bucket), matching
            // the WoInWindowBucket4Pct convention used in the summary.
            List<WriteOffNotDefaultRecord> inWindow = r.WoNd
                .Where(w => w.WriteOffVsScoringWindow == "IN WINDOW")
                .ToList();

            var buckets = inWindow
                .Where(w => !string.IsNullOrWhiteSpace(w.LastBucketRating))
                .GroupBy(w => w.LastBucketRating!.Trim())
                .Select(g => new { Bucket = g.Key, Count = g.Count(), Value = g.Sum(w => w.WriteOffAmount) })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Bucket, StringComparer.Ordinal)
                .ToList();

            if (buckets.Count > 0)
            {
                sb.AppendLine("      <h3 style='font-size:14px; margin:16px 0 8px 0;'>Where The Engine Last Had These Accounts</h3>");
                sb.AppendLine("      <p class='note'>Bucket&nbsp;4 is the worst non-default bucket, so a concentration there means the accounts were written off straight out of bucket&nbsp;4 without ever being moved to the default state.</p>");
                sb.AppendLine("      <table style='max-width:520px'>");
                sb.AppendLine("        <thead><tr><th>Last bucket seen</th><th class='num'>Accounts</th><th class='num'>Share</th><th class='num'>Value written off</th></tr></thead>");
                sb.AppendLine("        <tbody>");
                foreach (var b in buckets)
                {
                    double share = inWindow.Count > 0 ? (double)b.Count / inWindow.Count * 100.0 : 0.0;
                    sb.AppendLine($"          <tr><td>Bucket {WebUtility.HtmlEncode(b.Bucket)}</td><td class='num'>{b.Count:N0}</td><td class='num'>{share:F1}%</td><td class='num'>{AccountUtils.Money(b.Value)}</td></tr>");
                }
                sb.AppendLine("        </tbody>");
                sb.AppendLine("      </table>");
            }

            // Check 2 preview
            sb.AppendLine("      <h3 style='font-size:14px; margin:16px 0 8px 0;'>Write-Offs Not Defaulted (Top Exceptions)</h3>");
            sb.AppendLine("      <table>");
            sb.AppendLine("        <thead><tr><th>Account</th><th class='num'>Write-Off Amount</th><th>Last Write-Off Date</th><th>Window Status</th><th class='num'>Last Bucket Rating</th></tr></thead>");
            sb.AppendLine("        <tbody>");
            var inw = r.WoNd.Where(w => w.WriteOffVsScoringWindow == "IN WINDOW").Take(12).ToList();
            if (inw.Count == 0) inw = r.WoNd.Take(12).ToList();

            if (inw.Count > 0)
            {
                foreach (WriteOffNotDefaultRecord w in inw)
                {
                    string clsHtml = w.WriteOffVsScoringWindow == "IN WINDOW" ? $"<span class='flag red'>{w.WriteOffVsScoringWindow}</span>" : $"<span class='flag grey'>{w.WriteOffVsScoringWindow}</span>";
                    string dateStr = w.LastWriteOffDate.HasValue ? w.LastWriteOffDate.Value.ToString("yyyy-MM-dd") : "";
                    sb.AppendLine($"          <tr><td>{WebUtility.HtmlEncode(w.AccountNumber)}</td><td class='num'>{AccountUtils.Money(w.WriteOffAmount)}</td><td>{dateStr}</td><td>{clsHtml}</td><td class='num'>{WebUtility.HtmlEncode(w.LastBucketRating ?? "&mdash;")}</td></tr>");
                }
            }
            else
            {
                sb.AppendLine("          <tr><td colspan='5' class='sub'>No exceptions found.</td></tr>");
            }
            sb.AppendLine("        </tbody>");
            sb.AppendLine("      </table>");

            // Set keys carry spaces and dots ("JUN2026 0.5PCT"), so DOM ids and
            // JS arguments get a sanitised form while the reader still sees the key.
            string slug = Slug(k);
            List<(int Year, int Month)> periods = MigrationMatrixBuilder.PeriodsOf(r.Mig);
            bool hasMig = r.Mig.RawCounts.Count > 0;

            sb.AppendLine("      <div class='grid2'>");

            // Monthly account movements
            sb.AppendLine("        <div>");
            sb.AppendLine("          <h3>Bucket migration matrix &mdash; From (row) &rarr; To (column)</h3>");
            sb.AppendLine("          <p class='note'>Accounts moving between rating buckets 1&ndash;6, rebuilt from pd_scored. Pick a month and switch between counts and row&nbsp;%. Diagonal = stayed put. The &quot;All months&quot; total reconciles cell-for-cell to the engine&#39;s accumulated arrays in debug.json.</p>");
            if (hasMig)
            {
                Dictionary<string, int[][]> byPeriod = new()
                {
                    ["All months"] = ToJagged(MigrationMatrixBuilder.MatrixForPeriod(r.Mig))
                };
                foreach (var (y, mo) in periods)
                {
                    byPeriod[$"{y:D4}-{mo:D2}"] = ToJagged(MigrationMatrixBuilder.MatrixForPeriod(r.Mig, y, mo));
                }
                migData[slug] = byPeriod;

                sb.AppendLine("          <div class='migctl'>");
                sb.Append($"            <label>Month&nbsp;<select id='sel_{slug}' onchange=\"renderHeat('{slug}')\">");
                foreach (string p in byPeriod.Keys) sb.Append($"<option>{WebUtility.HtmlEncode(p)}</option>");
                sb.AppendLine("</select></label>");
                sb.AppendLine($"            <span class='tg'><button id='c_{slug}' class='on' onclick=\"setMode('{slug}','count')\">Counts</button><button id='p_{slug}' onclick=\"setMode('{slug}','pct')\">Row&nbsp;%</button></span>");
                sb.AppendLine("            <span class='note'>Row&nbsp;% = chance of moving from the row bucket to each column bucket (rows sum to 100%).</span>");
                sb.AppendLine("          </div>");
                sb.AppendLine($"          <div id='heat_{slug}' class='heatwrap'></div>");
            }
            else
            {
                sb.AppendLine("          <p class='note'>No PD-scored movements available.</p>");
            }
            sb.AppendLine("        </div>");

            sb.AppendLine("        <div>");
            sb.AppendLine("          <h3>Monthly account movements</h3>");
            if (hasMig && periods.Count > 0)
            {
                sb.AppendLine("          <table class='mini'>");
                sb.AppendLine("            <thead><tr><th>Month</th><th class='num'>Migrations</th></tr></thead>");
                sb.AppendLine("            <tbody>");
                foreach (var (y, mo) in periods)
                {
                    int[,] pm = MigrationMatrixBuilder.MatrixForPeriod(r.Mig, y, mo);
                    int total = 0;
                    for (int i = 0; i < 6; i++)
                        for (int j = 0; j < 6; j++) total += pm[i, j];
                    sb.AppendLine($"              <tr><td>{y:D4}-{mo:D2}</td><td class='num'>{total:N0}</td></tr>");
                }
                sb.AppendLine("            </tbody>");
                sb.AppendLine("          </table>");
            }
            else
            {
                sb.AppendLine("          <p class='note'>No monthly movements available.</p>");
            }
            sb.AppendLine("        </div>");
            sb.AppendLine("      </div>");

            // Engine hazard-rate matrix (full 6x6 from scenario.json)
            sb.AppendLine("      <h3>Engine hazard-rate matrix (scenario.json)</h3>");
            sb.AppendLine("      <p class='note'>The model&#39;s own fitted transition probabilities. Column&nbsp;5 is the default state, column&nbsp;6 is closed/settled; buckets 5 and 6 are absorbing.</p>");
            var hazard = r.Engine.HazardRateMatrix;
            if (hazard != null && hazard.Count >= 6)
            {
                sb.AppendLine("      <div class='heatwrap'>");
                sb.AppendLine("        <table class='heat pctm'>");
                sb.Append("          <thead><tr><th></th>");
                for (int j = 1; j <= 6; j++) sb.Append($"<th>To {j}</th>");
                sb.AppendLine("</tr></thead>");
                sb.AppendLine("          <tbody>");
                for (int i = 0; i < 6; i++)
                {
                    sb.Append($"            <tr><th>From {i + 1}</th>");
                    for (int j = 0; j < 6; j++)
                    {
                        double v = hazard[i].Count > j ? hazard[i][j] : 0.0;
                        double alpha = v <= 0 ? 0.0 : 0.10 + 0.80 * Math.Min(v, 1.0);
                        string diag = i == j ? "outline:2px solid #2f6fb0;outline-offset:-2px;" : "";
                        string txt = v == 0 ? "&ndash;" : (v >= 0.0001 ? $"{v * 100:F2}%" : "&lt;0.01%");
                        sb.Append($"<td style='background:rgba(106,81,148,{alpha:F2});{diag}'>{txt}</td>");
                    }
                    sb.AppendLine("</tr>");
                }
                sb.AppendLine("          </tbody>");
                sb.AppendLine("        </table>");
                sb.AppendLine("      </div>");
            }
            else
            {
                sb.AppendLine("      <p class='note'>scenario.json not found for this set.</p>");
            }

            sb.AppendLine("    </div>");
        }

        sb.AppendLine("  </div>");

        // Client-side migration heat map: the matrices are embedded as JSON and
        // re-rendered on demand so the file stays self-contained (no assets).
        sb.AppendLine("  <script>");
        sb.AppendLine("    const MIG = " + JsonSerializer.Serialize(migData) + ";");
        sb.AppendLine("    const MODE = {};");
        sb.AppendLine(@"    function renderHeat(k){
      var sel=document.getElementById('sel_'+k); if(!sel) return;
      var m=MIG[k][sel.value]; var mode=MODE[k]||'count';
      var mx=1,i,j; for(i=0;i<6;i++)for(j=0;j<6;j++){if(m[i][j]>mx)mx=m[i][j];}
      var h='<table class=""heat""><thead><tr><th></th>';
      for(j=1;j<=6;j++)h+='<th>To '+j+'</th>';
      h+='<th>Row total</th></tr></thead><tbody>';
      for(i=0;i<6;i++){
        var rs=0; for(j=0;j<6;j++)rs+=m[i][j];
        h+='<tr><th>From '+(i+1)+'</th>';
        for(j=0;j<6;j++){
          var v=m[i][j], a, txt;
          if(mode==='pct'){var p=rs?v/rs*100:0; a=p/100; txt=rs?p.toFixed(1)+'%':'–';}
          else {a=(v===0)?0:0.12+0.78*(v/mx); txt=v.toLocaleString();}
          var diag=(i===j)?'outline:2px solid #2f6fb0;outline-offset:-2px;':'';
          h+='<td style=""background:rgba(47,111,176,'+a.toFixed(2)+');'+diag+'"">'+txt+'</td>';
        }
        h+='<td class=""rt"">'+rs.toLocaleString()+'</td></tr>';
      }
      h+='</tbody></table>';
      document.getElementById('heat_'+k).innerHTML=h;
    }
    function setMode(k,m){
      MODE[k]=m;
      document.getElementById('c_'+k).classList.toggle('on',m==='count');
      document.getElementById('p_'+k).classList.toggle('on',m==='pct');
      renderHeat(k);
    }
    function initHeat(){Object.keys(MIG).forEach(function(k){MODE[k]='count';renderHeat(k);});}
    if(document.readyState==='loading'){document.addEventListener('DOMContentLoaded',initHeat);}else{initHeat();}");
        sb.AppendLine("  </script>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    public static string RenderDashboardAndSave(string outdir, Dictionary<string, SingleSetResult> results, string? analysisMd = null, Action<string, string>? log = null)
    {
        string filename = "reconciliation_dashboard.html";
        string path = Path.Combine(outdir, filename);

        string html = RenderDashboard(results, analysisMd);
        File.WriteAllText(path, html, Encoding.UTF8);

        log?.Invoke($"dashboard written: {filename}", LogKind.Ok);
        return filename;
    }

    /// <summary>
    /// Set keys are folder-derived ("JUN2026 0.5PCT"), so they are not safe as DOM
    /// ids or JS string literals. Collapse to alphanumerics and underscores.
    /// </summary>
    private static string Slug(string key)
    {
        StringBuilder sb = new(key.Length);
        foreach (char c in key) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        string s = sb.ToString();
        return string.IsNullOrEmpty(s) ? "set" : s;
    }

    private static int[][] ToJagged(int[,] m)
    {
        int[][] rows = new int[6][];
        for (int i = 0; i < 6; i++)
        {
            rows[i] = new int[6];
            for (int j = 0; j < 6; j++) rows[i][j] = m[i, j];
        }
        return rows;
    }
}
