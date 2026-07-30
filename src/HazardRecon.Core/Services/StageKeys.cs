namespace HazardRecon.Core.Services;

/// <summary>
/// The shape of a run, as the progress screen lists it.
///
/// Kept beside the engine rather than in the web layer because the engine decides
/// what the steps are; the browser only renders them. Per-set keys are prefixed
/// with the set key so several sets can be in the list at once.
/// </summary>
public static class StageKeys
{
    public const string Discover = "discover";
    public const string Workbook = "workbook";
    public const string Analysis = "analysis";
    public const string Dashboard = "dashboard";
    public const string Memo = "memo";

    public static string Load(string set) => set + ":load";
    public static string Check1(string set) => set + ":check1";
    public static string Migrations(string set) => set + ":migrations";
    public static string Check2(string set) => set + ":check2";
    public static string Validate(string set) => set + ":validate";
    public static string Export(string set) => set + ":export";

    /// <summary>The six steps every set goes through, in order.</summary>
    public static (string Key, string Name, string Detail)[] ForSet(string set, string label)
    {
        string who = string.IsNullOrWhiteSpace(label) ? set : label;
        return new[]
        {
            (Load(set), $"{who} - load inputs", "Read the write-off, defaults, scored and IFRS9 files"),
            (Check1(set), $"{who} - check 1", "Trace each default to the write-off or IFRS9 file"),
            (Migrations(set), $"{who} - migration matrix", "Build the rating migration matrix from the scored file"),
            (Check2(set), $"{who} - check 2", "Find write-offs inside the scoring window that never defaulted"),
            (Validate(set), $"{who} - validate migrations", "Compare the rebuilt matrix against the engine's own"),
            (Export(set), $"{who} - write CSVs", "Export the traced, untraced and migration detail"),
        };
    }

    /// <summary>What happens once every set is done.</summary>
    public static (string Key, string Name, string Detail)[] Tail(bool analyze)
    {
        List<(string, string, string)> tail = new()
        {
            (Workbook, "Build the workbook", "Write every set into one Excel file"),
        };

        if (analyze)
            tail.Add((Analysis, "Generate AI analysis", "Ask the model to explain the reconciliation"));

        tail.Add((Dashboard, "Render the dashboard", "Build the interactive migration matrix and engine results"));

        if (analyze)
            tail.Add((Memo, "Write the analysis memo", "Turn the analysis into a Word document"));

        return tail.ToArray();
    }
}
