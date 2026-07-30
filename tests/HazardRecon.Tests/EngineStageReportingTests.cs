using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

/// <summary>
/// The progress screen believes whatever the engine reports, so these check the
/// real run against the plan rather than the reporter in isolation.
/// </summary>
public class EngineStageReportingTests : IClassFixture<SyntheticDataFixture>
{
    private readonly SyntheticDataFixture _fixture;

    public EngineStageReportingTests(SyntheticDataFixture fixture) => _fixture = fixture;

    private (IReadOnlyList<RunStage> Final, List<int> Counts) RunWithStages(string outDir, bool analyze)
    {
        List<int> counts = new();
        StageReporter reporter = new(list => counts.Add(list.Count));

        ReconciliationEngine engine = new();
        engine.Run(_fixture.RootDir, outDir, logger: (_, _) => { }, analyze: analyze, analyst: null, stages: reporter);

        return (reporter.Snapshot(), counts);
    }

    [Fact]
    public void TestARunReportsEveryStageAndLeavesNonePending()
    {
        var (final, _) = RunWithStages(Path.Combine(_fixture.OutDir, "stages-plain"), analyze: false);

        Assert.NotEmpty(final);
        Assert.DoesNotContain(final, s => s.Status == StageStatus.Pending);
        Assert.DoesNotContain(final, s => s.Status == StageStatus.Running);
        Assert.DoesNotContain(final, s => s.Status == StageStatus.Error);
    }

    [Fact]
    public void TestDiscoveryIsFirstAndTheDashboardIsLast()
    {
        var (final, _) = RunWithStages(Path.Combine(_fixture.OutDir, "stages-order"), analyze: false);

        Assert.Equal(StageKeys.Discover, final[0].Key);
        Assert.Equal(StageKeys.Dashboard, final[^1].Key);
        // the workbook is built from every set, so it must come after them
        Assert.True(final.ToList().FindIndex(s => s.Key == StageKeys.Workbook)
                  > final.ToList().FindLastIndex(s => s.Key.Contains(':')));
    }

    [Fact]
    public void TestEverySetGetsItsSixSteps()
    {
        var (final, _) = RunWithStages(Path.Combine(_fixture.OutDir, "stages-perset"), analyze: false);

        List<string> setKeys = final.Where(s => s.Key.Contains(':'))
            .Select(s => s.Key.Split(':')[0]).Distinct().ToList();

        Assert.NotEmpty(setKeys);
        foreach (string set in setKeys)
        {
            Assert.Equal(new[]
            {
                StageKeys.Load(set), StageKeys.Check1(set), StageKeys.Migrations(set),
                StageKeys.Check2(set), StageKeys.Validate(set), StageKeys.Export(set),
            }, final.Where(s => s.Key.StartsWith(set + ":")).Select(s => s.Key));
        }
    }

    [Fact]
    public void TestFinishedStagesCarryADuration()
    {
        var (final, _) = RunWithStages(Path.Combine(_fixture.OutDir, "stages-timed"), analyze: false);

        // a step that never ran has nothing to time; every step that completed
        // must report how long it took, or the stage list shows blank durations
        List<RunStage> ran = final.Where(s => s.Status == StageStatus.Done).ToList();
        Assert.NotEmpty(ran);
        foreach (RunStage s in ran)
        {
            Assert.NotNull(s.Seconds);
            Assert.True(s.Seconds >= 0, $"{s.Key} reported {s.Seconds}s");
        }
    }

    [Fact]
    public void TestTheStageListOnlyEverGrows()
    {
        // the screen counts "step n of m", so m must not shrink mid-run
        var (_, counts) = RunWithStages(Path.Combine(_fixture.OutDir, "stages-growth"), analyze: false);

        Assert.NotEmpty(counts);
        for (int i = 1; i < counts.Count; i++)
            Assert.True(counts[i] >= counts[i - 1], $"count fell from {counts[i - 1]} to {counts[i]}");
    }

    [Fact]
    public void TestAnalysisIsPlannedAndSkippedWhenNoAnalystIsGiven()
    {
        var (final, _) = RunWithStages(Path.Combine(_fixture.OutDir, "stages-analyze"), analyze: true);

        RunStage analysis = final.Single(s => s.Key == StageKeys.Analysis);
        Assert.Equal(StageStatus.Skipped, analysis.Status);

        // the memo depends on analysis output, so it is planned but never reached
        Assert.Equal(StageStatus.Skipped, final.Single(s => s.Key == StageKeys.Memo).Status);
    }

    [Fact]
    public void TestNoAnalysisStagesArePlannedWhenNotAnalysing()
    {
        var (final, _) = RunWithStages(Path.Combine(_fixture.OutDir, "stages-noanalyze"), analyze: false);

        Assert.DoesNotContain(final, s => s.Key == StageKeys.Analysis);
        Assert.DoesNotContain(final, s => s.Key == StageKeys.Memo);
    }

    [Fact]
    public void TestAFailedDiscoveryMarksTheStageFailed()
    {
        StageReporter reporter = new();
        ReconciliationEngine engine = new();
        string empty = Path.Combine(_fixture.OutDir, "stages-empty-input");
        Directory.CreateDirectory(empty);

        Assert.ThrowsAny<Exception>(() => engine.Run(
            empty, Path.Combine(_fixture.OutDir, "stages-empty-out"),
            logger: (_, _) => { }, analyze: false, analyst: null, stages: reporter));

        Assert.Equal(StageStatus.Error, reporter.Snapshot().Single().Status);
    }

    [Fact]
    public void TestARunWithoutAReporterStillWorks()
    {
        // the CLI passes no reporter at all
        ReconciliationEngine engine = new();
        ReconciliationRunResult result = engine.Run(
            _fixture.RootDir, Path.Combine(_fixture.OutDir, "stages-none"),
            logger: (_, _) => { }, analyze: false, analyst: null);

        Assert.NotNull(result.Workbook);
    }
}
