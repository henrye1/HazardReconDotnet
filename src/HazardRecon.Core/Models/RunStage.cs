namespace HazardRecon.Core.Models;

/// <summary>
/// One step of a reconciliation run, as the progress screen shows it.
///
/// Distinct from a log line: the log says what happened, a stage says how far
/// along the run is. The whole list is published on every change, so a caller
/// that polls sees pending steps before they start and can count them.
/// </summary>
public record RunStage
{
    /// <summary>Stable identity, so an update replaces a row rather than adding one.</summary>
    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>One line on what the step does, shown under the name.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>pending, running, done, warn, skipped or error.</summary>
    public string Status { get; init; } = StageStatus.Pending;

    /// <summary>Wall-clock time the step took; null until it finishes.</summary>
    public double? Seconds { get; init; }
}

public static class StageStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Done = "done";

    /// <summary>Finished, but something was missing or did not reconcile.</summary>
    public const string Warn = "warn";

    /// <summary>Deliberately not run - an optional step with nothing to do.</summary>
    public const string Skipped = "skipped";

    public const string Error = "error";
}
