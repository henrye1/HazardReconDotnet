using System.Text.Json;
using HazardRecon.Core.Models;
using HazardRecon.Web;

namespace HazardRecon.Web.Runs;

/// <summary>
/// Builds one set's row for public.run_set_results, plus every child row, from
/// the same SingleSetResult that used to be serialized three ways (result.sets,
/// result.dashboard_sets, analysis_payload.sets). DashboardPayload.Build already
/// derives the months/migration/lgd/detail-row shapes correctly, so this reuses
/// it rather than re-deriving them from the engine output a second time.
/// </summary>
public static class RunSetResultMapper
{
    public static RunSetResultRecord Build(Guid runId, Guid userId, string key, SingleSetResult set)
    {
        ReconciliationSummary s = set.Summary;
        DashboardSet dash = DashboardPayload.Build(key, set);

        RunSetResultRecord rec = new()
        {
            RunId = runId,
            UserId = userId,
            SetKey = key,
            Label = s.Label,
            Window = s.Window,
            TotalDefaults = s.TotalDefaults,
            TotalExposure = s.TotalExposure,
            TracedWriteOff = s.TracedWriteOff,
            TracedIfrs9 = s.TracedIfrs9,
            TracedTotal = s.TracedTotal,
            UntracedTotal = s.UntracedTotal,
            TracedExposure = s.TracedExposure,
            UntracedExposure = s.UntracedExposure,
            TraceRate = s.TraceRate,
            Ifrs9KeyOverlap = s.Ifrs9KeyOverlap,
            Ifrs9Rows = s.Ifrs9Rows,
            Ifrs9File = s.Ifrs9File,
            WoNotDefaultTotal = s.WoNotDefaultTotal,
            WoNotDefaultAmount = s.WoNotDefaultAmount,
            WoInWindow = s.WoInWindow,
            WoInWindowAmount = s.WoInWindowAmount,
            WoPreWindow = s.WoPreWindow,
            WoPostWindow = s.WoPostWindow,
            ScoredInWriteOff = s.ScoredInWriteOff,
            ScoredInIfrs9 = s.ScoredInIfrs9,
            WoInWindowBucket4 = s.WoInWindowBucket4,
            WoInWindowBucket4Amount = s.WoInWindowBucket4Amount,
            WoInWindowBucket4Pct = s.WoInWindowBucket4Pct,
            MigValidation = s.MigValidation,
            MigValidationMaxDiff = s.MigValidationMaxDiff,
            ScoredDistinct = s.ScoredDistinct,
            WriteOffDistinct = s.WriteOffDistinct,
            Ifrs9Distinct = s.Ifrs9Distinct,
            DefaultsDistinct = s.DefaultsDistinct,
            DefaultPctOfScored = s.DefaultPctOfScored,
            PdRows = s.PdRows,
            UntracedFullyRecovered = s.UntracedFullyRecovered,
            UntracedFullyRecoveredAmount = s.UntracedFullyRecoveredAmount
        };

        rec.MigrationCells = MigrationCells(dash);
        rec.MonthlyTotals = MonthlyTotals(dash);
        rec.HazardMatrix = dash.Hazard == null ? new() : MatrixOf(dash.Hazard, (r, c, v) => new HazardMatrixCellRecord { RowIdx = r, ColIdx = c, Value = v });
        rec.CohortMatrix = dash.Cohort == null ? new() : MatrixOf(dash.Cohort, (r, c, v) => new CohortMatrixCellRecord { RowIdx = r, ColIdx = c, Value = v });

        rec.LgdPoints = set.Engine.Lgd
            .SelectMany(kv => kv.Value.Where(p => p.TermDays.HasValue)
                .Select(p => new LgdPointRecord { EventName = kv.Key, TermDays = p.TermDays!.Value, Value = p.Value }))
            .ToList();

        rec.LastBucketRows = LastBucketRows(set.WoNd);
        rec.UntracedRows = set.Untraced.Take(DashboardPayload.TopUntracedRows)
            .Select((u, i) => new UntracedRowRecord
            {
                Account = u.AccountNumber, CohortDate = u.CohortDate, Rating = u.Rating,
                Amount = u.DefaultAmount, Position = i
            })
            .ToList();
        rec.WoExceptionRows = WoExceptionRows(set.WoNd);

        rec.EngineParams = set.Engine.Params
            .Select(kv => new EngineParamRecord
            {
                ParamKey = kv.Key,
                ParamValue = JsonSerializer.SerializeToElement(kv.Value)
            })
            .ToList();

        return rec;
    }

