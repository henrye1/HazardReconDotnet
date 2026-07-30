using System.Globalization;
using HazardRecon.Core.Exporters;
using HazardRecon.Core.Helpers;
using HazardRecon.Core.Models;

namespace HazardRecon.Core.Services;

public class ReconciliationEngine
{
    private readonly InputDiscoverer _discoverer = new();
    private readonly DataLoaders _dataLoaders = new();
    private readonly MigrationMatrixBuilder _matrixBuilder = new();

    public (List<DefaultAccountRecord> Full, List<DefaultAccountRecord> Untraced, ReconciliationSummary Summary) ReconcileDefaults(
        List<DefaultAccountRecord> defaults,
        HashSet<string> woAccts,
        HashSet<string> ifrs9Accts,
        List<WriteOffAggRecord>? woAgg = null,
        Dictionary<string, double>? ifrs9Amounts = null,
        Action<string, string>? log = null)
    {
        List<DefaultAccountRecord> full = new();

        Dictionary<string, double> woAmtMap = woAgg != null ? woAgg.ToDictionary(a => a.AccountNormalized, a => a.WriteOffAmount) : new();
        ifrs9Amounts ??= new Dictionary<string, double>();

        foreach (DefaultAccountRecord d in defaults)
        {
            DefaultAccountRecord rec = new()
            {
                AccountNumber = d.AccountNumber,
                AccountNormalized = d.AccountNormalized,
                CohortDate = d.CohortDate,
                Rating = d.Rating,
                DefaultAmount = d.DefaultAmount,
                MinLgdBalance = d.MinLgdBalance,
                LastObsBucket = d.LastObsBucket,
                LastOutstanding = d.LastOutstanding,
                RecoveredAmount = d.RecoveredAmount,
                RecoveryStatus = d.RecoveryStatus,
                InWriteOff = woAccts.Contains(d.AccountNormalized),
                InIFRS9 = ifrs9Accts.Contains(d.AccountNormalized)
            };

            if (rec.InWriteOff && rec.InIFRS9) rec.TraceSource = "Write-off + IFRS9";
            else if (rec.InWriteOff) rec.TraceSource = "Write-off";
            else if (rec.InIFRS9) rec.TraceSource = "IFRS9";
            else rec.TraceSource = "UNTRACED";

            if (woAmtMap.TryGetValue(rec.AccountNormalized, out double woVal)) rec.WriteOffAmount = woVal;
            if (ifrs9Amounts.TryGetValue(rec.AccountNormalized, out double ifrs9Val)) rec.Ifrs9AmountOutstanding = ifrs9Val;

            if (rec.InWriteOff) rec.TraceAmount = rec.WriteOffAmount;
            else if (rec.InIFRS9) rec.TraceAmount = rec.Ifrs9AmountOutstanding;

            if (rec.TraceAmount.HasValue) rec.LossVsTraceDiff = rec.MinLgdBalance - rec.TraceAmount.Value;

            full.Add(rec);
        }

        List<DefaultAccountRecord> traced = full.Where(f => f.Traced).ToList();
        List<DefaultAccountRecord> untraced = full.Where(f => !f.Traced).ToList();

        ReconciliationSummary summary = new()
        {
            TotalDefaults = full.Count,
            TotalExposure = full.Sum(f => f.DefaultAmount),
            TracedWriteOff = full.Count(f => f.InWriteOff),
            TracedIfrs9 = full.Count(f => f.InIFRS9),
            TracedTotal = traced.Count,
            UntracedTotal = untraced.Count,
            TracedExposure = traced.Sum(f => f.DefaultAmount),
            UntracedExposure = untraced.Sum(f => f.DefaultAmount),
            TraceRate = full.Count > 0 ? (double)traced.Count / full.Count : 0.0
        };

        log?.Invoke($"CHECK 1: {summary.TracedTotal:N0} traced / {summary.UntracedTotal:N0} UNTRACED ({summary.TraceRate * 100:F1}% traced)", "ok");
        return (full, untraced, summary);
    }

