namespace HazardRecon.Web.Runs;

/// <summary>
/// The index of what a run has in object storage. Reads are scoped by user id so
/// one user can never enumerate another's artifacts.
/// </summary>
public interface IRunFileStore
{
    Task AddAsync(IReadOnlyList<RunFileRecord> files, CancellationToken ct = default);

    Task<IReadOnlyList<RunFileRecord>> ListAsync(Guid runId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Finds one file of a run by the name it is downloaded under. Scoped by user
    /// so an unknown owner is indistinguishable from an unknown file.
    /// </summary>
    Task<RunFileRecord?> FindOutputAsync(Guid runId, Guid userId, string fileName, CancellationToken ct = default);

    /// <summary>Drops the input rows for a run once its inputs have been purged.</summary>
    Task DeleteInputsAsync(Guid runId, CancellationToken ct = default);
}
