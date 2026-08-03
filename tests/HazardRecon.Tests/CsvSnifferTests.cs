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
    public void TestAnEmptyFileHasNoHeadersAndNoSamples()
    {
        string path = WriteFile("");

        CsvSniff sniff = CsvSniffer.Sniff(path);

        Assert.False(sniff.HasHeaders);
        Assert.Empty(sniff.SampleRows);
    }
}
