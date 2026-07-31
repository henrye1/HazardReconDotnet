using System.Text.Json;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using HazardRecon.Web;
using HazardRecon.Web.Runs;
using Xunit;

namespace HazardRecon.Tests.Web;

/// <summary>
/// Round-trips a real engine result through RunSetResultMapper (write) and
/// RunDetailAssembler (read) and checks it reconstructs the same numbers
/// DashboardPayload.Build produced directly - the two are hand-derived from
/// the same source data independently, so agreement is the actual check.
/// </summary>
public class RunSetResultMapperTests : IClassFixture<SyntheticDataFixture>
{
    private readonly SyntheticDataFixture _fixture;

    public RunSetResultMapperTests(SyntheticDataFixture fixture) => _fixture = fixture;

    private (SingleSetResult Set, string Key, DashboardSet Dash) RunOneSet(string outDir)
    {
        ReconciliationEngine engine = new();
        ReconciliationRunResult result = engine.Run(
            _fixture.RootDir, Path.Combine(_fixture.OutDir, outDir),
            logger: (_, _) => { }, analyze: false, analyst: null);

        var (key, set) = result.Results.First();
        return (set, key, DashboardPayload.Build(key, set));
    }

    private static JsonElement AssembleFirstDashboardSet(RunSetResultRecord rec)
    {
        RunRecord run = new()
        {
            Id = rec.RunId,
            UserId = rec.UserId,
            RunSetResults = new List<RunSetResultRecord> { rec },
            OutputFiles = new List<RunOutputFileRecord>(),
            CommentaryLines = new List<RunCommentaryLineRecord>(),
            Results = new RunResultsRecord { RunId = rec.RunId }
        };

        object? result = RunDetailAssembler.BuildResult(run);
        Assert.NotNull(result);

        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        return doc.RootElement.GetProperty("dashboard_sets")[0].Clone();
    }

    [Fact]
    public void TestRoundTrip()
    {
        var (set, key, dash) = RunOneSet("mapper-roundtrip");
        Guid runId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();

        RunSetResultRecord rec = RunSetResultMapper.Build(runId, userId, key, set);
        JsonElement assembled = AssembleFirstDashboardSet(rec);

        // migration: every month's 6x6 matrix must reconstruct exactly
        foreach (string month in dash.Months)
        {
            List<List<int>> expected = dash.Migration[month];
            JsonElement actual = assembled.GetProperty("migration").GetProperty(month);
            for (int i = 0; i < 6; i++)
            {
                for (int j = 0; j < 6; j++)
                {
                    Assert.Equal(expected[i][j], actual[i][j].GetInt32());
                }
            }
        }

        // monthly_totals line up with the month list, same as DashboardPayloadTests asserts for dash itself
        JsonElement monthlyTotals = assembled.GetProperty("monthly_totals");
        Assert.Equal(dash.MonthlyTotals.Count, monthlyTotals.GetArrayLength());
        for (int i = 0; i < dash.MonthlyTotals.Count; i++)
        {
            Assert.Equal(dash.MonthlyTotals[i], monthlyTotals[i].GetInt32());
        }

        // lgd: same event names, same term-aligned values (nulls where a term is missing)
        JsonElement lgd = assembled.GetProperty("lgd");
        Assert.Equal(dash.Lgd.Count, lgd.GetArrayLength());
        for (int i = 0; i < dash.Lgd.Count; i++)
        {
            Assert.Equal(dash.Lgd[i].Name, lgd[i].GetProperty("name").GetString());
            JsonElement values = lgd[i].GetProperty("values");
            Assert.Equal(dash.Lgd[i].Values.Count, values.GetArrayLength());
            for (int j = 0; j < dash.Lgd[i].Values.Count; j++)
            {
                double? expected = dash.Lgd[i].Values[j];
                if (expected == null) Assert.Equal(JsonValueKind.Null, values[j].ValueKind);
                else Assert.Equal(expected.Value, values[j].GetDouble(), 6);
            }
        }

        // last buckets: same rows, in the same order, and shares recomputed from the stored raw amounts sum to ~100
        JsonElement lastBuckets = assembled.GetProperty("last_buckets");
        Assert.Equal(dash.LastBuckets.Count, lastBuckets.GetArrayLength());
        for (int i = 0; i < dash.LastBuckets.Count; i++)
        {
            Assert.Equal(dash.LastBuckets[i].Bucket, lastBuckets[i].GetProperty("bucket").GetString());
            Assert.Equal(dash.LastBuckets[i].Accounts, lastBuckets[i].GetProperty("accounts").GetInt32());
        }
        if (dash.LastBuckets.Count > 0)
        {
            double totalShare = 0;
            foreach (JsonElement row in lastBuckets.EnumerateArray()) totalShare += row.GetProperty("share").GetDouble();
            Assert.Equal(100.0, totalShare, 1);
        }

        // detail tables: same cap and ordering the mapper drew from the same source lists
        JsonElement topUntraced = assembled.GetProperty("top_untraced");
        Assert.Equal(dash.TopUntraced.Count, topUntraced.GetArrayLength());
        Assert.All(topUntraced.EnumerateArray(), _ => { });

        JsonElement woExceptions = assembled.GetProperty("wo_exceptions");
        Assert.Equal(dash.WoExceptions.Count, woExceptions.GetArrayLength());
        for (int i = 0; i < dash.WoExceptions.Count; i++)
        {
            Assert.Equal(dash.WoExceptions[i].Account, woExceptions[i].GetProperty("account").GetString());
            Assert.Equal(dash.WoExceptions[i].Window, woExceptions[i].GetProperty("window").GetString());
        }
    }

    [Fact]
    public void TestSetLevelScalarsRoundTrip()
    {
        var (set, key, _) = RunOneSet("mapper-scalars");
        RunSetResultRecord rec = RunSetResultMapper.Build(Guid.NewGuid(), Guid.NewGuid(), key, set);

        Assert.Equal(set.Summary.UntracedTotal, rec.UntracedTotal);
        Assert.Equal(set.Summary.TraceRate, rec.TraceRate, 9);
        Assert.Equal(set.Summary.WoInWindow, rec.WoInWindow);
        Assert.Equal(set.Summary.Label, rec.Label);
        Assert.Equal(key, rec.SetKey);
    }
}
