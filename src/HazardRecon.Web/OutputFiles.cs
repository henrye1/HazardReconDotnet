using HazardRecon.Core.Models;

namespace HazardRecon.Web;

/// <summary>One downloadable artefact of a run, with its size on disk.</summary>
public record OutputFile(string Name, long Bytes);

/// <summary>
/// Lists what a finished run produced, for the run detail's files tab.
///
/// The sizes are read once, while the run's output folder is still on disk, and
/// travel with the stored result - so a reopened run can list them without the
/// files having to still be there.
/// </summary>
public static class OutputFiles
{
    public static List<OutputFile> Describe(string outdir, ReconciliationRunResult result)
    {
        // memo first, then the workbook and dashboard, then each set's CSVs -
        // the order the detail lists them in
        List<string> names = new();
        if (!string.IsNullOrWhiteSpace(result.Memo)) names.Add(result.Memo);
        if (!string.IsNullOrWhiteSpace(result.Workbook)) names.Add(result.Workbook);
        if (!string.IsNullOrWhiteSpace(result.Dashboard)) names.Add(result.Dashboard);

        foreach (SingleSetResult set in result.Results.Values)
            names.AddRange(set.Summary.Files.Where(f => !string.IsNullOrWhiteSpace(f)));

        return names.Distinct()
            .Select(n => new OutputFile(n, SizeOf(outdir, n)))
            .ToList();
    }

    private static long SizeOf(string outdir, string name)
    {
        try
        {
            FileInfo file = new(Path.Combine(outdir, name));
            return file.Exists ? file.Length : 0;
        }
        catch
        {
            // a file size is a nicety; it may never fail a run that has completed
            return 0;
        }
    }
}
