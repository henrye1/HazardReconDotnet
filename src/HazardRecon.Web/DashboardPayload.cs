using System.Text.Json.Serialization;
using HazardRecon.Core.Helpers;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;

namespace HazardRecon.Web;

/// <summary>One row of the last-bucket-seen census: where the engine last saw an account.</summary>
public record LastBucketRow(
    [property: JsonPropertyName("bucket")] string Bucket,
    [property: JsonPropertyName("accounts")] int Accounts,
    [property: JsonPropertyName("share")] double Share,
    [property: JsonPropertyName("amount")] string Amount);

/// <summary>A default that could not be traced, for the detail table.</summary>
public record UntracedRow(
    [property: JsonPropertyName("account")] string Account,
    [property: JsonPropertyName("cohort_date")] string CohortDate,
    [property: JsonPropertyName("rating")] string Rating,
    [property: JsonPropertyName("amount")] string Amount);

/// <summary>A write-off with no default flag, for the exceptions table.</summary>
public record WoExceptionRow(
    [property: JsonPropertyName("account")] string Account,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("window")] string Window,
    [property: JsonPropertyName("last_bucket")] string LastBucket);

/// <summary>LGD by days since default, one row per event type.</summary>
public record LgdRow(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("values")] List<double?> Values);

/// <summary>
/// Everything the run detail's dashboard tab draws, for one set.
///
/// The engine already computes all of this; it just never left the process. It is
/// captured at run time rather than recomputed on demand because rebuilding the
/// migration matrix needs pd_scored.csv, an input, and inputs are purged after
/// 30 days while runs are kept.
/// </summary>
public record DashboardSet
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>"All months" first, then each period the scored file covers.</summary>
    [JsonPropertyName("months")]
    public List<string> Months { get; init; } = new();

    /// <summary>Month name to a 6x6 matrix of account movements, from bucket to bucket.</summary>
    [JsonPropertyName("migration")]
    public Dictionary<string, List<List<int>>> Migration { get; init; } = new();

    /// <summary>Movements per month, in the order the months are listed.</summary>
    [JsonPropertyName("monthly_totals")]
    public List<int> MonthlyTotals { get; init; } = new();

    /// <summary>The model's own fitted transition probabilities, from scenario.json.</summary>
    [JsonPropertyName("hazard")]
    public List<List<double>>? Hazard { get; init; }

    [JsonPropertyName("cohort")]
    public List<List<double>>? Cohort { get; init; }
    [JsonPropertyName("lgd")]
    public List<LgdRow> Lgd { get; init; } = new();

    // the census, and the two check tables' remaining columns
    [JsonPropertyName("scored_in_writeoff")]
    public int ScoredInWriteOff { get; init; }
    [JsonPropertyName("scored_in_ifrs9")]
    public int? ScoredInIfrs9 { get; init; }
    [JsonPropertyName("defaults_distinct")]
    public int DefaultsDistinct { get; init; }
    [JsonPropertyName("writeoff_distinct")]
    public int WriteOffDistinct { get; init; }
    [JsonPropertyName("ifrs9_distinct")]
    public int Ifrs9Distinct { get; init; }
    [JsonPropertyName("wo_pre_window")]
    public int WoPreWindow { get; init; }
    [JsonPropertyName("default_pct_of_scored")]
    public double? DefaultPctOfScored { get; init; }

    [JsonPropertyName("last_buckets")]
    public List<LastBucketRow> LastBuckets { get; init; } = new();
    [JsonPropertyName("top_untraced")]
    public List<UntracedRow> TopUntraced { get; init; } = new();
    [JsonPropertyName("wo_exceptions")]
    public List<WoExceptionRow> WoExceptions { get; init; } = new();
}

public static class DashboardPayload
{
    /// <summary>How many rows the two detail tables show. The reference lists 12 and 4.</summary>
    public const int TopUntracedRows = 12;
    public const int TopWoExceptionRows = 10;

    private const int Buckets = 6;

    public static DashboardSet Build(string key, SingleSetResult set)
    {
        ReconciliationSummary s = set.Summary;

        return new DashboardSet
        {
            Key = key,
            Label = s.Label,
            Months = MonthNames(set.Mig),
            Migration = Matrices(set.Mig),
            MonthlyTotals = MonthlyTotals(set.Mig),
            Hazard = set.Engine.HazardRateMatrix,
            Cohort = set.Engine.CohortMatrix,
            Lgd = Lgd(set.Engine),
            ScoredInWriteOff = s.ScoredInWriteOff,
            ScoredInIfrs9 = s.ScoredInIfrs9,
            DefaultsDistinct = s.DefaultsDistinct,
            WriteOffDistinct = s.WriteOffDistinct,
            Ifrs9Distinct = s.Ifrs9Distinct,
            WoPreWindow = s.WoPreWindow,
            DefaultPctOfScored = s.DefaultPctOfScored,
            LastBuckets = LastBuckets(set.WoNd),
            TopUntraced = set.Untraced.Take(TopUntracedRows).Select(u => new UntracedRow(
                u.AccountNumber, u.CohortDate, u.Rating, AccountUtils.Money(u.DefaultAmount))).ToList(),
            WoExceptions = WoExceptions(set.WoNd),
        };
    }

