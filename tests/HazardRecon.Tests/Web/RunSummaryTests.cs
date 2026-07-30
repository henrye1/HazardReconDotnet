using System.Text.Json;
using HazardRecon.Web.Runs;
using Xunit;

namespace HazardRecon.Tests.Web;

public class RunSummaryTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public void TestSetsAreCountedAndUntracedSummed()
    {
        RunSummary s = RunSummary.From(Json("""
        {"sets":[
          {"key":"A","untraced":12,"trace_rate":97.5},
          {"key":"B","untraced":30,"trace_rate":92.5}
        ]}
        """));

        Assert.Equal(2, s.Sets);
        Assert.Equal(42, s.Untraced);
        Assert.Equal(95.0, s.TraceRate);
    }

    [Fact]
    public void TestTraceRateIsAveragedNotSummed()
    {
        // summing would show 195% traced, which is the obvious way to get this wrong
        RunSummary s = RunSummary.From(Json("""
        {"sets":[{"trace_rate":100},{"trace_rate":95}]}
        """));

        Assert.Equal(97.5, s.TraceRate);
    }

    [Fact]
    public void TestAResultWithNoSetsIsEmpty()
    {
        Assert.Equal(RunSummary.Empty, RunSummary.From(Json("""{"sets":[]}""")));
    }

    [Fact]
    public void TestAResultWithoutASetsArrayIsEmpty()
    {
        Assert.Equal(RunSummary.Empty, RunSummary.From(Json("""{"workbook":"x.xlsx"}""")));
    }

    [Fact]
    public void TestNullResultIsEmpty()
    {
        // a run that failed or is still going has no result at all
        Assert.Equal(RunSummary.Empty, RunSummary.From(null));
    }

    [Fact]
    public void TestMissingFieldsOnASetAreTreatedAsZero()
    {
        // an older run stored before a field existed must not throw
        RunSummary s = RunSummary.From(Json("""{"sets":[{"key":"A"},{"key":"B","untraced":5}]}"""));

        Assert.Equal(2, s.Sets);
        Assert.Equal(5, s.Untraced);
        Assert.Equal(0, s.TraceRate);
    }

    [Fact]
    public void TestANonObjectResultIsEmpty()
    {
        Assert.Equal(RunSummary.Empty, RunSummary.From(Json("[]")));
        Assert.Equal(RunSummary.Empty, RunSummary.From(Json("\"done\"")));
    }
}
