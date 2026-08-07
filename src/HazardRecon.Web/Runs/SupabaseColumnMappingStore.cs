using System.Text;
using System.Text.Json;
using HazardRecon.Web.Supabase;

namespace HazardRecon.Web.Runs;

public class SupabaseColumnMappingStore : IColumnMappingStore
{
    private const string SavedTable = "/rest/v1/saved_column_mappings";
    private const string RunTable = "/rest/v1/run_set_column_mappings";

    private static readonly Dictionary<string, string> MergeDuplicates =
        new() { ["Prefer"] = "resolution=merge-duplicates" };

    private readonly SupabaseRestClient _rest;

    public SupabaseColumnMappingStore(SupabaseRestClient rest) => _rest = rest;

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public async Task<IReadOnlyDictionary<string, string>> GetSavedMappingAsync(
        Guid userId, string fileKind, string columnSignature, CancellationToken ct = default)
    {
        string body = await _rest.SendAsync(HttpMethod.Get,
            $"{SavedTable}?user_id=eq.{userId}&file_kind=eq.{fileKind}&column_signature=eq.{columnSignature}&select=field_name,source_column",
            null, null, ct);

        List<SavedColumnMappingRecord> rows =
            JsonSerializer.Deserialize<List<SavedColumnMappingRecord>>(body) ?? new List<SavedColumnMappingRecord>();

        return rows.ToDictionary(r => r.FieldName, r => r.SourceColumn);
    }

    public async Task SaveMappingAsync(
        Guid userId, string fileKind, string columnSignature,
        IReadOnlyDictionary<string, string> mapping, CancellationToken ct = default)
    {
        if (mapping.Count == 0) return;

        var rows = mapping.Select(kv => new
        {
            user_id = userId,
            file_kind = fileKind,
            column_signature = columnSignature,
            field_name = kv.Key,
            source_column = kv.Value,
            last_used_at = DateTimeOffset.UtcNow
        });

        await _rest.SendAsync(HttpMethod.Post,
            $"{SavedTable}?on_conflict=user_id,file_kind,column_signature,field_name",
            Json(rows), MergeDuplicates, ct);
    }

    public async Task RecordRunMappingAsync(
        Guid runId, string setKey, string fileKind,
        IReadOnlyDictionary<string, string> mapping, bool? hasHeaders = null, CancellationToken ct = default)
    {
        string encodedSetKey = Uri.EscapeDataString(setKey);
        await _rest.SendAsync(HttpMethod.Delete,
            $"{RunTable}?run_id=eq.{runId}&set_key=eq.{encodedSetKey}&file_kind=eq.{fileKind}", null, null, ct);

        if (mapping.Count == 0) return;

        var rows = mapping.Select(kv => new
        {
            run_id = runId,
            set_key = setKey,
            file_kind = fileKind,
            field_name = kv.Key,
            source_column = kv.Value,
            has_headers = hasHeaders
        });

        await _rest.SendAsync(HttpMethod.Post, RunTable, Json(rows), null, ct);
    }

    public async Task<bool?> GetRunHasHeadersAsync(
        Guid runId, string setKey, string fileKind, CancellationToken ct = default)
    {
        string encodedSetKey = Uri.EscapeDataString(setKey);
        string body = await _rest.SendAsync(HttpMethod.Get,
            $"{RunTable}?run_id=eq.{runId}&set_key=eq.{encodedSetKey}&file_kind=eq.{fileKind}" +
            "&select=has_headers&limit=1",
            null, null, ct);

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement.ArrayEnumerator rows = doc.RootElement.EnumerateArray();
        if (!rows.MoveNext()) return null;

        JsonElement hasHeaders = rows.Current.GetProperty("has_headers");
        return hasHeaders.ValueKind == JsonValueKind.Null ? null : hasHeaders.GetBoolean();
    }
}
