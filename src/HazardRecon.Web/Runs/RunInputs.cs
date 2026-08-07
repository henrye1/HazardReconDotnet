namespace HazardRecon.Web.Runs;

/// <summary>One stored input file, as the Files step needs to show it.</summary>
public record RunInputFile(string Role, string Name, long SizeBytes);

/// <summary>One set's stored inputs, in the set order the run was uploaded in.</summary>
public record RunInputSet(int Index, string Label, IReadOnlyList<RunInputFile> Files);

/// <summary>
/// Turns a run's run_files index rows into per-set slots.
///
/// Input rows are keyed by set *index*, which is the leading segment of
/// relative_path ("0/IFRS9.csv") - PersistDirectoryAsync is called once for the
/// whole input directory, so set_key is null on these rows. Set labels come from
/// runs.set_labels, which is in the same order.
///
/// Role and original name are read where recorded and derived where not, so runs
/// created before those columns existed still list their inputs - with canonical
/// names, which is the best that can be done for them.
/// </summary>
public static class RunInputs
{
    public static string RoleOf(RunFileRecord file)
    {
        if (!string.IsNullOrEmpty(file.Role)) return file.Role;

        // debug files keep their own source-system names, so they cannot be
        // matched by name - they are whatever is not one of the three canonical
        // names the receiver assigns
        return Path.GetFileName(file.RelativePath).ToLowerInvariant() switch
        {
            "ifrs9.csv" => "exposure",
            "writeoff.csv" => "writeoff",
            "scenario.json" => "scenario",
            _ => "debug"
        };
    }

    private static int? SetIndexOf(RunFileRecord file)
    {
        string[] parts = file.RelativePath.Split('/');
        return parts.Length >= 2 && int.TryParse(parts[0], out int index) ? index : null;
    }

    public static IReadOnlyList<RunInputSet> Describe(
        IReadOnlyList<RunFileRecord> files, IReadOnlyList<string> setLabels)
    {
        return files
            .Where(f => f.Kind == "input")
            .Select(f => (File: f, Index: SetIndexOf(f)))
            .Where(x => x.Index != null)
            .GroupBy(x => x.Index!.Value)
            .OrderBy(g => g.Key)
            .Select(g => new RunInputSet(
                g.Key,
                g.Key < setLabels.Count ? setLabels[g.Key] : $"Set {g.Key + 1}",
                g.Select(x => new RunInputFile(
                        RoleOf(x.File),
                        string.IsNullOrEmpty(x.File.OriginalName)
                            ? Path.GetFileName(x.File.RelativePath)
                            : x.File.OriginalName,
                        x.File.SizeBytes))
                    .ToList()))
            .ToList();
    }
}
