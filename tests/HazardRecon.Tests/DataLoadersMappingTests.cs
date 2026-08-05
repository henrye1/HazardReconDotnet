using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

public class DataLoadersMappingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hr-dataloaders-mapping-tests", Guid.NewGuid().ToString("N")[..8]);
    private readonly DataLoaders _loaders = new();

    public DataLoadersMappingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void TestLoadWriteoffWithNoMapUsesLiteralHeaderNamesLikeToday()
    {
        string path = WriteFile("wo.csv", "LoanAccountNumber,CustomerId,Amount,ReportDate\nA1,C1,100,2026-04-30\n");

        var (agg, accts) = _loaders.LoadWriteoff(path);

        Assert.Contains("A1", accts);
        Assert.Equal(100, agg[0].WriteOffAmount);
    }

    [Fact]
    public void TestLoadWriteoffWithAHeaderedMapResolvesRenamedColumns()
    {
        string path = WriteFile("wo.csv", "AcctNo,Cust,Amt,Dt\nA1,C1,250.5,2026-05-01\n");
        ColumnMap map = new(hasHeaders: true, new Dictionary<string, string>
        {
            ["LoanAccountNumber"] = "AcctNo", ["CustomerId"] = "Cust", ["Amount"] = "Amt", ["ReportDate"] = "Dt"
        });

        var (agg, accts) = _loaders.LoadWriteoff(path, columnMap: map);

        Assert.Contains("A1", accts);
        Assert.Equal(250.5, agg[0].WriteOffAmount);
        Assert.Equal("C1", agg[0].CustomerId);
    }

    [Fact]
    public void TestLoadWriteoffWithAHeaderlessMapResolvesByIndex()
    {
        string path = WriteFile("wo.csv", "A1,C1,300,2026-05-02\n");
        ColumnMap map = new(hasHeaders: false, new Dictionary<string, string>
        {
            ["LoanAccountNumber"] = "0", ["CustomerId"] = "1", ["Amount"] = "2", ["ReportDate"] = "3"
        });

        var (agg, accts) = _loaders.LoadWriteoff(path, columnMap: map);

        Assert.Contains("A1", accts);
        Assert.Equal(300, agg[0].WriteOffAmount);
    }

    [Fact]
    public void TestLoadSourceAccountsWithAHeaderlessMapResolvesByIndex()
    {
        string path = WriteFile("ifrs9.csv", "A1,2026-06-30,150.25,Stage 2\n");
        ColumnMap map = new(hasHeaders: false, new Dictionary<string, string>
        {
            ["LoanAccountNumber"] = "0", ["AmountOutstanding"] = "2"
        });

        SourceAccountsResult res = _loaders.LoadSourceAccounts(path, "LoanAccountNumber", "test", "AmountOutstanding", columnMap: map);

        Assert.Contains("A1", res.AccountNumbers);
        Assert.Equal(150.25, res.AmountsPerAccount["A1"]);
    }

    /* The join key deciding nothing is not a result. A file whose account column
       was never found used to read as a file of no accounts, which traces no
       defaults and reports a clean 0% - a plausible number that is really a
       mapping failure, and one a reader could sign off. These pin the refusal. */

    [Fact]
    public void TestLoadWriteoffRefusesAFileWithoutTheAccountColumn()
    {
        // the export names it Account; unmapped, the loader looks for LoanAccountNumber
        string path = WriteFile("wo.csv", "Report_Date,Customer,Account,Write_off_amount\n2025-12-31,606323,606323,119.02\n");

        var ex = Assert.Throws<InvalidOperationException>(() => _loaders.LoadWriteoff(path));

        Assert.Contains("LoanAccountNumber", ex.Message);
        Assert.Contains("wo.csv", ex.Message);
        // the message has to say what the file does offer, or there is no next step
        Assert.Contains("Account", ex.Message);
    }

    [Fact]
    public void TestLoadSourceAccountsRefusesAFileWithoutTheAccountColumn()
    {
        string path = WriteFile("ifrs9.csv", "Contract_No,Closing_Balance\n636432,1140.84\n");

        var ex = Assert.Throws<InvalidOperationException>(
            () => _loaders.LoadSourceAccounts(path, "LoanAccountNumber", "IFRS9", "AmountOutstanding"));

        Assert.Contains("LoanAccountNumber", ex.Message);
        Assert.Contains("Contract_No", ex.Message);
    }

    [Fact]
    public void TestLoadWriteoffRefusesAHeaderlessMapPointingPastTheLastColumn()
    {
        string path = WriteFile("wo.csv", "A1,C1,300,2026-05-02\n");
        ColumnMap map = new(hasHeaders: false, new Dictionary<string, string>
        {
            ["LoanAccountNumber"] = "9", ["Amount"] = "2"
        });

        var ex = Assert.Throws<InvalidOperationException>(() => _loaders.LoadWriteoff(path, columnMap: map));

        Assert.Contains("wo.csv", ex.Message);
    }

    [Fact]
    public void TestLoadSourceAccountsRefusesAColumnThatIsThereButEmpty()
    {
        string path = WriteFile("ifrs9.csv", "LoanAccountNumber,AmountOutstanding\n,150.25\n,90.00\n");

        Assert.Throws<InvalidOperationException>(
            () => _loaders.LoadSourceAccounts(path, "LoanAccountNumber", "IFRS9", "AmountOutstanding"));
    }

    /* The refusal is about a file that is present and unreadable. A set uploaded
       without a write-off file, or without an IFRS9 file, is supported - the
       inventory says what each one costs - so neither may start throwing. */

    [Fact]
    public void TestAMissingFileIsStillToleratedRatherThanRefused()
    {
        string absent = Path.Combine(_dir, "not-here.csv");

        var (agg, accts) = _loaders.LoadWriteoff(absent);
        Assert.Empty(agg);
        Assert.Empty(accts);

        SourceAccountsResult res = _loaders.LoadSourceAccounts(absent, "LoanAccountNumber", "IFRS9", "AmountOutstanding");
        Assert.Empty(res.AccountNumbers);
    }

    [Fact]
    public void TestAFileOfHeadersAndNoRowsIsStillTolerated()
    {
        // nothing was written off this period: an empty population, not a broken map
        string path = WriteFile("wo.csv", "LoanAccountNumber,CustomerId,Amount,ReportDate\n");

        var (agg, accts) = _loaders.LoadWriteoff(path);

        Assert.Empty(agg);
        Assert.Empty(accts);
    }
}
