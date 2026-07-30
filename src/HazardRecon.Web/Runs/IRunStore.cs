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
    /// Writes the finished run in one call: status, log, summaries and the chat
    /// payload. Done once at the end rather than per log line, which would cost a
    /// round trip for every message the engine emits.
    /// </summary>
    Task SaveCompletionAsync(
        Guid runId,
        string status,
        string? error,
        object? result,
        object? analysisPayload,
        object log,
        CancellationToken ct = default);

    Task<int> CountSinceAsync(Guid userId, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>
    /// Marks every row still flagged running as interrupted. Called once at
    /// startup: a restart killed those runs and nothing will ever finish them.
    /// </summary>
    Task<int> MarkRunningAsInterruptedAsync(CancellationToken ct = default);
}