    /// <summary>
    /// OutputFiles.Describe's list (memo, workbook, dashboard, then each set's own
    /// CSVs), tagged with the set each file belongs to - null for the three
    /// run-level ones - so the completion RPC can wire up run_set_result_id.
    /// </summary>
    public static List<RunOutputFileRecord> BuildOutputFiles(
        Guid runId, Guid userId, string outdir, ReconciliationRunResult result)
    {
        List<OutputFile> files = OutputFiles.Describe(outdir, result);

        Dictionary<string, string> owningSet = new();
        foreach ((string key, SingleSetResult set) in result.Results)
        {
            foreach (string name in set.Summary.Files) owningSet.TryAdd(name, key);
        }

        return files.Select((f, i) => new RunOutputFileRecord
        {
            RunId = runId,
            UserId = userId,
            SetKey = owningSet.TryGetValue(f.Name, out string? key) ? key : null,
            Name = f.Name,
            Bytes = f.Bytes,
            Position = i
        }).ToList();
    }

    /// <summary>Every cell of every month's 6x6 matrix, buckets numbered 1..6 to match the domain.</summary>
    private static List<MigrationCellRecord> MigrationCells(DashboardSet dash)
    {
        List<MigrationCellRecord> cells = new();
        foreach ((string month, List<List<int>> rows) in dash.Migration)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                for (int j = 0; j < rows[i].Count; j++)
                {
                    cells.Add(new MigrationCellRecord
                    {
                        MonthLabel = month, FromBucket = (short)(i + 1), ToBucket = (short)(j + 1), Count = rows[i][j]
                    });
                }
            }
        }
        return cells;
    }

    /// <summary>Months, in the order dash.Months lists them (skipping "All months").</summary>
    private static List<MonthlyTotalRecord> MonthlyTotals(DashboardSet dash)
    {
        List<string> periods = dash.Months.Skip(1).ToList();
        List<MonthlyTotalRecord> totals = new();
        for (int i = 0; i < dash.MonthlyTotals.Count && i < periods.Count; i++)
        {
            totals.Add(new MonthlyTotalRecord { MonthLabel = periods[i], Total = dash.MonthlyTotals[i], Position = i });
        }
        return totals;
    }

    /// <summary>
    /// Mirrors DashboardPayload.LastBuckets - kept separate because that method is
    /// private and its record's Amount is a formatted string, not the raw figure
    /// this table stores.
    /// </summary>
    private static List<LastBucketRowRecord> LastBucketRows(List<WriteOffNotDefaultRecord> woNd)
    {
        List<WriteOffNotDefaultRecord> inWindow = woNd
            .Where(w => w.WriteOffVsScoringWindow == "IN WINDOW" && !string.IsNullOrWhiteSpace(w.LastBucketRating))
            .ToList();

        return inWindow
            .GroupBy(w => w.LastBucketRating!.Trim())
            .OrderBy(g => g.Key)
            .Select((g, i) => new LastBucketRowRecord
            {
                Bucket = "Bucket " + g.Key, Accounts = g.Count(), Amount = g.Sum(w => w.WriteOffAmount), Position = i
            })
            .ToList();
    }

    /// <summary>Mirrors DashboardPayload.WoExceptions - see LastBucketRows for why this is separate.</summary>
    private static List<WoExceptionRowRecord> WoExceptionRows(List<WriteOffNotDefaultRecord> woNd)
    {
        return woNd
            .Where(w => w.WriteOffVsScoringWindow == "IN WINDOW")
            .OrderByDescending(w => w.WriteOffAmount)
            .Take(DashboardPayload.TopWoExceptionRows)
            .Select((w, i) => new WoExceptionRowRecord
            {
                Account = w.AccountNumber,
                Amount = w.WriteOffAmount,
                WoDate = w.LastWriteOffDate.HasValue ? DateOnly.FromDateTime(w.LastWriteOffDate.Value) : null,
                Window = w.WriteOffVsScoringWindow,
                LastBucket = w.LastBucketRating?.Trim() ?? "",
                Position = i
            })
            .ToList();
    }

    private static List<T> MatrixOf<T>(List<List<double>> matrix, Func<short, short, double, T> make)
    {
        List<T> cells = new();
        for (int i = 0; i < matrix.Count; i++)
        {
            for (int j = 0; j < matrix[i].Count; j++)
            {
                cells.Add(make((short)i, (short)j, matrix[i][j]));
            }
        }
        return cells;
    }
}
