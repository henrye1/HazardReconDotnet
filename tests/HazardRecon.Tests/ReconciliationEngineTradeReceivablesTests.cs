using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

/// <summary>
/// End to end over a receivables book. Every file in the run is keyed on the
/// customer number, and these are the tests that catch a file left keyed on
/// something else - which shows up as a plausible zero rather than as an error.
///
/// Its own fixture rather than an extension of SyntheticDataFixture, whose exact
/// counts are pinned by four other suites and whose hand-computed CohortNlambda
/// would have to be recomputed cell by cell. debug.json here carries no
/// AccumulatedArrays, so matrix validation reports N/A and nothing needs
/// hand-computing.
/// </summary>
public class ReconciliationEngineTradeReceivablesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "hr-engine-tr-tests", Guid.NewGuid().ToString("N")[..8]);

    private readonly string _setDir;
    private readonly string _outDir;
    private readonly Dictionary<string, SetColumnMaps> _maps;
    private readonly Dictionary<string, SetIdentity> _identities;

    private const string Key = "JUN2026";

    public ReconciliationEngineTradeReceivablesTests()
    {
        _setDir = Path.Combine(_root, "set0");
        _outDir = Path.Combine(_root, "out");
        Directory.CreateDirectory(_setDir);
        Directory.CreateDirectory(_outDir);

        // C1 holds two defaulted accounts - one customer, one default, summed.
        // C2 holds one.
        File.WriteAllText(Path.Combine(_setDir, "lgd_defaults.csv"),
            "AccountNumber,ClientNumber,EventType,CohortDate,Bucket,Rating,Amount\n" +
            "A1,C1,Lifetime,2026-05-31,0,5,100.0\n" +
            "A2,C1,Lifetime,2026-05-31,0,5,200.0\n" +
            "A9,C2,Lifetime,2026-05-31,0,5,300.0\n");

        // C1 and C2 are scored, and C3 is scored but never defaulted - C3 is the
        // write-off exception check 2 should find.
        File.WriteAllText(Path.Combine(_setDir, "pd_scored.csv"),
            "AccountNumber,ClientNumber,Category1,ReportDate,BucketRating,NextBucketRating,DeltaLambda\n" +
            "A1,C1,Loans,2026-02-28,1,2,0.1\n" +
            "A9,C2,Loans,2026-02-28,2,3,0.1\n" +
            "A5,C3,Loans,2026-02-28,4,5,0.1\n");

        // The bank's age analysis, in the exposure slot's canonical name: one row
        // per customer, with the balance across aging buckets and no account number.
        File.WriteAllText(Path.Combine(_setDir, "IFRS9.csv"),
            "Client,Current,30 Days,60 Days,90 Days\n" +
            "C1,10,0,120,180\n" +
            "C2,30,0,100,200\n");

        // One write-off against C1, one against C3 which never defaulted.
        File.WriteAllText(Path.Combine(_setDir, "writeoff.csv"),
            "LoanAccountNumber,CustomerId,Amount,ReportDate\n" +
            "A1,C1,300,2026-02-15\n" +
            "A5,C3,500,2026-02-20\n");

        File.WriteAllText(Path.Combine(_setDir, "debug.json"),
            "{\"Parameters\":{\"PdMinDate\":\"2026-01-01\",\"PdMaxDate\":\"2026-06-30\"}}");
        File.WriteAllText(Path.Combine(_setDir, "scenario.json"), "{}");

        SetIdentity identity = new(Key, "AGE ANALYSIS JUNE 2026");
        _identities = new Dictionary<string, SetIdentity> { [_setDir] = identity };
        _maps = new Dictionary<string, SetColumnMaps>
        {
            [Key] = new SetColumnMaps(
                // the write-off file's own column names, mapped for a receivables run
                WriteOff: new ColumnMap(true, new Dictionary<string, string>
                {
                    ["LoanAccountNumber"] = "LoanAccountNumber",
                    ["CustomerId"] = "CustomerId",
                    ["Amount"] = "Amount",
                    ["ReportDate"] = "ReportDate"
                }),
                Exposure: new ColumnMap(true, new Dictionary<string, IReadOnlyList<string>>
                {
                    ["ClientNumber"] = new[] { "Client" },
                    // the user's rule: 60 and 90 days are in default, current and
                    // 30 days are not
                    ["AgingBuckets"] = new[] { "60 Days", "90 Days" }
                }))
        };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SingleSetResult Run()
    {
        ReconciliationEngine engine = new();
        return engine.Run(
            new List<string> { _setDir }, _outDir,
            logger: (_, _) => { }, analyze: false, analyst: null, stages: null,
            columnMaps: _maps, setIdentities: _identities,
            runType: EngineRunType.TradeReceivables).Results[Key];
    }

    [Fact]
    public void TestOneCustomerIsOneDefaultWithItsAmountsSummed()
    {
        SingleSetResult res = Run();

        // two customers, not three accounts
        Assert.Equal(2, res.Summary.TotalDefaults);
        // C1's two accounts summed, plus C2
        Assert.Equal(600.0, res.Summary.TotalExposure);
        Assert.Equal(300.0, res.Full.Single(f => f.AccountNumber == "C1").DefaultAmount);
    }

    /// <summary>
    /// The trap: if the write-off were still keyed on the loan account number it
    /// would share no key with the defaults, nothing would trace, and the run would
    /// report a plausible 0%.
    /// </summary>
    [Fact]
    public void TestTheWriteOffTracesOnTheCustomerNumber()
    {
        SingleSetResult res = Run();

        Assert.Equal(1, res.Summary.TracedWriteOff);
        DefaultAccountRecord c1 = res.Full.Single(f => f.AccountNumber == "C1");
        Assert.True(c1.InWriteOff);
        Assert.Equal(300.0, c1.WriteOffAmount);
    }

    [Fact]
    public void TestTheAgeAnalysisTracesOnTheCustomerNumber()
    {
        SingleSetResult res = Run();

        Assert.Equal(2, res.Summary.TracedIfrs9);
        Assert.Equal(2, res.Summary.Ifrs9KeyOverlap);
        Assert.Equal(0, res.Summary.UntracedTotal);
    }

    [Fact]
    public void TestTheExposureIsTheSumOfTheSelectedBucketsOnly()
    {
        SingleSetResult res = Run();

        // 120 + 180, and 100 + 200 - current and 30 days are excluded
        Assert.Equal(300.0, res.Full.Single(f => f.AccountNumber == "C1").Ifrs9AmountOutstanding);
        Assert.Equal(300.0, res.Full.Single(f => f.AccountNumber == "C2").Ifrs9AmountOutstanding);
    }

    /// <summary>
    /// The worst trap of all: with the scored population and the defaults keyed
    /// differently, check 2 finds nothing, and "no exceptions" is then reported as a
    /// clean run that ties out.
    /// </summary>
    [Fact]
    public void TestCheck2StillFindsACustomerWrittenOffButNeverDefaulted()
    {
        SingleSetResult res = Run();

        Assert.Equal(1, res.Summary.WoNotDefaultTotal);
        WriteOffNotDefaultRecord exception = Assert.Single(res.WoNd);
        Assert.Equal("C3", exception.AccountNumber);
        Assert.Equal(500.0, exception.WriteOffAmount);

        // and C1, which did default, is not reported as an exception
        Assert.DoesNotContain("C1", res.WoNd.Select(w => w.AccountNumber));
    }

    /// <summary>
    /// If pd_scored were left keyed on the account while the write-off moved to the
    /// customer, this would be null and the workbook would then claim a bucket-4
    /// figure it never had.
    /// </summary>
    [Fact]
    public void TestTheWriteOffExceptionKeepsItsLastSeenBucket()
    {
        SingleSetResult res = Run();

        WriteOffNotDefaultRecord exception = Assert.Single(res.WoNd);
        Assert.Equal("4", exception.LastBucketRating);
        Assert.Equal("2026-02-28", exception.LastScoredDate);
    }

    [Fact]
    public void TestEveryPopulationIsCountedPerCustomer()
    {
        ReconciliationSummary summary = Run().Summary;

        Assert.Equal(3, summary.ScoredDistinct);      // C1, C2, C3
        Assert.Equal(2, summary.WriteOffDistinct);    // C1, C3
        Assert.Equal(2, summary.DefaultsDistinct);    // C1, C2
        Assert.Equal(2, summary.Ifrs9Distinct);       // C1, C2
        Assert.Equal(2, summary.ScoredInWriteOff);    // C1 and C3
    }

    [Fact]
    public void TestAMissingClientNumberFailsTheRunRatherThanReportingZero()
    {
        File.WriteAllText(Path.Combine(_setDir, "lgd_defaults.csv"),
            "AccountNumber,EventType,CohortDate,Bucket,Rating,Amount\n" +
            "A1,Lifetime,2026-05-31,0,5,100.0\n");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Run());

        Assert.Contains("ClientNumber", ex.Message);
    }
}
