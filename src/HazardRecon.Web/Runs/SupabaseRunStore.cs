using System.Text;
using System.Text.Json;
using HazardRecon.Web.Supabase;

namespace HazardRecon.Web.Runs;

public class SupabaseRunStore : IRunStore
{
    private const string Table = "/rest/v1/runs";

    private static readonly Dictionary<string, string> ReturnRow =
        new() { ["Prefer"] = "return=representation" };

    private readonly SupabaseRestClient _rest;

    public SupabaseRunStore(SupabaseRestClient rest) => _rest = rest;

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static List<RunRecord> Parse(string body) =>
        JsonSerializer.Deserialize<List<RunRecord>>(body) ?? new List<RunRecord>();

    public async Task<RunRecord> CreateAsync(Guid userId, IReadOnlyList<string> setLabels, CancellationToken ct = default)
    {
        string body = await _rest.SendAsync(HttpMethod.Post, Table,
            Json(new { user_id = userId, status = "ready", set_labels = setLabels }),
            ReturnRow, ct);

        List<RunRecord> rows = Parse(body);
        if (rows.Count == 0)
        {
            throw new SupabaseException(500, "Insert into runs returned no row.");
        }

        return rows[0];
    }

    public async Task<RunRecord?> GetAsync(Guid runId, Guid userId, CancellationToken ct = default)
    {
        // filtered by user as well as id: an unknown owner must look identical to
        // a missing run, so the endpoint can answer 404 rather than 403
        string body = await _rest.SendAsync(HttpMethod.Get,
            $"{Table}?id=eq.{runId}&user_id=eq.{userId}&select=*", null, null, ct);

        return Parse(body).FirstOrDefault();
    }

    public async Task<IReadOnlyList<RunRecord>> ListAsync(Guid userId, int limit = 50, CancellationToken ct = default)
    {
        string body = await _rest.SendAsync(HttpMethod.Get,
            $"{Table}?user_id=eq.{userId}&select=*&order=created_at.desc&limit={limit}", null, null, ct);

        return Parse(body);
    }

    public async Task UpdateStatusAsync(Guid runId, string status, string? error, CancellationToken ct = default)
    {
        Dictionary<string, object?> patch = new()
        {
            ["status"] = status,
            ["error"] = error
        };

        if (status == "running") patch["started_at"] = DateTimeOffset.UtcNow;
        if (status is "done" or "error" or "interrupted") patch["finished_at"] = DateTimeOffset.UtcNow;

        await _rest.SendAsync(HttpMethod.Patch, $"{Table}?id=eq.{runId}",
            Json(patch), ReturnRow, ct);
    }

    public async Task SetModelAsync(Guid runId, string? modelId, CancellationToken ct = default)
    {
        await _rest.SendAsync(HttpMethod.Patch, $"{Table}?id=eq.{runId}",
            Json(new Dictionary<string, object?> { ["model_id"] = modelId }), ReturnRow, ct);
    }

    public async Task SaveCompletionAsync(
        Guid runId,
        string status,
        string? error,
        object? result,
        object? analysisPayload,
        object log,
        CancellationToken ct = default)
    {
        Dictionary<string, object?> patch = new()
        {
            ["status"] = status,
            ["error"] = error,
            ["result"] = result,
            ["analysis_payload"] = analysisPayload,
            ["log"] = log,
            ["finished_at"] = DateTimeOffset.UtcNow
        };

        await _rest.SendAsync(HttpMethod.Patch, $"{Table}?id=eq.{runId}",
            Json(patch), ReturnRow, ct);
    }

    public async Task<int> CountSinceAsync(Guid userId, DateTimeOffset since, CancellationToken ct = default)
    {
        // escaped: an unescaped '+' in the offset would arrive at PostgREST as a space
        string encoded = Uri.EscapeDataString(since.ToString("O"));
        string body = await _rest.SendAsync(HttpMethod.Get,
            $"{Table}?user_id=eq.{userId}&created_at=gte.{encoded}&select=id", null, null, ct);

        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetArrayLength();
    }

    public async Task<int> MarkRunningAsInterruptedAsync(CancellationToken ct = default)
    {
        string body = await _rest.SendAsync(HttpMethod.Patch,
            $"{Table}?status=eq.running",
            Json(new { status = "interrupted", finished_at = DateTimeOffset.UtcNow }),
            ReturnRow, ct);

        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetArrayLength();
    }
}
