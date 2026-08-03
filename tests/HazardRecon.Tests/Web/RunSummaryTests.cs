using HazardRecon.Web.Runs;
using Xunit;

namespace HazardRecon.Tests.Web;

public class RunSummaryTests
{
    private static RunSetResultRecord Set(int untraced = 0, double traceRate = 0, int woInWindow = 0) => new()
    {
        UntracedTotal = untraced,
        TraceRate = traceRate,
        WoInWindow = woInWindow
    };

    [Fact]
    public void TestSetsAreCountedAndUntracedSummed()
    {
        RunSummary s = RunSummary.From(new[]
        {
            Set(untraced: 12, traceRate: 0.975),
            Set(untraced: 30, traceRate: 0.925)
        });

        Assert.Equal(2, s.Sets);
        Assert.Equal(42, s.Untraced);
        Assert.Equal(95.0, s.TraceRate);
    }

    [Fact]
    public void TestExceptionsSumInWindowWriteOffsAcrossSets()
    {
        // "exceptions" in this product means write-offs inside the scoring window
        // that never reached default - what the run detail calls the priority ones
        RunSummary s = RunSummary.From(new[]
        {
            Set(woInWindow: 9),
            Set(woInWindow: 32)
        });

        Assert.Equal(41, s.Exceptions);
    }

    [Fact]
    public void TestTraceRateIsAveragedNotSummed()
    {
        // summing would show 195% traced, which is the obvious way to get this wrong
        RunSummary s = RunSummary.From(new[] { Set(traceRate: 1.0), Set(traceRate: 0.95) });

        Assert.Equal(97.5, s.TraceRate);
    }

    [Fact]
    public void TestAResultWithNoSetsIsEmpty()
    {
        Assert.Equal(RunSummary.Empty, RunSummary.From(Array.Empty<RunSetResultRecord>()));
    }

    [Fact]
    public void TestNullResultIsEmpty()
    {
        // a run that failed or is still going has no set results at all
        Assert.Equal(RunSummary.Empty, RunSummary.From(null));
    }
}
