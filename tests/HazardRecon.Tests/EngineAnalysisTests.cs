using HazardRecon.Core.Llm;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using HazardRecon.Tests.Llm;
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
        string outDir = Path.Combine(_fixture.OutDir, "no-analyst");

        ReconciliationRunResult result = engine.Run(
            _fixture.RootDir,
            outDir,
            logger: (m, k) => log.Add((m, k)),
            analyze: true,
            analyst: null);

        Assert.Null(result.Analysis);
        Assert.Null(result.Memo);
        Assert.True(File.Exists(Path.Combine(outDir, result.Workbook)));
        Assert.True(File.Exists(Path.Combine(outDir, result.Dashboard)));
        Assert.Contains(log, l => l.Kind == "warn" && l.Msg.Contains("no model selected"));
    }

    [Fact]
    public void TestAnalystFailureStillCompletesTheRunWithArtifactsIntact()
    {
        FakeLlmClient fakeClient = new() { ThrowOnChat = new LlmException("gateway is down") };
        AiAnalysisService analyst = new(fakeClient, "some-model-id");

        ReconciliationEngine engine = new();
        List<(string Msg, string Kind)> log = new();
        string outDir = Path.Combine(_fixture.OutDir, "throwing-analyst");

        ReconciliationRunResult result = engine.Run(
            _fixture.RootDir,
            outDir,
            logger: (m, k) => log.Add((m, k)),
            analyze: true,
            analyst: analyst);

        Assert.Null(result.Analysis);
        Assert.Null(result.Memo);
        Assert.True(File.Exists(Path.Combine(outDir, result.Workbook)));
        Assert.True(File.Exists(Path.Combine(outDir, result.Dashboard)));
        Assert.Contains(log, l => l.Kind == "warn" && l.Msg.Contains("AI analysis unavailable"));
    }
}
