using System.Text;
using HazardRecon.Web.Uploads;
using Xunit;

namespace HazardRecon.Tests.Web;

public class SetFileReceiverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "hr-setfile-tests", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static SetFileItem Item(int set, SetFileKind kind, string originalName, string content = "x") =>
        new(set, kind, originalName, new MemoryStream(Encoding.UTF8.GetBytes(content)), content.Length);

    private static SetFileItem Sized(int set, SetFileKind kind, string originalName, long length) =>
        new(set, kind, originalName, new MemoryStream(), length);

    private static IReadOnlyList<SetFileItem> FullSet(int index = 0) => new[]
    {
        Item(index, SetFileKind.Exposure, "IFRS9 FILE JUNE 2026.csv", "a,b\n1,2\n"),
        Item(index, SetFileKind.Writeoff, "2026_WRITEOFF.csv", "c,d\n3,4\n"),
        Item(index, SetFileKind.Debug, "debug.zip", "zipbytes"),
        Item(index, SetFileKind.Scenario, "scenario.json", "{}"),
    };

    [Fact]
    public async Task TestEachFileLandsUnderItsCanonicalName()
    {
        SetReceiveOutcome result = await new SetFileReceiver().ReceiveAsync(_root, FullSet());

        Assert.True(result.Ok, result.Error);
        ReceivedSet set = Assert.Single(result.Sets);
        Assert.True(File.Exists(Path.Combine(set.Root, "IFRS9.csv")));
        Assert.True(File.Exists(Path.Combine(set.Root, "writeoff.csv")));
        Assert.True(File.Exists(Path.Combine(set.Root, "debug.zip")));
        Assert.True(File.Exists(Path.Combine(set.Root, "scenario.json")));
        Assert.Equal("a,b\n1,2\n", File.ReadAllText(Path.Combine(set.Root, "IFRS9.csv")));
    }

    [Fact]
    public async Task TestTheLabelDefaultsToTheExposureFileNameWithoutExtension()
    {
        SetReceiveOutcome result = await new SetFileReceiver().ReceiveAsync(_root, FullSet());

        Assert.Equal("IFRS9 FILE JUNE 2026", result.Sets[0].Label);
        Assert.Equal("IFRS9 FILE JUNE 2026.csv", result.Sets[0].ExposureFileName);
        Assert.Equal("2026_WRITEOFF.csv", result.Sets[0].WriteOffFileName);
    }

    [Fact]
    public async Task TestALooseDebugFileSetKeepsItsOwnNames()
    {
        SetReceiveOutcome result = await new SetFileReceiver().ReceiveAsync(_root, new[]
        {
            Item(0, SetFileKind.Exposure, "ifrs9.csv"),
            Item(0, SetFileKind.Writeoff, "wo.csv"),
            Item(0, SetFileKind.Debug, "lgd_defaults.csv"),
            Item(0, SetFileKind.Debug, "pd_scored.csv"),
            Item(0, SetFileKind.Debug, "debug.json"),
            Item(0, SetFileKind.Scenario, "scenario.json"),
        });

        Assert.True(result.Ok, result.Error);
        Assert.True(File.Exists(Path.Combine(result.Sets[0].Root, "lgd_defaults.csv")));
        Assert.True(File.Exists(Path.Combine(result.Sets[0].Root, "pd_scored.csv")));
        Assert.True(File.Exists(Path.Combine(result.Sets[0].Root, "debug.json")));
    }

    [Fact]
    public async Task TestTwoSetsDoNotCollide()
    {
        List<SetFileItem> items = FullSet(0).Concat(FullSet(1)).ToList();

        SetReceiveOutcome result = await new SetFileReceiver().ReceiveAsync(_root, items);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, result.Sets.Count);
        Assert.NotEqual(result.Sets[0].Root, result.Sets[1].Root);
    }

    [Fact]
    public async Task TestAMissingExposureFileIsRejected()
    {
        SetReceiveOutcome result = await new SetFileReceiver().ReceiveAsync(_root, new[]
        {
            Item(0, SetFileKind.Writeoff, "wo.csv"),
            Item(0, SetFileKind.Debug, "debug.zip"),
            Item(0, SetFileKind.Scenario, "scenario.json"),
        });

        Assert.False(result.Ok);
        Assert.Contains("exposure", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestASetWithNoWriteOffFileIsAccepted()
    {
        // the engine copes without one - check 2 is skipped and check 1 traces
        // through the IFRS9 flag alone - so the receiver does not stand in the way
        SetReceiveOutcome result = await new SetFileReceiver().ReceiveAsync(_root, new[]
        {
            Item(0, SetFileKind.Exposure, "IFRS9 FILE JUNE 2026.csv", "a,b\n1,2\n"),
            Item(0, SetFileKind.Debug, "lgd_defaults.csv"),
            Item(0, SetFileKind.Scenario, "scenario.json"),
        });

        Assert.True(result.Ok, result.Error);
        ReceivedSet set = Assert.Single(result.Sets);
        Assert.Null(set.WriteOffFileName);
        Assert.False(File.Exists(Path.Combine(set.Root, "writeoff.csv")));

        // and the rest of the set still lands where discovery looks for it
        Assert.True(File.Exists(Path.Combine(set.Root, "IFRS9.csv")));
        Assert.True(File.Exists(Path.Combine(set.Root, "lgd_defaults.csv")));
        Assert.Equal("IFRS9 FILE JUNE 2026", set.Label);
    }

    [Fact]
    public async Task TestNoFilesIsRefused()
    {
        SetReceiveOutcome result = await new SetFileReceiver().ReceiveAsync(_root, Array.Empty<SetFileItem>());

        Assert.False(result.Ok);
        Assert.Contains("at least one", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestMoreThanFourSetsIsRefused()
    {
        List<SetFileItem> items = Enumerable.Range(0, 5).SelectMany(i => FullSet(i)).ToList();

        SetReceiveOutcome result = await new SetFileReceiver().ReceiveAsync(_root, items);

        Assert.False(result.Ok);
        Assert.Contains("maximum of 4", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestAnOversizedSetIsRefusedBeforeAnythingIsWritten()
    {
        long limit = 10L * 1024 * 1024;

        SetReceiveOutcome result = await new SetFileReceiver(limit).ReceiveAsync(_root, new[]
        {
            Sized(0, SetFileKind.Exposure, "IFRS9.csv", limit / 2),
            Sized(0, SetFileKind.Writeoff, "wo.csv", limit),
        });

        Assert.False(result.Ok);
        Assert.Contains("limit is 10 MB", result.Error!);
        Assert.False(Directory.Exists(Path.Combine(_root, "0")));
    }
}
