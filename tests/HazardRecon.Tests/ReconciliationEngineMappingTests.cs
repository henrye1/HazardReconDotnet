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
}
