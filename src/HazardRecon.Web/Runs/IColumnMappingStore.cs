namespace HazardRecon.Web.Runs;

/// <summary>Persistence for column mappings: the reusable saved profile, and what a run actually used.</summary>
public interface IColumnMappingStore
{
    /// <summary>The saved field-to-column mapping for this user/file kind/column shape, if one was ever confirmed.</summary>
    Task<IReadOnlyDictionary<string, string>> GetSavedMappingAsync(
        Guid userId, string fileKind, string columnSignature, CancellationToken ct = default);

    /// <summary>Upserts the saved mapping so a future upload of the same column shape reuses it.</summary>
    Task SaveMappingAsync(
        Guid userId, string fileKind, string columnSignature,
        IReadOnlyDictionary<string, string> mapping, CancellationToken ct = default);

    /// <summary>
    /// Replaces the audit record of what this run's set actually used for this
    /// file kind. hasHeaders is the confirmed "first row is a header" reading -
    /// null when the client never overrode the sniffer's guess - carried
    /// alongside the mapping so a reopened run can recompute the same column
    /// signature instead of falling back to a fresh sniff.
    /// </summary>
    Task RecordRunMappingAsync(
        Guid runId, string setKey, string fileKind,
        IReadOnlyDictionary<string, string> mapping, bool? hasHeaders = null, CancellationToken ct = default);

    /// <summary>
    /// The header reading confirmed for this run's set and file kind, or null if
    /// none was ever recorded (never confirmed, or the sniffer's guess was kept).
    /// </summary>
    Task<bool?> GetRunHasHeadersAsync(
        Guid runId, string setKey, string fileKind, CancellationToken ct = default);
}
