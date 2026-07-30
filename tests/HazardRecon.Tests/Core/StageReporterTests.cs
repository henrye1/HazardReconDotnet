using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests.Core;

public class StageReporterTests
{
    private static (string, string, string) Step(string key) => (key, key + " name", key + " detail");

    [Fact]
    public void TestPlannedStagesStartPending()
    {
        StageReporter r = new();
        r.Plan(Step("a"), Step("b"));

        Assert.Equal(new[] { "a", "b" }, r.Snapshot().Select(s => s.Key));
        Assert.All(r.Snapshot(), s => Assert.Equal(StageStatus.Pending, s.Status));
        Assert.All(r.Snapshot(), s => Assert.Null(s.Seconds));
    }

    [Fact]
    public void TestPlanningTheSameKeyTwiceDoesNotDuplicateIt()
    {
        // the per-set plan is published per set, so a repeated key is expected
        StageReporter r = new();
        r.Plan(Step("a"));
        r.Plan(Step("a"), Step("b"));

        Assert.Equal(new[] { "a", "b" }, r.Snapshot().Select(s => s.Key));
    }

    [Fact]
    public void TestPlanOrderIsPreserved()
    {
        StageReporter r = new();
        r.Plan(Step("discover"));
        r.Plan(Step("set:load"), Step("set:check1"));
        r.Plan(Step("workbook"));

        Assert.Equal(new[] { "discover", "set:load", "set:check1", "workbook" },
            r.Snapshot().Select(s => s.Key));
    }

    [Fact]
    public void TestTrackMarksDoneAndTimesTheStep()
    {
        StageReporter r = new();
        r.Plan(Step("a"));

        int result = r.Track("a", () => 42);

        Assert.Equal(42, result);
        RunStage stage = r.Snapshot().Single();
        Assert.Equal(StageStatus.Done, stage.Status);
        Assert.NotNull(stage.Seconds);
        Assert.True(stage.Seconds >= 0);
    }

    [Fact]
    public void TestTrackMarksErrorAndRethrows()
    {
        StageReporter r = new();
        r.Plan(Step("a"));

        Assert.Throws<InvalidOperationException>(() =>
            r.Track("a", () => throw new InvalidOperationException("boom")));

        // a crashed step must not be left running, or the screen spins forever
        Assert.Equal(StageStatus.Error, r.Snapshot().Single().Status);
        Assert.NotNull(r.Snapshot().Single().Seconds);
    }

    [Fact]
    public void TestEndAcceptsAnExplicitStatus()
    {
        StageReporter r = new();
        r.Plan(Step("a"), Step("b"));
        r.Begin("a");
        r.End("a", StageStatus.Warn);
        r.End("b", StageStatus.Skipped);

        Assert.Equal(StageStatus.Warn, r.Snapshot()[0].Status);
        Assert.Equal(StageStatus.Skipped, r.Snapshot()[1].Status);
    }

    [Fact]
    public void TestClosingATrackedStageAgainKeepsItsDuration()
    {
        // the validate stage does this: Track times it, then the result decides
        // whether it passed, warned or did not apply
        StageReporter r = new();
        r.Plan(Step("a"));
        r.Track("a", () => 0);
        double? timed = r.Snapshot().Single().Seconds;

        r.End("a", StageStatus.Warn);

        Assert.Equal(StageStatus.Warn, r.Snapshot().Single().Status);
        Assert.Equal(timed, r.Snapshot().Single().Seconds);
        Assert.NotNull(r.Snapshot().Single().Seconds);
    }

    [Fact]
    public void TestSettleClosesRunningAndPendingStages()
    {
        StageReporter r = new();
        r.Plan(Step("a"), Step("b"), Step("c"));
        r.Track("a", () => 0);
        r.Begin("b");

        r.Settle(StageStatus.Error);

        Assert.Equal(StageStatus.Done, r.Snapshot()[0].Status);
        Assert.Equal(StageStatus.Error, r.Snapshot()[1].Status);
        // never reached, so it did not fail - it just did not happen
        Assert.Equal(StageStatus.Skipped, r.Snapshot()[2].Status);
    }

    [Fact]
    public void TestSettleLeavesFinishedStagesAlone()
    {
        StageReporter r = new();
        r.Plan(Step("a"), Step("b"));
        r.End("a", StageStatus.Warn);
        r.Track("b", () => 0);

        r.Settle(StageStatus.Error);

        Assert.Equal(StageStatus.Warn, r.Snapshot()[0].Status);
        Assert.Equal(StageStatus.Done, r.Snapshot()[1].Status);
    }

    [Fact]
    public void TestUnknownKeysAreIgnored()
    {
        // an optional step that was never planned must not throw or appear
        StageReporter r = new();
        r.Plan(Step("a"));
        r.Begin("nope");
        r.End("nope");

        Assert.Single(r.Snapshot());
    }

    [Fact]
    public void TestTheWholeListIsPublishedOnEveryChange()
    {
        List<IReadOnlyList<RunStage>> seen = new();
        StageReporter r = new(list => seen.Add(list));

        r.Plan(Step("a"), Step("b"));
        r.Begin("a");
        r.End("a");

        Assert.Equal(3, seen.Count);
        Assert.All(seen, list => Assert.Equal(2, list.Count));
        // snapshots are independent, so a later change cannot rewrite an earlier one
        Assert.Equal(StageStatus.Pending, seen[0][0].Status);
        Assert.Equal(StageStatus.Running, seen[1][0].Status);
        Assert.Equal(StageStatus.Done, seen[2][0].Status);
    }

    [Fact]
    public void TestAThrowingCallbackCannotBreakTheRun()
    {
        StageReporter r = new(_ => throw new InvalidOperationException("subscriber is broken"));
        r.Plan(Step("a"));

        Assert.Equal(7, r.Track("a", () => 7));
        Assert.Equal(StageStatus.Done, r.Snapshot().Single().Status);
    }
}
