using System.Text.Json;

namespace HazardRecon.Web.Runs;

/// <summary>
/// The few figures the run list shows, pulled out of the stored result.
///
/// Read from the result already held on the row rather than recomputed or stored
/// twice, so the list cannot drift from what the run actually produced.
/// </summary>
public record RunSummary(int Sets, long Untraced, double TraceRate, long Exceptions)
{
    public static readonly RunSummary Empty = new(0, 0, 0, 0);

    public static RunSummary From(JsonElement? result)
    {
        if (result is not JsonElement root || root.ValueKind != JsonValueKind.Object) return Empty;
        if (!root.TryGetProperty("sets", out JsonElement sets) || sets.ValueKind != JsonValueKind.Array)
            return Empty;

        int count = 0;
        long untraced = 0;
        double rateTotal = 0;
        long exceptions = 0;

        foreach (JsonElement set in sets.EnumerateArray())
        {
            count++;

            if (set.TryGetProperty("untraced", out JsonElement u) &&
                u.ValueKind == JsonValueKind.Number && u.TryGetInt64(out long value))
            {
                untraced += value;
            }

            if (set.TryGetProperty("trace_rate", out JsonElement r) &&
                r.ValueKind == JsonValueKind.Number && r.TryGetDouble(out double rate))
            {
                rateTotal += rate;
            }

            // Check 2's in-window write-offs; the ones the run detail calls priority
            if (set.TryGetProperty("wo_in_window", out JsonElement w) &&
                w.ValueKind == JsonValueKind.Number && w.TryGetInt64(out long inWindow))
            {
                exceptions += inWindow;
            }
        }

        if (count == 0) return Empty;

        return new RunSummary(count, untraced, Math.Round(rateTotal / count, 1), exceptions);
    }
}
