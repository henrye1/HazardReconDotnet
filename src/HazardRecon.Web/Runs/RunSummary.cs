namespace HazardRecon.Web.Runs;

/// <summary>
/// The few figures the run list shows, folded over the run's own
/// run_set_results rows rather than recomputed or stored twice, so the list
/// cannot drift from what the run actually produced.
/// </summary>
public record RunSummary(int Sets, long Untraced, double TraceRate, long Exceptions)
{
    public static readonly RunSummary Empty = new(0, 0, 0, 0);

    public static RunSummary From(IReadOnlyList<RunSetResultRecord>? sets)
    {
        if (sets == null || sets.Count == 0) return Empty;

        long untraced = sets.Sum(s => (long)s.UntracedTotal);
        // trace_rate is stored as a 0..1 fraction; the list shows a percentage
        double rateTotal = sets.Sum(s => s.TraceRate * 100.0);
        // Check 2's in-window write-offs; the ones the run detail calls priority
        long exceptions = sets.Sum(s => (long)s.WoInWindow);

        return new RunSummary(sets.Count, untraced, Math.Round(rateTotal / sets.Count, 1), exceptions);
    }
}
