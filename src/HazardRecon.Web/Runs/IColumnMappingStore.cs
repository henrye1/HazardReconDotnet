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

    /// <summary>Replaces the audit record of what this run's set actually used for this file kind.</summary>
    Task RecordRunMappingAsync(
        Guid runId, string setKey, string fileKind,
        IReadOnlyDictionary<string, string> mapping, CancellationToken ct = default);
}
