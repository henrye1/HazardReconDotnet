using HazardRecon.Core.Exporters;
using HazardRecon.Core.Models;
using Xunit;

namespace HazardRecon.Tests;

/// <summary>
/// What the reports say once a default is a transaction rather than an account.
/// Driven from hand-built records rather than a full run, so each column can be
/// asserted on its own.
/// </summary>
public class TradeReceivablesReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "hr-tr-report-tests", Guid.NewGuid().ToString("N")[..8]);

    public TradeReceivablesReportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// Two transactions on one account, both traced through the account's single
    /// R300 write-off - the shape that makes the account-level total ambiguous.
    /// </summary>
    private static List<DefaultAccountRecord> TwoTransactionsOneAccount() => new()
    {
        new DefaultAccountRecord
        {
            AccountNumber = "A1", TransactionNumber = "T1", AccountNormalized = "A1T1",
            CohortDate = "2026-05-31", Rating = "5", DefaultAmount = 100, MinLgdBalance = 100,
            InWriteOff = true, InIFRS9 = true, TraceSource = "Write-off + IFRS9",
            AccountWriteOffTotal = 300, Ifrs9AmountOutstanding = 100, TraceAmount = 100,
            RecoveryStatus = "NO POST-DEFAULT DATA"
        },
        new DefaultAccountRecord
        {
            AccountNumber = "A1", TransactionNumber = "T2", AccountNormalized = "A1T2",
            CohortDate = "2026-05-31", Rating = "5", DefaultAmount = 200, MinLgdBalance = 200,
            InWriteOff = true, InIFRS9 = true, TraceSource = "Write-off + IFRS9",
            AccountWriteOffTotal = 300, Ifrs9AmountOutstanding = 200, TraceAmount = 200,
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

    private string[] Export(List<DefaultAccountRecord> full, string key)
    {
        CsvExporter.ExportSet(_dir, key, full, full, new List<WriteOffNotDefaultRecord>(), new MigrationMatrixResult());
        return File.ReadAllLines(Path.Combine(_dir, $"{key}_traced_defaults.csv"));
    }

    [Fact]
    public void TestTheTracedCsvNamesTheTransactionColumnSecond()
    {
        string[] lines = Export(TwoTransactionsOneAccount(), "TR");

        Assert.StartsWith("AccountNumber,TransactionNumber,CohortDate", lines[0]);
        Assert.Contains("A1,T1,", lines[1]);
        Assert.Contains("A1,T2,", lines[2]);
    }

    /// <summary>
    /// The account's write-off appears once, so the column still totals to the real
    /// figure. Repeated on both rows it would read as R600 of write-offs against an
    /// account that had R300.
    /// </summary>
    [Fact]
    public void TestTheAccountWriteOffTotalIsWrittenOncePerAccount()
    {
        string[] lines = Export(TwoTransactionsOneAccount(), "TR2");

        Assert.Contains("AccountWriteOffTotal", lines[0]);
        Assert.DoesNotContain("WriteOffAmount", lines[0]);

        int at = Array.IndexOf(lines[0].Split(','), "AccountWriteOffTotal");
        string[] first = lines[1].Split(',');
        string[] second = lines[2].Split(',');

        Assert.Equal("300", first[at]);
        Assert.Equal("", second[at]);
    }

    [Fact]
    public void TestTheExposureColumnIsNamedForTheAgeAnalysis()
    {
        string[] lines = Export(TwoTransactionsOneAccount(), "TR3");

        Assert.Contains("AgeAnalysisAmount", lines[0]);
        Assert.DoesNotContain("IFRS9AmountOutstanding", lines[0]);
    }

    /// <summary>
    /// The whole point of deriving the column set from the rows: a lending run must
    /// produce exactly the file it produced before.
    /// </summary>
    [Fact]
    public void TestALendingRunKeepsItsOriginalColumns()
    {
        string[] lines = Export(OneLendingAccount(), "LEND");

        Assert.Equal(
            "AccountNumber,CohortDate,Rating,DefaultAmount,TraceSource,WriteOffAmount," +
            "IFRS9AmountOutstanding,MinLgdBalance,TraceAmount,LossVsTraceDiff",
            lines[0]);
        Assert.DoesNotContain("TransactionNumber", lines[0]);
    }

    [Fact]
    public void TestTheUntracedCsvAlsoIdentifiesTheTransaction()
    {
        List<DefaultAccountRecord> untraced = TwoTransactionsOneAccount();
        untraced.ForEach(u => { u.InWriteOff = false; u.InIFRS9 = false; u.TraceSource = "UNTRACED"; });

        CsvExporter.ExportSet(_dir, "UT", untraced, untraced, new List<WriteOffNotDefaultRecord>(), new MigrationMatrixResult());
        string[] lines = File.ReadAllLines(Path.Combine(_dir, "UT_untraced_defaults.csv"));

        Assert.StartsWith("AccountNumber,TransactionNumber,CohortDate", lines[0]);
        // both rows are present and distinguishable, where before they differed only
        // by amount
        Assert.Contains(lines, l => l.StartsWith("A1,T1,"));
        Assert.Contains(lines, l => l.StartsWith("A1,T2,"));
    }

    [Fact]
    public void TestTheCommentaryCountsTransactionsRatherThanAccounts()
    {
        List<DefaultAccountRecord> untraced = TwoTransactionsOneAccount();
        untraced.ForEach(u => { u.InWriteOff = false; u.InIFRS9 = false; u.TraceSource = "UNTRACED"; });

        SingleSetResult set = new()
        {
            Full = untraced,
            Untraced = untraced,
            Summary = new ReconciliationSummary { UntracedTotal = 2, UntracedExposure = 300 }
        };

        List<string> lines = WorkbookExporter.CommentaryLines(
            new Dictionary<string, SingleSetResult> { ["TR"] = set });

        Assert.Contains(lines, l => l.Contains("defaulted transaction(s)"));
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
