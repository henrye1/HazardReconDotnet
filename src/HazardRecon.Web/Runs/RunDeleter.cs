using HazardRecon.Web.Files;

namespace HazardRecon.Web.Runs;

/// <summary>
/// Removes one run completely: its stored objects, its working folder on disk
/// and its database record.
///
/// Split from the endpoint for the same reason InputPurger is split from its
/// hosted service - what gets deleted is worth testing without a web host. The
/// order matters and is the reverse of how a run is built up: objects, then
/// disk, then the row. The row goes last so a storage failure leaves the run
/// listed and deletable again, rather than orphaning objects behind a record
/// that no longer exists.
/// </summary>
public class RunDeleter
{
    private readonly IRunStore _runs;
    private readonly IRunFileStore _runFiles;
    private readonly IFileStore _storage;
    private readonly string _runsDirectory;

    public RunDeleter(IRunStore runs, IRunFileStore runFiles, IFileStore storage, string runsDirectory)
    {
        _runs = runs;
        _runFiles = runFiles;
        _storage = storage;
        _runsDirectory = runsDirectory;
    }

    public async Task DeleteAsync(Guid runId, Guid userId, CancellationToken ct = default)
    {
        // run_files knows the exact path of everything that was uploaded, which a
        // prefix listing cannot reproduce: inputs are nested one folder per set,
        // and Supabase only lists a single level
        IReadOnlyList<RunFileRecord> files = await _runFiles.ListAsync(runId, userId, ct);
        await _storage.DeletePathsAsync(files.Select(f => f.StoragePath).ToList(), ct);

        // and a sweep of the two prefixes as well, for anything that reached
        // storage but never made it into the index - RunPersister swallows an
        // indexing failure by design, so those objects have nothing pointing at
        // them and only a prefix can find them
        await _storage.DeletePrefixAsync($"{userId}/{runId}/input", ct);
        await _storage.DeletePrefixAsync($"{userId}/{runId}/output", ct);

        DeleteWorkingFolder(runId);

        await _runs.DeleteAsync(runId, userId, ct);
    }

    /// <summary>
    /// The run's own folder under the runs directory. Absent for a run restored
    /// from history on a later process, which is not a failure - there is simply
    /// nothing local left to remove.
    /// </summary>
    private void DeleteWorkingFolder(Guid runId)
    {
        string folder = Path.Combine(_runsDirectory, runId.ToString());
        if (!Directory.Exists(folder)) return;

        Directory.Delete(folder, recursive: true);
    }
}
