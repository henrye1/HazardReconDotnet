namespace HazardRecon.Web.Runs;

/// <summary>
/// Persistence for runs. Every read is scoped by user id: callers pass the sub
/// claim of a verified token, never a value from the request body.
/// </summary>
public interface IRunStore
{
    Task<RunRecord> CreateAsync(Guid userId, IReadOnlyList<string> setLabels, CancellationToken ct = default);

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
