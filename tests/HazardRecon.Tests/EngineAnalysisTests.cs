using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

public class EngineAnalysisTests : IClassFixture<SyntheticDataFixture>
{
    private readonly SyntheticDataFixture _fixture;

    public EngineAnalysisTests(SyntheticDataFixture fixture) => _fixture = fixture;

    [Fact]
    public void TestAnalyzeWithoutAnAnalystCompletesTheRunAndLogsTheSkip()
    {
        ReconciliationEngine engine = new();
        List<(string Msg, string Kind)> log = new();

        ReconciliationRunResult result = engine.Run(
            _fixture.RootDir,
            Path.Combine(_fixture.OutDir, "no-analyst"),
            logger: (m, k) => log.Add((m, k)),
            analyze: true,
            analyst: null);

        Assert.Null(result.Analysis);
        Assert.Null(result.Memo);
        Assert.NotEmpty(result.Workbook);
        Assert.NotEmpty(result.Dashboard);
        Assert.Contains(log, l => l.Kind == "warn" && l.Msg.Contains("no model selected"));
    }
}
