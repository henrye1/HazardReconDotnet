using System.Text;
using System.Text.Json;
using HazardRecon.Web.Supabase;

namespace HazardRecon.Web.Runs;

public class SupabaseRunStore : IRunStore
{
    private const string Table = "/rest/v1/runs";
    private const string RpcSaveCompletion = "/rest/v1/rpc/save_run_completion";

    /// <summary>Every child table a run detail view needs, in one round trip.</summary>
    private const string DetailEmbed =
        "run_results(*)," +
        "logs(*)," +
        "run_output_files(*)," +
        "run_commentary_lines(*)," +
        "run_set_results(*," +
            "run_set_migration_cells(*)," +
            "run_set_monthly_totals(*)," +
            "run_set_hazard_matrix(*)," +
            "run_set_cohort_matrix(*)," +
            "run_set_lgd_points(*)," +
            "run_set_last_bucket_rows(*)," +
            "run_set_untraced_rows(*)," +
            "run_set_wo_exception_rows(*)," +
            "run_set_engine_params(*))";

    /// <summary>Only what RunSummary needs, so the history list stays cheap.</summary>
    private const string SummaryEmbed = "run_set_results(untraced_total,trace_rate,wo_in_window)";

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
            Json(new { user_id = userId, status_id = RunStatus.IdOf(RunStatus.Ready), set_labels = setLabels }),
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
            $"{Table}?id=eq.{runId}&user_id=eq.{userId}&select=*,{DetailEmbed}", null, null, ct);

        return Parse(body).FirstOrDefault();
    }

    public async Task<IReadOnlyList<RunRecord>> ListAsync(Guid userId, int limit = 50, CancellationToken ct = default)
    {
        string body = await _rest.SendAsync(HttpMethod.Get,
            $"{Table}?user_id=eq.{userId}&select=*,{SummaryEmbed}&order=created_at.desc&limit={limit}", null, null, ct);

        return Parse(body);
    }

    public async Task UpdateStatusAsync(Guid runId, string status, string? error, CancellationToken ct = default)
    {
        Dictionary<string, object?> patch = new()
        {
            ["status_id"] = RunStatus.IdOf(status),
            ["error"] = error
        };

        if (status == RunStatus.Running) patch["started_at"] = DateTimeOffset.UtcNow;
        if (status is RunStatus.Done or RunStatus.Error or RunStatus.Interrupted) patch["finished_at"] = DateTimeOffset.UtcNow;

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
        Guid userId,
        string status,
        string? error,
        RunResultsRecord runResults,
        IReadOnlyList<RunSetResultRecord> setResults,
        IReadOnlyList<LogEntryRecord> log,
        IReadOnlyList<RunOutputFileRecord> outputFiles,
        IReadOnlyList<RunCommentaryLineRecord> commentaryLines,
        CancellationToken ct = default)
    {
        var payload = new
        {
            status_id = RunStatus.IdOf(status),
            error,
            run_results = runResults,
            run_set_results = setResults,
            logs = log,
            run_output_files = outputFiles,
            run_commentary_lines = commentaryLines
        };

        var body = new
        {
            p_run_id = runId,
            p_user_id = userId,
            p_payload = payload
        };

        await _rest.SendAsync(HttpMethod.Post, RpcSaveCompletion, Json(body), null, ct);
    }

    public async Task DeleteAsync(Guid runId, Guid userId, CancellationToken ct = default)
    {
        // scoped to the owner for the same reason GetAsync is: someone else's run
        // must behave exactly like one that does not exist
        await _rest.SendAsync(HttpMethod.Delete, $"{Table}?id=eq.{runId}&user_id=eq.{userId}", null, null, ct);
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

    public async Task<IReadOnlyList<RunRecord>> ListWithUnpurgedInputsAsync(
        DateTimeOffset createdBefore, CancellationToken ct = default)
    {
        string encoded = Uri.EscapeDataString(createdBefore.ToString("O"));
        string body = await _rest.SendAsync(HttpMethod.Get,
            $"{Table}?created_at=lt.{encoded}&inputs_purged_at=is.null&select=*&order=created_at.asc",
            null, null, ct);

        return Parse(body);
    }

    public async Task MarkInputsPurgedAsync(Guid runId, CancellationToken ct = default)
    {
        await _rest.SendAsync(HttpMethod.Patch, $"{Table}?id=eq.{runId}",
            Json(new { inputs_purged_at = DateTimeOffset.UtcNow }), ReturnRow, ct);
    }

    public async Task<int> MarkRunningAsInterruptedAsync(CancellationToken ct = default)
    {
        string body = await _rest.SendAsync(HttpMethod.Patch,
            $"{Table}?status_id=eq.{RunStatus.IdOf(RunStatus.Running)}",
            Json(new { status_id = RunStatus.IdOf(RunStatus.Interrupted), finished_at = DateTimeOffset.UtcNow }),
            ReturnRow, ct);

        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetArrayLength();
    }
}
