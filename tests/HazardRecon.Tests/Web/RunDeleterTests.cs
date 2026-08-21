using HazardRecon.Web.Runs;
using Xunit;

namespace HazardRecon.Tests.Web;

/// <summary>
/// What deleting a run actually removes, and in what order - the endpoint's HTTP
/// behaviour is covered separately in DeleteRunEndpointTests.
/// </summary>
public class RunDeleterTests : IDisposable
{
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly string _runsDir;
    private readonly FakeRunStore _runs = new();
    private readonly FakeRunFileStore _runFiles = new();
    private readonly FakeFileStore _storage = new();

    public RunDeleterTests()
    {
        _runsDir = Path.Combine(Path.GetTempPath(), "hr-deleter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_runsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_runsDir)) Directory.Delete(_runsDir, recursive: true);
    }

    private RunDeleter Deleter() => new(_runs, _runFiles, _storage, _runsDir);

    /// <summary>A run with two indexed objects, a working folder and a row.</summary>
    private async Task<Guid> SeedRunAsync()
    {
        RunRecord run = await _runs.CreateAsync(User, "June 2026 book", RunTypeLookup.Lending, new[] { "JUN2026" });

        string inputPath = $"{User}/{run.Id}/input/0/IFRS9.csv";
        string outputPath = $"{User}/{run.Id}/output/hazard_rate_reconciliation.xlsx";

        foreach (string path in new[] { inputPath, outputPath })
        {
            using MemoryStream content = new(new byte[] { 1, 2, 3 });
            await _storage.UploadAsync(path, content, "text/csv");
        }

        await _runFiles.AddAsync(new[]
        {
            new RunFileRecord { RunId = run.Id, UserId = User, Kind = "input", RelativePath = "0/IFRS9.csv", StoragePath = inputPath },
            new RunFileRecord { RunId = run.Id, UserId = User, Kind = "output", RelativePath = "hazard_rate_reconciliation.xlsx", StoragePath = outputPath },
        });

        string folder = Path.Combine(_runsDir, run.Id.ToString(), "output");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "hazard_rate_reconciliation.xlsx"), "workbook");

        return run.Id;
    }

    [Fact]
    public async Task Removes_the_run_row()
    {
        Guid runId = await SeedRunAsync();

        await Deleter().DeleteAsync(runId, User);

        Assert.Null(await _runs.GetAsync(runId, User));
    }

    [Fact]
    public async Task Deletes_every_indexed_object_by_its_exact_path()
    {
        Guid runId = await SeedRunAsync();

        await Deleter().DeleteAsync(runId, User);

        Assert.Contains($"{User}/{runId}/input/0/IFRS9.csv", _storage.DeletedPaths);
        Assert.Contains($"{User}/{runId}/output/hazard_rate_reconciliation.xlsx", _storage.DeletedPaths);
        Assert.Empty(_storage.Objects);
    }

    [Fact]
    public async Task Sweeps_the_input_and_output_prefixes_for_unindexed_objects()
    {
        Guid runId = await SeedRunAsync();

        // reached storage but never made it into run_files, which RunPersister
        // tolerates by design - only a prefix sweep can find it
        using MemoryStream orphan = new(new byte[] { 9 });
        await _storage.UploadAsync($"{User}/{runId}/output/orphan.csv", orphan, "text/csv");

        await Deleter().DeleteAsync(runId, User);

        Assert.Contains($"{User}/{runId}/input", _storage.DeletedPrefixes);
        Assert.Contains($"{User}/{runId}/output", _storage.DeletedPrefixes);
        Assert.Empty(_storage.Objects);
    }

    [Fact]
    public async Task Deletes_the_working_folder_on_disk()
    {
        Guid runId = await SeedRunAsync();
        string folder = Path.Combine(_runsDir, runId.ToString());
        Assert.True(Directory.Exists(folder));

        await Deleter().DeleteAsync(runId, User);

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task A_run_with_no_working_folder_deletes_cleanly()
    {
        // a run reopened from history on a later process has nothing local left
        RunRecord run = await _runs.CreateAsync(User, "June 2026 book", RunTypeLookup.Lending, new[] { "JUN2026" });

        await Deleter().DeleteAsync(run.Id, User);

        Assert.Null(await _runs.GetAsync(run.Id, User));
    }

    [Fact]
    public async Task A_storage_failure_leaves_the_run_listed_so_it_can_be_deleted_again()
    {
        Guid runId = await SeedRunAsync();
        _storage.FailDeletesContaining = "IFRS9.csv";

        await Assert.ThrowsAsync<IOException>(() => Deleter().DeleteAsync(runId, User));

        // the row goes last precisely so this is still true
        Assert.NotNull(await _runs.GetAsync(runId, User));
        Assert.True(Directory.Exists(Path.Combine(_runsDir, runId.ToString())));
    }

    [Fact]
    public async Task Another_users_run_is_left_alone()
    {
        Guid runId = await SeedRunAsync();
        Guid intruder = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await Deleter().DeleteAsync(runId, intruder);

        // nothing of the owner's was touched: the row is scoped by owner, and the
        // file index turns up nothing for the wrong user
        Assert.NotNull(await _runs.GetAsync(runId, User));
        Assert.Empty(_storage.DeletedPaths);
    }
}
