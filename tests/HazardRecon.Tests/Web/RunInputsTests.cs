using HazardRecon.Web.Runs;
using Xunit;

namespace HazardRecon.Tests.Web;

public class RunInputsTests
{
    private static RunFileRecord Input(string relativePath, string? role = null,
        string? originalName = null, long size = 10) => new()
    {
        Kind = "input", RelativePath = relativePath, StoragePath = "s/" + relativePath,
        SizeBytes = size, Role = role, OriginalName = originalName
    };

    [Fact]
    public void TestGroupsInputsBySetIndexInOrder()
    {
        var files = new[]
        {
            Input("1/IFRS9.csv", "exposure", "JULY.csv"),
            Input("0/IFRS9.csv", "exposure", "JUNE.csv"),
        };

        var sets = RunInputs.Describe(files, new[] { "JUNE", "JULY" });

        Assert.Equal(new[] { 0, 1 }, sets.Select(s => s.Index).ToArray());
        Assert.Equal("JUNE", sets[0].Label);
        Assert.Equal("JULY", sets[1].Label);
    }

    [Fact]
    public void TestUsesTheOriginalNameAndRecordedRole()
    {
        var sets = RunInputs.Describe(
            new[] { Input("0/writeoff.csv", "writeoff", "2026_WRITEOFF.csv", 9000) },
            new[] { "JUNE" });

        RunInputFile file = Assert.Single(sets[0].Files);
        Assert.Equal("writeoff", file.Role);
        Assert.Equal("2026_WRITEOFF.csv", file.Name);
        Assert.Equal(9000, file.SizeBytes);
    }

    [Fact]
    public void TestFallsBackToTheCanonicalNameAndDerivedRoleForOlderRows()
    {
        // written before role/original_name existed
        var sets = RunInputs.Describe(
            new[]
            {
                Input("0/IFRS9.csv"), Input("0/writeoff.csv"),
                Input("0/scenario.json"), Input("0/debug.zip"),
            },
            new[] { "JUNE" });

        Assert.Equal(
            new[] { "debug", "exposure", "scenario", "writeoff" },
            sets[0].Files.Select(f => f.Role).OrderBy(r => r).ToArray());

        Assert.Equal("IFRS9.csv", sets[0].Files.Single(f => f.Role == "exposure").Name);
        Assert.Equal("debug.zip", sets[0].Files.Single(f => f.Role == "debug").Name);
    }

    [Fact]
    public void TestAnythingUnrecognisedInASetIsADebugFile()
    {
        // debug files keep their own names, so they cannot be matched by name -
        // they are what is left over
        var sets = RunInputs.Describe(
            new[] { Input("0/lgd_defaults.csv"), Input("0/pd_scored.csv") },
            new[] { "JUNE" });

        Assert.All(sets[0].Files, f => Assert.Equal("debug", f.Role));
    }

    [Fact]
    public void TestIgnoresOutputRows()
    {
        var files = new[]
        {
            Input("0/IFRS9.csv", "exposure", "JUNE.csv"),
            new RunFileRecord { Kind = "output", RelativePath = "workbook.xlsx", StoragePath = "s", SizeBytes = 1 },
        };

        var sets = RunInputs.Describe(files, new[] { "JUNE" });

        Assert.Single(Assert.Single(sets).Files);
    }

    [Fact]
    public void TestASetWithNoLabelStillDescribesItself()
    {
        var sets = RunInputs.Describe(new[] { Input("2/IFRS9.csv", "exposure", "X.csv") }, Array.Empty<string>());

        Assert.Equal(2, sets[0].Index);
        Assert.Equal("Set 3", sets[0].Label);
    }
}
