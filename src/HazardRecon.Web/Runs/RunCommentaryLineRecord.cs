using System.Text.Json.Serialization;

namespace HazardRecon.Web.Runs;

/// <summary>One row of public.run_commentary_lines - a sentence the workbook also opens with.</summary>
public class RunCommentaryLineRecord
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("run_id")]
    public Guid RunId { get; set; }

    [JsonPropertyName("user_id")]
    public Guid UserId { get; set; }

    [JsonPropertyName("line")]
    public string Line { get; set; } = string.Empty;

    [JsonPropertyName("position")]
    public int Position { get; set; }
}
