using System.Text.Json.Serialization;

namespace HazardRecon.Web.Runs;

/// <summary>
/// One row of public.run_output_files: a downloadable artefact of a run, with
/// its size on disk. run_set_result_id is null for run-level files (memo,
/// workbook, dashboard) and set for a set's own output CSVs.
/// </summary>
public class RunOutputFileRecord
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("run_id")]
    public Guid RunId { get; set; }

    [JsonPropertyName("user_id")]
    public Guid UserId { get; set; }

    [JsonPropertyName("run_set_result_id")]
    public long? RunSetResultId { get; set; }

    /// <summary>
    /// Write-only: which set this file belongs to (null for memo/workbook/dashboard).
    /// The completion RPC resolves this to run_set_result_id; a row read back
    /// from PostgREST never has it set.
    /// </summary>
    [JsonPropertyName("set_key")]
    public string? SetKey { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("position")]
    public int Position { get; set; }
}
