using System.Text.Json;
using System.Text.Json.Serialization;

namespace HazardRecon.Web.Runs;

/// <summary>
/// One row of public.runs. Property names map to the snake_case columns
/// PostgREST returns.
/// </summary>
public class RunRecord
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("user_id")]
    public Guid UserId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "ready";

    [JsonPropertyName("model_id")]
    public string? ModelId { get; set; }

    [JsonPropertyName("set_labels")]
    public List<string> SetLabels { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("log")]
    public JsonElement? Log { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("analysis_payload")]
    public JsonElement? AnalysisPayload { get; set; }

    [JsonPropertyName("inputs_purged_at")]
    public DateTimeOffset? InputsPurgedAt { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }
}
