using HazardRecon.Web.Runs;

namespace HazardRecon.Web.Runs;

/// <summary>
/// Rebuilds the aggregates /api/chat sends to the model, for a run that is no
/// longer in this process's job cache - one reopened from history, or any run at
/// all after a restart, since that cache only ever holds runs discovered by this
/// process.
///
/// This is what the normalization migration intended when it dropped
/// runs.analysis_payload: "analysis_payload.sets[] were two more serializations
/// of the same per-set aggregate ... they collapse into one run_set_results
/// table plus shared child tables". Nothing new is stored; the same figures are
/// read back out.
///
/// Must match AiAnalysisService.BuildAnalysisPayload key for key, or a question
/// would be answered from a different shape before and after a restart.
/// StoredAnalysisPayloadTests pins the two against each other through
/// RunSetResultMapper, so a change to either side fails there rather than in
/// front of a user.
///
/// One documented divergence: the live histogram files an in-window write-off
/// with no last bucket under "unknown", while run_set_last_bucket_rows does not
/// store those rows at all, so they cannot come back. The stored histogram is
/// therefore the same minus any unknown-bucket entry.
/// </summary>
internal static class StoredAnalysisPayload
{
    private const string AllMonths = "All months";
    private const string BucketPrefix = "Bucket ";

    public static Dictionary<string, object> Build(RunRecord run)
    {
        List<Dictionary<string, object?>> sets = (run.RunSetResults ?? new())
            .OrderBy(r => r.Id)
            .Select(BuildSet)
            .ToList();

        return new Dictionary<string, object> { ["sets"] = sets };
    }

    private static Dictionary<string, object?> BuildSet(RunSetResultRecord r) => new()
    {
        ["key"] = r.SetKey,
        ["label"] = r.Label,
        ["window"] = r.Window,
        ["defaults"] = r.TotalDefaults,
        ["default_exposure"] = r.TotalExposure,
        ["traced_writeoff"] = r.TracedWriteOff,
        ["traced_ifrs9"] = r.TracedIfrs9,
        ["untraced"] = r.UntracedTotal,
        ["untraced_exposure"] = r.UntracedExposure,
        ["untraced_fully_recovered"] = r.UntracedFullyRecovered,
        ["untraced_fully_recovered_amount"] = r.UntracedFullyRecoveredAmount,
        ["trace_rate"] = r.TraceRate,
        ["check2_total"] = r.WoNotDefaultTotal,
        ["check2_in_window"] = r.WoInWindow,
        ["check2_in_window_amount"] = r.WoInWindowAmount,
        ["check2_post_window"] = r.WoPostWindow,
        ["check2_pre_window"] = r.WoPreWindow,
        ["in_window_last_bucket_hist"] = Histogram(r),
        ["scored_distinct"] = r.ScoredDistinct,
        ["writeoff_distinct"] = r.WriteOffDistinct,
        ["ifrs9_distinct"] = r.Ifrs9Distinct,
        ["ifrs9_key_overlap"] = r.Ifrs9KeyOverlap,
        ["migration_matrix"] = Matrix(r),
        ["migration_validation"] = r.MigValidation,
        ["engine_params"] = EngineParams(r)
    };

    /// <summary>
    /// Bucket rating to in-window count. The stored rows label the bucket
    /// ("Bucket 5") where the live payload keys on the rating alone ("5"), so the
    /// prefix comes back off.
    /// </summary>
    private static Dictionary<string, int> Histogram(RunSetResultRecord r)
    {
        Dictionary<string, int> hist = new();

        foreach (LastBucketRowRecord row in (r.LastBucketRows ?? new()).OrderBy(x => x.Position))
        {
            string bucket = row.Bucket.StartsWith(BucketPrefix, StringComparison.Ordinal)
                ? row.Bucket[BucketPrefix.Length..]
                : row.Bucket;

            hist[bucket] = row.Accounts;
        }

        return hist;
    }

    /// <summary>
    /// The all-months matrix, which is the one the live payload carries -
    /// MatrixForPeriod over the whole window. Stored per month, so the other
    /// months' cells are skipped here. Null when the run recorded no cells, matching
    /// a live run with no migration data.
    /// </summary>
    private static Dictionary<string, object>? Matrix(RunSetResultRecord r)
    {
        List<MigrationCellRecord> cells = (r.MigrationCells ?? new())
            .Where(c => c.MonthLabel == AllMonths)
            .ToList();

        if (cells.Count == 0) return null;

        int[,] m = new int[6, 6];
        foreach (MigrationCellRecord c in cells)
        {
            // stored 1..6 to match the domain, so back to zero-based here
            if (c.FromBucket is >= 1 and <= 6 && c.ToBucket is >= 1 and <= 6)
            {
                m[c.FromBucket - 1, c.ToBucket - 1] = c.Count;
            }
        }

        List<List<int>> counts = new();
        for (int i = 0; i < 6; i++)
        {
            List<int> row = new();
            for (int j = 0; j < 6; j++) row.Add(m[i, j]);
            counts.Add(row);
        }

        return new Dictionary<string, object>
        {
            ["buckets"] = new List<int> { 1, 2, 3, 4, 5, 6 },
            ["from_to_counts"] = counts
        };
    }

    /// <summary>
    /// The engine's own parameters. Stored one row per key with the value as JSON,
    /// which serializes back exactly as the live dictionary's boxed value did.
    /// </summary>
    private static Dictionary<string, object?> EngineParams(RunSetResultRecord r)
    {
        Dictionary<string, object?> pars = new();
        foreach (EngineParamRecord p in r.EngineParams ?? new()) pars[p.ParamKey] = p.ParamValue;
        return pars;
    }
}
