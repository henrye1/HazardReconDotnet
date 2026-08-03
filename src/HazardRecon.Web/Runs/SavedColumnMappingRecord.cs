using System.Text.Json.Serialization;

namespace HazardRecon.Web.Runs;

/// <summary>One row of public.saved_column_mappings - a reusable mapping for one field of one file kind.</summary>
public class SavedColumnMappingRecord
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("user_id")]
    public Guid UserId { get; set; }

    [JsonPropertyName("file_kind")]
    public string FileKind { get; set; } = string.Empty;

    [JsonPropertyName("column_signature")]
    public string ColumnSignature { get; set; } = string.Empty;

    [JsonPropertyName("field_name")]
    public string FieldName { get; set; } = string.Empty;

    [JsonPropertyName("source_column")]
    public string SourceColumn { get; set; } = string.Empty;
}
