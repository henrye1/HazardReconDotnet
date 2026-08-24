using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

/// <summary>
/// End to end over a receivables book. These are the tests that would catch the
/// mixed-grain traps: the defaults and the scored population are keyed on
/// (account, transaction) while the write-off file is account-only, so a missed
/// projection shows up here as a plausible zero rather than as a crash.
///
/// Its own fixture rather than an extension of SyntheticDataFixture, whose exact
/// counts are pinned by four other suites and whose hand-computed CohortNlambda
/// would have to be recomputed cell by cell. debug.json here carries no
/// AccumulatedArrays, so the matrix validation reports N/A and nothing needs
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

        // Two transactions on A1 both default; one on A2 defaults too.
        File.WriteAllText(Path.Combine(_setDir, "lgd_defaults.csv"),
            "AccountNumber,ClientNumber,EventType,CohortDate,Bucket,Rating,Amount\n" +
            "A1,T1,Lifetime,2026-05-31,0,5,100.0\n" +
            "A1,T2,Lifetime,2026-05-31,0,5,200.0\n" +
            "A2,T9,Lifetime,2026-05-31,0,5,300.0\n");

        // A1's two transactions are scored, and A3 is scored but never defaulted -
        // A3 is the write-off exception check 2 should find.
        File.WriteAllText(Path.Combine(_setDir, "pd_scored.csv"),
            "AccountNumber,ClientNumber,Category1,ReportDate,BucketRating,NextBucketRating,DeltaLambda\n" +
            "A1,T1,Loans,2026-02-28,1,2,0.1\n" +
            "A1,T2,Loans,2026-02-28,2,3,0.1\n" +
            "A3,T5,Loans,2026-02-28,4,5,0.1\n");

        // The bank's age analysis, in the exposure slot's canonical name. A1's two
        // transactions and A2's one, with the balance across aging buckets.
        File.WriteAllText(Path.Combine(_setDir, "IFRS9.csv"),
            "Account,Txn,Current,30 Days,60 Days,90 Days\n" +
            "A1,T1,10,0,40,60\n" +
            "A1,T2,20,0,50,150\n" +
            "A2,T9,30,0,100,200\n");

        // One write-off on A1, with no transaction number - it must trace BOTH of
        // A1's defaulted transactions. And one on A3, which never defaulted.
        File.WriteAllText(Path.Combine(_setDir, "writeoff.csv"),
            "LoanAccountNumber,CustomerId,Amount,ReportDate\n" +
            "A1,C1,300,2026-02-15\n" +
            "A3,C3,500,2026-02-20\n");

        File.WriteAllText(Path.Combine(_setDir, "debug.json"),
            "{\"Parameters\":{\"PdMinDate\":\"2026-01-01\",\"PdMaxDate\":\"2026-06-30\"}}");
        File.WriteAllText(Path.Combine(_setDir, "scenario.json"), "{}");

        SetIdentity identity = new(Key, "AGE ANALYSIS JUNE 2026");
        _identities = new Dictionary<string, SetIdentity> { [_setDir] = identity };
        _maps = new Dictionary<string, SetColumnMaps>
        {
            [Key] = new SetColumnMaps(
                WriteOff: null,
                Exposure: new ColumnMap(true, new Dictionary<string, IReadOnlyList<string>>
                {
                    ["LoanAccountNumber"] = new[] { "Account" },
                    ["TransactionNumber"] = new[] { "Txn" },
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
    public void TestEachTransactionIsItsOwnDefault()
    {
        ReconciliationSummary summary = Run().Summary;

        // three (account, transaction) pairs, not two accounts
        Assert.Equal(3, summary.TotalDefaults);
        Assert.Equal(600.0, summary.TotalExposure);
    }

    /// <summary>
    /// The trap: the write-off file has no transaction number, so comparing a
    /// composite default key against it directly would trace nothing at all and
    /// report a plausible 0%.
    /// </summary>
    [Fact]
    public void TestOneWriteOffTracesEveryTransactionOnThatAccount()
    {
        SingleSetResult res = Run();

        Assert.Equal(2, res.Summary.TracedWriteOff);
        Assert.All(
            res.Full.Where(f => f.AccountNumber == "A1"),
            f => Assert.True(f.InWriteOff, $"{f.TransactionNumber} should trace through the account's write-off"));
    }

    [Fact]
    public void TestTheAgeAnalysisTracesOnTheCompositeKey()
    {
        SingleSetResult res = Run();

        // every default appears in the age analysis, at transaction grain
        Assert.Equal(3, res.Summary.TracedIfrs9);
        Assert.Equal(3, res.Summary.Ifrs9KeyOverlap);
        Assert.Equal(0, res.Summary.UntracedTotal);
    }

    [Fact]
    public void TestTheExposureIsTheSumOfTheSelectedBucketsOnly()
    {
        SingleSetResult res = Run();

        DefaultAccountRecord t1 = res.Full.Single(f => f.TransactionNumber == "T1");
        DefaultAccountRecord t2 = res.Full.Single(f => f.TransactionNumber == "T2");

        // 40 + 60, and 50 + 150 - current and 30 days are excluded
        Assert.Equal(100.0, t1.Ifrs9AmountOutstanding);
        Assert.Equal(200.0, t2.Ifrs9AmountOutstanding);
    }

    /// <summary>
    /// The worst trap of all: with the scored population and the defaults left at
    /// mismatched grains, check 2 finds nothing, and "no exceptions" is then
    /// reported as a clean run that ties out.
    /// </summary>
    [Fact]
    public void TestCheck2StillFindsAWriteOffThatNeverDefaulted()
    {
        SingleSetResult res = Run();

        Assert.Equal(1, res.Summary.WoNotDefaultTotal);
        WriteOffNotDefaultRecord exception = Assert.Single(res.WoNd);
        Assert.Equal("A3", exception.AccountNumber);
        Assert.Equal(500.0, exception.WriteOffAmount);

        // and A1, whose transactions did default, is not reported as an exception
        Assert.DoesNotContain("A1", res.WoNd.Select(w => w.AccountNumber));
    }

    /// <summary>
    /// LastRating is keyed by account; if it were keyed on the join key this would
    /// be null and the workbook would claim a bucket-4 figure it never had.
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
    public void TestTheCensusReportsBothGrainsCoherently()
    {
        ReconciliationSummary summary = Run().Summary;

        // scored is at join grain, write-off at account grain - each counting the
        // thing its own file actually holds
        Assert.Equal(3, summary.ScoredDistinct);
        Assert.Equal(2, summary.WriteOffDistinct);
        Assert.Equal(3, summary.DefaultsDistinct);
        // A1 and A3 are both scored and written off - counted once each, at account
        // grain, not once per scored transaction
        Assert.Equal(2, summary.ScoredInWriteOff);
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
