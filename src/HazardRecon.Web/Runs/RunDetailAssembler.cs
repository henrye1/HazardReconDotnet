using HazardRecon.Core.Helpers;

namespace HazardRecon.Web.Runs;

/// <summary>
/// Rebuilds the result/log JSON the run detail page has always consumed, from
/// the normalized rows a fully-embedded RunRecord carries. The frontend's
/// contract does not change - only where the server reads it from.
/// </summary>
public static class RunDetailAssembler
{
    public static object? BuildResult(RunRecord run)
    {
        if (run.RunSetResults == null || run.RunSetResults.Count == 0) return null;

        List<RunSetResultRecord> orderedSets = run.RunSetResults.OrderBy(r => r.Id).ToList();
        List<RunOutputFileRecord> outputFiles = (run.OutputFiles ?? new List<RunOutputFileRecord>())
            .OrderBy(f => f.Position).ToList();

        var sets = orderedSets.Select(r => new
        {
            key = r.SetKey,
            label = r.Label,
            window = r.Window,
            defaults = r.TotalDefaults,
            exposure = r.TotalExposure,
            exposure_fmt = AccountUtils.Money(r.TotalExposure),
            traced = r.TracedTotal,
            traced_writeoff = r.TracedWriteOff,
            traced_ifrs9 = r.TracedIfrs9,
            untraced = r.UntracedTotal,
            untraced_fmt = AccountUtils.Money(r.UntracedExposure),
            trace_rate = Math.Round(r.TraceRate * 100.0, 1),
            wo_total = r.WoNotDefaultTotal,
            wo_in_window = r.WoInWindow,
            wo_in_window_fmt = AccountUtils.Money(r.WoInWindowAmount),
            wo_pre_window = r.WoPreWindow,
            wo_post_window = r.WoPostWindow,
            scored = r.ScoredDistinct,
            ifrs9_overlap = r.Ifrs9KeyOverlap,
            mig_validation = r.MigValidation,
            mig_max_diff = r.MigValidationMaxDiff,
            files = outputFiles.Where(f => f.RunSetResultId == r.Id).Select(f => f.Name).ToList()
        }).ToList();

        return new
        {
            sets,
            workbook = run.Results?.WorkbookFilename,
            dashboard = run.Results?.DashboardFilename,
            memo = run.Results?.MemoFilename,
            dashboard_sets = orderedSets.Select(BuildDashboardSet).ToList(),
            commentary = (run.CommentaryLines ?? new List<RunCommentaryLineRecord>())
                .OrderBy(c => c.Position).Select(c => c.Line).ToList(),
            analysis = run.Results?.AnalysisMarkdown,
            model_id = run.ModelId,
            elapsed_seconds = run.StartedAt == null || run.FinishedAt == null
                ? (double?)null
                : Math.Round((run.FinishedAt.Value - run.StartedAt.Value).TotalSeconds, 1),
            outputs = outputFiles.Select(f => new { name = f.Name, bytes = f.Bytes }).ToList()
        };
    }

    public static List<object> BuildLog(RunRecord run) =>
        (run.Logs ?? new List<LogEntryRecord>())
            .OrderBy(l => l.Seq)
            .Select(l => (object)new
            {
                t = l.OccurredAt.ToLocalTime().ToString("HH:mm:ss"),
                msg = l.Message,
                kind = LogTypeLookup.CodeOf(l.TypeId)
            })
            .ToList();

    private static object BuildDashboardSet(RunSetResultRecord r)
    {
        List<MonthlyTotalRecord> monthlyTotals = (r.MonthlyTotals ?? new()).OrderBy(m => m.Position).ToList();

        List<string> months = new() { "All months" };
        months.AddRange(monthlyTotals.Select(m => m.MonthLabel));

        Dictionary<string, List<List<int>>> migration = (r.MigrationCells ?? new())
            .GroupBy(c => c.MonthLabel)
            .ToDictionary(g => g.Key, MigrationMatrixFromCells);

