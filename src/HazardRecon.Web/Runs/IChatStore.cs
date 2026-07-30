namespace HazardRecon.Web.Runs;

/// <summary>
/// The conversation attached to a run. Reads are scoped by user id, so reopening
/// someone else's run cannot surface their questions.
/// </summary>
public interface IChatStore
{
    Task AddAsync(IReadOnlyList<ChatMessageRecord> messages, CancellationToken ct = default);

    Task<IReadOnlyList<ChatMessageRecord>> ListAsync(Guid runId, Guid userId, CancellationToken ct = default);
}
