using HazardRecon.Core.Models;
using HazardRecon.Web;
using Xunit;

namespace HazardRecon.Tests.Web;

public class OutputFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "outputfiles-" + Guid.NewGuid().ToString("N"));

    public OutputFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private void Write(string name, int bytes) =>
        File.WriteAllBytes(Path.Combine(_dir, name), new byte[bytes]);

    private static ReconciliationRunResult Result(
        string? workbook = "wb.xlsx", string? dashboard = "dash.html", string? memo = null,
        params string[] setFiles)
    {
        return new ReconciliationRunResult
        {
            Workbook = workbook ?? "",
            Dashboard = dashboard ?? "",
            Memo = memo,
            Results = new Dictionary<string, SingleSetResult>
            {
                ["SET"] = new SingleSetResult
                {
                    Summary = new ReconciliationSummary { Files = setFiles.ToList() },
                },
            },
        };
    }

    [Fact]
    public void TestTheMemoComesFirstThenTheWorkbookAndDashboard()
    {
        // the detail lists them in this order, so the server settles it once
        Write("wb.xlsx", 10); Write("dash.html", 10); Write("memo.docx", 10);

        List<OutputFile> files = OutputFiles.Describe(_dir, Result(memo: "memo.docx"));

        Assert.Equal(new[] { "memo.docx", "wb.xlsx", "dash.html" }, files.Select(f => f.Name));
    }

    [Fact]
    public void TestSetFilesFollowTheRunLevelOnes()
    {
        Write("wb.xlsx", 1); Write("dash.html", 1); Write("a.csv", 1);

        List<OutputFile> files = OutputFiles.Describe(_dir, Result(setFiles: new[] { "a.csv" }));

        Assert.Equal(new[] { "wb.xlsx", "dash.html", "a.csv" }, files.Select(f => f.Name));
    }

    [Fact]
    public void TestSizesAreReadFromDisk()
    {
        Write("wb.xlsx", 2048); Write("dash.html", 100);

        List<OutputFile> files = OutputFiles.Describe(_dir, Result());

        Assert.Equal(2048, files.Single(f => f.Name == "wb.xlsx").Bytes);
        Assert.Equal(100, files.Single(f => f.Name == "dash.html").Bytes);
    }

    [Fact]
    public void TestAMissingFileIsListedWithNoSizeRatherThanDropped()
    {
        // the run produced it; if it is gone the detail should still name it
        Write("wb.xlsx", 5);

        List<OutputFile> files = OutputFiles.Describe(_dir, Result());

        Assert.Equal(0, files.Single(f => f.Name == "dash.html").Bytes);
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void TestADuplicateNameIsListedOnce()
    {
        // the dashboard is both a run-level artefact and, on some runs, a set file
        Write("wb.xlsx", 1); Write("dash.html", 1);

        List<OutputFile> files = OutputFiles.Describe(_dir, Result(setFiles: new[] { "dash.html" }));

        Assert.Equal(2, files.Count);
        Assert.Single(files, f => f.Name == "dash.html");
    }

    [Fact]
    public void TestBlankNamesAreSkipped()
    {
        Write("wb.xlsx", 1);

        List<OutputFile> files = OutputFiles.Describe(
            _dir, Result(dashboard: "", memo: "  ", setFiles: new[] { "", "wb.xlsx" }));

        Assert.Equal(new[] { "wb.xlsx" }, files.Select(f => f.Name));
    }

    [Fact]
    public void TestAnUnreadableFolderYieldsSizesOfZeroRatherThanThrowing()
    {
        // a completed run may never be failed by a missing output folder
        List<OutputFile> files = OutputFiles.Describe(
            Path.Combine(_dir, "does-not-exist"), Result(setFiles: new[] { "a.csv" }));

        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.Equal(0, f.Bytes));
    }
}
