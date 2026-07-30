using HazardRecon.Web.Files;

namespace HazardRecon.Web.Runs;

/// <summary>
/// Deletes the uploaded inputs of runs older than the retention window.
///
/// Inputs are the only large objects a run holds - outputs and metadata are
/// small and kept forever - so this is what stops storage growing without bound.
/// Re-running a month-old upload is rare; the history entry survives either way,
/// flagged so the UI can say the inputs have expired.
/// </summary>
public class InputPurger
{
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(30);

    private readonly IRunStore _runs;
    private readonly IRunFileStore _runFiles;
    private readonly IFileStore _storage;

    public InputPurger(IRunStore runs, IRunFileStore runFiles, IFileStore storage)
    {
        _runs = runs;
        _runFiles = runFiles;
        _storage = storage;
    }

    public record PurgeOutcome(int Purged, IReadOnlyList<string> Failed);

    public async Task<PurgeOutcome> PurgeAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        DateTimeOffset cutoff = now - RetentionWindow;
        IReadOnlyList<RunRecord> due = await _runs.ListWithUnpurgedInputsAsync(cutoff, ct);

        int purged = 0;
        List<string> failed = new();

        foreach (RunRecord run in due)
        {
            try
            {
                // the input prefix only - outputs live beside it under the same
                // run and must survive
                await _storage.DeletePrefixAsync($"{run.UserId}/{run.Id}/input", ct);
                await _runFiles.DeleteInputsAsync(run.Id, ct);

                // stamped last: if anything above failed, the run stays on the
                // list and is retried on the next sweep rather than being
                // silently marked done
                await _runs.MarkInputsPurgedAsync(run.Id, ct);
                purged++;
            }
            catch (Exception ex)
            {
                failed.Add($"{run.Id}: {ex.Message}");
            }
        }

        return new PurgeOutcome(purged, failed);
    }
}
