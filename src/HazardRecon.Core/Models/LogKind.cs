namespace HazardRecon.Core.Models;

/// <summary>
/// The kind of a log line, as passed to an <c>Action&lt;string, string&gt;</c> logger
/// throughout the engine. Mirrors public.log_types - a value here must have a
/// matching row there.
/// </summary>
public static class LogKind
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Info = "info";

    /// <summary>A section heading in the log, not a step outcome.</summary>
    public const string Head = "head";

    /// <summary>A tool invocation note, only emitted by the console-mode fallback logger.</summary>
    public const string Tool = "tool";
}