    /// <summary>The aggregate first, then each month, so a selector reads in that order.</summary>
    private static List<string> MonthNames(MigrationMatrixResult mig)
    {
        if (mig.RawCounts.Count == 0) return new List<string>();

        List<string> names = new() { "All months" };
        names.AddRange(MigrationMatrixBuilder.PeriodsOf(mig).Select(p => $"{p.Year:D4}-{p.Month:D2}"));
        return names;
    }

    private static Dictionary<string, List<List<int>>> Matrices(MigrationMatrixResult mig)
    {
        Dictionary<string, List<List<int>>> byMonth = new();
        if (mig.RawCounts.Count == 0) return byMonth;

        byMonth["All months"] = Rows(MigrationMatrixBuilder.MatrixForPeriod(mig));
        foreach (var (year, month) in MigrationMatrixBuilder.PeriodsOf(mig))
            byMonth[$"{year:D4}-{month:D2}"] = Rows(MigrationMatrixBuilder.MatrixForPeriod(mig, year, month));

        return byMonth;
    }

    private static List<int> MonthlyTotals(MigrationMatrixResult mig)
    {
        List<int> totals = new();
        if (mig.RawCounts.Count == 0) return totals;

        foreach (var (year, month) in MigrationMatrixBuilder.PeriodsOf(mig))
        {
            int[,] m = MigrationMatrixBuilder.MatrixForPeriod(mig, year, month);
            int total = 0;
            for (int i = 0; i < Buckets; i++)
                for (int j = 0; j < Buckets; j++) total += m[i, j];
            totals.Add(total);
        }
        return totals;
    }

    private static List<List<int>> Rows(int[,] m)
    {
        List<List<int>> rows = new();
        for (int i = 0; i < Buckets; i++)
        {
            List<int> row = new();
            for (int j = 0; j < Buckets; j++) row.Add(m[i, j]);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// LGD points come keyed by term in days. The table shows fixed columns, so each
    /// event type is flattened to the terms actually present, in ascending order.
    /// </summary>
    private static List<LgdRow> Lgd(EngineScenario engine)
    {
        List<int> terms = engine.Lgd.Values
            .SelectMany(points => points)
            .Where(p => p.TermDays.HasValue)
            .Select(p => p.TermDays!.Value)
            .Distinct().OrderBy(t => t).ToList();

        List<LgdRow> rows = new();
        foreach (var (name, points) in engine.Lgd)
        {
            List<double?> values = terms
                .Select(t => points.FirstOrDefault(p => p.TermDays == t)?.Value)
                .ToList();
            rows.Add(new LgdRow(name, values));
        }
        return rows;
    }

    /// <summary>
    /// Where the in-window write-offs were last seen. A concentration in the worst
    /// non-default bucket means accounts were written off without ever defaulting.
    /// </summary>
    private static List<LastBucketRow> LastBuckets(List<WriteOffNotDefaultRecord> woNd)
    {
        List<WriteOffNotDefaultRecord> inWindow = woNd
            .Where(w => w.WriteOffVsScoringWindow == "IN WINDOW" && !string.IsNullOrWhiteSpace(w.LastBucketRating))
            .ToList();
        if (inWindow.Count == 0) return new List<LastBucketRow>();

        return inWindow
            .GroupBy(w => w.LastBucketRating!.Trim())
            .OrderBy(g => g.Key)
            .Select(g => new LastBucketRow(
                "Bucket " + g.Key,
                g.Count(),
                Math.Round((double)g.Count() / inWindow.Count * 100.0, 1),
                AccountUtils.Money(g.Sum(w => w.WriteOffAmount))))
            .ToList();
    }

    /// <summary>The largest in-window exceptions - the ones worth chasing first.</summary>
    private static List<WoExceptionRow> WoExceptions(List<WriteOffNotDefaultRecord> woNd)
    {
        return woNd
            .Where(w => w.WriteOffVsScoringWindow == "IN WINDOW")
            .OrderByDescending(w => w.WriteOffAmount)
            .Take(TopWoExceptionRows)
            .Select(w => new WoExceptionRow(
                w.AccountNumber,
                AccountUtils.Money(w.WriteOffAmount),
                w.LastWriteOffDate?.ToString("dd MMM yyyy") ?? "",
                w.WriteOffVsScoringWindow,
                w.LastBucketRating?.Trim() ?? ""))
            .ToList();
    }
}
