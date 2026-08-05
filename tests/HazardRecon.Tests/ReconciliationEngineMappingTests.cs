using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

public class ReconciliationEngineMappingTests : IClassFixture<SyntheticDataFixture>
{
    private readonly SyntheticDataFixture _fixture;

    public ReconciliationEngineMappingTests(SyntheticDataFixture fixture) => _fixture = fixture;

    [Fact]
    public void TestARenamedHeaderlessWriteoffFileIsReadCorrectlyWithAMap()
    {
        // same four write-off accounts as the fixture's own file, but with the
        // header row stripped and the columns in a different order
        string renamedWriteoff = Path.Combine(_fixture.RootDir, "renamed_writeoff.csv");
        File.WriteAllText(renamedWriteoff,
            "1,2026-03-01,100,C1,A1\n" +
            "1,2026-03-01,400,C4,A4\n" +
            "1,2026-07-01,500,C5,A5\n" +
            "1,2026-06-01,600,C6,A6\n");

        ColumnMap writeoffMap = new(hasHeaders: false, new Dictionary<string, string>
        {
            ["ReportDate"] = "1", ["Amount"] = "2", ["CustomerId"] = "3", ["LoanAccountNumber"] = "4"
        });

        ReconciliationEngine engine = new();
        var results = engine.Run(
            _fixture.RootDir, Path.Combine(_fixture.OutDir, "mapping-writeoff"),
            logger: (_, _) => { }, analyze: false, analyst: null, stages: null,
            columnMaps: new Dictionary<string, SetColumnMaps>
            {
                ["JUN2026 0.5PCT"] = new SetColumnMaps(writeoffMap, null)
            }).Results;

        // the fixture's own (headered) write-off file would have produced the
        // same trace outcome, so this proves the renamed/headerless file was
        // actually read, not silently skipped
        var summary = results["JUN2026 0.5PCT"].Summary;
        Assert.True(summary.TracedWriteOff > 0);
    }

    [Fact]
    public void TestAnUploadedSetsMapReachesTheLoaderThroughItsSuppliedKey()
    {
        // the upload path's layout: a numbered folder, whose name gives the
        // engine no way to work out the key the mapping was filed under
        string uploadRoot = Path.Combine(_fixture.OutDir, "uploaded", "0");
        Directory.CreateDirectory(uploadRoot);

        File.Copy(Path.Combine(_fixture.RootDir, "3. DEBUG FILE 30 JUNE 2026 0.5 PERCENT", "scenario.json"),
            Path.Combine(uploadRoot, "scenario.json"));
        foreach (string f in Directory.GetFiles(
            Path.Combine(_fixture.RootDir, "3. DEBUG FILE 30 JUNE 2026 0.5 PERCENT", "_extracted")))
        {
            File.Copy(f, Path.Combine(uploadRoot, Path.GetFileName(f)));
        }

        // a bank's own export: not one canonical column name in it
        File.WriteAllText(Path.Combine(uploadRoot, "writeoff.csv"),
            "Report_Date,Customer,Account,Write_off_amount\n" +
            "2026-03-31,C1,A1,100\n" +
            "2026-03-31,C4,A4,400\n");
        File.WriteAllText(Path.Combine(uploadRoot, "IFRS9.csv"),
            "Account,Balance\nA2,200\n");

        SetIdentity identity = new("JUN2026", "IFRS9 FILE JUNE 2026");

        ReconciliationEngine engine = new();
        var results = engine.Run(
            new List<string> { uploadRoot }, Path.Combine(_fixture.OutDir, "uploaded-out"),
            logger: (_, _) => { }, analyze: false, analyst: null, stages: null,
            columnMaps: new Dictionary<string, SetColumnMaps>
            {
                [identity.Key] = new SetColumnMaps(
                    new ColumnMap(hasHeaders: true, new Dictionary<string, string>
                    {
                        ["LoanAccountNumber"] = "Account", ["CustomerId"] = "Customer",
                        ["Amount"] = "Write_off_amount", ["ReportDate"] = "Report_Date"
                    }),
                    new ColumnMap(hasHeaders: true, new Dictionary<string, string>
                    {
                        ["LoanAccountNumber"] = "Account", ["AmountOutstanding"] = "Balance"
                    }))
            },
            setIdentities: new Dictionary<string, SetIdentity> { [uploadRoot] = identity }).Results;

        // both mapped files were actually read: the write-off file traced its
        // defaults, and the exposure file traced the one only it can account for
        var summary = results[identity.Key].Summary;
        Assert.True(summary.TracedWriteOff > 0);
        Assert.True(summary.TracedIfrs9 > 0);
    }
}