    public (List<WriteOffNotDefaultRecord> Records, ReconciliationSummary Summary) ReconcileWriteoffNotDefault(
        HashSet<string> scoredAccts,
        List<WriteOffAggRecord> woAgg,
        HashSet<string> defaultAccts,
        (DateTime? Lo, DateTime? Hi) window,
        Dictionary<string, (string Date, string Bucket)>? lastRating = null,
        Action<string, string>? log = null)
    {
        ReconciliationSummary summary = new();
        if (woAgg.Count == 0 || scoredAccts.Count == 0)
        {
            log?.Invoke("CHECK 2 skipped (no write-off file or no scored population)", "warn");
            return (new List<WriteOffNotDefaultRecord>(), summary);
        }

        (DateTime? lo, DateTime? hi) = window;
        lastRating ??= new();

        // woAgg is already one row per account; hash it once so the scored
        // population can be intersected in a single pass rather than rescanning
        // the write-off list for every scored account.
        HashSet<string> woAcctSet = woAgg.Select(w => w.AccountNormalized).ToHashSet();

        List<WriteOffAggRecord> candidates = woAgg
            .Where(w => scoredAccts.Contains(w.AccountNormalized) && !defaultAccts.Contains(w.AccountNormalized))
            .ToList();

        string windowStr = (lo.HasValue && hi.HasValue) ? $"{lo.Value:yyyy-MM-dd} to {hi.Value:yyyy-MM-dd}" : "";

        List<WriteOffNotDefaultRecord> records = new();
        foreach (WriteOffAggRecord c in candidates)
        {
            string classification = "UNDATED";
            if (c.LastWriteOffDate.HasValue)
            {
                if (lo.HasValue && c.LastWriteOffDate.Value < lo.Value) classification = "PRE-WINDOW";
                else if (hi.HasValue && c.LastWriteOffDate.Value > hi.Value) classification = "POST-WINDOW";
                else classification = "IN WINDOW";
            }

            string? lastScoredDate = null;
            string? lastBucketRating = null;
            if (lastRating.TryGetValue(c.AccountNormalized, out var lr))
            {
                lastScoredDate = lr.Date;
                lastBucketRating = lr.Bucket;
            }

            records.Add(new WriteOffNotDefaultRecord
            {
                AccountNumber = c.AccountNormalized,
                CustomerId = c.CustomerId,
                WriteOffAmount = c.WriteOffAmount,
                FirstWriteOffDate = c.FirstWriteOffDate,
                LastWriteOffDate = c.LastWriteOffDate,
                WriteOffVsScoringWindow = classification,
                LastScoredDate = lastScoredDate,
                LastBucketRating = lastBucketRating,
                ScoringWindow = windowStr
            });
        }

        records = records
            .OrderBy(r => r.WriteOffVsScoringWindow)
            .ThenByDescending(r => r.WriteOffAmount)
            .ToList();

        List<WriteOffNotDefaultRecord> inw = records.Where(r => r.WriteOffVsScoringWindow == "IN WINDOW").ToList();

        summary.WoNotDefaultTotal = records.Count;
        summary.WoNotDefaultAmount = records.Sum(r => r.WriteOffAmount);
        summary.WoInWindow = inw.Count;
        summary.WoInWindowAmount = inw.Sum(r => r.WriteOffAmount);
        summary.WoPreWindow = records.Count(r => r.WriteOffVsScoringWindow == "PRE-WINDOW");
        summary.WoPostWindow = records.Count(r => r.WriteOffVsScoringWindow == "POST-WINDOW");
        summary.ScoredInWriteOff = scoredAccts.Count(woAcctSet.Contains);

        log?.Invoke($"CHECK 2: {summary.WoNotDefaultTotal:N0} written off but never defaulted; {summary.WoInWindow:N0} IN WINDOW ({AccountUtils.Money(summary.WoInWindowAmount)})", "ok");
        return (records, summary);
    }

