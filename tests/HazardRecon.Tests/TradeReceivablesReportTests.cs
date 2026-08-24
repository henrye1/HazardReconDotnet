using HazardRecon.Core.Exporters;
using HazardRecon.Core.Models;
using Xunit;

namespace HazardRecon.Tests;

/// <summary>
/// What the reports call things once a default is a customer rather than an
/// account. No report gains a column - the identifier column is renamed, because a
/// column of customer numbers headed "AccountNumber" is wrong in a signed-off
/// spreadsheet.
/// </summary>
public class TradeReceivablesReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "hr-tr-report-tests", Guid.NewGuid().ToString("N")[..8]);

    public TradeReceivablesReportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static List<DefaultAccountRecord> OneCustomer() => new()
    {
        new DefaultAccountRecord
        {
            AccountNumber = "C1", AccountNormalized = "C1",
            CohortDate = "2026-05-31", Rating = "5", DefaultAmount = 300, MinLgdBalance = 300,
            InWriteOff = true, InIFRS9 = true, TraceSource = "Write-off + IFRS9",
            WriteOffAmount = 300, Ifrs9AmountOutstanding = 300, TraceAmount = 300,
            RecoveryStatus = "NO POST-DEFAULT DATA"
        },
    };

    private static List<DefaultAccountRecord> OneLendingAccount() => new()
    {
        new DefaultAccountRecord
        {
            AccountNumber = "A1", AccountNormalized = "A1",
            CohortDate = "2026-05-31", Rating = "5", DefaultAmount = 100, MinLgdBalance = 100,
            InWriteOff = true, TraceSource = "Write-off", WriteOffAmount = 300, TraceAmount = 300,
            RecoveryStatus = "NO POST-DEFAULT DATA"
        },
    };

    private string[] Export(List<DefaultAccountRecord> full, string key, EngineRunType runType, string file)
    {
        CsvExporter.ExportSet(
            _dir, key, full, full, new List<WriteOffNotDefaultRecord>(), new MigrationMatrixResult(), runType);
        return File.ReadAllLines(Path.Combine(_dir, $"{key}_{file}.csv"));
    }

    [Fact]
    public void TestTheTracedCsvNamesTheIdentifierColumnForTheCustomer()
    {
        string[] lines = Export(OneCustomer(), "TR", EngineRunType.TradeReceivables, "traced_defaults");

        Assert.StartsWith("ClientNumber,CohortDate", lines[0]);
        Assert.DoesNotContain("AccountNumber", lines[0]);
        Assert.StartsWith("C1,", lines[1]);
    }

    [Fact]
    public void TestTheUntracedCsvNamesItToo()
    {
        List<DefaultAccountRecord> untraced = OneCustomer();
        untraced.ForEach(u => { u.InWriteOff = false; u.InIFRS9 = false; u.TraceSource = "UNTRACED"; });

        string[] lines = Export(untraced, "UT", EngineRunType.TradeReceivables, "untraced_defaults");

        Assert.StartsWith("ClientNumber,CohortDate", lines[0]);
    }

    [Fact]
    public void TestTheExposureColumnIsNamedForTheAgeAnalysis()
    {
        string[] lines = Export(OneCustomer(), "TR3", EngineRunType.TradeReceivables, "traced_defaults");

        Assert.Contains("AgeAnalysisAmount", lines[0]);
        Assert.DoesNotContain("IFRS9AmountOutstanding", lines[0]);
    }

    /// <summary>
    /// The write-off amount is per customer for a receivables run, so it is an
    /// ordinary per-row figure again - there is nothing to hold back.
    /// </summary>
    [Fact]
    public void TestTheWriteOffAmountIsAnOrdinaryColumn()
    {
        string[] lines = Export(OneCustomer(), "TR4", EngineRunType.TradeReceivables, "traced_defaults");

        Assert.Contains("WriteOffAmount", lines[0]);
        int at = Array.IndexOf(lines[0].Split(','), "WriteOffAmount");
        Assert.Equal("300", lines[1].Split(',')[at]);
    }

    /// <summary>The byte-identical regression: a lending run's file must not move.</summary>
    [Fact]
    public void TestALendingRunKeepsItsOriginalColumns()
    {
        string[] lines = Export(OneLendingAccount(), "LEND", EngineRunType.Lending, "traced_defaults");

        Assert.Equal(
            "AccountNumber,CohortDate,Rating,DefaultAmount,TraceSource,WriteOffAmount," +
            "IFRS9AmountOutstanding,MinLgdBalance,TraceAmount,LossVsTraceDiff",
            lines[0]);
    }

    [Fact]
    public void TestTheWriteOffExceptionCsvNamesTheIdentifierToo()
    {
        List<WriteOffNotDefaultRecord> woNd = new()
        {
            new WriteOffNotDefaultRecord
            {
                AccountNumber = "C3", CustomerId = "A5", WriteOffAmount = 500,
                WriteOffVsScoringWindow = "IN WINDOW", LastBucketRating = "4", ScoringWindow = "w"
            }
        };

        CsvExporter.ExportSet(_dir, "WO", new List<DefaultAccountRecord>(), new List<DefaultAccountRecord>(),
            woNd, new MigrationMatrixResult(), EngineRunType.TradeReceivables);

        string[] lines = File.ReadAllLines(Path.Combine(_dir, "WO_writeoff_not_default.csv"));

        Assert.StartsWith("ClientNumber,CustomerId,WriteOffAmount", lines[0]);
        Assert.StartsWith("C3,A5,500", lines[1]);
    }

    [Fact]
    public void TestALendingWriteOffExceptionCsvKeepsItsOriginalColumns()
    {
        List<WriteOffNotDefaultRecord> woNd = new()
        {
            new WriteOffNotDefaultRecord
            {
                AccountNumber = "A1", CustomerId = "C1", WriteOffAmount = 100,
                WriteOffVsScoringWindow = "IN WINDOW", ScoringWindow = "w"
            }
        };

        CsvExporter.ExportSet(_dir, "WOL", new List<DefaultAccountRecord>(), new List<DefaultAccountRecord>(),
            woNd, new MigrationMatrixResult());

        string[] lines = File.ReadAllLines(Path.Combine(_dir, "WOL_writeoff_not_default.csv"));

        Assert.Equal(
            "AccountNumber,CustomerId,WriteOffAmount,FirstWriteOffDate,LastWriteOffDate," +
            "WriteOffVsScoringWindow,LastScoredDate,LastBucketRating,ScoringWindow",
            lines[0]);
    }

    [Fact]
    public void TestTheCommentaryCountsCustomersRatherThanAccounts()
    {
        List<DefaultAccountRecord> untraced = OneCustomer();
        untraced.ForEach(u => { u.InWriteOff = false; u.InIFRS9 = false; u.TraceSource = "UNTRACED"; });

        SingleSetResult set = new()
        {
            Full = untraced,
            Untraced = untraced,
            Summary = new ReconciliationSummary { UntracedTotal = 1, UntracedExposure = 300 }
        };

        List<string> lines = WorkbookExporter.CommentaryLines(
            new Dictionary<string, SingleSetResult> { ["TR"] = set }, EngineRunType.TradeReceivables);

        Assert.Contains(lines, l => l.Contains("defaulted customer(s)"));
        // and it names the file a receivables run actually traced against
        Assert.Contains(lines, l => l.Contains("age analysis"));
    }

    [Fact]
    public void TestALendingCommentaryStillSaysAccounts()
    {
        List<DefaultAccountRecord> untraced = OneLendingAccount();
        untraced.ForEach(u => { u.InWriteOff = false; u.TraceSource = "UNTRACED"; });

        SingleSetResult set = new()
        {
            Full = untraced,
            Untraced = untraced,
            Summary = new ReconciliationSummary { UntracedTotal = 1, UntracedExposure = 100 }
        };

        List<string> lines = WorkbookExporter.CommentaryLines(
            new Dictionary<string, SingleSetResult> { ["LEND"] = set });

        Assert.Contains(lines, l => l.Contains("defaulted account(s)"));
        Assert.Contains(lines, l => l.Contains("write-off or IFRS9"));
    }
}
