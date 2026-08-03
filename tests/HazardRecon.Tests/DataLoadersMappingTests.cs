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
}
