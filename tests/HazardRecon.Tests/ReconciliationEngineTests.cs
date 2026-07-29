using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

public class ReconciliationEngineTests : IClassFixture<SyntheticDataFixture>
{
    private readonly SyntheticDataFixture _fixture;
    private readonly ReconciliationRunResult _runResult;

    public ReconciliationEngineTests(SyntheticDataFixture fixture)
    {
        _fixture = fixture;
        ReconciliationEngine engine = new();
        _runResult = engine.Run(_fixture.RootDir, _fixture.OutDir, analyze: false);
    }

    [Fact]
    public void TestDiscoversOneSetAndWriteoff()
    {
        Assert.Single(_runResult.Results);
        Assert.Contains("JUN2026 0.5PCT", _runResult.Results.Keys);
    }

    [Fact]
    public void TestCheck1TracesAndUntraced()
    {
        ReconciliationSummary s = _runResult.Results["JUN2026 0.5PCT"].Summary;
        Assert.Equal(3, s.TotalDefaults);
        Assert.Equal(1, s.TracedWriteOff);
        Assert.Equal(1, s.TracedIfrs9);
        Assert.Equal(2, s.TracedTotal);
        Assert.Equal(1, s.UntracedTotal);

        var untraced = _runResult.Results["JUN2026 0.5PCT"].Untraced;
        Assert.Single(untraced);
        Assert.Equal("A3", untraced[0].AccountNumber);
    }

    [Fact]
    public void TestCheck2ClassifiesAgainstScoringWindow()
    {
        var woNd = _runResult.Results["JUN2026 0.5PCT"].WoNd;
        var got = woNd.ToDictionary(r => r.AccountNumber, r => r.WriteOffVsScoringWindow);

        Assert.DoesNotContain("A1", got.Keys);
        Assert.Equal("IN WINDOW", got["A4"]);
        Assert.Equal("POST-WINDOW", got["A5"]);
        Assert.Equal("PRE-WINDOW", got["A6"]);

        ReconciliationSummary s = _runResult.Results["JUN2026 0.5PCT"].Summary;
        Assert.Equal(1, s.WoInWindow);
        Assert.Equal(1, s.WoPostWindow);
        Assert.Equal(1, s.WoPreWindow);
        Assert.Equal(400.0, s.WoInWindowAmount);
    }

    [Fact]
    public void TestCheck2ReportsLastBucketSeen()
    {
        var woNd = _runResult.Results["JUN2026 0.5PCT"].WoNd;
        var a4 = woNd.First(r => r.AccountNumber == "A4");
        Assert.Equal("4", a4.LastBucketRating);
        Assert.Equal("2026-02-28", a4.LastScoredDate);
    }

    [Fact]
    public void TestMigrationMatrixIgnoresOutOfRangeRows()
    {
        var mig = _runResult.Results["JUN2026 0.5PCT"].Mig;
        int[,] mat = MigrationMatrixBuilder.MatrixForPeriod(mig);

        Assert.Equal(1, mat[0, 1]); // A1: From 1 -> To 2
        Assert.Equal(1, mat[3, 4]); // A4: From 4 -> To 5
        Assert.Equal(2, mat[0, 0]); // A5 and A6: From 1 -> To 1

        int totalSum = 0;
        for (int i = 0; i < 6; i++)
            for (int j = 0; j < 6; j++)
                totalSum += mat[i, j];

        Assert.Equal(6, totalSum); // A7 with blank NextBucketRating is excluded
    }

    [Fact]
    public void TestMonthlyFrameRowsSumTo100Percent()
    {
        var mig = _runResult.Results["JUN2026 0.5PCT"].Mig;
        var mf = MigrationMatrixBuilder.BuildMonthlyFrame(mig);

        var liveRows = mf.Where(r => Convert.ToInt32(r["RowTotal"]) > 0).ToList();
        foreach (var row in liveRows)
        {
            double sumPct = 0.0;
            for (int t = 1; t <= 6; t++)
            {
                sumPct += Convert.ToDouble(row[$"To_{t}_%"]);
            }
            Assert.True(Math.Abs(sumPct - 100.0) < 0.05);
        }
    }

    [Fact]
    public void TestMigrationReconcilesToDebugJson()
    {
        ReconciliationSummary s = _runResult.Results["JUN2026 0.5PCT"].Summary;
        Assert.Equal("PASS", s.MigValidation);
        Assert.Equal(0, s.MigValidationMaxDiff);
    }

    [Fact]
    public void TestInWindowBucket4RootCauseMetric()
    {
        ReconciliationSummary s = _runResult.Results["JUN2026 0.5PCT"].Summary;
        Assert.Equal(1, s.WoInWindowBucket4);
        Assert.Equal(100.0, s.WoInWindowBucket4Pct);
    }

    [Fact]
    public void TestOutputFilesCreated()
    {
        Assert.True(File.Exists(Path.Combine(_fixture.OutDir, _runResult.Workbook)));
        Assert.True(File.Exists(Path.Combine(_fixture.OutDir, _runResult.Dashboard)));
        Assert.True(File.Exists(Path.Combine(_fixture.OutDir, "JUN2026 0.5PCT_untraced_defaults.csv")));
        Assert.True(File.Exists(Path.Combine(_fixture.OutDir, "JUN2026 0.5PCT_traced_defaults.csv")));
        Assert.True(File.Exists(Path.Combine(_fixture.OutDir, "JUN2026 0.5PCT_writeoff_not_default.csv")));
    }
}
