using HazardRecon.Core.Models;

namespace HazardRecon.Web;

/// <summary>One line the engine logged, with the real timestamp public.logs needs.</summary>
internal record JobLogEntry(DateTimeOffset OccurredAt, string Message, string Kind);

internal class JobState
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Owner of the run, taken from the token that created it.</summary>
    public Guid UserId { get; set; }

    public string Status { get; set; } = "ready";
    public List<string> Roots { get; set; } = new();
    public string Outdir { get; set; } = string.Empty;
    public string Indir { get; set; } = string.Empty;
    public List<JobLogEntry> Log { get; set; } = new();
    public object? Result { get; set; }
    public string? Error { get; set; }
    public string Started { get; set; } = string.Empty;

    /// <summary>How far the run has got. Replaced wholesale on every engine update.</summary>
    public IReadOnlyList<RunStage> Stages { get; set; } = Array.Empty<RunStage>();

    /// <summary>When the current attempt began, for the elapsed clock on the progress screen.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Set when the attempt ends, so the elapsed clock stops instead of climbing forever.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    public string? ModelId { get; set; }
    public Dictionary<string, object>? AnalysisPayload { get; set; }
}
