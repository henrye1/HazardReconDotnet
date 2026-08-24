using System.Text;
using System.Text.Json;
using HazardRecon.Web.Supabase;

namespace HazardRecon.Web.Runs;

public class SupabaseColumnMappingStore : IColumnMappingStore
{
    private const string SavedTable = "/rest/v1/saved_column_mappings";
    private const string RunTable = "/rest/v1/run_set_column_mappings";

    private readonly SupabaseRestClient _rest;

    public SupabaseColumnMappingStore(SupabaseRestClient rest) => _rest = rest;

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetSavedMappingAsync(
        Guid userId, string fileKind, string columnSignature, CancellationToken ct = default)
    {
        string body = await _rest.SendAsync(HttpMethod.Get,
            $"{SavedTable}?user_id=eq.{userId}&file_kind=eq.{fileKind}&column_signature=eq.{columnSignature}" +
            "&select=field_name,source_column,ordinal&order=field_name,ordinal",
            null, null, ct);

        List<SavedColumnMappingRecord> rows =
            JsonSerializer.Deserialize<List<SavedColumnMappingRecord>>(body) ?? new List<SavedColumnMappingRecord>();

        // Grouped rather than one row per field: a multi-valued field has several,
        // and ordering them here means a caller never has to know that the ordinal
        // exists. Ordered locally as well as in the query so the result does not
        // depend on the server honouring the order clause.
        return rows
            .GroupBy(r => r.FieldName)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.OrderBy(r => r.Ordinal).Select(r => r.SourceColumn).ToList());
    }

    public async Task SaveMappingAsync(
        Guid userId, string fileKind, string columnSignature,
        IReadOnlyDictionary<string, IReadOnlyList<string>> mapping, CancellationToken ct = default)
    {
        if (mapping.Count == 0) return;

        // Delete first, and only for the fields being written. An upsert would
        // leave a row behind for every column the user has deselected since last
        // time - so a bucket they removed would come back on the next upload.
        foreach (string field in mapping.Keys)
        {
            await _rest.SendAsync(HttpMethod.Delete,
                $"{SavedTable}?user_id=eq.{userId}&file_kind=eq.{fileKind}" +
                $"&column_signature=eq.{columnSignature}&field_name=eq.{Uri.EscapeDataString(field)}",
                null, null, ct);
        }

        var rows = mapping
            .SelectMany(kv => kv.Value.Select((column, ordinal) => new
            {
                user_id = userId,
                file_kind = fileKind,
                column_signature = columnSignature,
                field_name = kv.Key,
                source_column = column,
                ordinal,
                last_used_at = DateTimeOffset.UtcNow
            }))
            .ToList();

        if (rows.Count == 0) return;

        await _rest.SendAsync(HttpMethod.Post, SavedTable, Json(rows), null, ct);
    }

    public async Task RecordRunMappingAsync(
        Guid runId, string setKey, string fileKind,
        IReadOnlyDictionary<string, IReadOnlyList<string>> mapping, CancellationToken ct = default)
    {
        string encodedSetKey = Uri.EscapeDataString(setKey);
        await _rest.SendAsync(HttpMethod.Delete,
            $"{RunTable}?run_id=eq.{runId}&set_key=eq.{encodedSetKey}&file_kind=eq.{fileKind}", null, null, ct);

        var rows = mapping
            .SelectMany(kv => kv.Value.Select((column, ordinal) => new
            {
                run_id = runId,
                set_key = setKey,
                file_kind = fileKind,
                field_name = kv.Key,
                source_column = column,
                ordinal
            }))
            .ToList();

        if (rows.Count == 0) return;

        await _rest.SendAsync(HttpMethod.Post, RunTable, Json(rows), null, ct);
    }
}
