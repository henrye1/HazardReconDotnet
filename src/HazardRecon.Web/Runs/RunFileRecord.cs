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
}