    public ReconciliationRunResult Run(object root, string outdir = "output", Action<string, string>? logger = null, bool analyze = false, AiAnalysisService? analyst = null, StageReporter? stages = null)
    {
        Directory.CreateDirectory(outdir);
        Action<string, string> log = (msg, kind) =>
        {
            if (logger != null) logger(msg, kind);
            else
            {
                string mark = kind switch { "tool" => "→", "ok" => "✓", "warn" => "!", "head" => "■", _ => " " };
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {mark} {msg}");
            }
        };

        // callers that do not care about progress get a reporter wired to nothing,
        // so the stage calls below need no null checks
        stages ??= new StageReporter();

        log("Hazard-rate reconciliation starting", "head");

        stages.Plan((StageKeys.Discover, "Read the analysis folders", "Find the write-off, defaults, scored and IFRS9 files in each folder"));
        stages.Begin(StageKeys.Discover);

        Inventory inv;
        try
        {
            inv = root is IEnumerable<string> folderList
                ? _discoverer.DiscoverFromFolders(folderList.ToList(), log)
                : _discoverer.DiscoverInputs(root.ToString()!, log);
        }
        catch
        {
            stages.End(StageKeys.Discover, StageStatus.Error);
            throw;
        }

        if (inv.Sets.Count == 0)
        {
            stages.End(StageKeys.Discover, StageStatus.Error);
            throw new InvalidOperationException("No analysis sets found. Each folder needs debug.zip (or an extracted lgd_defaults.csv).");
        }

        stages.End(StageKeys.Discover);

        // the per-set steps are only knowable now, so the plan arrives in two waves
        foreach (var (planKey, planSet) in inv.Sets) stages.Plan(StageKeys.ForSet(planKey, planSet.Label));
        stages.Plan(StageKeys.Tail(analyze));

        Dictionary<string, (List<WriteOffAggRecord> Agg, HashSet<string> Accts)> woCache = new();
        (List<WriteOffAggRecord> Agg, HashSet<string> Accts) GetWoFor(InventorySet setInfo)
        {
            string? path = setInfo.WriteOff ?? inv.WriteOff;
            if (path == null) return (new List<WriteOffAggRecord>(), new HashSet<string>());

            if (!woCache.TryGetValue(path, out var cached))
            {
                cached = _dataLoaders.LoadWriteoff(path, log);
                woCache[path] = cached;
            }
            return cached;
        }

        Dictionary<string, SingleSetResult> results = new();

        foreach (var (key, setInfo) in inv.Sets)
        {
            log($"===== {key}  ({setInfo.Label}) =====", "head");

            var (woAgg, woAccts, engine, defaults, ifrs9Res) = stages.Track(StageKeys.Load(key), () =>
            {
                var (agg, accts) = GetWoFor(setInfo);
                return (agg, accts,
                    _dataLoaders.LoadScenario(setInfo.Scenario, setInfo.DebugJson, log),
                    _dataLoaders.LoadDefaults(setInfo.LgdDefaults, log),
                    _dataLoaders.LoadSourceAccounts(setInfo.Ifrs9, "LoanAccountNumber", $"{key} IFRS9", "AmountOutstanding", log));
            });

            var (full, untraced, summary) = stages.Track(StageKeys.Check1(key), () =>
                ReconcileDefaults(defaults, woAccts, ifrs9Res.AccountNumbers, woAgg, ifrs9Res.AmountsPerAccount, log));

            summary.Ifrs9KeyOverlap = defaults.Select(d => d.AccountNormalized).Intersect(ifrs9Res.AccountNumbers).Count();
            summary.Ifrs9Rows = ifrs9Res.TotalRows;
            summary.Ifrs9File = !string.IsNullOrEmpty(setInfo.Ifrs9) ? Path.GetFileName(setInfo.Ifrs9) : "(missing)";

            MigrationMatrixResult mig;
            if (!string.IsNullOrEmpty(setInfo.PdScored))
            {
                mig = stages.Track(StageKeys.Migrations(key), () =>
                    _matrixBuilder.BuildMigrationMatrix(setInfo.PdScored, woAccts, log));
            }
            else
            {
                log("pd_scored.csv missing - migrations and check 2 limited", "warn");
                mig = new MigrationMatrixResult();
                // nothing to build, and check 2 will be partial - say so rather than
                // leaving a row that looks like it succeeded
                stages.End(StageKeys.Migrations(key), StageStatus.Warn);
            }

            HashSet<string> scored = mig.ScoredAccts;

            DateTime? pdMin = engine.Params.TryGetValue("PdMinDate", out object? pMinObj) && DateTime.TryParse(pMinObj?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime pMin) ? pMin : null;
            DateTime? pdMax = engine.Params.TryGetValue("PdMaxDate", out object? pMaxObj) && DateTime.TryParse(pMaxObj?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime pMax) ? pMax : null;

            var (woNd, woSum) = stages.Track(StageKeys.Check2(key), () =>
                ReconcileWriteoffNotDefault(scored, woAgg, defaults.Select(d => d.AccountNormalized).ToHashSet(), (pdMin, pdMax), mig.LastRating, log));

            // Merge Check 2 summary properties into main summary
            summary.WoNotDefaultTotal = woSum.WoNotDefaultTotal;
            summary.WoNotDefaultAmount = woSum.WoNotDefaultAmount;
            summary.WoInWindow = woSum.WoInWindow;
            summary.WoInWindowAmount = woSum.WoInWindowAmount;
            summary.WoPreWindow = woSum.WoPreWindow;
            summary.WoPostWindow = woSum.WoPostWindow;
            summary.ScoredInWriteOff = woSum.ScoredInWriteOff;

            MigrationValidationResult val = stages.Track(StageKeys.Validate(key), () =>
                _matrixBuilder.ReconcileMigration(mig, engine.CohortNlambda));
            summary.MigValidation = val.Status;
            summary.MigValidationMaxDiff = val.MaxAbsDiff;

            if (val.Status != "N/A")
            {
                log($"validation: rebuilt migration matrix vs debug.json CohortNlambda = {val.Status} (max cell diff {val.MaxAbsDiff})", val.Status == "PASS" ? "ok" : "warn");
            }

            // a failed comparison is a finding, not a crash - the row says so
            stages.End(StageKeys.Validate(key), val.Status switch
            {
                "PASS" => StageStatus.Done,
                "N/A" => StageStatus.Skipped,
                _ => StageStatus.Warn,
            });

            int b4Count = 0;
            double b4Val = 0.0;
            if (woNd.Count > 0)
            {
                List<WriteOffNotDefaultRecord> iw = woNd.Where(r => r.WriteOffVsScoringWindow == "IN WINDOW").ToList();
                if (iw.Count > 0)
                {
                    List<WriteOffNotDefaultRecord> b4Rows = iw.Where(r => (r.LastBucketRating ?? "").Trim() == "4").ToList();
                    b4Count = b4Rows.Count;
                    b4Val = b4Rows.Sum(r => r.WriteOffAmount);
                }
            }

            summary.WoInWindowBucket4 = b4Count;
            summary.WoInWindowBucket4Amount = b4Val;
            summary.WoInWindowBucket4Pct = summary.WoInWindow > 0 ? (double)b4Count / summary.WoInWindow * 100.0 : 0.0;

            summary.ScoredDistinct = scored.Count;
            summary.WriteOffDistinct = woAccts.Count;
            summary.Ifrs9Distinct = ifrs9Res.AccountNumbers.Count;
            summary.DefaultsDistinct = defaults.Count;
            summary.ScoredInIfrs9 = scored.Count > 0 ? scored.Intersect(ifrs9Res.AccountNumbers).Count() : null;
            summary.DefaultPctOfScored = scored.Count > 0 ? (double)defaults.Count / scored.Count : null;
            summary.PdRows = mig.RowsTotal;
            summary.Window = (pdMin.HasValue && pdMax.HasValue) ? $"{pdMin.Value:dd MMM yyyy} to {pdMax.Value:dd MMM yyyy}" : "n/a";
            summary.Label = setInfo.Label;

            List<DefaultAccountRecord> fully = untraced.Where(u => u.RecoveryStatus == "FULLY RECOVERED").ToList();
            summary.UntracedFullyRecovered = fully.Count;
            summary.UntracedFullyRecoveredAmount = fully.Sum(f => f.DefaultAmount);

            List<string> files = stages.Track(StageKeys.Export(key), () =>
                CsvExporter.ExportSet(outdir, key, untraced, full, woNd, mig));
            summary.Files = files;

            results[key] = new SingleSetResult
            {
                Defaults = defaults,
                Full = full,
                Untraced = untraced.OrderByDescending(u => u.DefaultAmount).ToList(),
                WoNd = woNd,
                Summary = summary,
                Mig = mig,
                Engine = engine
            };

            log($"{key} complete: {summary.UntracedTotal:N0} untraced defaults, {summary.WoInWindow:N0} in-window write-offs never defaulted", "ok");
        }

        string xlsx = stages.Track(StageKeys.Workbook, () => WorkbookExporter.ExportWorkbook(outdir, results, log));

        string? analysisMd = null;
        if (analyze)
        {
            if (analyst == null)
            {
                log("no model selected - skipping AI analysis", "warn");
                stages.End(StageKeys.Analysis, StageStatus.Skipped);
            }
            else
            {
                log("Generating AI analysis", "head");
                analysisMd = stages.Track(StageKeys.Analysis, () =>
                {
                    var payload = AiAnalysisService.BuildAnalysisPayload(results);
                    return analyst.GenerateAnalysis(payload, log);
                });
            }
        }

        string html = stages.Track(StageKeys.Dashboard, () =>
            DashboardRenderer.RenderDashboardAndSave(outdir, results, analysisMd, log));

        string? memo = null;
        if (!string.IsNullOrEmpty(analysisMd))
        {
            memo = stages.Track(StageKeys.Memo, () =>
                DocxExporter.WriteMemo(analysisMd, outdir, DateTime.Today.ToString("yyyy-MM-dd"), results.Values.Select(r => r.Summary.Label).ToList()));
            log($"analysis memo written: {memo}", "ok");
        }

        log("Reconciliation complete", "head");

        // anything planned but never reached is marked skipped rather than left pending
        stages.Settle(StageStatus.Done);

        return new ReconciliationRunResult
        {
            Results = results,
            Workbook = xlsx,
            Dashboard = html,
            Outdir = outdir,
            Memo = memo,
            Analysis = analysisMd
        };
    }
}
