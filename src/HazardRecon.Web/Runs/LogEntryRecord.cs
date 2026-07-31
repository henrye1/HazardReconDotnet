using System.Text.Json.Serialization;

namespace HazardRecon.Web.Runs;

/// <summary>One row of public.logs.</summary>
public class LogEntryRecord
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("run_id")]
    public Guid RunId { get; set; }

    [JsonPropertyName("user_id")]
    public Guid UserId { get; set; }

    /// <summary>Stable tiebreaker: two lines can land in the same instant.</summary>
    [JsonPropertyName("seq")]
    public int Seq { get; set; }

    [JsonPropertyName("occurred_at")]
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>FK to log_types; the code (ok/warn/info/head/tool) is LogKind's constants.</summary>
    [JsonPropertyName("type_id")]
    public short TypeId { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
