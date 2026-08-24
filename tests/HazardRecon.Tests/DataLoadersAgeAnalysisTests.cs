using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

public class DataLoadersAgeAnalysisTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "hr-ageanalysis-tests", Guid.NewGuid().ToString("N")[..8]);

    private readonly DataLoaders _loaders = new();

    public DataLoadersAgeAnalysisTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string content)
    {
        string path = Path.Combine(_dir, "age_analysis.csv");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>An age analysis carries a customer number and the aging columns - nothing else.</summary>
    private const string Headers = "Client,Current,30 Days,60 Days,90 Days\n";

    /// <summary>The mapping a user would confirm on the Map-columns step.</summary>
    private static ColumnMap Map(params string[] buckets) =>
        new(true, new Dictionary<string, IReadOnlyList<string>>
        {
            ["ClientNumber"] = new[] { "Client" },
            ["AgingBuckets"] = buckets
        });

    [Fact]
    public void TestOnlyTheSelectedBucketsAreSummed()
    {
        string path = WriteFile(Headers + "C1,100,200,300,400\n");

        SourceAccountsResult res = _loaders.LoadAgeAnalysis(path, "age", null, Map("60 Days", "90 Days"));

        // Current and 30 Days are deliberately not counted - the selection is the
        // user's definition of which buckets are in default
        Assert.Equal(700.0, res.AmountsPerAccount["C1"]);
    }

    [Fact]
    public void TestTheKeyIsTheCustomerNumber()
    {
        string path = WriteFile(Headers + "C1,10,0,0,0\nC2,20,0,0,0\n");

        SourceAccountsResult res = _loaders.LoadAgeAnalysis(path, "age", null, Map("Current"));

        Assert.Equal(new[] { "C1", "C2" }, res.AccountNumbers.Order());
        Assert.Equal(10.0, res.AmountsPerAccount["C1"]);
        Assert.Equal(20.0, res.AmountsPerAccount["C2"]);
    }

    /// <summary>
    /// An age analysis may carry more than one row for a customer, and both belong
    /// to the same debt - so they add rather than overwrite.
    /// </summary>
    [Fact]
    public void TestTwoRowsForOneCustomerAccumulate()
    {
        string path = WriteFile(Headers + "C1,10,0,0,0\nC1,15,0,0,0\n");

        SourceAccountsResult res = _loaders.LoadAgeAnalysis(path, "age", null, Map("Current"));

        Assert.Single(res.AccountNumbers);
        Assert.Equal(25.0, res.AmountsPerAccount["C1"]);
    }

    [Fact]
    public void TestTheCustomerNumberIsNormalised()
    {
        // the same float-mangling that puts ".0" on an account number
        string path = WriteFile(Headers + "606323.0,100,0,0,0\n");

        SourceAccountsResult res = _loaders.LoadAgeAnalysis(path, "age", null, Map("Current"));

        Assert.Equal(100.0, res.AmountsPerAccount["606323"]);
    }

    /// <summary>
    /// Amounts arrive space-separated here, and NumberStyles.Any does not accept
    /// that - so without stripping them a whole aging column reads as zero.
    /// </summary>
    [Fact]
    public void TestSpaceSeparatedThousandsAreRead()
    {
        string path = WriteFile(Headers + "C1,1 234.56,0,0,0\n");

        SourceAccountsResult res = _loaders.LoadAgeAnalysis(path, "age", null, Map("Current"));

        Assert.Equal(1234.56, res.AmountsPerAccount["C1"]);
    }

    [Fact]
    public void TestBlankBucketCellsCountAsZeroRatherThanFailing()
    {
        string path = WriteFile(Headers + "C1,,,300,\n");

        SourceAccountsResult res = _loaders.LoadAgeAnalysis(path, "age", null,
            Map("Current", "30 Days", "60 Days", "90 Days"));

        Assert.Equal(300.0, res.AmountsPerAccount["C1"]);
    }

    [Fact]
    public void TestAnUnreadableBucketValueIsReportedPerColumn()
    {
        string path = WriteFile(Headers + "C1,n/a,50,0,0\n");
        List<string> warnings = new();

        SourceAccountsResult res = _loaders.LoadAgeAnalysis(path, "age",
            (m, k) => { if (k == LogKind.Warn) warnings.Add(m); }, Map("Current", "30 Days"));

        Assert.Equal(50.0, res.AmountsPerAccount["C1"]);
        // named, so it is clear which of several summed columns was unreadable
        Assert.Contains(warnings, w => w.Contains("Current") && w.Contains("unreadable"));
    }

    [Fact]
    public void TestANegativeRowTotalIsWarnedAbout()
    {
        // "(50.00)" is a credit in accounting notation, and NumberStyles.Any reads
        // it as -50, which reduces the defaulted exposure
        string path = WriteFile(Headers + "C1,(50.00),0,0,0\n");
        List<string> warnings = new();

        SourceAccountsResult res = _loaders.LoadAgeAnalysis(path, "age",
            (m, k) => { if (k == LogKind.Warn) warnings.Add(m); }, Map("Current"));

        Assert.Equal(-50.0, res.AmountsPerAccount["C1"]);
        Assert.Contains(warnings, w => w.Contains("negative"));
    }

    /// <summary>
    /// With nothing selected every row sums to zero, check 1 still "traces" every
    /// default to a zero exposure, and the run reports figures that look real.
    /// </summary>
    [Fact]
    public void TestNoSelectedBucketsRefusesTheRun()
    {
        string path = WriteFile(Headers + "C1,100,0,0,0\n");
        ColumnMap map = new(true, new Dictionary<string, IReadOnlyList<string>>
        {
            ["ClientNumber"] = new[] { "Client" },
            ["AgingBuckets"] = Array.Empty<string>()
        });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            _loaders.LoadAgeAnalysis(path, "age", null, map));

        Assert.Contains("no aging bucket columns were selected", ex.Message);
    }

    [Fact]
    public void TestABucketThatIsAlsoTheKeyColumnRefusesTheRun()
    {
        string path = WriteFile(Headers + "C1,100,0,0,0\n");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            _loaders.LoadAgeAnalysis(path, "age", null, Map("Client", "Current")));

        // customer numbers are numeric here, so this would otherwise parse cleanly
        // into a large and completely wrong exposure
        Assert.Contains("Client", ex.Message);
        Assert.Contains("join key", ex.Message);
    }

    [Fact]
    public void TestAMissingBucketColumnRefusesTheRun()
    {
        string path = WriteFile(Headers + "C1,100,0,0,0\n");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            _loaders.LoadAgeAnalysis(path, "age", null, Map("Current", "120 Days")));

        Assert.Contains("120 Days", ex.Message);
        Assert.Contains("age_analysis.csv", ex.Message);
    }

    [Fact]
    public void TestAMissingCustomerColumnRefusesTheRun()
    {
        string path = WriteFile("Cust,Current\nC1,100\n");
        ColumnMap map = new(true, new Dictionary<string, IReadOnlyList<string>>
        {
            ["ClientNumber"] = new[] { "Client" },
            ["AgingBuckets"] = new[] { "Current" }
        });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            _loaders.LoadAgeAnalysis(path, "age", null, map));

        Assert.Contains("customer number", ex.Message);
    }

    [Fact]
    public void TestARowsWorthOfBlankKeysStillRefuses()
    {
        string path = WriteFile(Headers + ",100,0,0,0\n");

        Assert.Throws<InvalidOperationException>(() =>
            _loaders.LoadAgeAnalysis(path, "age", null, Map("Current")));
    }

    [Fact]
    public void TestAHeaderlessFileIsReadByColumnPosition()
    {
        string path = WriteFile("C1,100,200\n");
        ColumnMap map = new(false, new Dictionary<string, IReadOnlyList<string>>
        {
            ["ClientNumber"] = new[] { "0" },
            ["AgingBuckets"] = new[] { "1", "2" }
        });

        SourceAccountsResult res = _loaders.LoadAgeAnalysis(path, "age", null, map);

        Assert.Equal(300.0, res.AmountsPerAccount["C1"]);
    }

    [Fact]
    public void TestAMissingFileIsEmptyRatherThanThrown()
    {
        SourceAccountsResult res = _loaders.LoadAgeAnalysis(
            Path.Combine(_dir, "nope.csv"), "age", null, Map("Current"));

        Assert.Empty(res.AccountNumbers);
    }
}
