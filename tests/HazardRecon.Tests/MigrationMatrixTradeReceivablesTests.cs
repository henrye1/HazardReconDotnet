using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

public class MigrationMatrixTradeReceivablesTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "hr-migmatrix-tr-tests", Guid.NewGuid().ToString("N")[..8]);

    private readonly MigrationMatrixBuilder _builder = new();

    public MigrationMatrixTradeReceivablesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private const string WithClient =
        "AccountNumber,ClientNumber,Category1,ReportDate,BucketRating,NextBucketRating,DeltaLambda\n" +
        "A1,T1,Loans,2026-01-31,1,2,0.1\n" +
        "A1,T2,Loans,2026-02-28,2,3,0.1\n";

    [Fact]
    public void TestTheScoredPopulationIsHeldAtBothGrains()
    {
        string path = WriteFile("pd_scored.csv", WithClient);

        MigrationMatrixResult result = _builder.BuildMigrationMatrix(
            path, null, null, EngineRunType.TradeReceivables);

        // two transactions at join grain...
        Assert.Equal(2, result.ScoredAccts.Count);
        // ...on one account, which is what check 2 compares with the write-off file
        Assert.Equal(new[] { "A1" }, result.ScoredAccounts);
    }

    /// <summary>
    /// LastRating is keyed by account even for trade receivables, because its only
    /// consumer is check 2. Keyed composite it would never be found, and every
    /// write-off exception would silently report no last-seen bucket - which then
    /// makes the workbook assert a bucket-4 concentration from an empty dictionary.
    /// </summary>
    [Fact]
    public void TestLastRatingIsKeyedByAccountNotByTheJoinKey()
    {
        string path = WriteFile("pd_scored.csv", WithClient);

        MigrationMatrixResult result = _builder.BuildMigrationMatrix(
            path, new HashSet<string> { "A1" }, null, EngineRunType.TradeReceivables);

        Assert.True(result.LastRating.ContainsKey("A1"));
        // the later report date wins, as it does for lending
        Assert.Equal("2026-02-28", result.LastRating["A1"].Date);
    }

    [Fact]
    public void TestAMissingClientNumberRefusesTheRun()
    {
        string path = WriteFile("pd_scored.csv",
            "AccountNumber,Category1,ReportDate,BucketRating,NextBucketRating,DeltaLambda\n" +
            "A1,Loans,2026-01-31,1,2,0.1\n");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            _builder.BuildMigrationMatrix(path, null, null, EngineRunType.TradeReceivables));

        Assert.Contains("pd_scored.csv", ex.Message);
        Assert.Contains("ClientNumber", ex.Message);
    }

    [Fact]
    public void TestALendingRunKeepsAccountOnlyKeys()
    {
        string path = WriteFile("pd_scored.csv", WithClient);

        MigrationMatrixResult result = _builder.BuildMigrationMatrix(path);

        Assert.Equal(new[] { "A1" }, result.ScoredAccts);
        Assert.Equal(new[] { "A1" }, result.ScoredAccounts);
    }

    /// <summary>
    /// The matrix itself is keyed by (year, month, from, to), not by account, so
    /// the composite key must not change a single cell.
    /// </summary>
    [Fact]
    public void TestTheMatrixCellsAreUnaffectedByTheKeyGrain()
    {
        string path = WriteFile("pd_scored.csv", WithClient);

        MigrationMatrixResult lending = _builder.BuildMigrationMatrix(path);
        MigrationMatrixResult receivables = _builder.BuildMigrationMatrix(
            path, null, null, EngineRunType.TradeReceivables);

        Assert.Equal(lending.RawCounts, receivables.RawCounts);
        Assert.Equal(lending.RowsInRange, receivables.RowsInRange);
    }
}
