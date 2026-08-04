using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

public class CsvSnifferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hr-sniffer-tests", Guid.NewGuid().ToString("N")[..8]);

    public CsvSnifferTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string content)
    {
        string path = Path.Combine(_dir, "in.csv");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void TestAFileWithTextHeadersIsDetectedAsHeadered()
    {
        string path = WriteFile("LoanAccountNumber,Amount,ReportDate\nA1,100,2026-04-30\nA2,200,2026-05-01\n");

        CsvSniff sniff = CsvSniffer.Sniff(path);

        Assert.True(sniff.HasHeaders);
        Assert.Equal(new[] { "LoanAccountNumber", "Amount", "ReportDate" }, sniff.Headers);
        Assert.Equal(2, sniff.SampleRows.Count);
        Assert.Equal("A1", sniff.SampleRows[0][0]);
    }

    [Fact]
    public void TestAFileWithNoHeaderRowIsDetectedAsHeaderless()
    {
        string path = WriteFile("A1,100,2026-04-30\nA2,200,2026-05-01\nA3,300,2026-05-02\n");

        CsvSniff sniff = CsvSniffer.Sniff(path);

        Assert.False(sniff.HasHeaders);
        Assert.Null(sniff.Headers);
        Assert.Equal(3, sniff.SampleRows.Count);
        Assert.Equal("A1", sniff.SampleRows[0][0]);
    }

    [Fact]
    public void TestSampleRowsAreCappedAtTheRequestedCount()
    {
        string content = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"A{i},{i * 10},2026-01-0{(i % 9) + 1}")) + "\n";
        string path = WriteFile(content);

        CsvSniff sniff = CsvSniffer.Sniff(path, sampleRowCount: 3);

        Assert.Equal(3, sniff.SampleRows.Count);
    }

    [Fact]
    public void TestHeadersAreDetectedEvenWhenHalfTheColumnsAreTextIdentifiers()
    {
        // account/customer numbers ("A1", "C1") are neither numeric nor date-like,
        // so a naive header heuristic under-counts them and misses the header row
        string path = WriteFile("LoanAccountNumber,CustomerId,Amount,ReportDate\nA1,C1,100,2026-04-30\n");

        CsvSniff sniff = CsvSniffer.Sniff(path);

        Assert.True(sniff.HasHeaders);
        Assert.Equal(new[] { "LoanAccountNumber", "CustomerId", "Amount", "ReportDate" }, sniff.Headers);
    }

    /// <summary>
    /// Labels carrying digits used to be read as data, which cost the file its
    /// header row and left the mapping step offering "Column 1, Column 2, ..."
    /// instead of the names the user uploaded. IFRS9_* is the case that matters
    /// most here, given what this tool reconciles.
    /// </summary>
    [Theory]
    [InlineData("IFRS9_ACCOUNT,IFRS9_DATE,IFRS9_BALANCE\n8104227719,2026-06-30,44912.10\n")]
    [InlineData("IFRS9_ACCOUNT,REPORT_DATE,BALANCE\n8104227719,2026-06-30,44912.10\n")]
    [InlineData("AccountNumber,Q1_BAL,Q2_BAL\nA1,100,200\n")]
    [InlineData("ACCOUNT,PD_12M,LGD_PCT\nA1,0.02,0.9\n")]
    [InlineData("COL_1,COL_2,COL_3\nA1,100,2026-06-30\n")]
    public void TestHeadersCarryingDigitsAreStillDetected(string content)
    {
        CsvSniff sniff = CsvSniffer.Sniff(WriteFile(content));

        Assert.True(sniff.HasHeaders);
        Assert.Equal(content.Split('\n')[0].Split(','), sniff.Headers);
    }

    [Fact]
    public void TestATwoColumnFileWithOneTextValueIsStillHeadered()
    {
        // the old majority test needed both columns to look header-like, so a
        // single non-numeric value column was enough to lose the header row
        CsvSniff sniff = CsvSniffer.Sniff(WriteFile("ACCT,STATUS\nA1,ACTIVE\nA2,CLOSED\n"));

        Assert.True(sniff.HasHeaders);
        Assert.Equal(new[] { "ACCT", "STATUS" }, sniff.Headers);
    }

    [Fact]
    public void TestAMonthNameLabelDoesNotDisqualifyTheHeaderRow()
    {
        // DateTime.TryParse accepts a bare "May", so a label like this would
        // otherwise read as a date value and make the file look headerless
        CsvSniff sniff = CsvSniffer.Sniff(WriteFile("ACCOUNT,May,June\nA1,100,200\n"));

        Assert.True(sniff.HasHeaders);
        Assert.Equal(new[] { "ACCOUNT", "May", "June" }, sniff.Headers);
    }

    [Fact]
    public void TestARowCarryingANumberOrDateIsNeverAHeader()
    {
        // the stricter half of the rule: one real value in the first row means it
        // is data, however word-like the rest of it looks
        Assert.False(CsvSniffer.Sniff(WriteFile("ACCT,100,STATUS\nA2,200,X\n")).HasHeaders);
        Assert.False(CsvSniffer.Sniff(WriteFile("ACCT,2026-06-30\nA2,2026-07-31\n")).HasHeaders);
    }

    [Fact]
    public void TestAFileOfNothingButWordsIsReportedHeaderless()
    {
        // A known limit, asserted so it is a decision rather than a surprise:
        // with no number, date or digit anywhere, nothing separates labels from
        // values and the sniffer cannot tell. The mapping step then offers
        // positional columns with sample values beside them.
        CsvSniff sniff = CsvSniffer.Sniff(WriteFile("ACCT,BRANCH,PRODUCT\nAAA,JHB,HOMELOAN\n"));

        Assert.False(sniff.HasHeaders);
        Assert.Null(sniff.Headers);
    }

    [Fact]
    public void TestABlankLeadingLineIsNotTreatedAsAHeader()
    {
        CsvSniff sniff = CsvSniffer.Sniff(WriteFile(",,\nA1,100,2026-06-30\n"));

        Assert.False(sniff.HasHeaders);
    }

    /* ---------- Reinterpret: the user overruling the guess ---------- */

    [Fact]
    public void TestReadingAHeaderlessFileAsHeaderedPromotesItsFirstRow()
    {
        // the undecidable case: all words, so the sniffer says headerless and the
        // user says otherwise
        CsvSniff sniffed = CsvSniffer.Sniff(WriteFile("ACCT,BRANCH,PRODUCT\nAAA,JHB,HOMELOAN\nBBB,CPT,VAF\n"));
        Assert.False(sniffed.HasHeaders);

        CsvSniff headered = CsvSniffer.Reinterpret(sniffed, true);

        Assert.True(headered.HasHeaders);
        Assert.Equal(new[] { "ACCT", "BRANCH", "PRODUCT" }, headered.Headers);
        // and the promoted row is no longer counted as data
        Assert.Equal(2, headered.SampleRows.Count);
        Assert.Equal(new[] { "AAA", "JHB", "HOMELOAN" }, headered.SampleRows[0]);
    }

    [Fact]
    public void TestReadingAHeaderedFileAsDataDemotesItsHeaderRow()
    {
        CsvSniff sniffed = CsvSniffer.Sniff(WriteFile("LoanAccountNumber,Amount\nA1,100\nA2,200\n"));
        Assert.True(sniffed.HasHeaders);

        CsvSniff headerless = CsvSniffer.Reinterpret(sniffed, false);

        Assert.False(headerless.HasHeaders);
        Assert.Null(headerless.Headers);
        // the header row comes back as the first data row, in file order
        Assert.Equal(3, headerless.SampleRows.Count);
        Assert.Equal(new[] { "LoanAccountNumber", "Amount" }, headerless.SampleRows[0]);
        Assert.Equal(new[] { "A1", "100" }, headerless.SampleRows[1]);
    }

    [Fact]
    public void TestAgreeingWithTheGuessChangesNothing()
    {
        CsvSniff sniffed = CsvSniffer.Sniff(WriteFile("LoanAccountNumber,Amount\nA1,100\n"));

        Assert.Same(sniffed, CsvSniffer.Reinterpret(sniffed, true));
    }

    [Fact]
    public void TestReinterpretingIsReversible()
    {
        // a user toggling twice must be back where they started, or the columns
        // they mapped would shift underneath them
        CsvSniff sniffed = CsvSniffer.Sniff(WriteFile("LoanAccountNumber,Amount\nA1,100\nA2,200\n"));

        CsvSniff back = CsvSniffer.Reinterpret(CsvSniffer.Reinterpret(sniffed, false), true);

        Assert.Equal(sniffed.HasHeaders, back.HasHeaders);
        Assert.Equal(sniffed.Headers, back.Headers);
        Assert.Equal(sniffed.SampleRows.Count, back.SampleRows.Count);
        Assert.Equal(sniffed.SampleRows[0], back.SampleRows[0]);
    }

    [Fact]
    public void TestTheSignatureFollowsTheChosenReading()
    {
        // what the override is for: a saved mapping is keyed on the column shape,
        // and the two readings of one file are not the same shape
        CsvSniff sniffed = CsvSniffer.Sniff(WriteFile("ACCT,BRANCH,PRODUCT\nAAA,JHB,HOMELOAN\n"));
        CsvSniff headered = CsvSniffer.Reinterpret(sniffed, true);

        string asData = ColumnSignature.Compute(sniffed.Headers, sniffed.SampleRows);
        string asHeaders = ColumnSignature.Compute(headered.Headers, headered.SampleRows);

        Assert.NotEqual(asData, asHeaders);
    }

    [Fact]
    public void TestReinterpretingAnEmptyFileIsHarmless()
    {
        CsvSniff sniffed = CsvSniffer.Sniff(WriteFile(""));

        CsvSniff headered = CsvSniffer.Reinterpret(sniffed, true);

        Assert.False(headered.HasHeaders);
        Assert.Empty(headered.SampleRows);
    }

    [Fact]
    public void TestAnEmptyFileHasNoHeadersAndNoSamples()
    {
        string path = WriteFile("");

        CsvSniff sniff = CsvSniffer.Sniff(path);

        Assert.False(sniff.HasHeaders);
        Assert.Empty(sniff.SampleRows);
    }
}
