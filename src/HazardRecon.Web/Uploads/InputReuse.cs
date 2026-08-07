using HazardRecon.Web.Files;
using HazardRecon.Web.Runs;

namespace HazardRecon.Web.Uploads;

/// <summary>The roles of one set that should come from the previous run rather than the upload.</summary>
public record ReuseRequest(int SetIndex, IReadOnlyList<string> Roles);

/// <summary>
/// Reused files as upload items, plus the handles holding them open. The caller
/// disposes Open after the receiver has copied them.
/// </summary>
public record ReuseOutcome(
    bool Ok, string? Error, IReadOnlyList<SetFileItem> Items, IReadOnlyList<IDisposable> Open)
{
    public static ReuseOutcome Fail(string error) =>
        new(false, error, Array.Empty<SetFileItem>(), Array.Empty<IDisposable>());
}

/// <summary>
/// Rebuilds a previous run's stored inputs as ordinary SetFileItems, so a re-run
/// that replaces one file does not have to re-upload the rest.
///
/// Deliberately produces upload items rather than writing into the new run's
/// input directory itself: handing them to SetFileReceiver alongside the real
/// uploads means canonical naming, the exposure requirement, the per-set size
/// limit and the set label all keep working, with no second code path that could
/// drift from the first.
/// </summary>
public static class InputReuse
{
    private static SetFileKind? KindOf(string role) => role switch
    {
        "exposure" => SetFileKind.Exposure,
        "writeoff" => SetFileKind.Writeoff,
        "debug" => SetFileKind.Debug,
        "scenario" => SetFileKind.Scenario,
        _ => null
    };

    public static async Task<ReuseOutcome> MaterialiseAsync(
        IReadOnlyList<ReuseRequest> requests,
        IReadOnlyList<RunFileRecord> previousFiles,
        IFileStore storage,
        string tempRoot,
        CancellationToken ct = default)
    {
        List<SetFileItem> items = new();
        List<IDisposable> open = new();

        // set index -> the rows for it, from the previous run's index
        var bySet = previousFiles
            .Where(f => f.Kind == "input")
            .Select(f => (File: f, Parts: f.RelativePath.Split('/')))
            .Where(x => x.Parts.Length >= 2 && int.TryParse(x.Parts[0], out _))
            .GroupBy(x => int.Parse(x.Parts[0]))
            .ToDictionary(g => g.Key, g => g.Select(x => x.File).ToList());

        foreach (ReuseRequest request in requests)
        {
            bySet.TryGetValue(request.SetIndex, out List<RunFileRecord>? setFiles);
            setFiles ??= new List<RunFileRecord>();

            foreach (string role in request.Roles)
            {
                SetFileKind? kind = KindOf(role);
                if (kind == null)
                {
                    Dispose(open);
                    return ReuseOutcome.Fail($"Unknown file role '{role}'.");
                }

                List<RunFileRecord> matching = setFiles.Where(f => RunInputs.RoleOf(f) == role).ToList();
                if (matching.Count == 0)
                {
                    Dispose(open);
                    return ReuseOutcome.Fail(
                        $"Set {request.SetIndex + 1} has no stored {role} file to reuse - please choose it again.");
                }

                foreach (RunFileRecord file in matching)
                {
                    string destination = Path.Combine(
                        tempRoot, request.SetIndex.ToString(), Path.GetFileName(file.RelativePath));

                    try
                    {
                        await storage.DownloadToFileAsync(file.StoragePath, destination, ct);
                    }
                    catch (Exception)
                    {
                        // indexed but not in the bucket: name the slot, because the
                        // user's only way forward is to pick that file again
                        Dispose(open);
                        return ReuseOutcome.Fail(
                            $"Set {request.SetIndex + 1}'s stored {role} file could not be read - please choose it again.");
                    }

                    FileStream content = File.OpenRead(destination);
                    open.Add(content);

                    items.Add(new SetFileItem(
                        request.SetIndex,
                        kind.Value,
                        string.IsNullOrEmpty(file.OriginalName)
                            ? Path.GetFileName(file.RelativePath)
                            : file.OriginalName,
                        content,
                        content.Length));
                }
            }
        }

        return new ReuseOutcome(true, null, items, open);
    }

    private static void Dispose(IReadOnlyList<IDisposable> open)
    {
        foreach (IDisposable d in open) d.Dispose();
    }
}
