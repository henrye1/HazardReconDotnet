using System.Text;
using HazardRecon.Web.Runs;
using HazardRecon.Web.Uploads;
using Xunit;

namespace HazardRecon.Tests.Web;

public class RunPersisterTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RunId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "hr-persist-tests", Guid.NewGuid().ToString("N")[..8]);

    public RunPersisterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void WriteFile(string relative, string content)
    {
        string full = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task TestEveryFileIsUploadedAndIndexedUnderTheUsersPrefix()
    {
        WriteFile("dashboard.html", "<html></html>");
        WriteFile("workbook.xlsx", "binary");
        WriteFile("nested/check1.csv", "a,b\n1,2\n");

        FakeFileStore files = new();
        FakeRunFileStore index = new();

        RunPersister.PersistOutcome outcome = await new RunPersister(files, index)
            .PersistDirectoryAsync(UserId, RunId, "output", _dir);

        Assert.Equal(3, outcome.Stored);
        Assert.Empty(outcome.Failed);

        // the user id leads the path, so ownership is legible in the bucket and a
        // per-user purge is a prefix delete
        Assert.Contains($"{UserId}/{RunId}/output/dashboard.html", files.Objects.Keys);
        Assert.Contains($"{UserId}/{RunId}/output/nested/check1.csv", files.Objects.Keys);
        Assert.Equal(3, index.Files.Count);
        Assert.All(index.Files, f => Assert.Equal("output", f.Kind));
    }

    [Fact]
    public async Task TestTheDashboardKeepsItsHtmlContentType()
    {
        // served into an iframe: as octet-stream the frame renders blank, which is
        // the same trap the local file route already documents
        WriteFile("dashboard.html", "<html></html>");

        FakeFileStore files = new();
        RunPersister persister = new(files, new FakeRunFileStore());

        await persister.PersistDirectoryAsync(UserId, RunId, "output", _dir);

        Assert.Single(files.Objects);
    }

    [Fact]
    public async Task TestOneFailedUploadDoesNotLoseTheOthers()
    {
        WriteFile("good1.csv", "a");
        WriteFile("bad.xlsx", "b");
        WriteFile("good2.csv", "c");

        FakeFileStore files = new() { FailUploadsContaining = "bad.xlsx" };
        FakeRunFileStore index = new();

        RunPersister.PersistOutcome outcome = await new RunPersister(files, index)
            .PersistDirectoryAsync(UserId, RunId, "output", _dir);

        Assert.Equal(2, outcome.Stored);
        Assert.Contains("bad.xlsx", outcome.Failed);
        Assert.Equal(2, index.Files.Count);
    }

    [Fact]
    public async Task TestAnIndexFailureIsReportedRatherThanThrown()
    {
        // the caller has a finished run in hand; this must never escape as an
        // exception and turn it into an error
        WriteFile("a.csv", "x");

        FakeRunFileStore index = new() { FailAdd = true };

        RunPersister.PersistOutcome outcome = await new RunPersister(new FakeFileStore(), index)
            .PersistDirectoryAsync(UserId, RunId, "output", _dir);

        Assert.Equal(0, outcome.Stored);
        Assert.NotEmpty(outcome.Failed);
    }

    [Fact]
    public async Task TestAMissingDirectoryIsNotAnError()
    {
        RunPersister.PersistOutcome outcome = await new RunPersister(new FakeFileStore(), new FakeRunFileStore())
            .PersistDirectoryAsync(UserId, RunId, "output", Path.Combine(_dir, "nope"));

        Assert.Equal(0, outcome.Stored);
        Assert.Empty(outcome.Failed);
    }

    [Fact]
    public async Task TestInputsAndOutputsLandInSeparatePrefixes()
    {
        WriteFile("debug.zip", "z");

        FakeFileStore files = new();
        RunPersister persister = new(files, new FakeRunFileStore());

        await persister.PersistDirectoryAsync(UserId, RunId, "input", _dir, setKey: "JUN2026");
        await persister.PersistDirectoryAsync(UserId, RunId, "output", _dir);

        // the 30-day purge deletes the input prefix only, so the split has to hold
        Assert.Contains($"{UserId}/{RunId}/input/debug.zip", files.Objects.Keys);
        Assert.Contains($"{UserId}/{RunId}/output/debug.zip", files.Objects.Keys);
    }

    [Fact]
    public async Task TestRecordsRoleAndOriginalNameForDescribedFiles()
    {
        WriteFile("0/IFRS9.csv", "a\n1\n");
        WriteFile("0/debug.zip", "zip");

        FakeFileStore files = new();
        FakeRunFileStore index = new();

        var described = new Dictionary<string, ReceivedFile>
        {
            ["0/IFRS9.csv"] = new("0/IFRS9.csv", "exposure", "IFRS9 FILE JUNE 2025.csv"),
            ["0/debug.zip"] = new("0/debug.zip", "debug", "debug.zip"),
        };

        await new RunPersister(files, index)
            .PersistDirectoryAsync(UserId, RunId, "input", _dir, describedBy: described);

        RunFileRecord exposure = index.Files.Single(f => f.RelativePath == "0/IFRS9.csv");
        Assert.Equal("exposure", exposure.Role);
        Assert.Equal("IFRS9 FILE JUNE 2025.csv", exposure.OriginalName);
    }

    [Fact]
    public async Task TestLeavesRoleAndNameNullWhenNothingDescribesTheFile()
    {
        // outputs are persisted the same way and have no slot
        WriteFile("workbook.xlsx", "x");

        FakeRunFileStore index = new();
        await new RunPersister(new FakeFileStore(), index)
            .PersistDirectoryAsync(UserId, RunId, "output", _dir);

        RunFileRecord only = Assert.Single(index.Files);
        Assert.Null(only.Role);
        Assert.Null(only.OriginalName);
    }
}
