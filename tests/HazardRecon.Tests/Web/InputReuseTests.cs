using System.Text;
using HazardRecon.Web.Runs;
using HazardRecon.Web.Uploads;
using Xunit;

namespace HazardRecon.Tests.Web;

public class InputReuseTests
{
    private static string NewTempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hr-reuse", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (FakeFileStore Store, RunFileRecord Record) Stored(
        string relativePath, string role, string originalName, string content)
    {
        FakeFileStore store = new();
        string storagePath = "u/r/input/" + relativePath;
        store.Objects[storagePath] = Encoding.UTF8.GetBytes(content);

        return (store, new RunFileRecord
        {
            Kind = "input", RelativePath = relativePath, StoragePath = storagePath,
            SizeBytes = content.Length, Role = role, OriginalName = originalName
        });
    }

    [Fact]
    public async Task TestRebuildsARequestedRoleAsAnUploadItem()
    {
        var (store, record) = Stored("0/IFRS9.csv", "exposure", "IFRS9 JUNE.csv", "a,b\n1,2\n");

        ReuseOutcome outcome = await InputReuse.MaterialiseAsync(
            new[] { new ReuseRequest(0, new[] { "exposure" }) },
            new[] { record }, store, NewTempRoot());

        Assert.True(outcome.Ok, outcome.Error);
        SetFileItem item = Assert.Single(outcome.Items);
        Assert.Equal(0, item.SetIndex);
        Assert.Equal(SetFileKind.Exposure, item.Kind);
        // the original name travels, so the receiver labels the set as before
        Assert.Equal("IFRS9 JUNE.csv", item.OriginalFileName);
        Assert.Equal(8, item.Length);

        using StreamReader reader = new(item.Content);
        Assert.Equal("a,b\n1,2\n", await reader.ReadToEndAsync());

        foreach (IDisposable d in outcome.Open) d.Dispose();
    }

    [Fact]
    public async Task TestRebuildsEveryDebugFileOfASet()
    {
        FakeFileStore store = new();
        store.Objects["u/r/input/0/lgd_defaults.csv"] = Encoding.UTF8.GetBytes("x");
        store.Objects["u/r/input/0/pd_scored.csv"] = Encoding.UTF8.GetBytes("y");

        var records = new[]
        {
            new RunFileRecord { Kind = "input", RelativePath = "0/lgd_defaults.csv",
                StoragePath = "u/r/input/0/lgd_defaults.csv", SizeBytes = 1, Role = "debug", OriginalName = "lgd_defaults.csv" },
            new RunFileRecord { Kind = "input", RelativePath = "0/pd_scored.csv",
                StoragePath = "u/r/input/0/pd_scored.csv", SizeBytes = 1, Role = "debug", OriginalName = "pd_scored.csv" },
        };

        ReuseOutcome outcome = await InputReuse.MaterialiseAsync(
            new[] { new ReuseRequest(0, new[] { "debug" }) }, records, store, NewTempRoot());

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(2, outcome.Items.Count);
        Assert.All(outcome.Items, i => Assert.Equal(SetFileKind.Debug, i.Kind));

        foreach (IDisposable d in outcome.Open) d.Dispose();
    }

    [Fact]
    public async Task TestARoleThePreviousRunDoesNotHaveIsRefusedByName()
    {
        var (store, record) = Stored("0/IFRS9.csv", "exposure", "IFRS9 JUNE.csv", "a\n1\n");

        ReuseOutcome outcome = await InputReuse.MaterialiseAsync(
            new[] { new ReuseRequest(0, new[] { "exposure", "writeoff" }) },
            new[] { record }, store, NewTempRoot());

        Assert.False(outcome.Ok);
        Assert.Contains("writeoff", outcome.Error);
        Assert.Empty(outcome.Items);
    }

    [Fact]
    public async Task TestAnObjectMissingFromStorageIsRefusedByName()
    {
        var (store, record) = Stored("0/IFRS9.csv", "exposure", "IFRS9 JUNE.csv", "a\n1\n");
        store.Objects.Clear();   // indexed, but gone from the bucket

        ReuseOutcome outcome = await InputReuse.MaterialiseAsync(
            new[] { new ReuseRequest(0, new[] { "exposure" }) },
            new[] { record }, store, NewTempRoot());

        Assert.False(outcome.Ok);
        Assert.Contains("exposure", outcome.Error);
    }

    [Fact]
    public async Task TestOnlyTheRequestedRolesAreRebuilt()
    {
        FakeFileStore store = new();
        store.Objects["u/r/input/0/IFRS9.csv"] = Encoding.UTF8.GetBytes("a");
        store.Objects["u/r/input/0/writeoff.csv"] = Encoding.UTF8.GetBytes("b");

        var records = new[]
        {
            new RunFileRecord { Kind = "input", RelativePath = "0/IFRS9.csv",
                StoragePath = "u/r/input/0/IFRS9.csv", SizeBytes = 1, Role = "exposure", OriginalName = "e.csv" },
            new RunFileRecord { Kind = "input", RelativePath = "0/writeoff.csv",
                StoragePath = "u/r/input/0/writeoff.csv", SizeBytes = 1, Role = "writeoff", OriginalName = "w.csv" },
        };

        // the write-off file is being replaced, so only the exposure file is reused
        ReuseOutcome outcome = await InputReuse.MaterialiseAsync(
            new[] { new ReuseRequest(0, new[] { "exposure" }) }, records, store, NewTempRoot());

        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal(SetFileKind.Exposure, Assert.Single(outcome.Items).Kind);

        foreach (IDisposable d in outcome.Open) d.Dispose();
    }
}
