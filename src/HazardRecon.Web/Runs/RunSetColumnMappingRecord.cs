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
}
