namespace HazardRecon.Web.Runs;

/// <summary>
/// Persistence for runs. Every read is scoped by user id: callers pass the sub
/// claim of a verified token, never a value from the request body.
/// </summary>
public interface IRunStore
{
    /// <param name="name">
    /// What the user called the run. Nullable because the column is; requiring
    /// one is the endpoint's job, not this store's.
    /// </param>
    /// <param name="runType">A code from <see cref="RunTypeLookup"/>, not an id.</param>
    Task<RunRecord> CreateAsync(Guid userId, string? name, string runType,
        IReadOnlyList<string> setLabels, CancellationToken ct = default);

    Task<RunRecord?> GetAsync(Guid runId, Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<RunRecord>> ListAsync(Guid userId, int limit = 50, CancellationToken ct = default);

    Task UpdateStatusAsync(Guid runId, string status, string? error, CancellationToken ct = default);

    /// <summary>Records the model chosen for a run, before it starts.</summary>
    Task SetModelAsync(Guid runId, string? modelId, CancellationToken ct = default);

    /// <summary>
    /// Writes the finished run in one call: status, log, per-set results and
    /// their dashboard/analysis detail, output files and commentary. Calls a
    /// Postgres function rather than patching several tables in sequence,
    /// because those writes must replace a run's prior completion data
    /// atomically - a run's id can be reused on re-run, and a PostgREST call
    /// per table would not be.
    /// </summary>
    Task SaveCompletionAsync(
        Guid runId,
        Guid userId,
        string status,
        string? error,
        RunResultsRecord runResults,
        IReadOnlyList<RunSetResultRecord> setResults,
        IReadOnlyList<LogEntryRecord> log,
        IReadOnlyList<RunOutputFileRecord> outputFiles,
        IReadOnlyList<RunCommentaryLineRecord> commentaryLines,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the run itself. Every child table - files, logs, results, chat,
    /// per-run column maps - is declared "on delete cascade" from runs(id), so
    /// this one statement takes the whole record with it. Filtered by owner as
    /// well as id, so another user's run is a no-op rather than a deletion.
    /// </summary>
    Task DeleteAsync(Guid runId, Guid userId, CancellationToken ct = default);

    Task<int> CountSinceAsync(Guid userId, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>
    /// Marks every row still flagged running as interrupted. Called once at
    /// startup: a restart killed those runs and nothing will ever finish them.
    /// </summary>
    Task<int> MarkRunningAsInterruptedAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs created before the cutoff whose inputs are still held. Not scoped by
    /// user: retention sweeps everyone's.
    /// </summary>
    Task<IReadOnlyList<RunRecord>> ListWithUnpurgedInputsAsync(
        DateTimeOffset createdBefore, CancellationToken ct = default);

    /// <summary>
    /// Stamps inputs_purged_at, which is what the history UI reads to say the
    /// inputs have expired rather than offering a re-run.
    /// </summary>
    Task MarkInputsPurgedAsync(Guid runId, CancellationToken ct = default);
}
