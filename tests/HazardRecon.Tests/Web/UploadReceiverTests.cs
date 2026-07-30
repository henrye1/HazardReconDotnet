using System.Text;
using HazardRecon.Web.Uploads;
using Xunit;

namespace HazardRecon.Tests.Web;

public class UploadReceiverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "hr-upload-tests", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static UploadItem Item(int set, string path, string content = "x") =>
        new(set, path, new MemoryStream(Encoding.UTF8.GetBytes(content)), content.Length);

    private static UploadItem Sized(int set, string path, long length) =>
        new(set, path, new MemoryStream(), length);

    [Fact]
    public async Task TestFilesLandUnderTheSetRootWithTheirStructureIntact()
    {
        UploadOutcome result = await new UploadReceiver().ReceiveAsync(_root, new[]
        {
            Item(0, "JUNE 2026 0.5 PERCENT/debug.zip", "zipbytes"),
            Item(0, "JUNE 2026 0.5 PERCENT/_extracted/lgd_defaults.csv", "a,b\n1,2\n"),
        });

        Assert.True(result.Ok, result.Error);
        UploadedSet set = Assert.Single(result.Sets);

        Assert.Equal("JUNE 2026 0.5 PERCENT", set.Label);
        Assert.Equal(2, set.FileCount);
        Assert.True(File.Exists(Path.Combine(set.Root, "debug.zip")));
        Assert.True(File.Exists(Path.Combine(set.Root, "_extracted", "lgd_defaults.csv")));
        Assert.Equal("a,b\n1,2\n", File.ReadAllText(Path.Combine(set.Root, "_extracted", "lgd_defaults.csv")));
    }

    [Fact]
    public async Task TestTheSetRootIsTheFolderTheUserPicked()
    {
        // the discoverer derives the label and set key from this folder's name,
        // so pointing it one level too high or low silently renames every run
        UploadOutcome result = await new UploadReceiver().ReceiveAsync(_root, new[]
        {
            Item(0, "DEBUG FILE 30 JUNE 2026 0.5 PERCENT/scenario.json", "{}"),
        });

        Assert.True(result.Ok, result.Error);
        Assert.Equal("DEBUG FILE 30 JUNE 2026 0.5 PERCENT", Path.GetFileName(result.Sets[0].Root));
    }

    [Fact]
    public async Task TestTwoFoldersSharingANameDoNotCollide()
    {
        UploadOutcome result = await new UploadReceiver().ReceiveAsync(_root, new[]
        {
            Item(0, "DEBUG/a.csv", "first"),
            Item(1, "DEBUG/a.csv", "second"),
        });

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, result.Sets.Count);
        Assert.NotEqual(result.Sets[0].Root, result.Sets[1].Root);
        Assert.Equal("first", File.ReadAllText(Path.Combine(result.Sets[0].Root, "a.csv")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(result.Sets[1].Root, "a.csv")));
    }

    [Fact]
    public async Task TestATraversingPathIsRefusedAndNothingEscapes()
    {
        UploadOutcome result = await new UploadReceiver().ReceiveAsync(_root, new[]
        {
            Item(0, "SET/../../escaped.csv", "pwned"),
        });

        Assert.False(result.Ok);
        Assert.Contains("unsafe", result.Error!, StringComparison.OrdinalIgnoreCase);

        string outside = Path.GetFullPath(Path.Combine(_root, "..", "escaped.csv"));
        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task TestNoFoldersIsRefused()
    {
        UploadOutcome result = await new UploadReceiver().ReceiveAsync(_root, Array.Empty<UploadItem>());

        Assert.False(result.Ok);
        Assert.Contains("at least one", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestMoreThanFourFoldersIsRefused()
    {
        UploadItem[] items = Enumerable.Range(0, 5).Select(i => Item(i, $"SET{i}/a.csv")).ToArray();

        UploadOutcome result = await new UploadReceiver().ReceiveAsync(_root, items);

        Assert.False(result.Ok);
        Assert.Contains("maximum of 4", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestTooManyFilesInOneFolderIsRefused()
    {
        UploadItem[] items = Enumerable.Range(0, UploadReceiver.MaxFilesPerSet + 1)
            .Select(i => Item(0, $"SET/f{i}.csv")).ToArray();

        UploadOutcome result = await new UploadReceiver().ReceiveAsync(_root, items);

        Assert.False(result.Ok);
        Assert.Contains("limit is 500", result.Error!);
    }

    [Fact]
    public async Task TestAnOversizedFolderIsRefusedBeforeAnythingIsWritten()
    {
        long limit = 10L * 1024 * 1024;

        UploadOutcome result = await new UploadReceiver(limit).ReceiveAsync(_root, new[]
        {
            Sized(0, "SET/huge.zip", limit + 1),
        });

        Assert.False(result.Ok);
        Assert.Contains("limit is 10 MB", result.Error!);
        // refused on the declared size, so no disk was consumed proving it
        Assert.False(Directory.Exists(Path.Combine(_root, "0")));
    }

    [Fact]
    public async Task TestEachFolderGetsItsOwnBudget()
    {
        // just under the cap twice over: two sets are not summed into one limit
        long limit = 10L * 1024 * 1024;

        UploadOutcome result = await new UploadReceiver(limit).ReceiveAsync(_root, new[]
        {
            Sized(0, "A/big.zip", limit - 1),
            Sized(1, "B/big.zip", limit - 1),
        });

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, result.Sets.Count);
    }

    [Fact]
    public async Task TestARealSizedDebugFolderIsAccepted()
    {
        // the case that prompted the limit change: a genuine debug folder
        // carrying debug.zip alongside its extracted contents, ~160 MB
        UploadOutcome result = await new UploadReceiver().ReceiveAsync(_root, new[]
        {
            Sized(0, "DEBUG FILE 30 JUNE 2026 3 MONTHS/debug.zip", 80L * 1024 * 1024),
            Sized(0, "DEBUG FILE 30 JUNE 2026 3 MONTHS/_extracted/lgd_defaults.csv", 80L * 1024 * 1024),
        });

        Assert.True(result.Ok, result.Error);
        Assert.Equal("DEBUG FILE 30 JUNE 2026 3 MONTHS", result.Sets[0].Label);
    }
}