        List<int> lgdTerms = (r.LgdPoints ?? new()).Select(p => p.TermDays).Distinct().OrderBy(t => t).ToList();
        var lgd = (r.LgdPoints ?? new())
            .GroupBy(p => p.EventName)
            .Select(g => new
            {
                name = g.Key,
                values = lgdTerms.Select(t => g.FirstOrDefault(p => p.TermDays == t)?.Value).ToList()
            })
            .ToList();

        List<LastBucketRowRecord> lastBuckets = (r.LastBucketRows ?? new()).OrderBy(x => x.Position).ToList();
        int totalInWindow = lastBuckets.Sum(x => x.Accounts);

        return new
        {
            key = r.SetKey,
            label = r.Label,
            months,
            migration,
            monthly_totals = monthlyTotals.Select(m => m.Total).ToList(),
            hazard = BuildMatrix((r.HazardMatrix ?? new()).Select(c => (c.RowIdx, c.ColIdx, c.Value))),
            cohort = BuildMatrix((r.CohortMatrix ?? new()).Select(c => (c.RowIdx, c.ColIdx, c.Value))),
            lgd,
            scored_in_writeoff = r.ScoredInWriteOff,
            scored_in_ifrs9 = r.ScoredInIfrs9,
            defaults_distinct = r.DefaultsDistinct,
            writeoff_distinct = r.WriteOffDistinct,
            ifrs9_distinct = r.Ifrs9Distinct,
            wo_pre_window = r.WoPreWindow,
            default_pct_of_scored = r.DefaultPctOfScored,
            last_buckets = lastBuckets.Select(x => new
            {
                bucket = x.Bucket,
                accounts = x.Accounts,
                share = totalInWindow == 0 ? 0.0 : Math.Round((double)x.Accounts / totalInWindow * 100.0, 1),
                amount = AccountUtils.Money(x.Amount)
            }).ToList(),
            top_untraced = (r.UntracedRows ?? new()).OrderBy(x => x.Position).Select(x => new
            {
                account = x.Account, transaction = x.TransactionNumber,
                cohort_date = x.CohortDate, rating = x.Rating, amount = AccountUtils.Money(x.Amount)
            }).ToList(),
            wo_exceptions = (r.WoExceptionRows ?? new()).OrderBy(x => x.Position).Select(x => new
            {
                account = x.Account,
                amount = AccountUtils.Money(x.Amount),
                date = x.WoDate.HasValue ? x.WoDate.Value.ToString("dd MMM yyyy") : "",
                window = x.Window,
                last_bucket = x.LastBucket
            }).ToList()
        };
    }

    /// <summary>Rebuilds the 6x6 matrix for one month; from/to buckets are stored 1..6.</summary>
    private static List<List<int>> MigrationMatrixFromCells(IEnumerable<MigrationCellRecord> cells)
    {
        int[,] m = new int[6, 6];
        foreach (MigrationCellRecord c in cells) m[c.FromBucket - 1, c.ToBucket - 1] = c.Count;

        List<List<int>> rows = new();
        for (int i = 0; i < 6; i++)
        {
            List<int> row = new();
            for (int j = 0; j < 6; j++) row.Add(m[i, j]);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Rebuilds a jagged matrix from sparse (row, col, value) cells; null if there were none.</summary>
    private static List<List<double>>? BuildMatrix(IEnumerable<(short RowIdx, short ColIdx, double Value)> cellsQuery)
    {
        List<(short RowIdx, short ColIdx, double Value)> cells = cellsQuery.ToList();
        if (cells.Count == 0) return null;

        int rows = cells.Max(c => c.RowIdx) + 1;
        int cols = cells.Max(c => c.ColIdx) + 1;
        double[,] m = new double[rows, cols];
        foreach (var c in cells) m[c.RowIdx, c.ColIdx] = c.Value;

        List<List<double>> result = new();
        for (int i = 0; i < rows; i++)
        {
            List<double> row = new();
            for (int j = 0; j < cols; j++) row.Add(m[i, j]);
            result.Add(row);
        }
        return result;
    }
}
