using System.Text.Json.Serialization;

namespace HazardRecon.Web.Runs;

/// <summary>One row of public.run_results - the 1:1 completion-only extension of a run.</summary>
public class RunResultsRecord
{
    [JsonPropertyName("run_id")]
    public Guid RunId { get; set; }

    [JsonPropertyName("workbook_filename")]
    public string? WorkbookFilename { get; set; }

    [JsonPropertyName("dashboard_filename")]
    public string? DashboardFilename { get; set; }

    [JsonPropertyName("memo_filename")]
    public string? MemoFilename { get; set; }

    [JsonPropertyName("analysis_markdown")]
    public string? AnalysisMarkdown { get; set; }
}
