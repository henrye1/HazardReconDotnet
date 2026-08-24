namespace HazardRecon.Web.Runs;

/// <summary>
/// Persistence for column mappings: the reusable saved profile, and what a run
/// actually used.
///
/// A field maps to a LIST of columns, in the order the user picked them. Nearly
/// always that list holds one, but an age analysis' aging buckets are summed, so
/// several is the point.
/// </summary>
public interface IColumnMappingStore
{
    /// <summary>The saved field-to-columns mapping for this user/file kind/column shape, if one was ever confirmed.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetSavedMappingAsync(
        Guid userId, string fileKind, string columnSignature, CancellationToken ct = default);

    /// <summary>
    /// Replaces the saved mapping for the fields it is given, so a future upload of
    /// the same column shape reuses it.
    ///
    /// Replaces rather than upserts: with several columns per field, an upsert
    /// leaves behind the rows for columns the user has since deselected, and they
    /// come back on the next upload. Deselection has to be representable.
    /// </summary>
    Task SaveMappingAsync(
        Guid userId, string fileKind, string columnSignature,
        IReadOnlyDictionary<string, IReadOnlyList<string>> mapping, CancellationToken ct = default);

    /// <summary>Replaces the audit record of what this run's set actually used for this file kind.</summary>
    Task RecordRunMappingAsync(
        Guid runId, string setKey, string fileKind,
        IReadOnlyDictionary<string, IReadOnlyList<string>> mapping, CancellationToken ct = default);
}
