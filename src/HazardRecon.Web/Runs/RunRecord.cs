using System.Text.Json.Serialization;

namespace HazardRecon.Web.Runs;

/// <summary>
/// One row of public.runs. Property names map to the snake_case columns
/// PostgREST returns. The list/nested properties are only populated when the
/// row is read via a PostgREST embedded select (see SupabaseRunStore) - a
/// bare create/patch response leaves them null.
/// </summary>
public class RunRecord
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("user_id")]
    public Guid UserId { get; set; }

    [JsonPropertyName("status_id")]
    public short StatusId { get; set; } = RunStatus.IdOf(RunStatus.Ready);

    /// <summary>ready/running/done/error/interrupted - derived, not its own column.</summary>
    [JsonIgnore]
    public string Status => RunStatus.CodeOf(StatusId);

    /// <summary>
    /// What the user called this run. Null for every run created before the
    /// wizard asked, which the UI falls back out of rather than inventing one.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Initialised rather than left at 0 so a row that predates the column - or a
    /// bare create/patch response that does not carry it - still resolves through
    /// <see cref="RunType"/> instead of throwing on an unknown id.
    /// </summary>
    [JsonPropertyName("run_type_id")]
    public short RunTypeId { get; set; } = RunTypeLookup.IdOf(RunTypeLookup.Default);

    /// <summary>lending/trade_receivables - derived, not its own column.</summary>
    [JsonIgnore]
    public string RunType => RunTypeLookup.CodeOf(RunTypeId);

    [JsonPropertyName("model_id")]
    public string? ModelId { get; set; }

    [JsonPropertyName("set_labels")]
    public List<string> SetLabels { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("inputs_purged_at")]
    public DateTimeOffset? InputsPurgedAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }

    [JsonPropertyName("run_results")]
    public RunResultsRecord? Results { get; set; }

    [JsonPropertyName("logs")]
    public List<LogEntryRecord>? Logs { get; set; }

    [JsonPropertyName("run_set_results")]
    public List<RunSetResultRecord>? RunSetResults { get; set; }

    [JsonPropertyName("run_output_files")]
    public List<RunOutputFileRecord>? OutputFiles { get; set; }

    [JsonPropertyName("run_commentary_lines")]
    public List<RunCommentaryLineRecord>? CommentaryLines { get; set; }
}
