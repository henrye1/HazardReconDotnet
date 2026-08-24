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
        "A1,C1,Loans,2026-01-31,1,2,0.1\n" +
        "A2,C1,Loans,2026-02-28,2,3,0.1\n";

    /// <summary>
    /// The scored population has to be keyed the same way as the defaults, or Check 1
    /// and Check 2 compare different populations and both quietly find nothing.
    /// </summary>
    [Fact]
    public void TestTheScoredPopulationIsKeyedOnTheCustomer()
    {
        string path = WriteFile("pd_scored.csv", WithClient);

        MigrationMatrixResult result = _builder.BuildMigrationMatrix(
            path, null, null, EngineRunType.TradeReceivables);

        // two rows, one customer
        Assert.Equal(new[] { "C1" }, result.ScoredAccts);
        Assert.Equal(1, result.ScoredDistinct);
    }

    [Fact]
    public void TestALendingRunKeepsAccountKeys()
    {
        string path = WriteFile("pd_scored.csv", WithClient);

        MigrationMatrixResult result = _builder.BuildMigrationMatrix(path);

        Assert.Equal(new[] { "A1", "A2" }, result.ScoredAccts.Order());
    }

    /// <summary>
    /// LastRating feeds the bucket-4 concentration figure in the workbook and the
    /// dashboard. It is looked up by the write-off population, so it must be keyed
    /// the same way - keyed differently it silently returns nothing and the report
    /// then asserts a figure it never had.
    /// </summary>
    [Fact]
    public void TestLastRatingIsFoundByTheSameKeyTheWriteOffUses()
    {
        string path = WriteFile("pd_scored.csv", WithClient);

        MigrationMatrixResult result = _builder.BuildMigrationMatrix(
            path, new HashSet<string> { "C1" }, null, EngineRunType.TradeReceivables);

        Assert.True(result.LastRating.ContainsKey("C1"));
        // the later report date wins, as it does for lending
        Assert.Equal("2026-02-28", result.LastRating["C1"].Date);
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

    /// <summary>
    /// The matrix is keyed by (year, month, from, to), not by account, so which
    /// column the rows are identified by must not move a single cell.
    /// </summary>
    [Fact]
    public void TestTheMatrixCellsAreUnaffectedByTheKeyColumn()
    {
        string path = WriteFile("pd_scored.csv", WithClient);

        MigrationMatrixResult lending = _builder.BuildMigrationMatrix(path);
        MigrationMatrixResult receivables = _builder.BuildMigrationMatrix(
            path, null, null, EngineRunType.TradeReceivables);

        Assert.Equal(lending.RawCounts, receivables.RawCounts);
        Assert.Equal(lending.RowsInRange, receivables.RowsInRange);
    }
}
