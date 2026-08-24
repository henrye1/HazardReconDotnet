using System.Text.Json.Serialization;

namespace HazardRecon.Web.Runs;

/// <summary>One row of public.run_set_column_mappings - the mapping actually used for one run's set.</summary>
public class RunSetColumnMappingRecord
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("run_id")]
    public Guid RunId { get; set; }

    [JsonPropertyName("set_key")]
    public string SetKey { get; set; } = string.Empty;

    [JsonPropertyName("file_kind")]
    public string FileKind { get; set; } = string.Empty;

    [JsonPropertyName("field_name")]
    public string FieldName { get; set; } = string.Empty;

    [JsonPropertyName("source_column")]
    public string SourceColumn { get; set; } = string.Empty;

    /// <summary>
    /// Where this column sits among the ones mapped to the same field. Zero for a
    /// single-valued field; 0..n-1, in the order the user picked them, for the
    /// aging buckets of an age analysis.
    /// </summary>
    [JsonPropertyName("ordinal")]
    public int Ordinal { get; set; }
}
