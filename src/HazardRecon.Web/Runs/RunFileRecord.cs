using System.Text.Json.Serialization;

namespace HazardRecon.Web.Runs;

/// <summary>One row of public.run_files.</summary>
public class RunFileRecord
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("run_id")]
    public Guid RunId { get; set; }

    [JsonPropertyName("user_id")]
    public Guid UserId { get; set; }

    /// <summary>"input" or "output".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "output";

    [JsonPropertyName("set_key")]
    public string? SetKey { get; set; }

    [JsonPropertyName("relative_path")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("storage_path")]
    public string StoragePath { get; set; } = string.Empty;

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    /// <summary>
    /// Which slot the user picked this file for: exposure | writeoff | debug |
    /// scenario. Null for output rows and for input rows written before this was
    /// recorded, where it is derived from the canonical file name instead.
    /// </summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    /// <summary>
    /// The name the file had when it was picked. SetFileReceiver renames exposure,
    /// write-off and scenario files to canonical names, so without this a card can
    /// only say "writeoff.csv", which identifies neither period nor source. Null
    /// for output rows and for input rows written before this was recorded.
    /// </summary>
    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }
}
